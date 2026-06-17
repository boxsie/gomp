using Gomp.Host;
using Xunit;

namespace Gomp.Host.Tests;

public sealed class AdminStateTests
{
    private static string TempDir() => Directory.CreateTempSubdirectory("admin").FullName;

    [Fact]
    public void OwnerAndSeedAdmin_AreAuthorized()
    {
        var s = AdminState.Load(TempDir(), "Eseed");
        s.SetOwner("Ebase");                          // gomp's own base account (option B)
        Assert.True(s.IsAuthorized("Ebase"));         // the owner
        Assert.True(s.IsAuthorized("Eseed"));         // the env-delegated admin
        Assert.False(s.IsAuthorized("Estranger"));
        // The base manifest allowlist is the dial-in set: the env seed, NOT the
        // owner (a service is never in its own allowlist).
        Assert.Contains("Eseed", s.RemoteAdminAddresses());
        Assert.DoesNotContain("Ebase", s.RemoteAdminAddresses());
    }

    [Fact]
    public void Promote_AddsAdmin_AndPersists()
    {
        var dir = TempDir();
        var s = AdminState.Load(dir, "Eseed");
        Assert.True(s.Promote("Ealice"));
        Assert.True(s.IsAuthorized("Ealice"));
        Assert.Contains("Ealice", s.RemoteAdminAddresses());

        // A fresh load (restart) sees the persisted admin.
        var reloaded = AdminState.Load(dir, "Eseed");
        Assert.True(reloaded.IsAuthorized("Ealice"));
    }

    [Fact]
    public void Promote_AuthorityOrDuplicate_IsNoOp()
    {
        var s = AdminState.Load(TempDir(), "Eseed");
        s.SetOwner("Ebase");
        Assert.False(s.Promote("Ebase"));      // owner is authority by role
        Assert.False(s.Promote("Eseed"));      // env seed is authority by env
        Assert.True(s.Promote("Ealice"));
        Assert.False(s.Promote("Ealice"));     // already admin
    }

    [Fact]
    public void Demote_RemovesAdmin()
    {
        var dir = TempDir();
        var s = AdminState.Load(dir, "Eseed");
        s.Promote("Ealice");
        Assert.True(s.Demote("Ealice"));
        Assert.False(s.IsAuthorized("Ealice"));
        Assert.False(s.Demote("Ealice"));      // not an admin anymore
        Assert.False(s.Demote("Eseed"));       // env seed can't be demoted

        var reloaded = AdminState.Load(dir, "Eseed");
        Assert.False(reloaded.IsAuthorized("Ealice"));
    }

    [Fact]
    public void SeedAdmin_NeverPersists_AcrossReload()
    {
        var dir = TempDir();
        var s = AdminState.Load(dir, "Eseed1");
        s.Promote("Ealice");   // a real promoted admin persists; the env seed does not

        // Reload with a rotated env seed: the old seed has no lingering authority,
        // the promoted admin survives, the new seed is authorized.
        var reloaded = AdminState.Load(dir, "Eseed2");
        Assert.True(reloaded.IsAuthorized("Ealice"));    // persisted admin survives
        Assert.True(reloaded.IsAuthorized("Eseed2"));    // new env seed
        Assert.False(reloaded.IsAuthorized("Eseed1"));   // old seed gone (never persisted)
    }

    [Fact]
    public void Owner_RotatesFreely_NotPersisted()
    {
        var dir = TempDir();
        var s = AdminState.Load(dir, "Eseed");
        s.SetOwner("Ebase1");
        Assert.Equal("Ebase1", s.Owner);

        // The owner is the base service address — re-derived at registration each
        // boot, never persisted; a fresh load binds whatever address it's given.
        var reloaded = AdminState.Load(dir, "Eseed");
        reloaded.SetOwner("Ebase2");
        Assert.Equal("Ebase2", reloaded.Owner);
        Assert.True(reloaded.IsAuthorized("Ebase2"));
        Assert.False(reloaded.IsAuthorized("Ebase1"));
    }

    [Fact]
    public void CorruptFile_StartsWithSeedAdminOnly()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "admins.json"), "{ this is not json");
        var s = AdminState.Load(dir, "Eseed");
        Assert.True(s.IsAuthorized("Eseed"));
        Assert.Single(s.RemoteAdminAddresses());   // just the env seed
    }

    [Fact]
    public void Load_RequiresSeedAdmin()
    {
        Assert.Throws<ArgumentException>(() => AdminState.Load(TempDir(), ""));
    }
}
