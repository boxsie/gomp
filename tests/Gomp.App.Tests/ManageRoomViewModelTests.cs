using Gomp.App.Services;
using Gomp.App.ViewModels;
using Gomp.Protocol;
using Xunit;

namespace Gomp.App.Tests;

public sealed class ManageRoomViewModelTests
{
    private const string Self = "Eself";

    private static RoomViewModel OwnedRoom(RoomKind kind = RoomKind.Invite) =>
        new("Eroom", kind, Self, new ImmediateDispatcher(),
            new AdminContext(Self, "snug", CanRemove: kind == RoomKind.Invite));

    private static ManageRoomViewModel New(
        FakeGateway gw, RoomViewModel room,
        Func<RoomViewModel, Task>? close = null, Action? dismiss = null) =>
        new(gw, room, close ?? (_ => Task.CompletedTask), dismiss ?? (() => { }));

    private static FakeGateway WithDetail(RoomKind kind, params RoomMemberInfo[] members)
    {
        var gw = new FakeGateway(Self);
        gw.DetailResult = new Gomp.App.Services.RoomDetail(kind, "The Snug", "after hours", 250, members);
        return gw;
    }

    [Fact]
    public async Task Load_PopulatesSettingsAndMembers_AndSyncsCard()
    {
        var gw = WithDetail(RoomKind.Friends,
            new RoomMemberInfo(Self, IsAdmin: true, Online: true),
            new RoomMemberInfo("Ebob", IsAdmin: false, Online: true));
        var room = OwnedRoom();
        var vm = New(gw, room);

        await vm.LoadAsync();

        Assert.Equal(RoomKind.Friends, vm.SelectedKind);
        Assert.Equal("The Snug", vm.DisplayName);
        Assert.Equal("after hours", vm.Topic);
        Assert.Equal(250, vm.RetentionMax);
        Assert.Equal(2, vm.Members.Count);

        // The room card follows the authoritative host state.
        Assert.Equal(RoomKind.Friends, room.Kind);
        Assert.Equal("The Snug", room.Title);

        var me = vm.Members.First(m => m.Address == Self);
        Assert.True(me.IsSelf);
        Assert.True(me.IsAdmin);
        Assert.False(me.CanRemove);        // never act on yourself
        Assert.False(me.CanPromote);

        var bob = vm.Members.First(m => m.Address == "Ebob");
        Assert.True(bob.CanPromote);       // non-admin, not self
        Assert.False(bob.CanDemote);
        Assert.True(bob.CanRemove);
    }

    [Fact]
    public async Task ApplyVisibility_SendsSelectedKind()
    {
        var gw = WithDetail(RoomKind.Invite);
        var vm = New(gw, OwnedRoom());
        await vm.LoadAsync();

        vm.SelectedKind = RoomKind.Open;
        await vm.ApplyVisibilityCommand.ExecuteAsync(null);

        Assert.Contains(gw.KindSet, x => x is { Room: "snug", Kind: RoomKind.Open });
    }

    [Fact]
    public async Task AddMember_TrimsAndSends_ThenClears()
    {
        var gw = WithDetail(RoomKind.Invite);
        var vm = New(gw, OwnedRoom());

        vm.NewMemberAddress = "  Ecarol  ";
        await vm.AddMemberCommand.ExecuteAsync(null);

        Assert.Contains(gw.Added, x => x is { Room: "snug", Addr: "Ecarol" });
        Assert.Equal("", vm.NewMemberAddress);
    }

    [Fact]
    public async Task AddMember_Empty_DoesNotSend()
    {
        var gw = WithDetail(RoomKind.Invite);
        var vm = New(gw, OwnedRoom());

        vm.NewMemberAddress = "   ";
        await vm.AddMemberCommand.ExecuteAsync(null);

        Assert.Empty(gw.Added);
        Assert.False(string.IsNullOrEmpty(vm.Status));
    }

    [Fact]
    public async Task SaveSettings_SendsTrimmedDisplayTopicAndRetention()
    {
        var gw = WithDetail(RoomKind.Invite);
        var vm = New(gw, OwnedRoom());

        vm.DisplayName = "  Snuggery  ";
        vm.Topic = "  lock-in  ";
        vm.RetentionMax = 42;
        await vm.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Contains(gw.Updated, x => x is { Room: "snug", Display: "Snuggery", Topic: "lock-in", Retention: 42 });
    }

    [Fact]
    public async Task ClearHistory_SendsClear()
    {
        var gw = WithDetail(RoomKind.Invite);
        var vm = New(gw, OwnedRoom());

        await vm.ClearHistoryCommand.ExecuteAsync(null);

        Assert.Contains(gw.Histories, x => x.Room == "snug");
    }

    [Fact]
    public async Task Member_PromoteDemoteRemove_HitGateway()
    {
        var gw = WithDetail(RoomKind.Invite,
            new RoomMemberInfo("Ebob", IsAdmin: false, Online: true));
        var vm = New(gw, OwnedRoom());
        await vm.LoadAsync();

        var bob = vm.Members.Single(m => m.Address == "Ebob");
        await bob.PromoteCommand.ExecuteAsync(null);
        await bob.RemoveCommand.ExecuteAsync(null);

        Assert.Contains(gw.Promoted, x => x.Addr == "Ebob");
        Assert.Contains(gw.Removed, x => x.Addr == "Ebob");
    }

    [Fact]
    public async Task CloseRoom_DismissesThenCloses()
    {
        var gw = WithDetail(RoomKind.Invite);
        var room = OwnedRoom();
        var dismissed = false;
        RoomViewModel? closed = null;
        var vm = New(gw, room, close: r => { closed = r; return Task.CompletedTask; }, dismiss: () => dismissed = true);

        await vm.CloseRoomCommand.ExecuteAsync(null);

        Assert.True(dismissed);
        Assert.Same(room, closed);
    }

    [Fact]
    public void Done_Dismisses()
    {
        var dismissed = false;
        var vm = New(WithDetail(RoomKind.Invite), OwnedRoom(), dismiss: () => dismissed = true);

        vm.DoneCommand.Execute(null);

        Assert.True(dismissed);
    }
}
