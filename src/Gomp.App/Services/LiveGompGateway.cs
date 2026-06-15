using Ensemble.Client;
using Gomp.Client;
using Gomp.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gomp.App.Services;

/// <summary>
/// The live gateway: a thin shim from the app's <see cref="IGompGateway"/> onto a
/// real <see cref="GompClient"/>. This is the un-unit-tested glue (no daemon in
/// tests) — the view-models it feeds are exercised against a fake gateway.
/// </summary>
internal sealed class LiveGompGateway : IGompGateway
{
    private readonly EnsembleClient _client;
    private readonly GompClient _gomp;

    public LiveGompGateway(EnsembleClient client, GompClient gomp)
    {
        _client = client;
        _gomp = gomp;
    }

    public string SelfAddress => _gomp.Address;

    public async Task<IRoomHandle> JoinAsync(
        string roomAddress, IRoomObserver observer, bool requestHistory = true, CancellationToken ct = default)
    {
        var session = await _gomp.JoinRoomAsync(roomAddress, observer, requestHistory, ct).ConfigureAwait(false);
        return new LiveRoomHandle(session);
    }

    public Task LeaveAsync(string roomAddress, CancellationToken ct = default)
        => _gomp.LeaveRoomAsync(roomAddress, ct);

    public async Task<AdminResult> CreateRoomAsync(
        string hostBase, string name, RoomKind kind, IEnumerable<string>? members = null, CancellationToken ct = default)
        => Reshape(await _gomp.CreateRoomAsync(hostBase, name, kind, members, ct).ConfigureAwait(false));

    public async Task<AdminResult> ListRoomsAsync(string hostBase, CancellationToken ct = default)
        => Reshape(await _gomp.ListRoomsAsync(hostBase, ct).ConfigureAwait(false));

    public async Task<AdminResult> CloseRoomAsync(string hostBase, string name, CancellationToken ct = default)
        => Reshape(await _gomp.CloseRoomAsync(hostBase, name, ct).ConfigureAwait(false));

    public async Task<AdminResult> AddMemberAsync(string hostBase, string room, string addr, CancellationToken ct = default)
        => Reshape(await _gomp.AddMemberAsync(hostBase, room, addr, ct).ConfigureAwait(false));

    public async Task<AdminResult> RemoveMemberAsync(string hostBase, string room, string addr, CancellationToken ct = default)
        => Reshape(await _gomp.RemoveMemberAsync(hostBase, room, addr, ct).ConfigureAwait(false));

    public async Task<AdminResult> PromoteAdminAsync(string hostBase, string addr, CancellationToken ct = default)
        => Reshape(await _gomp.PromoteAdminAsync(hostBase, addr, ct).ConfigureAwait(false));

    public async Task<AdminResult> DemoteAdminAsync(string hostBase, string addr, CancellationToken ct = default)
        => Reshape(await _gomp.DemoteAdminAsync(hostBase, addr, ct).ConfigureAwait(false));

    private static AdminResult Reshape(AdminResponse resp)
    {
        var rooms = resp.Rooms
            .Select(r => new RoomSummary(r.Name, r.Addr, r.Kind))
            .ToList();
        return new AdminResult(resp.Ok, string.IsNullOrEmpty(resp.Error) ? null : resp.Error, rooms);
    }

    public async ValueTask DisposeAsync()
    {
        await _gomp.DisposeAsync().ConfigureAwait(false);
        await _client.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class LiveRoomHandle : IRoomHandle
    {
        private readonly RoomSession _session;
        public LiveRoomHandle(RoomSession session) => _session = session;

        public string Address => _session.RoomAddress;
        public Task SendChatAsync(string text, CancellationToken ct = default) => _session.SendChatAsync(text, ct);
        public Task RequestRosterAsync(CancellationToken ct = default) => _session.RequestRosterAsync(ct);
        public Task BackfillAsync(long sinceSeq, CancellationToken ct = default) => _session.BackfillAsync(sinceSeq, 0, ct);
    }
}

/// <summary>
/// Builds a <see cref="LiveGompGateway"/> from the ambient daemon socket
/// (<c>ENSEMBLE_SOCKET</c> et al. via <see cref="EnsembleClient.FromEnv"/>). The
/// member registers one RPC rooms-client identity; it only ever dials out, so its
/// inbound ACL is permissive (replies ride the connection it opened). Wire
/// details here are finalised by the live-daemon e2e ticket.
/// </summary>
internal sealed class LiveGompGatewayFactory : IGompGatewayFactory
{
    private const long MaxPayloadBytes = 128 * 1024;

    public async Task<IGompGateway> ConnectAsync(CancellationToken ct = default)
    {
        var client = EnsembleClient.FromEnv(NullLogger<EnsembleClient>.Instance);
        try
        {
            var manifest = ServiceManifest.NewBuilder("gomp")
                .Description("gomp rooms client")
                .Transport(ServiceTransport.Rpc)
                .Acl(ServiceAcl.Public)
                .MaxPayloadBytes(MaxPayloadBytes)
                .Build();

            var gomp = await GompClient.ConnectAsync(client, manifest, ct).ConfigureAwait(false);
            return new LiveGompGateway(client, gomp);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
