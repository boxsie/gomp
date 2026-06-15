using Gomp.Client;
using Gomp.Protocol;

namespace Gomp.App.Services;

/// <summary>
/// A live handle on one joined pub — the app-layer face of a
/// <see cref="RoomSession"/>, trimmed to what the UI drives and free of any
/// type a test can't construct.
/// </summary>
public interface IRoomHandle
{
    string Address { get; }
    Task SendChatAsync(string text, CancellationToken ct = default);
    Task RequestRosterAsync(CancellationToken ct = default);
    Task BackfillAsync(long sinceSeq, CancellationToken ct = default);
}

/// <summary>
/// The app's whole conversation with the gomp client core, behind one interface
/// so view-models can run against a fake. The live implementation wraps
/// <see cref="GompClient"/>; admin ops collapse the proto response into
/// <see cref="AdminResult"/>.
/// </summary>
public interface IGompGateway : IAsyncDisposable
{
    string SelfAddress { get; }

    Task<IRoomHandle> JoinAsync(string roomAddress, IRoomObserver observer, bool requestHistory = true, CancellationToken ct = default);
    Task LeaveAsync(string roomAddress, CancellationToken ct = default);

    Task<AdminResult> CreateRoomAsync(string hostBase, string name, RoomKind kind, IEnumerable<string>? members = null, CancellationToken ct = default);
    Task<AdminResult> ListRoomsAsync(string hostBase, CancellationToken ct = default);
    Task<AdminResult> CloseRoomAsync(string hostBase, string name, CancellationToken ct = default);
    Task<AdminResult> AddMemberAsync(string hostBase, string room, string addr, CancellationToken ct = default);
    Task<AdminResult> RemoveMemberAsync(string hostBase, string room, string addr, CancellationToken ct = default);
    Task<AdminResult> PromoteAdminAsync(string hostBase, string addr, CancellationToken ct = default);
    Task<AdminResult> DemoteAdminAsync(string hostBase, string addr, CancellationToken ct = default);
}

/// <summary>
/// Establishes the live connection (register the rooms-client service on the
/// daemon). Behind an interface so the connect screen's view-model is testable;
/// the live factory needs a real daemon socket via <c>EnsembleClient.FromEnv()</c>.
/// </summary>
public interface IGompGatewayFactory
{
    Task<IGompGateway> ConnectAsync(CancellationToken ct = default);
}
