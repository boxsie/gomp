using Google.Protobuf;
using Gomp.Protocol;

namespace Gomp.Client;

/// <summary>
/// Builds and signs a member's posts (ADR-0007 §6). Assembles the
/// <see cref="SignedPostBody"/> — binding the author, room, a wall-clock and a
/// per-message nonce (so a captured post can't be replayed into another room or
/// as another author) — signs the serialized body via the daemon, and wraps it
/// as a <see cref="SignedPost"/> carrying a 64-byte ed25519 signature only (the
/// binding lives in the roster, not on every post).
/// </summary>
internal sealed class PostSigner
{
    private readonly IRoomClientTransport _tx;
    private readonly Func<long> _clock;
    private readonly Func<string> _nonce;

    public PostSigner(IRoomClientTransport tx, Func<long>? clock = null, Func<string>? nonce = null)
    {
        _tx = tx;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _nonce = nonce ?? (() => Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// Sign <paramref name="content"/> as a post in <paramref name="roomAddr"/>.
    /// The author (<c>SignedPostBody.sender</c>) is this member's own address —
    /// which the host independently checks equals the transport <c>from_addr</c>,
    /// so a member can't post under another's name.
    /// </summary>
    public async Task<SignedPost> SignAsync(string roomAddr, byte[] content, CancellationToken ct = default)
    {
        var body = new SignedPostBody
        {
            Sender = _tx.Address,
            Room = roomAddr,
            Content = ByteString.CopyFrom(content),
            ClientTs = _clock(),
            Nonce = _nonce(),
        };
        // proto3 has no canonical form, so we sign the exact serialized bytes and
        // carry them verbatim as SignedPost.body — recipients verify over these.
        var bodyBytes = body.ToByteArray();
        var signed = await _tx.SignAsync(bodyBytes, ct).ConfigureAwait(false);
        return new SignedPost
        {
            Body = ByteString.CopyFrom(bodyBytes),
            Sig = ByteString.CopyFrom(signed.Signature),
        };
    }
}
