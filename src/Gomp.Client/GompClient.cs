using System.Collections.Concurrent;
using Google.Protobuf;
using Ensemble.Client;
using Gomp.Protocol;

namespace Gomp.Client;

/// <summary>
/// The gomp client core: one member's view of the room network. Owns the single
/// rooms-client service identity, the connection lifecycle, and the inbound
/// event router that fans daemon events to the right <see cref="RoomSession"/>
/// (by <c>from_addr</c>) or completes a pending admin request.
///
/// Two channels, both over the one identity:
/// <list type="bullet">
///   <item><b>Chat</b> — dial a room sub-identity and speak <see cref="RoomEnvelope"/>
///   (<see cref="JoinRoomAsync"/>).</item>
///   <item><b>Admin</b> — dial a host base identity and speak <see cref="AdminEnvelope"/>
///   request/response, correlated by <c>request_id</c>
///   (<see cref="CreateRoomAsync"/> et al.).</item>
/// </list>
///
/// Construct the live client with <see cref="EnsembleClientTransport.CreateAsync"/>
/// + <c>new GompClient(transport, ownsTransport: true)</c>; tests inject a fake
/// <see cref="IRoomClientTransport"/>.
/// </summary>
public sealed class GompClient : IAsyncDisposable
{
    private readonly IRoomClientTransport _tx;
    private readonly bool _ownsTransport;

    private readonly ConcurrentDictionary<string, RoomSession> _rooms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AdminResponse>> _pendingAdmin = new();

    // Peers whose link is currently up, and one-shot waiters for a connect that
    // hasn't established yet (admin needs an established link before it sends).
    private readonly ConcurrentDictionary<string, byte> _established = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _connectWaiters = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _bindingLock = new(1, 1);
    private IdentityBinding? _ownBinding;

    /// <summary>
    /// Connect a live client: register the member's rooms-client service on the
    /// daemon and wire up routing. The manifest should be RPC-transport (room-ops
    /// are raw protobuf). The returned client owns the transport and deregisters
    /// the service on <see cref="DisposeAsync"/>.
    /// </summary>
    public static async Task<GompClient> ConnectAsync(
        EnsembleClient client, ServiceManifest manifest, CancellationToken ct = default)
    {
        var transport = await EnsembleClientTransport.CreateAsync(client, manifest, ct).ConfigureAwait(false);
        return new GompClient(transport, ownsTransport: true);
    }

    internal GompClient(IRoomClientTransport transport, bool ownsTransport = false)
    {
        _tx = transport;
        _ownsTransport = ownsTransport;
        _tx.PeerConnected += OnPeerConnectedAsync;
        _tx.PeerDisconnected += OnPeerDisconnectedAsync;
        _tx.MessageReceived += OnMessageReceivedAsync;
    }

    /// <summary>The member's own E-address.</summary>
    public string Address => _tx.Address;

    // ---- own identity binding (signed once, reused across rooms) ----

