using Gomp.Host;
using Gomp.Protocol;
using Xunit;

namespace Gomp.Host.Tests;

public sealed class RoomCatalogTests
{
    private static string TempDir() => Directory.CreateTempSubdirectory("catalog").FullName;

    [Fact]
    public void PutAndGet_RoundTrips()
    {
        var c = RoomCatalog.Load(TempDir());
        c.Put(new RoomRecord("lobby", RoomKind.Open, Array.Empty<string>()));
        Assert.True(c.Contains("lobby"));
        Assert.True(c.TryGet("lobby", out var rec));
        Assert.Equal(RoomKind.Open, rec.Kind);
    }

    [Fact]
    public void Persistence_SurvivesReload()
    {
        var dir = TempDir();
        var c = RoomCatalog.Load(dir);
        c.Put(new RoomRecord("vip", RoomKind.Invite, new[] { "Ealice" }));

        var reloaded = RoomCatalog.Load(dir);
        Assert.True(reloaded.TryGet("vip", out var rec));
        Assert.Equal(RoomKind.Invite, rec.Kind);
        Assert.Equal(new[] { "Ealice" }, rec.Members);
    }

    [Fact]
    public void AddAndRemoveMember_Persist()
    {
        var dir = TempDir();
        var c = RoomCatalog.Load(dir);
        c.Put(new RoomRecord("vip", RoomKind.Invite, Array.Empty<string>()));

        Assert.NotNull(c.AddMember("vip", "Ebob"));
        Assert.Null(c.AddMember("vip", "Ebob"));      // duplicate is a no-op
        Assert.Null(c.AddMember("ghost", "Ebob"));    // unknown room

        var reloaded = RoomCatalog.Load(dir);
        reloaded.TryGet("vip", out var rec);
        Assert.Contains("Ebob", rec.Members);

        Assert.NotNull(reloaded.RemoveMember("vip", "Ebob"));
        Assert.Null(reloaded.RemoveMember("vip", "Ebob"));
    }

    [Fact]
    public void Remove_DropsRoom()
    {
        var dir = TempDir();
        var c = RoomCatalog.Load(dir);
        c.Put(new RoomRecord("lobby", RoomKind.Open, Array.Empty<string>()));
        c.Remove("lobby");
        Assert.False(c.Contains("lobby"));

        Assert.False(RoomCatalog.Load(dir).Contains("lobby"));
    }

    [Fact]
    public void SetKind_ChangesKind_AndPersists()
    {
        var dir = TempDir();
        var c = RoomCatalog.Load(dir);
        c.Put(new RoomRecord("snug", RoomKind.Open, Array.Empty<string>()));

        var updated = c.SetKind("snug", RoomKind.Invite);
        Assert.NotNull(updated);
        Assert.Equal(RoomKind.Invite, updated!.Kind);
        Assert.Null(c.SetKind("snug", RoomKind.Invite));   // unchanged → no-op
        Assert.Null(c.SetKind("ghost", RoomKind.Open));    // unknown → null

        Assert.True(RoomCatalog.Load(dir).TryGet("snug", out var rec));
        Assert.Equal(RoomKind.Invite, rec.Kind);
    }

    [Fact]
    public void UpdateMeta_SetsDisplayTopicRetention_AndPersists()
    {
        var dir = TempDir();
        var c = RoomCatalog.Load(dir);
        c.Put(new RoomRecord("snug", RoomKind.Invite, new[] { "Ea" }));

        var updated = c.UpdateMeta("snug", "The Snug", "after hours", 250);
        Assert.NotNull(updated);
        Assert.Equal("The Snug", updated!.DisplayName);
        Assert.Equal("after hours", updated.Topic);
        Assert.Equal(250, updated.RetentionMax);
        Assert.Equal(new[] { "Ea" }, updated.Members);     // members untouched
        Assert.Null(c.UpdateMeta("ghost", "x", "y", 1));   // unknown → null

        c.Put(new RoomRecord("other", RoomKind.Open, Array.Empty<string>()));
        Assert.Equal(0, c.UpdateMeta("other", "", "", -5)!.RetentionMax); // negative clamps to 0

        Assert.True(RoomCatalog.Load(dir).TryGet("snug", out var rec));
        Assert.Equal("The Snug", rec.DisplayName);
        Assert.Equal("after hours", rec.Topic);
    }
}
