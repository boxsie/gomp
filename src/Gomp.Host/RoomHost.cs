using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Ensemble.Client;
using Gomp.Protocol;

namespace Gomp.Host;

/// <summary>
/// A multi-room host (ADR-0007): one installed, supervised service that registers
/// a base identity plus one sub-identity per room. Admin operations (create/close
/// a room, add/remove a member, promote/demote an admin) arrive in-band over the
/// base identity and are authorized against the owner + designated admins. Each
/// room is its own dialable address with its own ACL gate, roster, fan-out and
/// history.
/// </summary>
internal sealed class RoomHost : IAsyncDisposable
{
    private const long BaseMaxPayload = 16 * 1024;     // admin requests are small
    private const long RoomMaxPayload = 128 * 1024;    // chat posts

    private readonly EnsembleClient _client;
    private readonly string _hostName;
    private readonly string _dataDir;
    private readonly int _historyMax;
    private readonly AdminState _admin;
    private readonly RoomCatalog _catalog;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RoomHost> _log;

    private readonly SemaphoreSlim _roomsLock = new(1, 1);
    private readonly Dictionary<string, RoomHandle> _rooms = new(StringComparer.Ordinal);

    private RegisteredService? _baseSvc;

    private sealed record RoomHandle(Room Room, RegisteredService Service);

    public RoomHost(
        EnsembleClient client,
        string hostName,
        string dataDir,
        string owner,
        int historyMax,
        ILoggerFactory loggerFactory)
    {
        _client = client;
        _hostName = hostName;
        _dataDir = dataDir;
        _historyMax = historyMax;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<RoomHost>();
        _admin = AdminState.Load(dataDir, owner);
        _catalog = RoomCatalog.Load(dataDir);
    }

    /// <summary>The base host E-address admins dial to drive admin operations.</summary>
    public string? BaseAddress => _baseSvc?.ServiceAddress;

    /// <summary>
    /// Register the base identity (ALLOWLIST-gated to the owner + admins) and
    /// re-register every room from the persisted catalog so a restart restores
    /// the same rooms at the same addresses.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        var manifest = ServiceManifest.NewBuilder(_hostName)
            .Description("Ensemble room host (ADR-0007)")
            .Transport(ServiceTransport.Rpc)
            .Acl(ServiceAcl.Allowlist, _admin.AuthorizedAddresses().ToArray())
            .MaxPayloadBytes(BaseMaxPayload)
            .Build();

        _baseSvc = await _client.RegisterServiceAsync(
            manifest,
            HandleBaseEventAsync,
            OnBaseErrorAsync,
            ct).ConfigureAwait(false);

        _log.LogInformation("room host {Name} base identity {Addr} registered; owner {Owner}",
            _hostName, _baseSvc.ServiceAddress, _admin.Owner);

        // Headless room definition: reconcile the catalog from the operator's
        // data/rooms.yaml BEFORE (re)registering, so config-defined rooms flow
        // through the same registration pass below. Ensure-exists — a bad config
        // must never strand rooms that already exist in the catalog.
        try
        {
            var result = RoomConfig.Reconcile(RoomConfig.Load(_dataDir), _catalog);
            foreach (var name in result.Created)
                _log.LogInformation("rooms-from-config: created room {Room}", name);
            foreach (var m in result.MembersAdded)
                _log.LogInformation("rooms-from-config: added member {Member}", m);
            foreach (var s in result.Skipped)
                _log.LogWarning("rooms-from-config: skipped {Detail}", s);
        }
        catch (RoomConfigException ex)
        {
            _log.LogError("rooms-from-config: {Message}; existing rooms unaffected", ex.Message);
        }