    /// <summary>
    /// This member's rotatable identity binding — the token it hands every host
    /// at join. Materialised once (any signed document carries it) and cached;
    /// concurrent first-callers share the single sign.
    /// </summary>
    internal async Task<IdentityBinding> OwnBindingAsync(CancellationToken ct = default)
    {
        if (_ownBinding is not null)
            return _ownBinding;

        await _bindingLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ownBinding is null)
            {
                var doc = await _tx.SignAsync(Array.Empty<byte>(), ct).ConfigureAwait(false);
                _ownBinding = new IdentityBinding
                {
                    Binding = ByteString.CopyFrom(doc.Binding),
                    DilithiumPub = ByteString.CopyFrom(doc.DilithiumPub),
                };
            }
        }
        finally
        {
            _bindingLock.Release();
        }
        return _ownBinding;
    }

    // ---- join / leave ----

    /// <summary>
    /// Join a room: register a <see cref="RoomSession"/> and dial the room. The
    /// dial is fire-and-forget — Hello + history fire when the link establishes
    /// (and re-fire on reconnect), and the observer sees the roster/posts as they
    /// arrive. Returns the session immediately so the UI can show "connecting".
    /// Throws if already joined to <paramref name="roomAddress"/>.
    /// </summary>
    public async Task<RoomSession> JoinRoomAsync(
        string roomAddress, IRoomObserver observer, bool requestHistory = true, CancellationToken ct = default)
    {
        var binding = await OwnBindingAsync(ct).ConfigureAwait(false);
        var session = new RoomSession(roomAddress, _tx, binding, observer, requestHistory);
        if (!_rooms.TryAdd(roomAddress, session))
            throw new InvalidOperationException($"already joined room {roomAddress}");

        try
        {
            await _tx.ConnectAsync(roomAddress, ct).ConfigureAwait(false);
            // Already connected (idempotent re-join of a live peer)? Kick the
            // Hello now — no PeerConnected event will fire to do it for us.
            if (_established.ContainsKey(roomAddress))
                await session.OnConnectedAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            _rooms.TryRemove(roomAddress, out _);
            throw;
        }
        return session;
    }

    /// <summary>Leave a room: drop the link and forget the session.</summary>
    public async Task LeaveRoomAsync(string roomAddress, CancellationToken ct = default)
    {
        if (_rooms.TryRemove(roomAddress, out _))
            await _tx.DisconnectAsync(roomAddress, ct).ConfigureAwait(false);
    }

    // ---- admin ops against a host base identity ----

    public Task<AdminResponse> CreateRoomAsync(
        string hostBase, string name, RoomKind kind, IEnumerable<string>? members = null, CancellationToken ct = default)
    {
        var op = new CreateRoom { Name = name, Kind = kind };
        if (members is not null)
            op.Members.AddRange(members);
        return AdminAsync(hostBase, new AdminRequest { CreateRoom = op }, ct);
    }

    public Task<AdminResponse> CloseRoomAsync(string hostBase, string name, CancellationToken ct = default)
        => AdminAsync(hostBase, new AdminRequest { CloseRoom = new CloseRoom { Name = name } }, ct);

    public Task<AdminResponse> ListRoomsAsync(string hostBase, CancellationToken ct = default)
        => AdminAsync(hostBase, new AdminRequest { ListRooms = new ListRooms() }, ct);

    public Task<AdminResponse> AddMemberAsync(string hostBase, string room, string addr, CancellationToken ct = default)
        => AdminAsync(hostBase, new AdminRequest { AddMember = new AddMember { Room = room, Addr = addr } }, ct);

    public Task<AdminResponse> RemoveMemberAsync(string hostBase, string room, string addr, CancellationToken ct = default)
        => AdminAsync(hostBase, new AdminRequest { RemoveMember = new RemoveMember { Room = room, Addr = addr } }, ct);

    public Task<AdminResponse> PromoteAdminAsync(string hostBase, string addr, CancellationToken ct = default)
        => AdminAsync(hostBase, new AdminRequest { PromoteAdmin = new PromoteAdmin { Addr = addr } }, ct);

    public Task<AdminResponse> DemoteAdminAsync(string hostBase, string addr, CancellationToken ct = default)
        => AdminAsync(hostBase, new AdminRequest { DemoteAdmin = new DemoteAdmin { Addr = addr } }, ct);

    private async Task<AdminResponse> AdminAsync(string hostBase, AdminRequest req, CancellationToken ct)
    {
        req.RequestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<AdminResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingAdmin.TryAdd(req.RequestId, tcs))
            throw new InvalidOperationException("duplicate admin request id");

        try
        {
            await WaitConnectedAsync(hostBase, ct).ConfigureAwait(false);
            await _tx.SendAsync(hostBase, new AdminEnvelope { Request = req }.ToByteArray(), ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _pendingAdmin.TryRemove(req.RequestId, out _);
        }
    }

    /// <summary>Dial <paramref name="addr"/> if needed and return once its link
    /// is established (admin must not send before the host can receive).</summary>
    private async Task WaitConnectedAsync(string addr, CancellationToken ct)
    {
        if (_established.ContainsKey(addr))
            return;

        var tcs = _connectWaiters.GetOrAdd(
            addr, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        await _tx.ConnectAsync(addr, ct).ConfigureAwait(false);
        // A PeerConnected may have raced in between the check and the dial.
        if (_established.ContainsKey(addr))
        {
            tcs.TrySetResult();
            return;
        }
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        await tcs.Task.ConfigureAwait(false);
    }

    // ---- inbound event router ----

    private async Task OnPeerConnectedAsync(string addr)
    {
        _established[addr] = 0;
        if (_connectWaiters.TryRemove(addr, out var waiter))
            waiter.TrySetResult();
        if (_rooms.TryGetValue(addr, out var session))
            await session.OnConnectedAsync().ConfigureAwait(false);
    }

    private Task OnPeerDisconnectedAsync(string addr)
    {
        _established.TryRemove(addr, out _);
        // Keep the RoomSession: the daemon's watchdog re-dials and a fresh
        // PeerConnected re-announces our Hello. Admin re-connects on next call.
        return Task.CompletedTask;
    }

    private async Task OnMessageReceivedAsync(string from, byte[] payload)
    {
        if (_rooms.TryGetValue(from, out var session))
        {
            await session.OnRpcMessageAsync(payload).ConfigureAwait(false);
            return;
        }
        // Not a joined room → an admin response from a host base identity.
        CompleteAdmin(payload);
    }

    private void CompleteAdmin(byte[] payload)
    {
        AdminEnvelope env;
        try
        {
            env = AdminEnvelope.Parser.ParseFrom(payload);
        }
        catch (InvalidProtocolBufferException)
        {
            return;
        }
        if (env.BodyCase != AdminEnvelope.BodyOneofCase.Response)
            return;

        if (_pendingAdmin.TryRemove(env.Response.RequestId, out var tcs))
            tcs.TrySetResult(env.Response);
    }

    public async ValueTask DisposeAsync()
    {
        _tx.PeerConnected -= OnPeerConnectedAsync;
        _tx.PeerDisconnected -= OnPeerDisconnectedAsync;
        _tx.MessageReceived -= OnMessageReceivedAsync;

        foreach (var kvp in _pendingAdmin)
            kvp.Value.TrySetCanceled();
        foreach (var kvp in _connectWaiters)
            kvp.Value.TrySetCanceled();

        if (_ownsTransport && _tx is IAsyncDisposable d)
            await d.DisposeAsync().ConfigureAwait(false);
        _bindingLock.Dispose();
    }
}
