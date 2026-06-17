using System.Collections.Concurrent;
using Google.Protobuf;
using Ensemble.Client;
using Gomp.Client;
using Gomp.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Gw = Gomp.Protocol.Gateway;

namespace Gomp.App.Services;

/// <summary>
/// The launched frontend's gateway: a pure consumer that relays every op through
/// its own supervised backend over the daemon's launched-client relay (ADR-0011
/// §5). It registers no service and holds no identity — the member identity, post
/// signing and room hosting all live in the backend. Outbound ops become
/// <see cref="Gw.FeRequest"/> payloads on the <see cref="AttachedService"/>;
/// inbound posts / rosters / admin results arrive as <see cref="Gw.BeEvent"/>
/// payloads and are fanned to the joined rooms' observers (by room address) or
/// the pending admin call (by request id).
/// </summary>
internal sealed class RelayGompGateway : IGompGateway
{
    private readonly IBackendRelay _relay;

    private readonly ConcurrentDictionary<string, IRoomObserver> _observers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AdminResult>> _pendingAdmin = new();

    public RelayGompGateway(IBackendRelay relay) => _relay = relay;

    // The backend's base/member address, echoed by the daemon at attach time —
    // what posts are signed under and what the UI renders as "you".
    public string SelfAddress => _relay.ServiceAddress;

    /// <summary>Dispatch one backend→frontend event. Called from the attach
    /// reader loop (background) — observers already marshal to the UI thread.</summary>
    internal async Task OnBackendPayloadAsync(byte[] payload)
    {
        Gw.BeEvent ev;
        try
        {
            ev = Gw.BeEvent.Parser.ParseFrom(payload);
        }
        catch (InvalidProtocolBufferException)
        {
            return;
        }

        switch (ev.BodyCase)
        {
            case Gw.BeEvent.BodyOneofCase.Post:
                if (_observers.TryGetValue(ev.Post.RoomAddress, out var po))
                    await po.OnPostAsync(ToReceivedPost(ev.Post)).ConfigureAwait(false);
                break;
            case Gw.BeEvent.BodyOneofCase.Presence:
                if (_observers.TryGetValue(ev.Presence.RoomAddress, out var pr))
                    await pr.OnPresenceAsync(new RoomMemberPresence(ev.Presence.Addr, ev.Presence.Online)).ConfigureAwait(false);
                break;
            case Gw.BeEvent.BodyOneofCase.Roster:
                if (_observers.TryGetValue(ev.Roster.RoomAddress, out var ro))
                {
                    var members = ev.Roster.Members
                        .Select(m => new RoomMemberPresence(m.Addr, m.Online))
                        .ToList();
                    await ro.OnRosterAsync(members).ConfigureAwait(false);
                }
                break;
            case Gw.BeEvent.BodyOneofCase.RoomError:
                if (_observers.TryGetValue(ev.RoomError.RoomAddress, out var eo))
                    await eo.OnErrorAsync(ev.RoomError.Code, ev.RoomError.Message).ConfigureAwait(false);
                break;
            case Gw.BeEvent.BodyOneofCase.AdminResult:
                if (_pendingAdmin.TryRemove(ev.AdminResult.RequestId, out var tcs))
                    tcs.TrySetResult(ToAdminResult(ev.AdminResult));
                break;
            case Gw.BeEvent.BodyOneofCase.Welcome:
                // Self address already known via AttachedService.ServiceAddress.
                break;
        }
    }

    // ---- join / leave ----

