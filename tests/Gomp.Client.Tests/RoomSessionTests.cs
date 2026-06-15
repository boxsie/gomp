using System.Text;
using Gomp.Protocol;
using Xunit;

namespace Gomp.Client.Tests;

public sealed class RoomSessionTests
{
    private const string Room = "Eroom";

    private static RoomSession NewSession(
        FakeTransport fake, FakeIdentity self, RecordingObserver obs, bool history = true) =>
        new(Room, fake, self.ToBinding(), obs, history);

    [Fact]
    public async Task OnConnected_AnnouncesHello_AndRequestsHistory()
    {
        var self = new FakeIdentity("Eself");
        var fake = new FakeTransport(self);
        var session = NewSession(fake, self, new RecordingObserver());

        await session.OnConnectedAsync();

        var envs = fake.RoomEnvelopesTo(Room);
        var hello = Assert.Single(envs, e => e.BodyCase == RoomEnvelope.BodyOneofCase.Hello);
        Assert.Equal("binding:Eself", hello.Hello.Binding.Binding.ToStringUtf8());
        Assert.Contains(envs, e => e.BodyCase == RoomEnvelope.BodyOneofCase.BackfillReq && e.BackfillReq.SinceSeq == 0);
    }

    [Fact]
    public async Task OnConnected_Twice_RequestsHistoryOnce_ButReannouncesHello()
    {
        var self = new FakeIdentity("Eself");
        var fake = new FakeTransport(self);
        var session = NewSession(fake, self, new RecordingObserver());

        await session.OnConnectedAsync();
        await session.OnConnectedAsync(); // a reconnect

        var envs = fake.RoomEnvelopesTo(Room);
        Assert.Equal(2, envs.Count(e => e.BodyCase == RoomEnvelope.BodyOneofCase.Hello));
        Assert.Equal(1, envs.Count(e => e.BodyCase == RoomEnvelope.BodyOneofCase.BackfillReq));
    }

    [Fact]
    public async Task SendChat_SubmitsSignedPostForThisRoom()
    {
        var self = new FakeIdentity("Eself");
        var fake = new FakeTransport(self);
        var session = NewSession(fake, self, new RecordingObserver());

        await session.SendChatAsync("evening all");

        var submit = Assert.Single(fake.RoomEnvelopesTo(Room), e => e.BodyCase == RoomEnvelope.BodyOneofCase.Submit);
        var body = SignedPostBody.Parser.ParseFrom(submit.Submit.Post.Body);
        Assert.Equal("Eself", body.Sender);
        Assert.Equal(Room, body.Room);
        Assert.Equal("evening all", body.Content.ToStringUtf8());
    }

    [Fact]
    public async Task Inbound_Message_HostTrusted_WhenNoBindingYet()
    {
        var self = new FakeIdentity("Eself");
        var peer = new FakeIdentity("Epeer");
        var obs = new RecordingObserver();
        var session = NewSession(new FakeTransport(self, peer), self, obs);

        await session.OnRpcMessageAsync(Wire.Message(Wire.SignedPost(peer, Room, "hi")));

        var post = Assert.Single(obs.Posts);
        Assert.Equal("Epeer", post.Sender);
        Assert.Equal("hi", Encoding.UTF8.GetString(post.Content));
        Assert.Equal(PostTrust.Unverified, post.Trust);
    }

    [Fact]
    public async Task Inbound_Roster_LearnsBindings_ThenPostsVerify()
    {
        var self = new FakeIdentity("Eself");
        var peer = new FakeIdentity("Epeer");
        var obs = new RecordingObserver();
        var session = NewSession(new FakeTransport(self, peer), self, obs);

        await session.OnRpcMessageAsync(Wire.Roster((peer, true)));
        var roster = Assert.Single(obs.Rosters);
        Assert.Contains(roster, m => m.Addr == "Epeer" && m.Online);
        Assert.True(session.Directory.Knows("Epeer"));

        await session.OnRpcMessageAsync(Wire.Message(Wire.SignedPost(peer, Room, "after roster")));
        Assert.Equal(PostTrust.Verified, obs.Posts.Single().Trust);
    }

    [Fact]
    public async Task Inbound_Presence_Online_LearnsBinding()
    {
        var self = new FakeIdentity("Eself");
        var peer = new FakeIdentity("Epeer");
        var obs = new RecordingObserver();
        var session = NewSession(new FakeTransport(self, peer), self, obs);

        await session.OnRpcMessageAsync(Wire.Presence(peer, online: true));

        Assert.Contains(obs.Presence, m => m.Addr == "Epeer" && m.Online);
        Assert.True(session.Directory.Knows("Epeer"));
    }

    [Fact]
    public async Task Inbound_Backfill_SurfacesAllPosts_Verified()
    {
        var self = new FakeIdentity("Eself");
        var peer = new FakeIdentity("Epeer");
        var obs = new RecordingObserver();
        var session = NewSession(new FakeTransport(self, peer), self, obs);

        await session.OnRpcMessageAsync(Wire.Roster((peer, true)));
        await session.OnRpcMessageAsync(Wire.Backfill(
            Wire.SignedPost(peer, Room, "one"),
            Wire.SignedPost(peer, Room, "two", "n2")));

        Assert.Equal(2, obs.Posts.Count);
        Assert.All(obs.Posts, p => Assert.Equal(PostTrust.Verified, p.Trust));
    }

    [Fact]
    public async Task Inbound_Error_Surfaced()
    {
        var self = new FakeIdentity("Eself");
        var obs = new RecordingObserver();
        var session = NewSession(new FakeTransport(self), self, obs);

        await session.OnRpcMessageAsync(Wire.Error("sender_mismatch", "nope"));

        Assert.Contains(obs.Errors, e => e.Code == "sender_mismatch");
    }
}
