using System.Text;
using Gomp.App.Services;
using Gomp.Client;
using Gomp.Protocol;

namespace Gomp.App.Tests;

/// <summary>Runs the action inline — no real UI thread needed in tests.</summary>
internal sealed class ImmediateDispatcher : IUiDispatcher
{
    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}

/// <summary>A room handle that records what the UI asked of it and hands back the
/// observer so a test can push posts/roster/presence as a host would.</summary>
internal sealed class FakeRoomHandle : IRoomHandle
{
    public FakeRoomHandle(string address, IRoomObserver observer)
    {
        Address = address;
        Observer = observer;
    }

    public string Address { get; }
    public IRoomObserver Observer { get; }
    public List<string> Sent { get; } = new();
    public int RosterRequests { get; private set; }
    public List<long> Backfills { get; } = new();

    public Task SendChatAsync(string text, CancellationToken ct = default)
    {
        Sent.Add(text);
        return Task.CompletedTask;
    }

    public Task RequestRosterAsync(CancellationToken ct = default)
    {
        RosterRequests++;
        return Task.CompletedTask;
    }

    public Task BackfillAsync(long sinceSeq, CancellationToken ct = default)
    {
        Backfills.Add(sinceSeq);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A scripted <see cref="IGompGateway"/> (and its factory): no daemon, every call
/// recorded, admin results canned. Captures each joined room's handle/observer so
/// tests can drive the room from the "host" side.
/// </summary>
internal sealed class FakeGateway : IGompGateway, IGompGatewayFactory
{
    public FakeGateway(string self = "Eself") => SelfAddress = self;

    public string SelfAddress { get; }
    public bool FailConnect { get; set; }
    public string ConnectError { get; set; } = "no daemon";

    public List<FakeRoomHandle> Joined { get; } = new();
    public List<string> Left { get; } = new();
    public List<(string Host, string Room)> Closed { get; } = new();
    public List<(string Host, string Room, string Addr)> Removed { get; } = new();

    public Func<string, string, RoomKind, AdminResult>? OnCreate { get; set; }
    public AdminResult RemoveResult { get; set; } = new(true, null, Array.Empty<RoomSummary>());

    public FakeRoomHandle HandleFor(string address) => Joined.First(h => h.Address == address);

    // factory
    public Task<IGompGateway> ConnectAsync(CancellationToken ct = default)
    {
        if (FailConnect)
            throw new InvalidOperationException(ConnectError);
        return Task.FromResult<IGompGateway>(this);
    }

    public Task<IRoomHandle> JoinAsync(string roomAddress, IRoomObserver observer, bool requestHistory = true, CancellationToken ct = default)
    {
        var handle = new FakeRoomHandle(roomAddress, observer);
        Joined.Add(handle);
        return Task.FromResult<IRoomHandle>(handle);
    }

    public Task LeaveAsync(string roomAddress, CancellationToken ct = default)
    {
        Left.Add(roomAddress);
        return Task.CompletedTask;
    }

    public Task<AdminResult> CreateRoomAsync(string hostBase, string name, RoomKind kind, IEnumerable<string>? members = null, CancellationToken ct = default)
    {
        var result = OnCreate?.Invoke(hostBase, name, kind)
            ?? new AdminResult(true, null, new[] { new RoomSummary(name, "Eroom-" + name, kind) });
        return Task.FromResult(result);
    }

    public List<RoomSummary> Hosted { get; } = new();

    public Task<AdminResult> ListRoomsAsync(string hostBase, CancellationToken ct = default)
        => Task.FromResult(new AdminResult(true, null, Hosted.ToArray()));

    public AdminResult CloseResult { get; set; } = new(true, null, Array.Empty<RoomSummary>());

    public Task<AdminResult> CloseRoomAsync(string hostBase, string name, CancellationToken ct = default)
    {
        Closed.Add((hostBase, name));
        return Task.FromResult(CloseResult);
    }

    public Task<AdminResult> AddMemberAsync(string hostBase, string room, string addr, CancellationToken ct = default)
    {
        Added.Add((hostBase, room, addr));
        return Task.FromResult(new AdminResult(true, null, Array.Empty<RoomSummary>()));
    }

    public Task<AdminResult> RemoveMemberAsync(string hostBase, string room, string addr, CancellationToken ct = default)
    {
        Removed.Add((hostBase, room, addr));
        return Task.FromResult(RemoveResult);
    }

    public List<(string Host, string Addr)> Promoted { get; } = new();
    public List<(string Host, string Addr)> Demoted { get; } = new();

    public Task<AdminResult> PromoteAdminAsync(string hostBase, string addr, CancellationToken ct = default)
    {
        Promoted.Add((hostBase, addr));
        return Task.FromResult(new AdminResult(true, null, Array.Empty<RoomSummary>()));
    }

    public Task<AdminResult> DemoteAdminAsync(string hostBase, string addr, CancellationToken ct = default)
    {
        Demoted.Add((hostBase, addr));
        return Task.FromResult(new AdminResult(true, null, Array.Empty<RoomSummary>()));
    }

    // ---- room management ----
    public List<(string Host, string Room)> Detailed { get; } = new();
    public List<(string Host, string Room, RoomKind Kind)> KindSet { get; } = new();
    public List<(string Host, string Room)> Histories { get; } = new();
    public List<(string Host, string Room, string Display, string Topic, int Retention)> Updated { get; } = new();
    public List<(string Host, string Room, string Addr)> Added { get; } = new();

    /// <summary>The detail a <see cref="RoomDetailAsync"/> returns; default has no members.</summary>
    public Gomp.App.Services.RoomDetail DetailResult { get; set; } =
        new(RoomKind.Invite, "", "", 0, Array.Empty<RoomMemberInfo>());

    public Task<AdminResult> RoomDetailAsync(string hostBase, string room, CancellationToken ct = default)
    {
        Detailed.Add((hostBase, room));
        return Task.FromResult(new AdminResult(true, null, Array.Empty<RoomSummary>(), DetailResult));
    }

    public Task<AdminResult> SetKindAsync(string hostBase, string room, RoomKind kind, CancellationToken ct = default)
    {
        KindSet.Add((hostBase, room, kind));
        return Task.FromResult(new AdminResult(true, null, Array.Empty<RoomSummary>()));
    }

    public Task<AdminResult> ClearHistoryAsync(string hostBase, string room, CancellationToken ct = default)
    {
        Histories.Add((hostBase, room));
        return Task.FromResult(new AdminResult(true, null, Array.Empty<RoomSummary>()));
    }

    public Task<AdminResult> UpdateRoomAsync(string hostBase, string room, string displayName, string topic, int retentionMax, CancellationToken ct = default)
    {
        Updated.Add((hostBase, room, displayName, topic, retentionMax));
        return Task.FromResult(new AdminResult(true, null, Array.Empty<RoomSummary>()));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Builders for the inbound shapes a host hands the observer.</summary>
internal static class Posts
{
    public static ReceivedPost Post(string sender, string text, long seq = 1, PostTrust trust = PostTrust.Unverified) =>
        new(seq, HostTs: seq, sender, Encoding.UTF8.GetBytes(text), ClientTs: seq, Nonce: "n" + seq, trust);

    public static RoomMemberPresence Member(string addr, bool online = true) => new(addr, online);
}
