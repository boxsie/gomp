using System.Collections.Concurrent;
using Ensemble.Client;
using Gomp.Protocol;

namespace Gomp.Client;

/// <summary>
/// Holds peers' verified identity keys for a room and answers per-post
/// authorship questions cheaply (ADR-0007 §6 — "distribute-binding-once /
/// sign-cheaply-per-message", the TLS/PKI pattern).
///
/// A peer's binding is verified <b>once</b> (one ML-DSA-65 round-trip to the
/// daemon via <see cref="IRoomClientTransport.VerifyBindingAsync"/>) and the
/// returned ed25519 key is pinned. Thereafter every post that peer authors is
/// verified in-process against the pin — no daemon round-trip on the read path.
/// Re-verification happens only when a peer presents a <i>different</i> binding
/// (a key rotation), so a stale binding never silently keeps validating.
/// </summary>
internal sealed class MemberDirectory
{
    private sealed record Pin(byte[] BindingBytes, byte[] Ed25519Pub);

    private readonly IRoomClientTransport _tx;
    private readonly ConcurrentDictionary<string, Pin> _pins = new(StringComparer.Ordinal);

    public MemberDirectory(IRoomClientTransport tx) => _tx = tx;

    /// <summary>
    /// Verify and pin <paramref name="addr"/>'s binding. Returns true if the
    /// peer is now pinned (verified just now, or already pinned to this exact
    /// binding); false if there was nothing to verify or the binding failed to
    /// verify (in which case the peer stays whatever it was — host-trusted).
    /// Idempotent: the same binding re-presented does not re-hit the daemon.
    /// </summary>
    public async Task<bool> LearnBindingAsync(string addr, IdentityBinding? binding, CancellationToken ct = default)
    {
        if (binding is null || binding.Binding.IsEmpty)
            return false;

        var bindingBytes = binding.Binding.ToByteArray();
        if (_pins.TryGetValue(addr, out var existing) &&
            existing.BindingBytes.AsSpan().SequenceEqual(bindingBytes))
            return true; // already verified this exact binding — no daemon round-trip

        var vb = await _tx.VerifyBindingAsync(
            bindingBytes, binding.DilithiumPub.ToByteArray(), addr, ct).ConfigureAwait(false);
        if (!vb.Ok)
            return false;

        _pins[addr] = new Pin(bindingBytes, vb.Ed25519Pub);
        return true;
    }

    /// <summary>True once <paramref name="addr"/>'s binding has been verified.</summary>
    public bool Knows(string addr) => _pins.ContainsKey(addr);

    /// <summary>
    /// Classify a post's authorship. <paramref name="body"/> is the exact signed
    /// bytes (<c>SignedPost.body</c>); <paramref name="sig"/> the 64-byte
    /// ed25519 signature. Returns <see cref="PostTrust.Unverified"/> when no pin
    /// is held for <paramref name="sender"/> (host-trusted), otherwise
    /// <see cref="PostTrust.Verified"/> / <see cref="PostTrust.Forged"/>.
    /// </summary>
    public PostTrust VerifyPost(string sender, byte[] body, byte[] sig)
    {
        if (!_pins.TryGetValue(sender, out var pin))
            return PostTrust.Unverified;
        return ServiceDocument.VerifyPostSignature(pin.Ed25519Pub, body, sig)
            ? PostTrust.Verified
            : PostTrust.Forged;
    }
}