        foreach (var rec in _catalog.All())
        {
            try
            {
                await RegisterRoomAsync(rec, ct).ConfigureAwait(false);
                _log.LogInformation("restored room {Room}", rec.Name);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "failed to restore room {Room}", rec.Name);
            }
        }
    }

    // ---- base identity: admin operations ----

    private async ValueTask HandleBaseEventAsync(ServiceEvent ev)
    {
        if (ev is not ServiceEvent.RpcMessage msg)
            return; // connection_established/closed on the base identity need no action

        AdminEnvelope env;
        try
        {
            env = AdminEnvelope.Parser.ParseFrom(msg.Payload);
        }
        catch (InvalidProtocolBufferException)
        {
            return;
        }
        if (env.BodyCase != AdminEnvelope.BodyOneofCase.Request)
            return;

        var req = env.Request;
        AdminResponse resp;
        // Authority is the daemon-verified from_addr against {owner} ∪ admins —
        // the base identity's ALLOWLIST gate already blocks everyone else, this is
        // defense in depth (ADR-0007 §4).
        if (!_admin.IsAuthorized(msg.FromAddr))
        {
            resp = Fail(req.RequestId, "unauthorized");
            _log.LogWarning("rejected admin op {Op} from non-admin {Addr}", req.OpCase, msg.FromAddr);
        }
        else
        {
            try
            {
                resp = await DispatchAdminAsync(req).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "admin op {Op} failed", req.OpCase);
                resp = Fail(req.RequestId, "internal_error");
            }
        }

        await SendBaseAsync(msg.FromAddr, new AdminEnvelope { Response = resp }).ConfigureAwait(false);
    }

    private async Task<AdminResponse> DispatchAdminAsync(AdminRequest req)
    {
        switch (req.OpCase)
        {
            case AdminRequest.OpOneofCase.CreateRoom:
                return await CreateRoomAsync(req.RequestId, req.CreateRoom).ConfigureAwait(false);
            case AdminRequest.OpOneofCase.CloseRoom:
                return await CloseRoomAsync(req.RequestId, req.CloseRoom.Name).ConfigureAwait(false);
            case AdminRequest.OpOneofCase.ListRooms:
                return ListRooms(req.RequestId);
            case AdminRequest.OpOneofCase.AddMember:
                return await AddMemberAsync(req.RequestId, req.AddMember.Room, req.AddMember.Addr).ConfigureAwait(false);
            case AdminRequest.OpOneofCase.RemoveMember:
                return await RemoveMemberAsync(req.RequestId, req.RemoveMember.Room, req.RemoveMember.Addr).ConfigureAwait(false);
            case AdminRequest.OpOneofCase.PromoteAdmin:
                return await PromoteAdminAsync(req.RequestId, req.PromoteAdmin.Addr).ConfigureAwait(false);
            case AdminRequest.OpOneofCase.DemoteAdmin:
                return await DemoteAdminAsync(req.RequestId, req.DemoteAdmin.Addr).ConfigureAwait(false);
            default:
                return Fail(req.RequestId, "unknown_op");
        }
    }

    private async Task<AdminResponse> CreateRoomAsync(string reqId, CreateRoom op)
    {
        if (op.Kind == RoomKind.Unspecified)
            return Fail(reqId, "kind_required");

        await _roomsLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_catalog.Contains(op.Name) || _rooms.ContainsKey(op.Name))
                return Fail(reqId, "room_exists");

            var rec = new RoomRecord(op.Name, op.Kind, op.Members.ToList());
            RoomHandle handle;
            try
            {
                handle = await RegisterRoomAsync(rec, CancellationToken.None).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                // Invalid room name (slug rules) — surface as a clean failure.
                return Fail(reqId, $"invalid_name: {ex.Message}");
            }
            _catalog.Put(rec);

            _log.LogInformation("created room {Room} ({Kind}) {Addr}", op.Name, op.Kind, handle.Room.Address);
            return new AdminResponse { RequestId = reqId, Ok = true, Rooms = { RoomInfoFor(rec, handle.Room) } };
        }
        finally
        {
            _roomsLock.Release();
        }
    }

    private async Task<AdminResponse> CloseRoomAsync(string reqId, string name)
    {
        await _roomsLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_rooms.Remove(name, out var handle))
                await handle.Service.DisposeAsync().ConfigureAwait(false);
            else if (!_catalog.Contains(name))
                return Fail(reqId, "no_such_room");

            _catalog.Remove(name);
            _log.LogInformation("closed room {Room}", name);
            return Ok(reqId);
        }
        finally
        {
            _roomsLock.Release();
        }
    }

    private AdminResponse ListRooms(string reqId)
    {
        var resp = new AdminResponse { RequestId = reqId, Ok = true };
        foreach (var rec in _catalog.All())
        {
            _rooms.TryGetValue(rec.Name, out var handle);
            resp.Rooms.Add(RoomInfoFor(rec, handle?.Room));
        }
        return resp;
    }

    private async Task<AdminResponse> AddMemberAsync(string reqId, string roomName, string addr)
    {
        if (!_catalog.TryGet(roomName, out var rec)) return Fail(reqId, "no_such_room");
        if (rec.Kind != RoomKind.Invite) return Fail(reqId, "not_invite_room");
        if (!_rooms.TryGetValue(roomName, out var handle)) return Fail(reqId, "room_not_live");

        await handle.Room.AddMemberAsync(addr).ConfigureAwait(false);
        _catalog.AddMember(roomName, addr);
        return Ok(reqId);
    }

    private async Task<AdminResponse> RemoveMemberAsync(string reqId, string roomName, string addr)
    {
        if (!_catalog.TryGet(roomName, out var rec)) return Fail(reqId, "no_such_room");
        if (rec.Kind != RoomKind.Invite) return Fail(reqId, "not_invite_room");
        if (!_rooms.TryGetValue(roomName, out var handle)) return Fail(reqId, "room_not_live");

        await handle.Room.RemoveMemberAsync(addr).ConfigureAwait(false);
        _catalog.RemoveMember(roomName, addr);
        return Ok(reqId);
    }

    private async Task<AdminResponse> PromoteAdminAsync(string reqId, string addr)
    {
        if (!_admin.Promote(addr)) return Fail(reqId, "already_admin");
        // The new admin must be able to reach the base identity.
        if (_baseSvc is not null)
            await _baseSvc.UpdateAllowlistAsync(add: new[] { addr }).ConfigureAwait(false);
        _log.LogInformation("promoted admin {Addr}", addr);
        return Ok(reqId);
    }

    private async Task<AdminResponse> DemoteAdminAsync(string reqId, string addr)
    {
        if (!_admin.Demote(addr)) return Fail(reqId, "not_admin");
        if (_baseSvc is not null)
        {
            await _baseSvc.UpdateAllowlistAsync(remove: new[] { addr }).ConfigureAwait(false);
            await _baseSvc.DisconnectPeerAsync(addr).ConfigureAwait(false);
        }
        _log.LogInformation("demoted admin {Addr}", addr);
        return Ok(reqId);
    }

    // ---- test seam (integration e2e only; see InternalsVisibleTo) ----

    /// <summary>
    /// Integration-test seam: the live <see cref="RegisteredService"/> backing the
    /// sub-identity for room <paramref name="name"/>, plus its address. An honest
    /// submit is sender-checked (<c>sender_mismatch</c>), so the only way a Forged
    /// post reaches a member is a MALICIOUS host fabricating a fan-out under a
    /// member's name — this hands the e2e exactly that capability. Returns null if
    /// no such room is live.
    /// </summary>
    internal async Task<(RegisteredService Service, string Address)?> DebugRoomServiceAsync(string name)
    {
        await _roomsLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return _rooms.TryGetValue(name, out var h) ? (h.Service, h.Room.Address) : null;
        }
        finally
        {
            _roomsLock.Release();
        }
    }

    // ---- room registration ----

    private async Task<RoomHandle> RegisterRoomAsync(RoomRecord rec, CancellationToken ct)
    {
        var builder = ServiceManifest.NewSubIdentityBuilder(_hostName, rec.Name)
            .Description($"Ensemble room {rec.Name}")
            .Transport(ServiceTransport.Rpc)
            .MaxPayloadBytes(RoomMaxPayload);

        switch (rec.Kind)
        {
            case RoomKind.Open:
                builder.Acl(ServiceAcl.Public);
                break;
            case RoomKind.Friends:
                builder.Acl(ServiceAcl.Contacts);
                break;
            case RoomKind.Invite:
                // Seed the owner so the allowlist is never empty (the SDK rejects
                // an empty allowlist) and the owner can always enter their room.
                var allow = rec.Members.Append(_admin.Owner).Distinct().ToArray();
                builder.Acl(ServiceAcl.Allowlist, allow);
                break;
            default:
                throw new ArgumentException($"unknown room kind {rec.Kind}");
        }

        var manifest = builder.Build();

        // The room object needs the RegisteredService (for its transport), but the
        // event stream can deliver before we return from registration — a
        // reconnecting member re-dialing a restored room. Gate the handler on a
        // promise that completes the instant the Room is wired, so no event is
        // dropped and ordering is preserved.
        var ready = new TaskCompletionSource<Room>(TaskCreationOptions.RunContinuationsAsynchronously);
        var roomLog = _loggerFactory.CreateLogger($"room:{rec.Name}");

        var svc = await _client.RegisterServiceAsync(
            manifest,
            async ev =>
            {
                var room = await ready.Task.ConfigureAwait(false);
                await DispatchRoomEventAsync(room, ev).ConfigureAwait(false);
            },
            err =>
            {
                roomLog.LogDebug("room {Room} service error: {Code} {Msg}", rec.Name, err.Code, err.Message);
                return ValueTask.CompletedTask;
            },
            ct).ConfigureAwait(false);

        var store = RoomStore.Open(_dataDir, rec.Name, _historyMax);
        var room = new Room(rec.Name, rec.Kind, new RegisteredServiceTransport(svc), store, roomLog);
        ready.SetResult(room);

        var handle = new RoomHandle(room, svc);
        _rooms[rec.Name] = handle;
        return handle;
    }

    private static Task DispatchRoomEventAsync(Room room, ServiceEvent ev) => ev switch
    {
        ServiceEvent.ConnectionEstablished e => room.OnConnectionEstablishedAsync(e.FromAddr),
        ServiceEvent.ConnectionClosed e => room.OnConnectionClosedAsync(e.FromAddr),
        ServiceEvent.RpcMessage m => room.OnRpcMessageAsync(m.FromAddr, m.Payload),
        _ => Task.CompletedTask,
    };

    // ---- helpers ----

    private RoomInfo RoomInfoFor(RoomRecord rec, Room? room) => new()
    {
        Name = rec.Name,
        Addr = room?.Address ?? string.Empty,
        Kind = rec.Kind,
        Members = rec.Members.Count,
        Online = room?.OnlineCount ?? 0,
    };

    private ValueTask OnBaseErrorAsync(ServiceError err)
    {
        _log.LogDebug("base identity service error: {Code} {Msg}", err.Code, err.Message);
        return ValueTask.CompletedTask;
    }

    private Task SendBaseAsync(string to, AdminEnvelope env)
        => _baseSvc is null ? Task.CompletedTask : _baseSvc.SendBytesAsync(to, env.ToByteArray());

    private static AdminResponse Ok(string reqId) => new() { RequestId = reqId, Ok = true };
    private static AdminResponse Fail(string reqId, string error) => new() { RequestId = reqId, Ok = false, Error = error };

    public async ValueTask DisposeAsync()
    {
        await _roomsLock.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var handle in _rooms.Values)
                await handle.Service.DisposeAsync().ConfigureAwait(false);
            _rooms.Clear();
        }
        finally
        {
            _roomsLock.Release();
        }
        if (_baseSvc is not null)
            await _baseSvc.DisposeAsync().ConfigureAwait(false);
        _roomsLock.Dispose();
    }
}
