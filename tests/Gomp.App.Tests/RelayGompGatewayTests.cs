using System.Text;
using Google.Protobuf;
using Gomp.App.Services;
using Gomp.Client;
using Gomp.Protocol;
using Xunit;
using Gw = Gomp.Protocol.Gateway;

namespace Gomp.App.Tests;

/// <summary>
/// Records frontend→backend payloads and lets a test push backend→frontend
/// events, standing in for the live AttachService relay so
/// <see cref="RelayGompGateway"/> is exercised with no daemon.
/// </summary>
internal sealed class FakeBackendRelay : IBackendRelay
{
    public string ServiceAddress { get; set; } = "Ebackend00000000000000000000000000000";
    public List<byte[]> Sent { get; } = new();
    public bool Disposed { get; private set; }

    public Task SendAsync(byte[] payload, CancellationToken ct = default)
    {
        Sent.Add(payload);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    public Gw.FeRequest Last() => Gw.FeRequest.Parser.ParseFrom(Sent[^1]);
}

/// <summary>
/// A recording <see cref="IRoomObserver"/>: captures everything the gateway fans
/// out for a joined room.
/// </summary>
internal sealed class RecordingObserver : IRoomObserver
{
    public List<ReceivedPost> Posts { get; } = new();
    public List<RoomMemberPresence> Presences { get; } = new();
    public List<IReadOnlyList<RoomMemberPresence>> Rosters { get; } = new();
    public List<(string Code, string Message)> Errors { get; } = new();

    public Task OnPostAsync(ReceivedPost post) { Posts.Add(post); return Task.CompletedTask; }
    public Task OnPresenceAsync(RoomMemberPresence p) { Presences.Add(p); return Task.CompletedTask; }
    public Task OnRosterAsync(IReadOnlyList<RoomMemberPresence> m) { Rosters.Add(m); return Task.CompletedTask; }
    public Task OnErrorAsync(string code, string message) { Errors.Add((code, message)); return Task.CompletedTask; }
}

public sealed class RelayGompGatewayTests
{
    private const string Room = "Eroom0000000000000000000000000000000";

    [Fact]
    public void SelfAddress_ComesFromRelay()
    {
        var relay = new FakeBackendRelay { ServiceAddress = "Eme000000000000000000000000000000000" };
        var gw = new RelayGompGateway(relay);
        Assert.Equal("Eme000000000000000000000000000000000", gw.SelfAddress);
    }

    [Fact]
    public async Task Join_SendsJoinRequest_AndRoutesPostToObserver()
    {
        var relay = new FakeBackendRelay();
        var gw = new RelayGompGateway(relay);
        var observer = new RecordingObserver();

        await gw.JoinAsync(Room, observer, requestHistory: true);

        var req = relay.Last();
        Assert.Equal(Gw.FeRequest.BodyOneofCase.Join, req.BodyCase);
        Assert.Equal(Room, req.Join.RoomAddress);
        Assert.True(req.Join.RequestHistory);

        // The backend relays a verified post for that room.
        await gw.OnBackendPayloadAsync(new Gw.BeEvent
        {
            Post = new Gw.Post
            {
                RoomAddress = Room,
                Seq = 7,
                HostTs = 111,
                Sender = "Esender0000000000000000000000000000",
                Content = ByteString.CopyFromUtf8("hello pub"),
                ClientTs = 222,
                Nonce = "n1",
                Trust = Gw.PostTrust.Verified,
            },
        }.ToByteArray());

        var post = Assert.Single(observer.Posts);
        Assert.Equal(7, post.Seq);
        Assert.Equal("Esender0000000000000000000000000000", post.Sender);
        Assert.Equal("hello pub", Encoding.UTF8.GetString(post.Content));
        Assert.Equal(PostTrust.Verified, post.Trust);
    }

    [Fact]
    public async Task Post_ForUnjoinedRoom_IsDropped()
    {
        var relay = new FakeBackendRelay();
        var gw = new RelayGompGateway(relay);
        var observer = new RecordingObserver();
        await gw.JoinAsync(Room, observer);

        await gw.OnBackendPayloadAsync(new Gw.BeEvent
        {
            Post = new Gw.Post { RoomAddress = "Eother00000000000000000000000000000", Seq = 1 },
        }.ToByteArray());

        Assert.Empty(observer.Posts);
    }

