using System.Text;
using Google.Protobuf;
using Gomp.Client;
using Xunit;
using Gw = Gomp.Protocol.Gateway;

namespace Gomp.Host.Tests;

/// <summary>
/// The backend→frontend half of the relay glue (ADR-0011 §5): a joined room's
/// posts / roster / presence / errors become tagged <see cref="Gw.BeEvent"/>
/// payloads. No daemon — drive the observer directly and capture what it would
/// push to the operator.
/// </summary>
public sealed class RelayRoomObserverTests
{
    private const string Room = "Eroom0000000000000000000000000000000";

    private static (RelayRoomObserver observer, List<Gw.BeEvent> sent) Make()
    {
        var sent = new List<Gw.BeEvent>();
        var observer = new RelayRoomObserver(Room, ev => { sent.Add(ev); return Task.CompletedTask; });
        return (observer, sent);
    }

    [Fact]
    public async Task Post_MapsFieldsAndTrust()
    {
        var (observer, sent) = Make();
        await observer.OnPostAsync(new ReceivedPost(
            Seq: 9, HostTs: 100, Sender: "Esender0000000000000000000000000000",
            Content: Encoding.UTF8.GetBytes("evening all"), ClientTs: 200, Nonce: "nn",
            Trust: PostTrust.Forged));

        var ev = Assert.Single(sent);
        Assert.Equal(Gw.BeEvent.BodyOneofCase.Post, ev.BodyCase);
        Assert.Equal(Room, ev.Post.RoomAddress);
        Assert.Equal(9, ev.Post.Seq);
        Assert.Equal("Esender0000000000000000000000000000", ev.Post.Sender);
        Assert.Equal("evening all", ev.Post.Content.ToStringUtf8());
        Assert.Equal("nn", ev.Post.Nonce);
        Assert.Equal(Gw.PostTrust.Forged, ev.Post.Trust);
    }

    [Fact]
    public async Task Roster_Presence_Error_Map()
    {
        var (observer, sent) = Make();

        await observer.OnRosterAsync(new[]
        {
            new RoomMemberPresence("Ea", true),
            new RoomMemberPresence("Eb", false),
        });
        await observer.OnPresenceAsync(new RoomMemberPresence("Ec", true));
        await observer.OnErrorAsync("wrong_room", "no");

        Assert.Equal(Gw.BeEvent.BodyOneofCase.Roster, sent[0].BodyCase);
        Assert.Equal(Room, sent[0].Roster.RoomAddress);
        Assert.Equal(2, sent[0].Roster.Members.Count);
        Assert.Equal("Ea", sent[0].Roster.Members[0].Addr);

        Assert.Equal(Gw.BeEvent.BodyOneofCase.Presence, sent[1].BodyCase);
        Assert.Equal("Ec", sent[1].Presence.Addr);
        Assert.True(sent[1].Presence.Online);

        Assert.Equal(Gw.BeEvent.BodyOneofCase.RoomError, sent[2].BodyCase);
        Assert.Equal("wrong_room", sent[2].RoomError.Code);
    }

    [Fact]
    public void GatewayEnvelopes_RoundTrip()
    {
        // The relay carries these as opaque bytes; prove both envelopes survive a
        // serialize → parse cycle with their oneof intact.
        var req = new Gw.FeRequest { SendChat = new Gw.SendChat { RoomAddress = Room, Text = "hi" } };
        var reqBack = Gw.FeRequest.Parser.ParseFrom(req.ToByteArray());
        Assert.Equal(Gw.FeRequest.BodyOneofCase.SendChat, reqBack.BodyCase);
        Assert.Equal("hi", reqBack.SendChat.Text);

        var ev = new Gw.BeEvent { Welcome = new Gw.Welcome { SelfAddress = "Eself" } };
        var evBack = Gw.BeEvent.Parser.ParseFrom(ev.ToByteArray());
        Assert.Equal(Gw.BeEvent.BodyOneofCase.Welcome, evBack.BodyCase);
        Assert.Equal("Eself", evBack.Welcome.SelfAddress);
    }
}
