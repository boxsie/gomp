using System.Text;
using Google.Protobuf;
using Ensemble.Client;
using Gomp.Protocol;
using NSec.Cryptography;

namespace Gomp.Client.Tests;

/// <summary>
/// A test identity: a real ed25519 keypair (so signatures genuinely verify)
/// plus opaque stand-in binding / Dilithium material the fake transport keys
/// its verification on. Mirrors what the daemon would hand back from a sign.
/// </summary>
internal sealed class FakeIdentity
{
    private readonly Key _edKey;

    public string Address { get; }
    public byte[] Ed25519Pub { get; }
    public byte[] BindingBytes { get; }
    public byte[] DilithiumPub { get; }

    public FakeIdentity(string address)
    {
        Address = address;
        _edKey = Key.Create(SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        Ed25519Pub = _edKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        BindingBytes = Encoding.UTF8.GetBytes("binding:" + address);
        DilithiumPub = Encoding.UTF8.GetBytes("dpub:" + address);
    }

    /// <summary>Sign the domain-separated form of <paramref name="payload"/> as the daemon would.</summary>
    public byte[] SignDoc(byte[] payload) =>
        SignatureAlgorithm.Ed25519.Sign(_edKey, ServiceDocument.SigningBytes(payload));

    public SignedDocument Sign(byte[] payload) =>
        new(SignDoc(payload), DilithiumPub, BindingBytes, Address);

    public IdentityBinding ToBinding() => new()
    {
        Binding = ByteString.CopyFrom(BindingBytes),
        DilithiumPub = ByteString.CopyFrom(DilithiumPub),
    };
}

/// <summary>
/// In-memory <see cref="IRoomClientTransport"/> recording all outbound calls and
/// letting a test drive inbound events. Verification mimics the daemon: a
/// binding verifies iff a known identity matches the claimed address and the
/// binding bytes line up; the bound ed25519 key is returned.
/// </summary>
internal sealed class FakeTransport : IRoomClientTransport
{
    private readonly FakeIdentity _self;
    private readonly Dictionary<string, FakeIdentity> _known;

    public readonly List<(string To, byte[] Bytes)> Sent = new();
    public readonly List<string> Connected = new();
    public readonly List<string> Disconnected = new();
    public int SignCalls;
    public int VerifyCalls;

    public FakeTransport(FakeIdentity self, params FakeIdentity[] peers)
    {
        _self = self;
        _known = new Dictionary<string, FakeIdentity>(StringComparer.Ordinal) { [self.Address] = self };
        foreach (var p in peers)
            _known[p.Address] = p;
    }

    public string Address => _self.Address;

    public Task ConnectAsync(string toAddr, CancellationToken ct = default)
    {
        Connected.Add(toAddr);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(string toAddr, CancellationToken ct = default)
    {
        Disconnected.Add(toAddr);
        return Task.CompletedTask;
    }

    public Task SendAsync(string toAddr, byte[] payload, CancellationToken ct = default)
    {
        Sent.Add((toAddr, payload.ToArray()));
        return Task.CompletedTask;
    }

    public Task<SignedDocument> SignAsync(byte[] payload, CancellationToken ct = default)
    {
        Interlocked.Increment(ref SignCalls);
        return Task.FromResult(_self.Sign(payload));
    }

    public Task<BindingVerification> VerifyBindingAsync(
        byte[] binding, byte[] dilithiumPub, string address, CancellationToken ct = default)
    {
        Interlocked.Increment(ref VerifyCalls);
        if (_known.TryGetValue(address, out var id) && binding.AsSpan().SequenceEqual(id.BindingBytes))
            return Task.FromResult(new BindingVerification(true, id.Ed25519Pub, ""));
        return Task.FromResult(new BindingVerification(false, Array.Empty<byte>(), "unknown binding"));
    }

    public event Func<string, Task>? PeerConnected;
    public event Func<string, Task>? PeerDisconnected;
    public event Func<string, byte[], Task>? MessageReceived;

    public Task RaiseConnectedAsync(string addr) => PeerConnected?.Invoke(addr) ?? Task.CompletedTask;
    public Task RaiseDisconnectedAsync(string addr) => PeerDisconnected?.Invoke(addr) ?? Task.CompletedTask;
    public Task RaiseMessageAsync(string from, byte[] bytes) => MessageReceived?.Invoke(from, bytes) ?? Task.CompletedTask;

    public IReadOnlyList<RoomEnvelope> RoomEnvelopesTo(string to) =>
        Sent.Where(s => s.To == to).Select(s => RoomEnvelope.Parser.ParseFrom(s.Bytes)).ToList();

    public IReadOnlyList<AdminEnvelope> AdminEnvelopesTo(string to) =>
        Sent.Where(s => s.To == to).Select(s => AdminEnvelope.Parser.ParseFrom(s.Bytes)).ToList();
}

internal sealed class RecordingObserver : IRoomObserver
{
    public readonly List<ReceivedPost> Posts = new();
    public readonly List<RoomMemberPresence> Presence = new();
    public readonly List<IReadOnlyList<RoomMemberPresence>> Rosters = new();
    public readonly List<(string Code, string Message)> Errors = new();