    [Fact]
    public async Task Roster_Presence_Error_RouteToObserver()
    {
        var relay = new FakeBackendRelay();
        var gw = new RelayGompGateway(relay);
        var observer = new RecordingObserver();
        await gw.JoinAsync(Room, observer);

        await gw.OnBackendPayloadAsync(new Gw.BeEvent
        {
            Roster = new Gw.RosterSnap
            {
                RoomAddress = Room,
                Members = { new Gw.RosterMember { Addr = "Ea", Online = true }, new Gw.RosterMember { Addr = "Eb", Online = false } },
            },
        }.ToByteArray());
        await gw.OnBackendPayloadAsync(new Gw.BeEvent
        {
            Presence = new Gw.Presence { RoomAddress = Room, Addr = "Ec", Online = true },
        }.ToByteArray());
        await gw.OnBackendPayloadAsync(new Gw.BeEvent
        {
            RoomError = new Gw.RoomError { RoomAddress = Room, Code = "sender_mismatch", Message = "nope" },
        }.ToByteArray());

        Assert.Equal(2, Assert.Single(observer.Rosters).Count);
        Assert.Equal(("Ec", true), (observer.Presences[0].Addr, observer.Presences[0].Online));
        Assert.Equal(("sender_mismatch", "nope"), observer.Errors[0]);
    }

    [Fact]
    public async Task RoomHandle_Ops_SendTaggedRequests()
    {
        var relay = new FakeBackendRelay();
        var gw = new RelayGompGateway(relay);
        var handle = await gw.JoinAsync(Room, new RecordingObserver());

        await handle.SendChatAsync("oi oi");
        Assert.Equal(Gw.FeRequest.BodyOneofCase.SendChat, relay.Last().BodyCase);
        Assert.Equal("oi oi", relay.Last().SendChat.Text);

        await handle.RequestRosterAsync();
        Assert.Equal(Gw.FeRequest.BodyOneofCase.Roster, relay.Last().BodyCase);

        await handle.BackfillAsync(42);
        Assert.Equal(Gw.FeRequest.BodyOneofCase.Backfill, relay.Last().BodyCase);
        Assert.Equal(42, relay.Last().Backfill.SinceSeq);
    }

    [Fact]
    public async Task Leave_SendsLeave_AndStopsRouting()
    {
        var relay = new FakeBackendRelay();
        var gw = new RelayGompGateway(relay);
        var observer = new RecordingObserver();
        await gw.JoinAsync(Room, observer);

        await gw.LeaveAsync(Room);
        Assert.Equal(Gw.FeRequest.BodyOneofCase.Leave, relay.Last().BodyCase);

        // A post arriving after leave is no longer routed.
        await gw.OnBackendPayloadAsync(new Gw.BeEvent
        {
            Post = new Gw.Post { RoomAddress = Room, Seq = 1 },
        }.ToByteArray());
        Assert.Empty(observer.Posts);
    }

    [Fact]
    public async Task CreateRoom_CorrelatesAdminResultByRequestId()
    {
        var relay = new FakeBackendRelay();
        var gw = new RelayGompGateway(relay);

        // Issue the op; it sends synchronously then awaits the correlated result.
        var task = gw.CreateRoomAsync("Ehost000000000000000000000000000000", "pub", RoomKind.Open);

        var sent = relay.Last();
        Assert.Equal(Gw.FeRequest.BodyOneofCase.Admin, sent.BodyCase);
        Assert.Equal(Gw.AdminOp.OpOneofCase.Create, sent.Admin.OpCase);
        Assert.Equal("pub", sent.Admin.Create.Name);
        Assert.Equal(RoomKind.Open, sent.Admin.Create.Kind);
        Assert.Equal("Ehost000000000000000000000000000000", sent.Admin.HostBase);

        await gw.OnBackendPayloadAsync(new Gw.BeEvent
        {
            AdminResult = new Gw.AdminResult
            {
                RequestId = sent.Admin.RequestId,
                Ok = true,
                Rooms = { new Gw.RoomSummary { Name = "pub", Addr = Room, Kind = RoomKind.Open } },
            },
        }.ToByteArray());

        var result = await task;
        Assert.True(result.Ok);
        Assert.Null(result.Error);
        var room = Assert.Single(result.Rooms);
        Assert.Equal("pub", room.Name);
        Assert.Equal(Room, room.Address);
        Assert.Equal(RoomKind.Open, room.Kind);
    }

    [Fact]
    public async Task SelfHostCreate_SendsEmptyHostBase()
    {
        var relay = new FakeBackendRelay();
        var gw = new RelayGompGateway(relay);

        _ = gw.CreateRoomAsync("", "mypub", RoomKind.Invite, new[] { "Efriend" });

        var sent = relay.Last();
        Assert.Equal(string.Empty, sent.Admin.HostBase);
        Assert.Equal(RoomKind.Invite, sent.Admin.Create.Kind);
        Assert.Contains("Efriend", sent.Admin.Create.Members);
    }

    [Fact]
    public async Task Dispose_DisposesRelay_AndCancelsPendingAdmin()
    {
        var relay = new FakeBackendRelay();
        var gw = new RelayGompGateway(relay);

        var pending = gw.ListRoomsAsync("Ehost000000000000000000000000000000");
        await gw.DisposeAsync();

        Assert.True(relay.Disposed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }
}
