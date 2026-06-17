using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gomp.App.Services;
// Alias just the enum rather than importing all of Gomp.Protocol, so RoomDetail
// resolves unambiguously to the app-side record (not the proto message).
using RoomKind = Gomp.Protocol.RoomKind;

namespace Gomp.App.ViewModels;

/// <summary>
/// The room-management surface for a room you own: change visibility (ACL tier),
/// manage members (add / remove / promote / demote), edit settings (display name,
/// topic, per-room history retention), clear history, copy the address, and close
/// the room. Everything drives through <see cref="IGompGateway"/> against the
/// room's own host, so the whole page runs against a fake in tests.
/// </summary>
public sealed partial class ManageRoomViewModel : ObservableObject
{
    private readonly IGompGateway _gateway;
    private readonly RoomViewModel _room;
    private readonly Func<RoomViewModel, Task> _closeRoom;
    private readonly Action _dismiss;
    private readonly string _self;

    public ManageRoomViewModel(
        IGompGateway gateway, RoomViewModel room,
        Func<RoomViewModel, Task> closeRoom, Action dismiss)
    {
        _gateway = gateway;
        _room = room;
        _closeRoom = closeRoom;
        _dismiss = dismiss;
        _self = gateway.SelfAddress;
        _selectedKind = room.Kind;
        PropertyChanged += OnFieldChanged;
    }

