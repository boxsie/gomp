using Gomp.Host;
using Xunit;

namespace Gomp.Host.Tests;

public sealed class AdminStateTests
{
    private static string TempDir() => Directory.CreateTempSubdirectory("admin").FullName;

    [Fact]
    public void Owner_IsAlwaysAuthorized()
    {
        var s = AdminState.Load(TempDir(), "Eowner");
        Assert.True(s.IsAuthorized("Eowner"));
        Assert.False(s.IsAuthorized("Estranger"));
        Assert.Contains("Eowner", s.AuthorizedAddresses());
    }

    [Fact]
    public void Promote_AddsAdmin_AndPersists()
    {
        var dir = TempDir();
        var s = AdminState.Load(dir, "Eowner");
        Assert.True(s.Promote("Ealice"));
        Assert.True(s.IsAuthorized("Ealice"));

        // A fresh load (restart) sees the persisted admin.
        var reloaded = AdminState.Load(dir, "Eowner");
        Assert.True(reloaded.IsAuthorized("Ealice"));
    }

    [Fact]
    public void Promote_OwnerOrDuplicate_IsNoOp()
    {
        var s = AdminState.Load(TempDir(), "Eowner");
        Assert.False(s.Promote("Eowner"));     // owner is authority by role
        Assert.True(s.Promote("Ealice"));
        Assert.False(s.Promote("Ealice"));     // already admin
    }

    [Fact]
    public void Demote_RemovesAdmin()
    {
        var dir = TempDir();
        var s = AdminState.Load(dir, "Eowner");
        s.Promote("Ealice");
        Assert.True(s.Demote("Ealice"));
        Assert.False(s.IsAuthorized("Ealice"));
        Assert.False(s.Demote("Ealice"));      // not an admin anymore

        var reloaded = AdminState.Load(dir, "Eowner");
        Assert.False(reloaded.IsAuthorized("Ealice"));
    }

    [Fact]
    public void OwnerRotation_NotOverriddenByStaleAdminFile()
    {
        var dir = TempDir();
        var s = AdminState.Load(dir, "Eowner1");
        s.Promote("Eowner2");

        // Reload with a different owner. Eowner2 was persisted as an admin AND is
        // now the owner — it's dropped from the admin set on load but stays
        // authorized by being the owner. Eowner1 was the OLD owner, which never
        // persisted (owner is always re-read from env), so after rotation it has
        // no leftover authority.
        var rotated = AdminState.Load(dir, "Eowner2");
        Assert.Equal("Eowner2", rotated.Owner);
        Assert.True(rotated.IsAuthorized("Eowner2"));
        Assert.False(rotated.IsAuthorized("Eowner1"));
    }

    [Fact]
    public void CorruptFile_StartsWithOwnerOnly()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "admins.json"), "{ this is not json");
        var s = AdminState.Load(dir, "Eowner");
        Assert.True(s.IsAuthorized("Eowner"));
        Assert.Single(s.AuthorizedAddresses());
    }

    [Fact]
    public void Load_RequiresOwner()
    {
        Assert.Throws<ArgumentException>(() => AdminState.Load(TempDir(), ""));
    }
}