    public Task OnPostAsync(ReceivedPost post) { Posts.Add(post); return Task.CompletedTask; }
    public Task OnPresenceAsync(RoomMemberPresence presence) { Presence.Add(presence); return Task.CompletedTask; }
    public Task OnRosterAsync(IReadOnlyList<RoomMemberPresence> members) { Rosters.Add(members); return Task.CompletedTask; }
    public Task OnErrorAsync(string code, string message) { Errors.Add((code, message)); return Task.CompletedTask; }
}

/// <summary>Builders for inbound room/admin wire bytes a host would emit.</summary>
internal static class Wire
{
    public static SignedPost SignedPost(FakeIdentity author, string room, string text, string nonce = "n")
    {
        var body = new SignedPostBody
        {
            Sender = author.Address,
            Room = room,
            Content = ByteString.CopyFromUtf8(text),
            ClientTs = 1,
            Nonce = nonce,
        };
        var bytes = body.ToByteArray();
        return new SignedPost { Body = ByteString.CopyFrom(bytes), Sig = ByteString.CopyFrom(author.SignDoc(bytes)) };
    }

    /// <summary>A post that CLAIMS <paramref name="author"/> but is signed by
    /// <paramref name="forger"/> — a host fabricating under a member's name.</summary>
    public static SignedPost ForgedPost(FakeIdentity author, string room, string text, FakeIdentity forger)
    {
        var body = new SignedPostBody
        {
            Sender = author.Address,
            Room = room,
            Content = ByteString.CopyFromUtf8(text),
            ClientTs = 1,
            Nonce = "n",
        };
        var bytes = body.ToByteArray();
        return new SignedPost { Body = ByteString.CopyFrom(bytes), Sig = ByteString.CopyFrom(forger.SignDoc(bytes)) };
    }

    public static byte[] Message(SignedPost post, long seq = 1) =>
        new RoomEnvelope { Message = new RoomMessage { Seq = seq, HostTs = 1, Post = post } }.ToByteArray();

    public static byte[] Backfill(params SignedPost[] posts)
    {
        var resp = new BackfillResponse { LatestSeq = posts.Length, Complete = true };
        long seq = 1;
        foreach (var p in posts)
            resp.Messages.Add(new RoomMessage { Seq = seq++, HostTs = 1, Post = p });
        return new RoomEnvelope { BackfillResp = resp }.ToByteArray();
    }

    public static byte[] Roster(params (FakeIdentity id, bool online)[] members)
    {
        var roster = new Roster();
        foreach (var (id, online) in members)
            roster.Members.Add(new Member { Addr = id.Address, Online = online, Binding = id.ToBinding() });
        return new RoomEnvelope { Roster = roster }.ToByteArray();
    }

    public static byte[] Presence(FakeIdentity id, bool online) =>
        new RoomEnvelope
        {
            Presence = new PresenceUpdate
            {
                Addr = id.Address,
                Online = online,
                Binding = online ? id.ToBinding() : null,
            },
        }.ToByteArray();

    public static byte[] Error(string code, string message = "") =>
        new RoomEnvelope { Error = new RoomError { Code = code, Message = message } }.ToByteArray();

    public static byte[] AdminResponse(string requestId, bool ok, params RoomInfo[] rooms)
    {
        var resp = new AdminResponse { RequestId = requestId, Ok = ok };
        resp.Rooms.AddRange(rooms);
        return new AdminEnvelope { Response = resp }.ToByteArray();
    }
}
