using System.Threading.Channels;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Ensemble.Client;
using Gomp.Client;
using Gomp.Protocol;
using Gw = Gomp.Protocol.Gateway;

namespace Gomp.Host;

/// <summary>
/// The unified, daemon-supervised gomp backend (ADR-0011 §5 / 65e99655). ONE
/// process, ONE base "gomp" identity that plays both roles a launched_client gomp
/// needs:
/// <list type="bullet">
///   <item><b>Host</b> — accepts admin ops + mints room sub-identities (the v1
///   <see cref="RoomHost"/>, now attached to the shared base).</item>
///   <item><b>Member</b> — dials other hosts' pubs and posts, via
///   <see cref="GompClient"/> over a <see cref="SharedBaseClientTransport"/> on
///   the same base service.</item>
/// </list>
/// The launched Avalonia frontend never touches the daemon directly: it relays
/// every op as a <see cref="Gw.FeRequest"/> (arriving here as an
/// <see cref="ServiceEvent.OperatorMessage"/>) and sees every post / result as a
/// <see cref="Gw.BeEvent"/> the backend pushes via
/// <see cref="RegisteredService.SendToOperatorAsync"/>.
///
/// With NO frontend attached the backend is byte-for-byte the v1 headless host —
/// it registers, restores rooms, and serves admin; the member machinery just
/// sits idle until an operator drives it.
/// </summary>
internal sealed class GompBackend : IAsyncDisposable
{
    private readonly EnsembleClient _client;
    private readonly ILogger<GompBackend> _log;
    private readonly RoomHost _host;

    private RegisteredService? _baseSvc;
    private SharedBaseClientTransport? _memberTx;
    private GompClient? _member;
    private string _selfAddress = "";

    // Rooms the operator has joined, keyed by room address → live session (for
    // chat / roster / backfill ops the frontend drives).
    private readonly Dictionary<string, RoomSession> _joined = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _joinedLock = new(1, 1);

    // Frontend requests are processed off the register-stream reader: a remote
    // admin op awaits the host's reply, which arrives as another RpcMessage on
    // THIS same base stream — handling it inline on the reader would deadlock.
    // The reader just enqueues; this worker drains in FIFO order.
    private readonly Channel<byte[]> _feRequests =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private Task? _feWorker;

    public GompBackend(
        EnsembleClient client,
        string hostName,
        string dataDir,
        string seedAdmin,
        int historyMax,
        ILoggerFactory loggerFactory)
    {
        _client = client;
        _log = loggerFactory.CreateLogger<GompBackend>();
        _host = new RoomHost(client, hostName, dataDir, seedAdmin, historyMax, loggerFactory);
    }

    /// <summary>The backend's base (member) E-address — what posts are signed
    /// under and what the frontend shows as "you".</summary>
    public string BaseAddress => _selfAddress;

