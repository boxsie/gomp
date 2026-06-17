using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gomp.App.Services;
using Gomp.Protocol;

namespace Gomp.App.ViewModels;

/// <summary>
/// The shell view-model: owns the connection to the daemon, the list of rooms the
/// member has joined, and the lightweight overlays for joining a room by address
/// or creating a new one. All network work goes through <see cref="IGompGateway"/>,
/// so the whole thing drives off a fake in tests.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IGompGatewayFactory _factory;
    private readonly IUiDispatcher _ui;
    private IGompGateway? _gateway;

    public MainWindowViewModel(IGompGatewayFactory factory, IUiDispatcher ui)
    {
        _factory = factory;
        _ui = ui;
    }

    public ObservableCollection<RoomViewModel> Rooms { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRooms))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private RoomViewModel? _selectedRoom;

    public bool HasRooms => Rooms.Count > 0;
    public bool HasSelection => SelectedRoom is not null;

    // ---- connection ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShortSelf))]
    private string? _selfAddress;

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string? _connectError;

    public string ShortSelf => Addr.Short(SelfAddress);

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsConnecting || IsConnected)
            return;

        IsConnecting = true;
        ConnectError = null;
        try
        {
            _gateway = await _factory.ConnectAsync().ConfigureAwait(true);
            SelfAddress = _gateway.SelfAddress;
            IsConnected = true;
            await LoadHostedRoomsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ConnectError = ex.Message;
        }
        finally
        {
            IsConnecting = false;
        }
    }

    // ---- join / create overlays ----

    [ObservableProperty]
    private bool _isJoinOpen;

    [ObservableProperty]
    private bool _isCreateOpen;

    [ObservableProperty]
    private string _joinAddress = "";

    [ObservableProperty]
    private string _createName = "";

    [ObservableProperty]
    private RoomKind _createKind = RoomKind.Open;

    [ObservableProperty]
    private bool _isWorking;

    [ObservableProperty]
    private string? _dialogError;

    // ---- leave-vs-close confirm (owner only) ----

    [ObservableProperty]
    private bool _isLeaveConfirmOpen;

    [ObservableProperty]
    private string? _leaveRoomTitle;

    private RoomViewModel? _leaveTarget;

    public IReadOnlyList<RoomKind> RoomKinds { get; } =
        new[] { RoomKind.Open, RoomKind.Friends, RoomKind.Invite };

    [RelayCommand]
    private void ShowJoin()
    {
        DialogError = null;
        JoinAddress = "";
        IsCreateOpen = false;
        IsJoinOpen = true;
    }

    [RelayCommand]
    private void ShowCreate()
    {
        DialogError = null;
        CreateName = "";
        CreateKind = RoomKind.Open;
        IsJoinOpen = false;
        IsCreateOpen = true;
    }

    [RelayCommand]
    private void CancelDialog()
    {
        IsJoinOpen = false;
        IsCreateOpen = false;
        DialogError = null;
    }

    [RelayCommand]
    private async Task ConfirmJoinAsync()
    {
        var addr = JoinAddress.Trim();
        if (string.IsNullOrEmpty(addr))
        {
            DialogError = "paste a room address";
            return;
        }

        IsWorking = true;
        DialogError = null;
        try
        {
            await JoinAsync(addr, RoomKind.Unspecified).ConfigureAwait(true);
            IsJoinOpen = false;
        }
        catch (Exception ex)
        {
            DialogError = ex.Message;
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private async Task ConfirmCreateAsync()
    {
        var name = CreateName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            DialogError = "a room name, please";
            return;
        }
        if (_gateway is null)
            return;

        // The room is always created on THIS app's own backend host — the owner
        // and host identity were settled on the Ensemble side (ROOM_HOST_OWNER).
        // There is no separate "host address" to ask for: it is our own self
        // address from the daemon's Welcome, which the backend routes locally.
        var host = SelfAddress;
        if (string.IsNullOrEmpty(host))
        {
            DialogError = "not connected to a host yet";
            return;
        }

        IsWorking = true;
        DialogError = null;
        try
        {
            var res = await _gateway.CreateRoomAsync(host, name, CreateKind).ConfigureAwait(true);
            if (!res.Ok)
            {
                DialogError = res.Error ?? "the host refused";
                return;
            }

            var room = res.Rooms.FirstOrDefault(r => !string.IsNullOrEmpty(r.Address));
            if (room is null)
            {
                DialogError = "created, but the host returned no address";
                return;
            }

            var admin = new AdminContext(host, room.Name, CanRemove: room.Kind == RoomKind.Invite);
            await JoinAsync(room.Address, room.Kind, admin, room.Name).ConfigureAwait(true);
            IsCreateOpen = false;
        }
        catch (Exception ex)
        {
            DialogError = ex.Message;
        }
        finally
        {
            IsWorking = false;
        }
    }

    // ---- room membership ----

    // After connecting, re-surface the rooms this node HOSTS (the backend
    // re-registers them from its catalog on startup) so they're manageable —
    // re-enter, or leave→close — instead of being orphaned and invisible.
    // Re-attached with an AdminContext so the owner can close them.
    private async Task LoadHostedRoomsAsync()
    {
        if (_gateway is null || string.IsNullOrEmpty(SelfAddress))
            return;

        AdminResult res;
        try
        {
            res = await _gateway.ListRoomsAsync(SelfAddress).ConfigureAwait(true);
        }
        catch
        {
            return; // best-effort: a listing failure must not break connect
        }
        if (!res.Ok)
            return;

        foreach (var room in res.Rooms)
        {
            if (string.IsNullOrEmpty(room.Address) || Rooms.Any(r => r.Address == room.Address))
                continue;
            var admin = new AdminContext(SelfAddress, room.Name, CanRemove: room.Kind == RoomKind.Invite);
            await JoinAsync(room.Address, room.Kind, admin, room.Name).ConfigureAwait(true);
        }
    }

    private async Task JoinAsync(string address, RoomKind kind, AdminContext? admin = null, string? title = null)
    {
        if (_gateway is null)
            return;

        var existing = Rooms.FirstOrDefault(r => r.Address == address);
        if (existing is not null)
        {
            SelectedRoom = existing;
            return;
        }

        var room = new RoomViewModel(address, kind, _gateway.SelfAddress, _ui, admin, RemoveMemberAsync, LeaveRoomAsync, title);
        var handle = await _gateway.JoinAsync(address, room).ConfigureAwait(true);
        room.Attach(handle);
        Rooms.Add(room);
        OnPropertyChanged(nameof(HasRooms));
        SelectedRoom = room;
    }

    private Task LeaveRoomAsync(RoomViewModel room)
    {
        // Leaving a room you HOST is ambiguous: detach (keep hosting it
        // headless) vs close (tear the sub-identity down and free its onion,
        // DHT announce, listener and on-disk history). Prompt so a self-host
        // owner never silently leaks a room. A non-owned room just detaches.
        if (room.IsOwner)
        {
            _leaveTarget = room;
            LeaveRoomTitle = room.Title;
            IsLeaveConfirmOpen = true;
            return Task.CompletedTask;
        }

        return DetachRoomAsync(room);
    }

    // Drop the local member session and remove the card; the room keeps
    // running on the host. This is the only thing "leave" ever did.
    private async Task DetachRoomAsync(RoomViewModel room)
    {
        if (_gateway is not null)
            await _gateway.LeaveAsync(room.Address).ConfigureAwait(true);
        RemoveRoomCard(room);
    }

    private void RemoveRoomCard(RoomViewModel room)
    {
        var wasSelected = ReferenceEquals(SelectedRoom, room);
        Rooms.Remove(room);
        OnPropertyChanged(nameof(HasRooms));
        if (wasSelected)
            SelectedRoom = Rooms.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ConfirmLeaveKeepHosting()
    {
        var room = _leaveTarget;
        CloseLeaveConfirm();
        if (room is not null)
            await DetachRoomAsync(room).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ConfirmCloseRoom()
    {
        var room = _leaveTarget;
        CloseLeaveConfirm();
        if (room is null || _gateway is null || room.Admin is null)
            return;

        // CloseRoom is the admin op that actually tears the room down on the
        // host: disposes the sub-identity service (onion/listener/DHT/allowlist)
        // and removes it from the catalog so it doesn't come back on restart.
        var res = await _gateway.CloseRoomAsync(room.Admin.HostBase, room.Admin.RoomName)
            .ConfigureAwait(true);
        if (!res.Ok)
        {
            room.AddSystem($"couldn't close the room: {res.Error}");
            return;
        }
        RemoveRoomCard(room);
    }

    [RelayCommand]
    private void CancelLeave() => CloseLeaveConfirm();

    private void CloseLeaveConfirm()
    {
        IsLeaveConfirmOpen = false;
        LeaveRoomTitle = null;
        _leaveTarget = null;
    }

    private async Task RemoveMemberAsync(RoomViewModel room, MemberViewModel member)
    {
        if (_gateway is null || room.Admin is null)
            return;

        var res = await _gateway.RemoveMemberAsync(room.Admin.HostBase, room.Admin.RoomName, member.Address)
            .ConfigureAwait(true);
        room.AddSystem(res.Ok
            ? $"removed {member.DisplayName} from the room"
            : $"couldn't remove {member.DisplayName}: {res.Error}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_gateway is not null)
            await _gateway.DisposeAsync().ConfigureAwait(false);
    }
}
