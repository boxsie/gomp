using Gomp.App.Services;
using Gomp.App.ViewModels;
using Gomp.Client;
using Gomp.Protocol;
using Xunit;

namespace Gomp.App.Tests;

public sealed class MainWindowViewModelTests
{
    private static MainWindowViewModel New(FakeGateway gateway) =>
        new(gateway, new ImmediateDispatcher());

    [Fact]
    public async Task Connect_Success_SetsConnectedAndSelf()
    {
        var gw = new FakeGateway("Eme");
        var vm = New(gw);

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.True(vm.IsConnected);
        Assert.False(vm.IsConnecting);
        Assert.Equal("Eme", vm.SelfAddress);
        Assert.Null(vm.ConnectError);
    }

    [Fact]
    public async Task Connect_Failure_SurfacesError()
    {
        var gw = new FakeGateway { FailConnect = true, ConnectError = "socket missing" };
        var vm = New(gw);

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.False(vm.IsConnected);
        Assert.Equal("socket missing", vm.ConnectError);
    }

    [Fact]
    public async Task Join_AddsRoom_SelectsIt_AndAttachesHandle()
    {
        var gw = new FakeGateway();
        var vm = New(gw);
        await vm.ConnectCommand.ExecuteAsync(null);

        vm.JoinAddress = "Eroom1";
        await vm.ConfirmJoinCommand.ExecuteAsync(null);

        var room = Assert.Single(vm.Rooms);
        Assert.Equal("Eroom1", room.Address);
        Assert.Same(room, vm.SelectedRoom);
        Assert.True(vm.HasSelection);
        Assert.False(vm.IsJoinOpen);
        Assert.Equal("Eroom1", Assert.Single(gw.Joined).Address);

        // The handle is wired: a post pushed by the "host" lands in the room.
        await ((IRoomObserver)room).OnPostAsync(Posts.Post("Epeer", "hello"));
        Assert.Single(room.Messages);
    }

    [Fact]
    public async Task Join_EmptyAddress_ShowsDialogError()
    {
        var gw = new FakeGateway();
        var vm = New(gw);
        await vm.ConnectCommand.ExecuteAsync(null);

        vm.ShowJoinCommand.Execute(null);
        vm.JoinAddress = "   ";
        await vm.ConfirmJoinCommand.ExecuteAsync(null);

        Assert.True(vm.IsJoinOpen);
        Assert.NotNull(vm.DialogError);
        Assert.Empty(vm.Rooms);
    }

    [Fact]
    public async Task Join_Duplicate_SelectsExistingInsteadOfRejoining()
    {
        var gw = new FakeGateway();
        var vm = New(gw);
        await vm.ConnectCommand.ExecuteAsync(null);

        vm.JoinAddress = "Eroom1";
        await vm.ConfirmJoinCommand.ExecuteAsync(null);
        vm.ShowJoinCommand.Execute(null);
        vm.JoinAddress = "Eroom1";
        await vm.ConfirmJoinCommand.ExecuteAsync(null);

        Assert.Single(vm.Rooms);
        Assert.Single(gw.Joined);
    }

    [Fact]
    public async Task Create_Invite_JoinsAsOwner_WithRemovableMembers()
    {
        var gw = new FakeGateway();
        gw.OnCreate = (host, name, kind) =>
            new AdminResult(true, null, new[] { new RoomSummary(name, "Eroom-new", kind) });
        var vm = New(gw);
        await vm.ConnectCommand.ExecuteAsync(null);

        vm.ShowCreateCommand.Execute(null);
        vm.CreateHost = "Ehost";
        vm.CreateName = "snug";
        vm.CreateKind = RoomKind.Invite;
        await vm.ConfirmCreateCommand.ExecuteAsync(null);

        var room = Assert.Single(vm.Rooms);
        Assert.Equal("Eroom-new", room.Address);
        Assert.True(room.IsOwner);
        Assert.Equal("snug", room.Title);
        Assert.False(vm.IsCreateOpen);

        await ((IRoomObserver)room).OnRosterAsync(new[] { Posts.Member("Epeer") });
        Assert.True(room.Members.Single().CanRemove);
    }

    [Fact]
    public async Task Create_HostRefuses_KeepsDialogOpenWithError()
    {
        var gw = new FakeGateway { };
        gw.OnCreate = (_, _, _) => new AdminResult(false, "room_exists", Array.Empty<RoomSummary>());
        var vm = New(gw);
        await vm.ConnectCommand.ExecuteAsync(null);

        vm.ShowCreateCommand.Execute(null);
        vm.CreateHost = "Ehost";
        vm.CreateName = "snug";
        await vm.ConfirmCreateCommand.ExecuteAsync(null);

        Assert.True(vm.IsCreateOpen);
        Assert.Equal("room_exists", vm.DialogError);
        Assert.Empty(vm.Rooms);
    }

    [Fact]
    public async Task Remove_CallsGateway_AndPostsSystemNotice()
    {
        var gw = new FakeGateway();
        gw.OnCreate = (_, name, kind) => new AdminResult(true, null, new[] { new RoomSummary(name, "Eroom-new", kind) });
        var vm = New(gw);
        await vm.ConnectCommand.ExecuteAsync(null);

        vm.ShowCreateCommand.Execute(null);
        vm.CreateHost = "Ehost";
        vm.CreateName = "snug";
        vm.CreateKind = RoomKind.Invite;
        await vm.ConfirmCreateCommand.ExecuteAsync(null);

        var room = vm.SelectedRoom!;
        await ((IRoomObserver)room).OnRosterAsync(new[] { Posts.Member("Epeer") });
        var peer = room.Members.Single();

        await peer.RemoveCommand.ExecuteAsync(null);

        var call = Assert.Single(gw.Removed);
        Assert.Equal(("Ehost", "snug", "Epeer"), call);
        Assert.Contains(room.Messages, m => m.IsSystem && m.Content.Contains("removed"));
    }

    [Fact]
    public async Task Leave_DropsRoom_AndReselects()
    {
        var gw = new FakeGateway();
        var vm = New(gw);
        await vm.ConnectCommand.ExecuteAsync(null);

        vm.JoinAddress = "Eroom1";
        await vm.ConfirmJoinCommand.ExecuteAsync(null);
        vm.ShowJoinCommand.Execute(null);
        vm.JoinAddress = "Eroom2";
        await vm.ConfirmJoinCommand.ExecuteAsync(null);

        var second = vm.SelectedRoom!;
        await second.LeaveCommand.ExecuteAsync(null);

        Assert.Equal("Eroom2", Assert.Single(gw.Left));
        Assert.Single(vm.Rooms);
        Assert.Equal("Eroom1", vm.SelectedRoom!.Address);
    }
}