    /// <summary>
    /// Register the single base identity (combined handler), wire the member core
    /// onto it, and restore the hosted rooms. After this returns the backend is a
    /// fully functional headless host; a frontend may attach at any time.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        _baseSvc = await _client.RegisterServiceAsync(
            _host.BuildBaseManifest(),
            HandleBaseEventAsync,
            OnBaseErrorAsync,
            ct).ConfigureAwait(false);
        _selfAddress = _baseSvc.ServiceAddress;

        _memberTx = new SharedBaseClientTransport(_client, _baseSvc);
        _member = new GompClient(_memberTx, ownsTransport: false);

        await _host.AttachAsync(_baseSvc, ct).ConfigureAwait(false);

        _feWorker = Task.Run(FeWorkerLoopAsync, CancellationToken.None);
        _log.LogInformation("gomp backend running; base identity {Addr}", _selfAddress);
    }

    // ---- the one base-identity event handler (demuxes host / member / FE) ----

    private async ValueTask HandleBaseEventAsync(ServiceEvent ev)
    {
        switch (ev)
        {
            case ServiceEvent.OperatorMessage op:
                // Frontend → backend. Offload; never block the reader (below).
                _feRequests.Writer.TryWrite(op.Payload);
                break;

            case ServiceEvent.ConnectionEstablished e:
                if (_memberTx is { } tc) await tc.RaisePeerConnected(e.FromAddr).ConfigureAwait(false);
                break;

            case ServiceEvent.ConnectionClosed e:
                if (_memberTx is { } td) await td.RaisePeerDisconnected(e.FromAddr).ConfigureAwait(false);
                break;

            case ServiceEvent.RpcMessage m:
                await RouteRpcAsync(m).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Demux an inbound RPC on the shared base. A payload from a room we've
    /// joined is a room post → the member. Otherwise it's admin traffic: a
    /// Request we serve as host, a Response to an op we issued as member.
    /// </summary>
    private async Task RouteRpcAsync(ServiceEvent.RpcMessage m)
    {
        if (_member is { } mem && mem.IsJoinedRoom(m.FromAddr))
        {
            if (_memberTx is { } tx) await tx.RaiseMessageReceived(m.FromAddr, m.Payload).ConfigureAwait(false);
            return;
        }

        AdminEnvelope env;
        try
        {
            env = AdminEnvelope.Parser.ParseFrom(m.Payload);
        }
        catch (InvalidProtocolBufferException)
        {
            return; // not admin traffic, not a joined room — ignore
        }

        switch (env.BodyCase)
        {
            case AdminEnvelope.BodyOneofCase.Response:
                // Our own member admin op's reply → completes a pending request.
                if (_memberTx is { } tx) await tx.RaiseMessageReceived(m.FromAddr, m.Payload).ConfigureAwait(false);
                break;
            case AdminEnvelope.BodyOneofCase.Request:
                // A remote admin driving OUR host (authority-checked + replied
                // inside RoomHost; runs on a separate stream, no reader deadlock).
                await _host.HandleBaseEventAsync(m).ConfigureAwait(false);
                break;
        }
    }

    private ValueTask OnBaseErrorAsync(ServiceError err)
    {
        _log.LogDebug("base identity service error: {Code} {Msg}", err.Code, err.Message);
        return ValueTask.CompletedTask;
    }

    // ---- frontend request worker ----

    private async Task FeWorkerLoopAsync()
    {
        await foreach (var payload in _feRequests.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await HandleFeRequestAsync(payload).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "frontend request failed");
            }
        }
    }

    private async Task HandleFeRequestAsync(byte[] payload)
    {
        Gw.FeRequest req;
        try
        {
            req = Gw.FeRequest.Parser.ParseFrom(payload);
        }
        catch (InvalidProtocolBufferException)
        {
            return;
        }

        switch (req.BodyCase)
        {
            case Gw.FeRequest.BodyOneofCase.Join: await OnJoinAsync(req.Join).ConfigureAwait(false); break;
            case Gw.FeRequest.BodyOneofCase.Leave: await OnLeaveAsync(req.Leave).ConfigureAwait(false); break;
            case Gw.FeRequest.BodyOneofCase.SendChat: await OnSendChatAsync(req.SendChat).ConfigureAwait(false); break;
            case Gw.FeRequest.BodyOneofCase.Roster: await OnRosterAsync(req.Roster).ConfigureAwait(false); break;
            case Gw.FeRequest.BodyOneofCase.Backfill: await OnBackfillAsync(req.Backfill).ConfigureAwait(false); break;
            case Gw.FeRequest.BodyOneofCase.Admin: await OnAdminAsync(req.Admin).ConfigureAwait(false); break;
        }
    }

    private async Task OnJoinAsync(Gw.JoinRoom j)
    {
        if (_member is not { } mem) return;

        // The backend outlives the launched frontend (it's daemon-supervised; only
        // the Avalonia UI relaunches). A relaunched UI re-joins rooms the member is
        // already in — don't silently no-op, or the fresh UI sits empty (no roster,
        // no history) until the user sends a message. Replay current state over the
        // live session so it repopulates. SendToOperator targets whatever frontend
        // is attached now, so the existing observer routes to the new UI fine.
        var existing = await SessionFor(j.RoomAddress).ConfigureAwait(false);
        if (existing is not null)
        {
            await existing.RequestRosterAsync().ConfigureAwait(false);
            if (j.RequestHistory) await existing.BackfillAsync(0).ConfigureAwait(false);
            return;
        }

        var observer = new RelayRoomObserver(j.RoomAddress, SendToFeAsync);
        try
        {
            var session = await mem.JoinRoomAsync(j.RoomAddress, observer, j.RequestHistory).ConfigureAwait(false);
            await _joinedLock.WaitAsync().ConfigureAwait(false);
            _joined[j.RoomAddress] = session;
            _joinedLock.Release();
        }
        catch (Exception ex)
        {
            await SendToFeAsync(new Gw.BeEvent
            {
                RoomError = new Gw.RoomError { RoomAddress = j.RoomAddress, Code = "join_failed", Message = ex.Message },
            }).ConfigureAwait(false);
        }
    }

    private async Task OnLeaveAsync(Gw.LeaveRoom l)
    {
        if (_member is { } mem) await mem.LeaveRoomAsync(l.RoomAddress).ConfigureAwait(false);
        await _joinedLock.WaitAsync().ConfigureAwait(false);
        _joined.Remove(l.RoomAddress);
        _joinedLock.Release();
    }

    private async Task OnSendChatAsync(Gw.SendChat s)
    {
        var session = await SessionFor(s.RoomAddress).ConfigureAwait(false);
        if (session is not null) await session.SendChatAsync(s.Text).ConfigureAwait(false);
    }

    private async Task OnRosterAsync(Gw.Roster r)
    {
        var session = await SessionFor(r.RoomAddress).ConfigureAwait(false);
        if (session is not null) await session.RequestRosterAsync().ConfigureAwait(false);
    }

    private async Task OnBackfillAsync(Gw.Backfill b)
    {
        var session = await SessionFor(b.RoomAddress).ConfigureAwait(false);
        if (session is not null) await session.BackfillAsync(b.SinceSeq, 0).ConfigureAwait(false);
    }

    private async Task<RoomSession?> SessionFor(string roomAddress)
    {
        await _joinedLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return _joined.TryGetValue(roomAddress, out var s) ? s : null;
        }
        finally
        {
            _joinedLock.Release();
        }
    }

    private async Task OnAdminAsync(Gw.AdminOp op)
    {
        AdminResponse resp;
        try
        {
            // Empty / self host_base ⇒ "host it here": run against our own host
            // through the privileged operator door (no from_addr authority check).
            var local = string.IsNullOrEmpty(op.HostBase) || op.HostBase == _selfAddress;
            resp = local
                ? await _host.ExecuteAdminAsync(ToRoomsRequest(op)).ConfigureAwait(false)
                : await DispatchRemoteAdminAsync(op).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            resp = new AdminResponse { RequestId = op.RequestId, Ok = false, Error = ex.Message };
        }
        await SendToFeAsync(ToBeAdminResult(op.RequestId, resp)).ConfigureAwait(false);
    }

    private async Task<AdminResponse> DispatchRemoteAdminAsync(Gw.AdminOp op)
    {
        var mem = _member!;
        return op.OpCase switch
        {
            Gw.AdminOp.OpOneofCase.Create =>
                await mem.CreateRoomAsync(op.HostBase, op.Create.Name, op.Create.Kind, op.Create.Members).ConfigureAwait(false),
            Gw.AdminOp.OpOneofCase.List =>
                await mem.ListRoomsAsync(op.HostBase).ConfigureAwait(false),
            Gw.AdminOp.OpOneofCase.Close =>
                await mem.CloseRoomAsync(op.HostBase, op.Close.Name).ConfigureAwait(false),
            Gw.AdminOp.OpOneofCase.Add =>
                await mem.AddMemberAsync(op.HostBase, op.Add.Room, op.Add.Addr).ConfigureAwait(false),
            Gw.AdminOp.OpOneofCase.Remove =>
                await mem.RemoveMemberAsync(op.HostBase, op.Remove.Room, op.Remove.Addr).ConfigureAwait(false),
            Gw.AdminOp.OpOneofCase.Promote =>
                await mem.PromoteAdminAsync(op.HostBase, op.Promote.Addr).ConfigureAwait(false),
            Gw.AdminOp.OpOneofCase.Demote =>
                await mem.DemoteAdminAsync(op.HostBase, op.Demote.Addr).ConfigureAwait(false),
            _ => new AdminResponse { RequestId = op.RequestId, Ok = false, Error = "unknown_op" },
        };
    }

    // ---- mapping: gateway ⇄ rooms wire ----

    private static AdminRequest ToRoomsRequest(Gw.AdminOp op)
    {
        var req = new AdminRequest { RequestId = op.RequestId };
        switch (op.OpCase)
        {
            case Gw.AdminOp.OpOneofCase.Create:
                var cr = new CreateRoom { Name = op.Create.Name, Kind = op.Create.Kind };
                cr.Members.AddRange(op.Create.Members);
                req.CreateRoom = cr;
                break;
            case Gw.AdminOp.OpOneofCase.List:
                req.ListRooms = new ListRooms();
                break;
            case Gw.AdminOp.OpOneofCase.Close:
                req.CloseRoom = new CloseRoom { Name = op.Close.Name };
                break;
            case Gw.AdminOp.OpOneofCase.Add:
                req.AddMember = new AddMember { Room = op.Add.Room, Addr = op.Add.Addr };
                break;
            case Gw.AdminOp.OpOneofCase.Remove:
                req.RemoveMember = new RemoveMember { Room = op.Remove.Room, Addr = op.Remove.Addr };
                break;
            case Gw.AdminOp.OpOneofCase.Promote:
                req.PromoteAdmin = new PromoteAdmin { Addr = op.Promote.Addr };
                break;
            case Gw.AdminOp.OpOneofCase.Demote:
                req.DemoteAdmin = new DemoteAdmin { Addr = op.Demote.Addr };
                break;
            // The management ops carry rooms.proto messages verbatim — no re-map.
            case Gw.AdminOp.OpOneofCase.SetKind:
                req.SetKind = op.SetKind;
                break;
            case Gw.AdminOp.OpOneofCase.ClearHistory:
                req.ClearHistory = op.ClearHistory;
                break;
            case Gw.AdminOp.OpOneofCase.Update:
                req.UpdateRoom = op.Update;
                break;
            case Gw.AdminOp.OpOneofCase.Detail:
                req.RoomDetail = op.Detail;
                break;
        }
        return req;
    }

    private static Gw.BeEvent ToBeAdminResult(string requestId, AdminResponse resp)
    {
        // Stamp the FRONTEND's request_id: a member admin op carries the host's
        // own correlation id in resp, not the FE's.
        var ar = new Gw.AdminResult { RequestId = requestId, Ok = resp.Ok, Error = resp.Error ?? string.Empty };
        foreach (var r in resp.Rooms)
            ar.Rooms.Add(new Gw.RoomSummary { Name = r.Name, Addr = r.Addr, Kind = r.Kind, DisplayName = r.DisplayName });
        if (resp.Detail is not null)
            ar.Detail = resp.Detail; // reused verbatim from the rooms wire
        return new Gw.BeEvent { AdminResult = ar };
    }

    private Task SendToFeAsync(Gw.BeEvent ev)
        => _baseSvc is { } svc ? svc.SendToOperatorAsync(ev.ToByteArray()) : Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _feRequests.Writer.TryComplete();
        if (_feWorker is not null)
        {
            try { await _feWorker.ConfigureAwait(false); } catch { /* best effort */ }
        }
        if (_member is not null) await _member.DisposeAsync().ConfigureAwait(false);
        await _host.DisposeAsync().ConfigureAwait(false); // disposes room sub-identities (not the shared base)
        if (_baseSvc is not null) await _baseSvc.DisposeAsync().ConfigureAwait(false);
        _joinedLock.Dispose();
    }
}

