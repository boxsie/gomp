using Google.Protobuf;
using Gomp.Host;
using Gomp.Protocol;

namespace Gomp.Host.Tests;

/// <summary>An <see cref="IRoomTransport"/> that records everything the room sends/does.</summary>
internal sealed class FakeTransport : IRoomTransport
{
    public string Address { get; }
    public readonly List<(string To, byte[] Bytes)> Sent = new();
    public readonly List<string> Added = new();
    public readonly List<string> Removed = new();
    public readonly List<string> Disconnected = new();

    public FakeTransport(string address) => Address = address;

    public Task SendAsync(string toAddr, byte[] payload, CancellationToken ct = default)
    {
        Sent.Add((toAddr, payload.ToArray()));
        return Task.CompletedTask;
    }

    public Task UpdateAllowlistAsync(IEnumerable<string>? add, IEnumerable<string>? remove, CancellationToken ct = default)
    {
        if (add is not null) Added.AddRange(add);
        if (remove is not null) Removed.AddRange(remove);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(string addr, CancellationToken ct = default)
    {
        Disconnected.Add(addr);
        return Task.CompletedTask;
    }

    /// <summary>All room envelopes delivered to a given member, decoded.</summary>
    public IReadOnlyList<RoomEnvelope> EnvelopesTo(string to) =>
        Sent.Where(s => s.To == to).Select(s => RoomEnvelope.Parser.ParseFrom(s.Bytes)).ToList();

    public IReadOnlyList<RoomEnvelope> MessagesTo(string to) =>
        EnvelopesTo(to).Where(e => e.BodyCase == RoomEnvelope.BodyOneofCase.Message).ToList();
}

internal static class Posts
{
    /// <summary>Build a SignedPost as a member would (the signature is opaque to the host).</summary>
    public static SignedPost Make(string sender, string room, string text, string nonce = "n")
    {
        var body = new SignedPostBody
        {
            Sender = sender,
            Room = room,
            Content = ByteString.CopyFromUtf8(text),
            ClientTs = 1000,
            Nonce = nonce,
        };
        return new SignedPost { Body = body.ToByteString(), Sig = ByteString.CopyFromUtf8("opaque-sig") };
    }

    public static byte[] Submit(SignedPost post) =>
        new RoomEnvelope { Submit = new SubmitPost { Post = post } }.ToByteArray();

    public static byte[] Backfill(long sinceSeq, int limit = 0) =>
        new RoomEnvelope { BackfillReq = new BackfillRequest { SinceSeq = sinceSeq, Limit = limit } }.ToByteArray();

    public static byte[] RosterReq() =>
        new RoomEnvelope { RosterReq = new RosterRequest() }.ToByteArray();

    /// <summary>A member's Hello carrying an (opaque-to-the-host) identity binding.</summary>
    public static byte[] Hello(string bindingTag = "binding") =>
        new RoomEnvelope
        {
            Hello = new Hello
            {
                Binding = new IdentityBinding
                {
                    Binding = ByteString.CopyFromUtf8(bindingTag),
                    DilithiumPub = ByteString.CopyFromUtf8("dpub"),
                },
            },
        }.ToByteArray();
}
