using Gomp.Protocol;

namespace Gomp.App.Services;

/// <summary>One room as the host describes it in an admin response (the fields
/// the app consumes today — a future room browser can carry counts too).</summary>
public sealed record RoomSummary(string Name, string Address, RoomKind Kind);

/// <summary>The flattened outcome of an admin op — the proto <c>AdminResponse</c>
/// reshaped into something the UI (and tests) can hold without protobuf.</summary>
public sealed record AdminResult(bool Ok, string? Error, IReadOnlyList<RoomSummary> Rooms)
{
    public static AdminResult Fail(string error) => new(false, error, Array.Empty<RoomSummary>());
}

/// <summary>
/// What the member knows about a room it owns: the host base address admin ops go
/// to, the room's name on that host, and whether removing a member (allowlist
/// removal) is even possible (invite rooms only). Absent when the member merely
/// joined by address — then no owner controls show.
/// </summary>
public sealed record AdminContext(string HostBase, string RoomName, bool CanRemove);
