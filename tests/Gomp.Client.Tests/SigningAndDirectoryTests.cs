using System.Text;
using Ensemble.Client;
using Gomp.Protocol;
using Xunit;

namespace Gomp.Client.Tests;

public sealed class PostSignerTests
{
    [Fact]
    public async Task SignAsync_BuildsBodyAsSelf_AndSignatureVerifies()
    {
        var self = new FakeIdentity("Eself");
        var fake = new FakeTransport(self);
        var signer = new PostSigner(fake, clock: () => 42, nonce: () => "nonce-1");

        var post = await signer.SignAsync("Eroom", Encoding.UTF8.GetBytes("hello pub"));

        var body = SignedPostBody.Parser.ParseFrom(post.Body);
        Assert.Equal("Eself", body.Sender);
        Assert.Equal("Eroom", body.Room);
        Assert.Equal("hello pub", body.Content.ToStringUtf8());
        Assert.Equal(42, body.ClientTs);
        Assert.Equal("nonce-1", body.Nonce);

        // The 64-byte sig is over the EXACT signed bytes and checks out against
        // the signer's ed25519 key (the per-message read path).
        Assert.Equal(64, post.Sig.Length);
        Assert.True(ServiceDocument.VerifyPostSignature(
            self.Ed25519Pub, post.Body.ToByteArray(), post.Sig.ToByteArray()));
    }
}

public sealed class MemberDirectoryTests
{
    [Fact]
    public void VerifyPost_Unverified_WhenNoBindingHeld()
    {
        var self = new FakeIdentity("Eself");
        var peer = new FakeIdentity("Epeer");
        var dir = new MemberDirectory(new FakeTransport(self, peer));

        var post = Wire.SignedPost(peer, "Eroom", "hi");
        Assert.Equal(PostTrust.Unverified,
            dir.VerifyPost(peer.Address, post.Body.ToByteArray(), post.Sig.ToByteArray()));
    }

    [Fact]
    public async Task LearnBinding_ThenVerifyPost_Verified()
    {
        var self = new FakeIdentity("Eself");
        var peer = new FakeIdentity("Epeer");
        var dir = new MemberDirectory(new FakeTransport(self, peer));

        Assert.True(await dir.LearnBindingAsync(peer.Address, peer.ToBinding()));
        Assert.True(dir.Knows(peer.Address));

        var post = Wire.SignedPost(peer, "Eroom", "yo");
        Assert.Equal(PostTrust.Verified,
            dir.VerifyPost(peer.Address, post.Body.ToByteArray(), post.Sig.ToByteArray()));
    }

    [Fact]
    public async Task LearnBinding_SameBindingTwice_HitsDaemonOnce()
    {
        var self = new FakeIdentity("Eself");
        var peer = new FakeIdentity("Epeer");
        var fake = new FakeTransport(self, peer);
        var dir = new MemberDirectory(fake);

        await dir.LearnBindingAsync(peer.Address, peer.ToBinding());
        await dir.LearnBindingAsync(peer.Address, peer.ToBinding());

        // verify-once: the second presentation of the same binding is a no-op.
        Assert.Equal(1, fake.VerifyCalls);
    }

    [Fact]
    public async Task VerifyPost_Forged_WhenSignedUnderWrongKey()
    {
        var self = new FakeIdentity("Eself");
        var peer = new FakeIdentity("Epeer");
        var dir = new MemberDirectory(new FakeTransport(self, peer));
        await dir.LearnBindingAsync(peer.Address, peer.ToBinding());

        // A host fabricates a post under peer's name, signed with its own key.
        var forged = Wire.ForgedPost(peer, "Eroom", "I never said this", forger: self);
        Assert.Equal(PostTrust.Forged,
            dir.VerifyPost(peer.Address, forged.Body.ToByteArray(), forged.Sig.ToByteArray()));
    }

    [Fact]
    public async Task LearnBinding_False_WhenDaemonRejects()
    {
        var self = new FakeIdentity("Eself");
        var stranger = new FakeIdentity("Estranger");
        // The fake does NOT know the stranger → VerifyBinding returns ok=false.
        var dir = new MemberDirectory(new FakeTransport(self));

        Assert.False(await dir.LearnBindingAsync(stranger.Address, stranger.ToBinding()));
        Assert.False(dir.Knows(stranger.Address));
    }

    [Fact]
    public async Task LearnBinding_False_OnEmptyBinding_NoDaemonCall()
    {
        var self = new FakeIdentity("Eself");
        var fake = new FakeTransport(self);
        var dir = new MemberDirectory(fake);

        Assert.False(await dir.LearnBindingAsync("Epeer", new IdentityBinding()));
        Assert.Equal(0, fake.VerifyCalls);
    }
}
