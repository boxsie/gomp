using Microsoft.Extensions.Logging.Abstractions;
using Gomp.Host;
using Gomp.Protocol;
using Xunit;

namespace Gomp.Host.Tests;

public sealed class RoomTests
{
    private const string RoomAddr = "Eroom";

    private static (Room room, FakeTransport tx) NewRoom(RoomKind kind = RoomKind.Open)
    {
        var tx = new FakeTransport(RoomAddr);
        var store = RoomStore.Open(Directory.CreateTempSubdirectory("room").FullName, "r", 100);
        var room = new Room("r", kind, tx, store, NullLogger.Instance);
        return (room, tx);
    }

    [Fact]
    public async Task Submit_FansOutSequencedMessage_ToAllOnline()
    {
        var (room, tx) = NewRoom();
        await room.OnConnectionEstablishedAsync("A");
        await room.OnConnectionEstablishedAsync("B");
        tx.Sent.Clear();

        await room.OnRpcMessageAsync("A", Posts.Submit(Posts.Make("A", RoomAddr, "hello")));

        // Both A (as seq confirmation) and B receive the fanned-out message.
        var toA = tx.MessagesTo("A");
        var toB = tx.MessagesTo("B");
        Assert.Single(toA);
        Assert.Single(toB);
        Assert.Equal(1, toA[0].Message.Seq);
        Assert.Equal(1, toB[0].Message.Seq);
        // The signed post is relayed verbatim (same bytes the member submitted).
        Assert.Equal("opaque-sig", toB[0].Message.Post.Sig.ToStringUtf8());
    }

    [Fact]
    public async Task Submit_SenderMismatch_Rejected_NoFanout()
    {
        var (room, tx) = NewRoom();
        await room.OnConnectionEstablishedAsync("A");
        await room.OnConnectionEstablishedAsync("B");
        tx.Sent.Clear();

        // A posts claiming to be B — the host's anti-spoof check rejects it.
        await room.OnRpcMessageAsync("A", Posts.Submit(Posts.Make("B", RoomAddr, "spoof")));

        Assert.Empty(tx.MessagesTo("B"));
        var err = Assert.Single(tx.EnvelopesTo("A"));
        Assert.Equal(RoomEnvelope.BodyOneofCase.Error, err.BodyCase);
        Assert.Equal("sender_mismatch", err.Error.Code);
    }

    [Fact]
    public async Task Submit_WrongRoom_Rejected()
    {
        var (room, tx) = NewRoom();
        await room.OnConnectionEstablishedAsync("A");
        tx.Sent.Clear();

        await room.OnRpcMessageAsync("A", Posts.Submit(Posts.Make("A", "EotherRoom", "x")));

        var err = Assert.Single(tx.EnvelopesTo("A"));
        Assert.Equal("wrong_room", err.Error.Code);
    }

    [Fact]
    public async Task Join_SendsRosterToJoiner_AndPresenceToOthers()
    {
        var (room, tx) = NewRoom();
        await room.OnConnectionEstablishedAsync("A");
        tx.Sent.Clear();

        await room.OnConnectionEstablishedAsync("B");

        // B (the joiner) gets a roster snapshot.
        Assert.Contains(tx.EnvelopesTo("B"), e => e.BodyCase == RoomEnvelope.BodyOneofCase.Roster);
        // A (already present) gets a presence announcement for B.
        var pres = Assert.Single(tx.EnvelopesTo("A"), e => e.BodyCase == RoomEnvelope.BodyOneofCase.Presence);
        Assert.Equal("B", pres.Presence.Addr);
        Assert.True(pres.Presence.Online);
    }

    [Fact]
    public async Task Leave_AnnouncesOffline_AndDropsFromRoster()
    {
        var (room, tx) = NewRoom();
        await room.OnConnectionEstablishedAsync("A");
        await room.OnConnectionEstablishedAsync("B");
        tx.Sent.Clear();

        await room.OnConnectionClosedAsync("B");

        Assert.Equal(1, room.OnlineCount);
        var pres = Assert.Single(tx.EnvelopesTo("A"), e => e.BodyCase == RoomEnvelope.BodyOneofCase.Presence);
        Assert.Equal("B", pres.Presence.Addr);
        Assert.False(pres.Presence.Online);
    }

    [Fact]
    public async Task RosterRequest_ReturnsCurrentMembers()
    {
        var (room, tx) = NewRoom();
        await room.OnConnectionEstablishedAsync("A");
        await room.OnConnectionEstablishedAsync("B");
        tx.Sent.Clear();

        await room.OnRpcMessageAsync("A", Posts.RosterReq());

        var roster = Assert.Single(tx.EnvelopesTo("A"), e => e.BodyCase == RoomEnvelope.BodyOneofCase.Roster);
        Assert.Equal(2, roster.Roster.Members.Count);
        Assert.Contains(roster.Roster.Members, m => m.Addr == "A");
        Assert.Contains(roster.Roster.Members, m => m.Addr == "B");
    }

    [Fact]
    public async Task Backfill_ReturnsHistory()
    {
        var (room, tx) = NewRoom();
        await room.OnConnectionEstablishedAsync("A");
        await room.OnRpcMessageAsync("A", Posts.Submit(Posts.Make("A", RoomAddr, "one")));
        await room.OnRpcMessageAsync("A", Posts.Submit(Posts.Make("A", RoomAddr, "two", "n2")));
        tx.Sent.Clear();

        await room.OnRpcMessageAsync("A", Posts.Backfill(0));

        var resp = Assert.Single(tx.EnvelopesTo("A"), e => e.BodyCase == RoomEnvelope.BodyOneofCase.BackfillResp);
        Assert.Equal(2, resp.BackfillResp.Messages.Count);
        Assert.True(resp.BackfillResp.Complete);
    }

    [Fact]
    public async Task RemoveMember_Invite_RevokesThenDisconnects()
    {
        var (room, tx) = NewRoom(RoomKind.Invite);
        await room.RemoveMemberAsync("M");
        Assert.Contains("M", tx.Removed);
        Assert.Contains("M", tx.Disconnected);
    }

    [Fact]
    public async Task AddMember_Invite_GrantsAllowlist()
    {
        var (room, tx) = NewRoom(RoomKind.Invite);
        await room.AddMemberAsync("M");
        Assert.Contains("M", tx.Added);
    }
}
