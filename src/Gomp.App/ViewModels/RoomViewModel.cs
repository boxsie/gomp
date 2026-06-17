using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gomp.App.Services;
using Gomp.Client;
using Gomp.Protocol;

namespace Gomp.App.ViewModels;

/// <summary>
/// One open room from the member's side: the chat log, the member list, the
/// composer, and (if you own it) the remove control. Implements
/// <see cref="IRoomObserver"/> directly — the gomp client core pushes
/// posts/presence/roster/errors in here off the daemon stream, and every callback
/// hops onto the UI thread before touching a bound collection.
/// </summary>
public sealed partial class RoomViewModel : ObservableObject, IRoomObserver
{
    private readonly IUiDispatcher _ui;
    private readonly string _self;
    private readonly Func<RoomViewModel, MemberViewModel, Task>? _remove;
    private readonly Func<RoomViewModel, Task>? _leave;
    private readonly Func<RoomViewModel, Task>? _manage;
    private IRoomHandle? _handle;
    private bool _rosterPrimed;

    public RoomViewModel(
        string address,
        RoomKind kind,
        string selfAddress,
        IUiDispatcher ui,
        AdminContext? admin = null,
        Func<RoomViewModel, MemberViewModel, Task>? remove = null,
        Func<RoomViewModel, Task>? leave = null,
        string? title = null,
        Func<RoomViewModel, Task>? manage = null)
    {
        Address = address;
        _kind = kind;
        _self = selfAddress;
        _ui = ui;
        Admin = admin;
        _remove = remove;
        _leave = leave;
        _manage = manage;
        _title = string.IsNullOrWhiteSpace(title) ? Addr.Short(address) : title;
    }

    public string Address { get; }
    public AdminContext? Admin { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Initial))]
    private string _title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KindLabel))]
    private RoomKind _kind;

    public bool IsOwner => Admin is not null;

    public string Initial => string.IsNullOrWhiteSpace(Title)
        ? "#"
        : Title.TrimStart()[..1].ToUpperInvariant();

    public string KindLabel => Kind switch
    {
        RoomKind.Open => "open",
        RoomKind.Friends => "friends",
        RoomKind.Invite => "invite only",
        _ => "room",
    };

    /// <summary>Reflect a host-side visibility change (from the management page).</summary>
    internal void ApplyKind(RoomKind kind) => Kind = kind;

    /// <summary>Reflect a host-side display-name change; empty falls back to the short address.</summary>
    internal void SetDisplayName(string displayName) =>
        Title = string.IsNullOrWhiteSpace(displayName) ? Addr.Short(Address) : displayName;

    public ObservableCollection<MessageViewModel> Messages { get; } = new();
    public ObservableCollection<MemberViewModel> Members { get; } = new();

    [ObservableProperty]
    private string _draft = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOnline))]
    private int _onlineCount;

    public bool HasOnline => OnlineCount > 0;

    /// <summary>True once any signal has arrived — the room is live.</summary>
    [ObservableProperty]
    private bool _isLit;

    /// <summary>Set when a forgery is seen, so the UI can raise a persistent flag.</summary>
    [ObservableProperty]
    private bool _sawForgery;

    public void Attach(IRoomHandle handle) => _handle = handle;

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = Draft.Trim();
        if (string.IsNullOrEmpty(text) || _handle is null)
            return;

        Draft = "";
        try
        {
            await _handle.SendChatAsync(text).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AddSystem($"couldn't send: {ex.Message}");
        }
    }

    [RelayCommand]
    private Task LeaveAsync() => _leave is null ? Task.CompletedTask : _leave(this);

    /// <summary>Open the management page for this room (owner only).</summary>
    [RelayCommand]
    private Task ManageAsync() => _manage is null ? Task.CompletedTask : _manage(this);

    /// <summary>Relay a member-initiated remove up to the shell (owner only).</summary>
    internal Task RemoveMember(MemberViewModel member) =>
        _remove is null ? Task.CompletedTask : _remove(this, member);

    /// <summary>Add a centred system line (used for remove notices and errors).</summary>
    public void AddSystem(string text) => Messages.Add(MessageViewModel.System(text));

    // ---- IRoomObserver (off-thread → marshalled) ----

    Task IRoomObserver.OnPostAsync(ReceivedPost post) =>
        _ui.InvokeAsync(() => ApplyPost(post));

    Task IRoomObserver.OnPresenceAsync(RoomMemberPresence presence) =>
        _ui.InvokeAsync(() => ApplyPresence(presence));

    Task IRoomObserver.OnRosterAsync(IReadOnlyList<RoomMemberPresence> members) =>
        _ui.InvokeAsync(() => ApplyRoster(members));

    Task IRoomObserver.OnErrorAsync(string code, string message) =>
        _ui.InvokeAsync(() => { Lit(); AddSystem($"⚠ {code}{(string.IsNullOrEmpty(message) ? "" : ": " + message)}"); });

    // ---- mutation (UI thread) ----

    private void ApplyPost(ReceivedPost post)
    {
        Lit();
        var vm = MessageViewModel.FromPost(post, _self);
        if (vm.IsForged)
            SawForgery = true;

        // Keep ordered by host sequence: backfill can interleave with a live
        // message, and a re-delivery of the same seq is a no-op.
        var i = Messages.Count - 1;
        while (i >= 0 && !Messages[i].IsSystem && Messages[i].Seq > vm.Seq)
            i--;
        if (i >= 0 && !Messages[i].IsSystem && Messages[i].Seq == vm.Seq)
            return;
        Messages.Insert(i + 1, vm);
    }

    private void ApplyRoster(IReadOnlyList<RoomMemberPresence> members)
    {
        Lit();
        Members.Clear();
        foreach (var m in members)
            Members.Add(NewMember(m.Addr, m.Online));
        RecountOnline();
    }

    private void ApplyPresence(RoomMemberPresence presence)
    {
        Lit();
        var existing = Members.FirstOrDefault(m => m.Address == presence.Addr);
        if (existing is null)
            Members.Add(NewMember(presence.Addr, presence.Online));
        else
            existing.Online = presence.Online;
        RecountOnline();
    }

    private MemberViewModel NewMember(string addr, bool online)
    {
        var canRemove = Admin?.CanRemove ?? false;
        return new MemberViewModel(
            addr, online,
            isSelf: addr == _self,
            canRemove: canRemove,
            remove: canRemove ? RemoveMember : null);
    }

    private void RecountOnline() => OnlineCount = Members.Count(m => m.Online);

    private void Lit()
    {
        IsLit = true;
        // The core requests history but not the roster; prime it on the first
        // signal (the link is demonstrably up) so existing members appear.
        if (_rosterPrimed || _handle is null)
            return;
        _rosterPrimed = true;
        _ = _handle.RequestRosterAsync();
    }
}
