using System.Text;
using Gomp.Protocol;
using Xunit;

namespace Gomp.Client.Tests;

public sealed class GompClientTests
{
    private const string Host = "Ehost";
    private const string Room = "Eroom";

    [Fact]
    public async Task CreateRoom_SendsAdminRequest_AndCorrelatesResponse()
    {
        var self = new FakeIdentity("Eself");
        var fake = new FakeTransport(self);
        await using var client = new GompClient(fake);

        // Dialers just send: the request goes out immediately (the daemon auto-dials).
        var task = client.CreateRoomAsync(Host, "pub", RoomKind.Open);

        var req = Assert.Single(
            fake.AdminEnvelopesTo(Host), e => e.BodyCase == AdminEnvelope.BodyOneofCase.Request).Request;
        Assert.Equal(AdminRequest.OpOneofCase.CreateRoom, req.OpCase);
        Assert.Equal("pub", req.CreateRoom.Name);
        Assert.Equal(RoomKind.Open, req.CreateRoom.Kind);

        await fake.RaiseMessageAsync(Host, Wire.AdminResponse(
            req.RequestId, ok: true, new RoomInfo { Name = "pub", Addr = Room, Kind = RoomKind.Open }));

        var resp = await task;
        Assert.True(resp.Ok);
        Assert.Equal(Room, Assert.Single(resp.Rooms).Addr);
    }

    [Fact]
    public async Task Admin_IgnoresResponseWithUnknownRequestId()
    {
        var self = new FakeIdentity("Eself");
        var fake = new FakeTransport(self);
        await using var client = new GompClient(fake);

        var task = client.CloseRoomAsync(Host, "pub");
        var req = Assert.Single(fake.AdminEnvelopesTo(Host), e => e.BodyCase == AdminEnvelope.BodyOneofCase.Request).Request;

        // A stale/mismatched response must not complete the pending request.
        await fake.RaiseMessageAsync(Host, Wire.AdminResponse("not-the-id", ok: true));
        Assert.False(task.IsCompleted);

        await fake.RaiseMessageAsync(Host, Wire.AdminResponse(req.RequestId, ok: true));
        var resp = await task;
        Assert.True(resp.Ok);
    }

    [Fact]
    public async Task JoinRoom_AnnouncesHelloImmediately()
    {
        var self = new FakeIdentity("Eself");
        var fake = new FakeTransport(self);
        await using var client = new GompClient(fake);

        // A dialer announces eagerly: Hello goes out on join — the first room-ops
        // send auto-dials the room — without waiting for a connection_established
        // event (which the daemon never emits to a pure dialer).
        await client.JoinRoomAsync(Room, new RecordingObserver());

        Assert.Contains(fake.RoomEnvelopesTo(Room), e => e.BodyCase == RoomEnvelope.BodyOneofCase.Hello);
    }

    [Fact]
    public async Task Routing_RoomMessage_ReachesThatRoomsObserver()
    {
        var self = new FakeIdentity("Eself");
        var peer = new FakeIdentity("Epeer");
        var fake = new FakeTransport(self, peer);
        await using var client = new GompClient(fake);

        var obs = new RecordingObserver();
        await client.JoinRoomAsync(Room, obs);

        await fake.RaiseMessageAsync(Room, Wire.Message(Wire.SignedPost(peer, Room, "hey"), seq: 7));

        var post = Assert.Single(obs.Posts);
        Assert.Equal("hey", Encoding.UTF8.GetString(post.Content));
        Assert.Equal(7, post.Seq);
        Assert.Equal(1, post.HostTs);
        Assert.Equal(1, post.ClientTs);
        Assert.Equal("n", post.Nonce);
    }

    [Fact]
    public async Task Reconnect_ReannouncesHello()
    {
        var self = new FakeIdentity("Eself");
        var fake = new FakeTransport(self);
        await using var client = new GompClient(fake);

        await client.JoinRoomAsync(Room, new RecordingObserver());   // eager join → Hello #1
        await fake.RaiseDisconnectedAsync(Room);       // transient drop
        await fake.RaiseConnectedAsync(Room);          // re-establish → Hello #2

        var hellos = fake.RoomEnvelopesTo(Room).Count(e => e.BodyCase == RoomEnvelope.BodyOneofCase.Hello);
        Assert.Equal(2, hellos);
        // History is only pulled once, not on every reconnect.
        Assert.Equal(1, fake.RoomEnvelopesTo(Room).Count(e => e.BodyCase == RoomEnvelope.BodyOneofCase.BackfillReq));
    }

    [Fact]
    public async Task JoinRoom_Twice_Throws()
    {
        var self = new FakeIdentity("Eself");
        var fake = new FakeTransport(self);
        await using var client = new GompClient(fake);

        await client.JoinRoomAsync(Room, new RecordingObserver());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.JoinRoomAsync(Room, new RecordingObserver()));
    }

    [Fact]
    public async Task LeaveRoom_DisconnectsAndForgets()
    {
        var self = new FakeIdentity("Eself");
        var fake = new FakeTransport(self);
        await using var client = new GompClient(fake);

        await client.JoinRoomAsync(Room, new RecordingObserver());
        await client.LeaveRoomAsync(Room);

        Assert.Contains(Room, fake.Disconnected);
        // A fresh join is allowed again after leaving.
        await client.JoinRoomAsync(Room, new RecordingObserver());
    }
}