    public async Task<IRoomHandle> JoinAsync(
        string roomAddress, IRoomObserver observer, bool requestHistory = true, CancellationToken ct = default)
    {
        // Register the observer BEFORE asking the backend to join, so an early
        // roster/post can't arrive before we can route it.
        _observers[roomAddress] = observer;
        try
        {
            await SendAsync(new Gw.FeRequest
            {
                Join = new Gw.JoinRoom { RoomAddress = roomAddress, RequestHistory = requestHistory },
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            _observers.TryRemove(roomAddress, out _);
            throw;
        }
        return new RelayRoomHandle(this, roomAddress);
    }

    public async Task LeaveAsync(string roomAddress, CancellationToken ct = default)
    {
        _observers.TryRemove(roomAddress, out _);
        await SendAsync(new Gw.FeRequest { Leave = new Gw.LeaveRoom { RoomAddress = roomAddress } }, ct)
            .ConfigureAwait(false);
    }

    // ---- admin ops (correlated by FE-minted request id) ----

    public Task<AdminResult> CreateRoomAsync(
        string hostBase, string name, RoomKind kind, IEnumerable<string>? members = null, CancellationToken ct = default)
    {
        var create = new Gw.CreateRoom { Name = name, Kind = kind };
        if (members is not null) create.Members.AddRange(members);
        return AdminAsync(hostBase, op => op.Create = create, ct);
    }

    public Task<AdminResult> ListRoomsAsync(string hostBase, CancellationToken ct = default)
        => AdminAsync(hostBase, op => op.List = new Gw.ListRooms(), ct);

    public Task<AdminResult> CloseRoomAsync(string hostBase, string name, CancellationToken ct = default)
        => AdminAsync(hostBase, op => op.Close = new Gw.CloseRoom { Name = name }, ct);

    public Task<AdminResult> AddMemberAsync(string hostBase, string room, string addr, CancellationToken ct = default)
        => AdminAsync(hostBase, op => op.Add = new Gw.AddMember { Room = room, Addr = addr }, ct);

    public Task<AdminResult> RemoveMemberAsync(string hostBase, string room, string addr, CancellationToken ct = default)
        => AdminAsync(hostBase, op => op.Remove = new Gw.RemoveMember { Room = room, Addr = addr }, ct);

    public Task<AdminResult> PromoteAdminAsync(string hostBase, string addr, CancellationToken ct = default)
        => AdminAsync(hostBase, op => op.Promote = new Gw.PromoteAdmin { Addr = addr }, ct);

    public Task<AdminResult> DemoteAdminAsync(string hostBase, string addr, CancellationToken ct = default)
        => AdminAsync(hostBase, op => op.Demote = new Gw.DemoteAdmin { Addr = addr }, ct);

    public Task<AdminResult> RoomDetailAsync(string hostBase, string room, CancellationToken ct = default)
        => AdminAsync(hostBase, op => op.Detail = new Gomp.Protocol.RoomDetail { Room = room }, ct);

    public Task<AdminResult> SetKindAsync(string hostBase, string room, RoomKind kind, CancellationToken ct = default)
        => AdminAsync(hostBase, op => op.SetKind = new SetKind { Room = room, Kind = kind }, ct);

    public Task<AdminResult> ClearHistoryAsync(string hostBase, string room, CancellationToken ct = default)
        => AdminAsync(hostBase, op => op.ClearHistory = new ClearHistory { Room = room }, ct);

    public Task<AdminResult> UpdateRoomAsync(
        string hostBase, string room, string displayName, string topic, int retentionMax, CancellationToken ct = default)
        => AdminAsync(hostBase, op => op.Update = new UpdateRoom
        {
            Room = room,
            DisplayName = displayName,
            Topic = topic,
            RetentionMax = retentionMax,
        }, ct);

    private async Task<AdminResult> AdminAsync(string hostBase, Action<Gw.AdminOp> fill, CancellationToken ct)
    {
        var op = new Gw.AdminOp { RequestId = Guid.NewGuid().ToString("N"), HostBase = hostBase };
        fill(op);

        var tcs = new TaskCompletionSource<AdminResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingAdmin.TryAdd(op.RequestId, tcs))
            throw new InvalidOperationException("duplicate admin request id");

        try
        {
            await SendAsync(new Gw.FeRequest { Admin = op }, ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _pendingAdmin.TryRemove(op.RequestId, out _);
        }
    }

    // ---- room handle ops ----

    private Task SendChatAsync(string roomAddress, string text, CancellationToken ct)
        => SendAsync(new Gw.FeRequest { SendChat = new Gw.SendChat { RoomAddress = roomAddress, Text = text } }, ct);

    private Task RequestRosterAsync(string roomAddress, CancellationToken ct)
        => SendAsync(new Gw.FeRequest { Roster = new Gw.Roster { RoomAddress = roomAddress } }, ct);

    private Task BackfillAsync(string roomAddress, long sinceSeq, CancellationToken ct)
        => SendAsync(new Gw.FeRequest { Backfill = new Gw.Backfill { RoomAddress = roomAddress, SinceSeq = sinceSeq } }, ct);

    private Task SendAsync(Gw.FeRequest req, CancellationToken ct) => _relay.SendAsync(req.ToByteArray(), ct);

    // ---- mapping: gateway → app/client types ----

    private static ReceivedPost ToReceivedPost(Gw.Post p) => new(
        p.Seq, p.HostTs, p.Sender, p.Content.ToByteArray(), p.ClientTs, p.Nonce, MapTrust(p.Trust));

    private static PostTrust MapTrust(Gw.PostTrust t) => t switch
    {
        Gw.PostTrust.Verified => PostTrust.Verified,
        Gw.PostTrust.Forged => PostTrust.Forged,
        _ => PostTrust.Unverified,
    };

    private static AdminResult ToAdminResult(Gw.AdminResult r)
    {
        var rooms = r.Rooms.Select(x => new RoomSummary(x.Name, x.Addr, x.Kind, x.DisplayName)).ToList();
        return new AdminResult(r.Ok, string.IsNullOrEmpty(r.Error) ? null : r.Error, rooms, ToRoomDetail(r.Detail));
    }

    private static RoomDetail? ToRoomDetail(RoomDetailInfo? d)
    {
        if (d is null || string.IsNullOrEmpty(d.Name)) return null;
        var members = d.Members
            .Select(m => new RoomMemberInfo(m.Addr, m.IsAdmin, m.Online))
            .ToList();
        return new RoomDetail(d.Kind, d.DisplayName, d.Topic, d.RetentionMax, members);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var kvp in _pendingAdmin)
            kvp.Value.TrySetCanceled();
        await _relay.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class RelayRoomHandle : IRoomHandle
    {
        private readonly RelayGompGateway _gw;
        public RelayRoomHandle(RelayGompGateway gw, string address)
        {
            _gw = gw;
            Address = address;
        }

        public string Address { get; }
        public Task SendChatAsync(string text, CancellationToken ct = default) => _gw.SendChatAsync(Address, text, ct);
        public Task RequestRosterAsync(CancellationToken ct = default) => _gw.RequestRosterAsync(Address, ct);
        public Task BackfillAsync(long sinceSeq, CancellationToken ct = default) => _gw.BackfillAsync(Address, sinceSeq, ct);
    }
}

/// <summary>
/// The backend channel a <see cref="RelayGompGateway"/> talks over: send opaque
/// bytes to the backend, learn the backend's address, detach on dispose. Behind
/// an interface so the gateway is unit-testable without a live daemon attach (the
/// live impl wraps the SDK's sealed <see cref="AttachedService"/>).
/// </summary>
internal interface IBackendRelay : IAsyncDisposable
{
    /// <summary>The backend service's E-address (echoed by the daemon on attach).</summary>
    string ServiceAddress { get; }

    /// <summary>Send a frontend→backend payload (a serialized FeRequest).</summary>
    Task SendAsync(byte[] payload, CancellationToken ct = default);
}

/// <summary>
/// Live <see cref="IBackendRelay"/> over the daemon's launched-client relay: owns
/// the <see cref="EnsembleClient"/> and its <see cref="AttachedService"/>, and
/// disposes both on detach.
/// </summary>
internal sealed class AttachedServiceRelay : IBackendRelay
{
    private readonly EnsembleClient _client;
    private readonly AttachedService _attached;

    public AttachedServiceRelay(EnsembleClient client, AttachedService attached)
    {
        _client = client;
        _attached = attached;
    }

    public string ServiceAddress => _attached.ServiceAddress;

    public Task SendAsync(byte[] payload, CancellationToken ct = default) => _attached.SendAsync(payload, ct);

    public async ValueTask DisposeAsync()
    {
        await _attached.DisposeAsync().ConfigureAwait(false);
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Builds a <see cref="RelayGompGateway"/> by attaching to the ambient backend
/// over the daemon's launched-client relay. The launch grant rides in
/// <c>ENSEMBLE_SERVICE_TOKEN</c> (read by <see cref="EnsembleClient.FromEnv"/>);
/// the daemon validates it and relays to the backend supervised under the same
/// install. No service is registered — a launched frontend is a consumer only.
/// </summary>
internal sealed class RelayGompGatewayFactory : IGompGatewayFactory
{
    public async Task<IGompGateway> ConnectAsync(CancellationToken ct = default)
    {
        var client = EnsembleClient.FromEnv(NullLogger<EnsembleClient>.Instance);
        try
        {
            // The gateway must exist before the first backend payload can arrive,
            // but AttachAsync starts the reader immediately. Capture the gateway in
            // a closure so onPayload routes into it once constructed.
            RelayGompGateway? gateway = null;
            var attached = await client.AttachAsync(
                onPayload: bytes => gateway is { } g ? new ValueTask(g.OnBackendPayloadAsync(bytes)) : ValueTask.CompletedTask,
                onError: null,
                ct: ct).ConfigureAwait(false);
            gateway = new RelayGompGateway(new AttachedServiceRelay(client, attached));
            return gateway;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
