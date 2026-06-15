using Gomp.App.Services;
using Gomp.Client;
using Gomp.Protocol;

namespace Gomp.Snapshot;

/// <summary>Canned gateway so the snapshot harness can render a populated UI
/// with no daemon. Records the observer per room so the driver can push posts.</summary>
internal sealed class SampleGateway : IGompGateway, IGompGatewayFactory
{
    public string SelfAddress => "Eself9mQ2kP7vX";
    public readonly Dictionary<string, IRoomObserver> Observers = new(StringComparer.Ordinal);

    public Task<IGompGateway> ConnectAsync(CancellationToken ct = default) => Task.FromResult<IGompGateway>(this);

    public Task<IRoomHandle> JoinAsync(string roomAddress, IRoomObserver observer, bool requestHistory = true, CancellationToken ct = default)
    {
        Observers[roomAddress] = observer;
        return Task.FromResult<IRoomHandle>(new NoopHandle(roomAddress));
    }

    public Task LeaveAsync(string roomAddress, CancellationToken ct = default) => Task.CompletedTask;

    public Task<AdminResult> CreateRoomAsync(string hostBase, string name, RoomKind kind, IEnumerable<string>? members = null, CancellationToken ct = default)
        => Task.FromResult(new AdminResult(true, null, new[] { new RoomSummary(name, "Eroom-" + name, kind) }));

    public Task<AdminResult> ListRoomsAsync(string hostBase, CancellationToken ct = default) => Ok();
    public Task<AdminResult> CloseRoomAsync(string hostBase, string name, CancellationToken ct = default) => Ok();
    public Task<AdminResult> AddMemberAsync(string hostBase, string room, string addr, CancellationToken ct = default) => Ok();
    public Task<AdminResult> RemoveMemberAsync(string hostBase, string room, string addr, CancellationToken ct = default) => Ok();
    public Task<AdminResult> PromoteAdminAsync(string hostBase, string addr, CancellationToken ct = default) => Ok();
    public Task<AdminResult> DemoteAdminAsync(string hostBase, string addr, CancellationToken ct = default) => Ok();

    private static Task<AdminResult> Ok() => Task.FromResult(new AdminResult(true, null, Array.Empty<RoomSummary>()));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class NoopHandle : IRoomHandle
    {
        public NoopHandle(string address) => Address = address;
        public string Address { get; }
        public Task SendChatAsync(string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task RequestRosterAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task BackfillAsync(long sinceSeq, CancellationToken ct = default) => Task.CompletedTask;
    }
}

internal sealed class SyncDispatcher : IUiDispatcher
{
    public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
}