/// <summary>
/// An <see cref="IRoomObserver"/> that forwards a joined room's posts / roster /
/// presence / errors to the launched frontend as tagged <see cref="Gw.BeEvent"/>
/// payloads over the operator relay.
/// </summary>
internal sealed class RelayRoomObserver : IRoomObserver
{
    private readonly string _room;
    private readonly Func<Gw.BeEvent, Task> _send;

    public RelayRoomObserver(string room, Func<Gw.BeEvent, Task> send)
    {
        _room = room;
        _send = send;
    }

    public Task OnPostAsync(ReceivedPost post) => _send(new Gw.BeEvent
    {
        Post = new Gw.Post
        {
            RoomAddress = _room,
            Seq = post.Seq,
            HostTs = post.HostTs,
            Sender = post.Sender,
            Content = ByteString.CopyFrom(post.Content),
            ClientTs = post.ClientTs,
            Nonce = post.Nonce,
            Trust = MapTrust(post.Trust),
        },
    });

    public Task OnPresenceAsync(RoomMemberPresence presence) => _send(new Gw.BeEvent
    {
        Presence = new Gw.Presence { RoomAddress = _room, Addr = presence.Addr, Online = presence.Online },
    });

    public Task OnRosterAsync(IReadOnlyList<RoomMemberPresence> members)
    {
        var snap = new Gw.RosterSnap { RoomAddress = _room };
        foreach (var m in members)
            snap.Members.Add(new Gw.RosterMember { Addr = m.Addr, Online = m.Online });
        return _send(new Gw.BeEvent { Roster = snap });
    }

    public Task OnErrorAsync(string code, string message) => _send(new Gw.BeEvent
    {
        RoomError = new Gw.RoomError { RoomAddress = _room, Code = code, Message = message },
    });

    private static Gw.PostTrust MapTrust(PostTrust t) => t switch
    {
        PostTrust.Verified => Gw.PostTrust.Verified,
        PostTrust.Forged => Gw.PostTrust.Forged,
        _ => Gw.PostTrust.Unverified,
    };
}