    // Settings (display name / topic / retention) commit themselves: an edit
    // re-arms a debounce, then writes — no save button. Apply()'s host-driven
    // writes are suppressed via _loading so they don't echo straight back.
    private void OnFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DisplayName) or nameof(Topic) or nameof(RetentionMax))
            QueueSettingsSave();
    }

    // Only owned rooms ever open the manager, so Admin is always present here.
    private AdminContext Admin => _room.Admin!;

    public string RoomAddress => _room.Address;
    public string Title => _room.Title;

    public ObservableCollection<ManageMemberViewModel> Members { get; } = new();

    public IReadOnlyList<KindOption> Kinds { get; } = new[]
    {
        new KindOption(RoomKind.Open, "Open", "anyone with the address can join"),
        new KindOption(RoomKind.Friends, "Friends", "only your contacts can join"),
        new KindOption(RoomKind.Invite, "Invite only", "only addresses you add can join"),
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInvite))]
    [NotifyPropertyChangedFor(nameof(SelectedKindOption))]
    private RoomKind _selectedKind;

    /// <summary>The access picker binds to the option object; this maps it to the kind.
    /// Nullable because the ComboBox can momentarily report no selection.</summary>
    public KindOption? SelectedKindOption
    {
        get => Kinds.First(k => k.Kind == SelectedKind);
        set { if (value is not null) SelectedKind = value.Kind; }
    }

    /// <summary>Add-member only makes sense on an allowlist (invite) room.</summary>
    public bool IsInvite => SelectedKind == RoomKind.Invite;

    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _topic = "";
    [ObservableProperty] private int _retentionMax;
    [ObservableProperty] private string _newMemberAddress = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _status;

    // Settings (display name / topic / retention) commit themselves — no save
    // button (visibility still needs an explicit "apply"). _loading suppresses the
    // auto-save while Apply() pushes host state into the fields; _settingsDirty +
    // the debounce coalesce a burst of keystrokes into one write.
    private bool _loading;
    private bool _settingsDirty;
    private CancellationTokenSource? _saveDebounce;

    // Debounce window before an edited setting auto-commits.
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(600);

    /// <summary>Fetch the room's full detail (members, settings) and populate the form.</summary>
    public async Task LoadAsync()
    {
        var res = await _gateway.RoomDetailAsync(Admin.HostBase, Admin.RoomName).ConfigureAwait(true);
        if (!res.Ok || res.Detail is null)
        {
            Status = res.Error ?? "couldn't load the room";
            return;
        }
        Apply(res.Detail);
    }

    private void Apply(RoomDetail d)
    {
        _loading = true;
        try
        {
            SelectedKind = d.Kind;
            DisplayName = d.DisplayName;
            Topic = d.Topic;
            RetentionMax = d.RetentionMax;
        }
        finally
        {
            _loading = false;
            _settingsDirty = false; // these values came FROM the host
        }

        Members.Clear();
        foreach (var m in d.Members)
            Members.Add(new ManageMemberViewModel(m, isSelf: m.Address == _self, PromoteAsync, DemoteAsync, RemoveAsync));

        // Keep the room card in step with the authoritative host state.
        _room.ApplyKind(d.Kind);
        _room.SetDisplayName(d.DisplayName);
    }

    [RelayCommand] private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private Task ApplyVisibilityAsync() =>
        RunAsync("visibility updated", () => _gateway.SetKindAsync(Admin.HostBase, Admin.RoomName, SelectedKind));

    [RelayCommand]
    private async Task AddMemberAsync()
    {
        var addr = NewMemberAddress.Trim();
        if (string.IsNullOrEmpty(addr))
        {
            Status = "paste an address to add";
            return;
        }
        await RunAsync($"added {Addr.Short(addr)}", () => _gateway.AddMemberAsync(Admin.HostBase, Admin.RoomName, addr))
            .ConfigureAwait(true);
        NewMemberAddress = "";
    }

    private Task PromoteAsync(ManageMemberViewModel m) =>
        RunAsync($"{m.DisplayName} is now an admin", () => _gateway.PromoteAdminAsync(Admin.HostBase, m.Address));

    private Task DemoteAsync(ManageMemberViewModel m) =>
        RunAsync($"{m.DisplayName} is no longer an admin", () => _gateway.DemoteAdminAsync(Admin.HostBase, m.Address));

    private Task RemoveAsync(ManageMemberViewModel m) =>
        RunAsync($"removed {m.DisplayName}", () => _gateway.RemoveMemberAsync(Admin.HostBase, Admin.RoomName, m.Address));

    private void QueueSettingsSave()
    {
        if (_loading) return;
        _settingsDirty = true;
        _saveDebounce?.Cancel();
        _saveDebounce?.Dispose();
        var cts = new CancellationTokenSource();
        _saveDebounce = cts;
        _ = DebounceThenSaveAsync(cts.Token);
    }

    private async Task DebounceThenSaveAsync(CancellationToken ct)
    {
        try { await Task.Delay(SaveDebounce, ct).ConfigureAwait(true); }
        catch (OperationCanceledException) { return; } // superseded by a newer edit / a flush
        await SaveSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>Commit a pending debounced edit immediately (on Done / close) so an
    /// in-flight keystroke isn't dropped.</summary>
    internal async Task FlushPendingSaveAsync()
    {
        _saveDebounce?.Cancel();
        if (_settingsDirty) await SaveSettingsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        var display = DisplayName.Trim();
        var topic = Topic.Trim();
        var ok = await RunAsync("settings saved",
            () => _gateway.UpdateRoomAsync(Admin.HostBase, Admin.RoomName, display, topic, RetentionMax),
            reload: false).ConfigureAwait(true);
        if (!ok) return; // stays dirty — a flush (Done) or the next edit retries
        _settingsDirty = false;
        _room.SetDisplayName(display); // reflect on the room card at once
    }

    [RelayCommand]
    private Task ClearHistoryAsync() =>
        RunAsync("history cleared", () => _gateway.ClearHistoryAsync(Admin.HostBase, Admin.RoomName), reload: false);

    [RelayCommand]
    private async Task CloseRoomAsync()
    {
        _dismiss();
        await _closeRoom(_room).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DoneAsync()
    {
        await FlushPendingSaveAsync().ConfigureAwait(true);
        _dismiss();
    }

    // Run a gateway admin call with busy/status handling, then reload the detail
    // (so members, admin badges and kind reflect the change) unless told not to.
    // Returns whether the op succeeded.
    private async Task<bool> RunAsync(string okMsg, Func<Task<AdminResult>> op, bool reload = true)
    {
        if (IsBusy) return false;
        IsBusy = true;
        Status = null;
        try
        {
            var res = await op().ConfigureAwait(true);
            if (!res.Ok)
            {
                Status = res.Error ?? "the host refused";
                return false;
            }
            Status = okMsg;
            if (reload) await LoadAsync().ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>One visibility choice in the access picker.</summary>
public sealed record KindOption(RoomKind Kind, string Label, string Hint);

/// <summary>
/// One member row in the management roster: address, admin/online state, and the
/// owner controls (promote / demote / remove). You can never act on yourself —
/// you're the owner.
/// </summary>
public sealed partial class ManageMemberViewModel : ObservableObject
{
    private readonly Func<ManageMemberViewModel, Task> _promote;
    private readonly Func<ManageMemberViewModel, Task> _demote;
    private readonly Func<ManageMemberViewModel, Task> _remove;

    public ManageMemberViewModel(
        RoomMemberInfo info, bool isSelf,
        Func<ManageMemberViewModel, Task> promote,
        Func<ManageMemberViewModel, Task> demote,
        Func<ManageMemberViewModel, Task> remove)
    {
        Address = info.Address;
        DisplayName = Addr.Short(info.Address);
        NameColorHex = isSelf ? "#BF94FF" : Addr.NameColor(info.Address);
        IsAdmin = info.IsAdmin;
        IsOnline = info.Online;
        IsSelf = isSelf;
        _promote = promote;
        _demote = demote;
        _remove = remove;
    }

    public string Address { get; }
    public string DisplayName { get; }
    public string NameColorHex { get; }
    public bool IsAdmin { get; }
    public bool IsOnline { get; }
    public bool IsSelf { get; }

    public bool CanPromote => !IsSelf && !IsAdmin;
    public bool CanDemote => !IsSelf && IsAdmin;
    public bool CanRemove => !IsSelf;

    [RelayCommand] private Task Promote() => _promote(this);
    [RelayCommand] private Task Demote() => _demote(this);
    [RelayCommand] private Task Remove() => _remove(this);
}
