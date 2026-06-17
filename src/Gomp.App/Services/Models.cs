using Gomp.Protocol;

namespace Gomp.App.Services;

/// <summary>One room as the host describes it in an admin response. <see cref="DisplayName"/>
/// is the friendly name (empty falls back to the slug <see cref="Name"/>).</summary>
public sealed record RoomSummary(string Name, string Address, RoomKind Kind, string DisplayName = "");

/// <summary>The flattened outcome of an admin op — the proto <c>AdminResponse</c>
/// reshaped into something the UI (and tests) can hold without protobuf.
/// <see cref="Detail"/> is populated only for a room-detail op.</summary>
public sealed record AdminResult(bool Ok, string? Error, IReadOnlyList<RoomSummary> Rooms, RoomDetail? Detail = null)
{
    public static AdminResult Fail(string error) => new(false, error, Array.Empty<RoomSummary>());
}

/// <summary>The management-page view of a room: its settings plus the full member
/// roster (allowlist ∪ currently-online), protobuf-free for the UI and tests.</summary>
public sealed record RoomDetail(
    RoomKind Kind,
    string DisplayName,
    string Topic,
    int RetentionMax,
    IReadOnlyList<RoomMemberInfo> Members);

/// <summary>One member in the management roster: their address, admin status, and presence.</summary>
public sealed record RoomMemberInfo(string Address, bool IsAdmin, bool Online);

/// <summary>
/// What the member knows about a room it owns: the host base address admin ops go
/// to, the room's name on that host, and whether removing a member (allowlist
/// removal) is even possible (invite rooms only). Absent when the member merely
/// joined by address — then no owner controls show.
/// </summary>
public sealed record AdminContext(string HostBase, string RoomName, bool CanRemove);
