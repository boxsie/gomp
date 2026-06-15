using Gomp.App.Services;
using Gomp.App.ViewModels;
using Gomp.Client;
using Gomp.Protocol;
using Xunit;

namespace Gomp.App.Tests;

public sealed class RoomViewModelTests
{
    private const string Self = "Eself";
    private const string Room = "Eroom";

    private static (RoomViewModel room, FakeRoomHandle handle) NewRoom(
        RoomKind kind = RoomKind.Open, AdminContext? admin = null,
        Func<RoomViewModel, MemberViewModel, Task>? remove = null)
    {
        var room = new RoomViewModel(Room, kind, Self, new ImmediateDispatcher(), admin, remove);
        var handle = new FakeRoomHandle(Room, room);
        room.Attach(handle);
        return (room, handle);
    }

    private static IRoomObserver Obs(RoomViewModel room) => room;

    [Fact]
    public async Task Post_AppendsMessage_AndFlagsSelf()
    {
        var (room, _) = NewRoom();
        await Obs(room).OnPostAsync(Posts.Post("Epeer", "evening", 1, PostTrust.Verified));
        await Obs(room).OnPostAsync(Posts.Post(Self, "evening all", 2, PostTrust.Verified));

        Assert.Equal(2, room.Messages.Count);
        Assert.False(room.Messages[0].IsSelf);
        Assert.True(room.Messages[1].IsSelf);
        Assert.True(room.IsLit);
    }

    [Fact]
    public async Task Posts_KeptInSequenceOrder_EvenWhenInterleaved()
    {
        var (room, _) = NewRoom();
        await Obs(room).OnPostAsync(Posts.Post("Epeer", "three", 3));
        await Obs(room).OnPostAsync(Posts.Post("Epeer", "one", 1));
        await Obs(room).OnPostAsync(Posts.Post("Epeer", "two", 2));

        Assert.Equal(new[] { "one", "two", "three" }, room.Messages.Select(m => m.Content));
    }

    [Fact]
    public async Task Post_SameSeqTwice_IsIgnored()
    {
        var (room, _) = NewRoom();
        await Obs(room).OnPostAsync(Posts.Post("Epeer", "once", 5));
        await Obs(room).OnPostAsync(Posts.Post("Epeer", "echo", 5));
        Assert.Single(room.Messages);
    }

    [Fact]
    public async Task ForgedPost_RaisesPersistentFlag()
    {
        var (room, _) = NewRoom();
        await Obs(room).OnPostAsync(Posts.Post("Epeer", "fake", 1, PostTrust.Forged));
        Assert.True(room.SawForgery);
    }

    [Fact]
    public async Task Roster_PopulatesMembers_CountsOnline_AndPrimesOnce()
    {
        var (room, handle) = NewRoom();
        await Obs(room).OnRosterAsync(new[]
        {
            Posts.Member("Epeer", online: true),
            Posts.Member(Self, online: true),
            Posts.Member("Eoff", online: false),
        });

        Assert.Equal(3, room.Members.Count);
        Assert.Equal(2, room.OnlineCount);
        Assert.True(room.HasOnline);
        Assert.True(room.Members.Single(m => m.Address == Self).IsSelf);
        Assert.Equal(1, handle.RosterRequests); // primed exactly once on first signal

        await Obs(room).OnPresenceAsync(Posts.Member("Enew", online: true));
        Assert.Equal(1, handle.RosterRequests); // not re-primed
    }

    [Fact]
    public async Task Presence_FlipsExistingMember_AndAddsUnknown()
    {
        var (room, _) = NewRoom();
        await Obs(room).OnRosterAsync(new[] { Posts.Member("Epeer", online: true) });
        await Obs(room).OnPresenceAsync(Posts.Member("Epeer", online: false));
        Assert.False(room.Members.Single(m => m.Address == "Epeer").Online);
        Assert.Equal(0, room.OnlineCount);

        await Obs(room).OnPresenceAsync(Posts.Member("Elate", online: true));
        Assert.Equal(2, room.Members.Count);
        Assert.Equal(1, room.OnlineCount);
    }

    [Fact]
    public async Task Send_ForwardsText_AndClearsDraft()
    {
        var (room, handle) = NewRoom();
        room.Draft = "  oi oi  ";
        await room.SendCommand.ExecuteAsync(null);

        Assert.Equal("oi oi", Assert.Single(handle.Sent));
        Assert.Equal("", room.Draft);
    }

    [Fact]
    public async Task Send_EmptyDraft_DoesNothing()
    {
        var (room, handle) = NewRoom();
        room.Draft = "   ";
        await room.SendCommand.ExecuteAsync(null);
        Assert.Empty(handle.Sent);
    }

    [Fact]
    public async Task Error_SurfacesAsSystemLine()
    {
        var (room, _) = NewRoom();
        await Obs(room).OnErrorAsync("sender_mismatch", "nope");
        var line = Assert.Single(room.Messages);
        Assert.True(line.IsSystem);
        Assert.Contains("sender_mismatch", line.Content);
    }

    [Fact]
    public async Task OwnedInviteRoom_LetsYouRemoveOthersButNotYourself()
    {
        MemberViewModel? removed = null;
        var admin = new AdminContext("Ehost", "snug", CanRemove: true);
        var (room, _) = NewRoom(RoomKind.Invite, admin, remove: (_, m) => { removed = m; return Task.CompletedTask; });

        await Obs(room).OnRosterAsync(new[] { Posts.Member("Epeer"), Posts.Member(Self) });

        var peer = room.Members.Single(m => m.Address == "Epeer");
        var me = room.Members.Single(m => m.Address == Self);
        Assert.True(peer.CanRemove);
        Assert.False(me.CanRemove);

        await peer.RemoveCommand.ExecuteAsync(null);
        Assert.Same(peer, removed);
    }
}
