using Gomp.Host;
using Gomp.Protocol;
using Xunit;

namespace Gomp.Host.Tests;

public sealed class RoomConfigTests
{
    private static string TempDir() => Directory.CreateTempSubdirectory("roomcfg").FullName;

    // ---- Load / Parse ----

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(RoomConfig.Load(TempDir()));
    }

    [Fact]
    public void Parse_FullSchema_RoundTrips()
    {
        var entries = RoomConfig.Parse(@"
rooms:
  - name: lobby
    kind: open
  - name: mates
    kind: friends
  - name: vip
    kind: invite
    members: [Ealice, Ebob]
");
        Assert.Equal(3, entries.Count);
        Assert.Equal(RoomKind.Open, entries[0].Kind);
        Assert.Equal("lobby", entries[0].Name);
        Assert.Equal(RoomKind.Friends, entries[1].Kind);
        Assert.Equal(RoomKind.Invite, entries[2].Kind);
        Assert.Equal(new[] { "Ealice", "Ebob" }, entries[2].Members);
    }

    [Fact]
    public void Parse_KindIsCaseInsensitive()
    {
        var entries = RoomConfig.Parse("rooms:\n  - name: a\n    kind: INVITE\n");
        Assert.Equal(RoomKind.Invite, entries[0].Kind);
    }

    [Fact]
    public void Parse_EmptyOrNoRooms_ReturnsEmpty()
    {
        Assert.Empty(RoomConfig.Parse(""));
        Assert.Empty(RoomConfig.Parse("rooms:"));
        Assert.Empty(RoomConfig.Parse("# just a comment\n"));
    }

    [Fact]
    public void Parse_Malformed_Throws()
    {
        Assert.Throws<RoomConfigException>(() => RoomConfig.Parse("rooms:\n  - name: a\n   kind: open\n  bad: : :"));
    }

    [Fact]
    public void Load_ReadsFileFromDataDir()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "rooms.yaml"), "rooms:\n  - name: lobby\n    kind: open\n");
        var entries = RoomConfig.Load(dir);
        Assert.Single(entries);
        Assert.Equal("lobby", entries[0].Name);
    }

    // ---- Reconcile ----

    [Fact]
    public void Reconcile_CreatesMissingRooms()
    {
        var catalog = RoomCatalog.Load(TempDir());
        var result = RoomConfig.Reconcile(new[]
        {
            new RoomConfigEntry("lobby", RoomKind.Open, Array.Empty<string>()),
            new RoomConfigEntry("vip", RoomKind.Invite, new[] { "Ealice" }),
        }, catalog);

        Assert.Equal(new[] { "lobby", "vip" }, result.Created);
        Assert.True(catalog.Contains("lobby"));
        Assert.True(catalog.TryGet("vip", out var vip));
        Assert.Equal(new[] { "Ealice" }, vip.Members);
    }

    [Fact]
    public void Reconcile_NeverDeletesRoomsAbsentFromConfig()
    {
        var catalog = RoomCatalog.Load(TempDir());
        catalog.Put(new RoomRecord("livecreated", RoomKind.Open, Array.Empty<string>()));

        RoomConfig.Reconcile(new[] { new RoomConfigEntry("lobby", RoomKind.Open, Array.Empty<string>()) }, catalog);

        Assert.True(catalog.Contains("livecreated")); // not in config, must survive
        Assert.True(catalog.Contains("lobby"));
    }

    [Fact]
    public void Reconcile_ExistingRoom_AddsInviteMembersOnly()
    {
        var catalog = RoomCatalog.Load(TempDir());
        catalog.Put(new RoomRecord("vip", RoomKind.Invite, new[] { "Ealice" }));

        var result = RoomConfig.Reconcile(
            new[] { new RoomConfigEntry("vip", RoomKind.Invite, new[] { "Ealice", "Ebob" }) }, catalog);

        Assert.Empty(result.Created);
        Assert.Equal(new[] { "vip: Ebob" }, result.MembersAdded);
        catalog.TryGet("vip", out var vip);
        Assert.Equal(new[] { "Ealice", "Ebob" }, vip.Members);
    }

    [Fact]
    public void Reconcile_NeverChangesKind_AndDoesNotRemoveMembers()
    {
        var catalog = RoomCatalog.Load(TempDir());
        catalog.Put(new RoomRecord("vip", RoomKind.Invite, new[] { "Ealice", "Ebob" }));

        // config says open, with only Ealice — must keep invite + both members.
        var result = RoomConfig.Reconcile(
            new[] { new RoomConfigEntry("vip", RoomKind.Open, new[] { "Ealice" }) }, catalog);

        catalog.TryGet("vip", out var vip);
        Assert.Equal(RoomKind.Invite, vip.Kind);
        Assert.Equal(new[] { "Ealice", "Ebob" }, vip.Members);
        Assert.Contains(result.Skipped, s => s.Contains("kind differs"));
    }

    [Fact]
    public void Reconcile_SkipsUnknownKindAndEmptyName()
    {
        var catalog = RoomCatalog.Load(TempDir());
        var result = RoomConfig.Reconcile(new[]
        {
            new RoomConfigEntry("", RoomKind.Open, Array.Empty<string>()),
            new RoomConfigEntry("mystery", RoomKind.Unspecified, Array.Empty<string>()),
            new RoomConfigEntry("good", RoomKind.Open, Array.Empty<string>()),
        }, catalog);

        Assert.Equal(new[] { "good" }, result.Created);
        Assert.Equal(2, result.Skipped.Count);
        Assert.False(catalog.Contains("mystery"));
    }

    [Fact]
    public void Reconcile_SkipsDuplicateNameInConfig()
    {
        var catalog = RoomCatalog.Load(TempDir());
        var result = RoomConfig.Reconcile(new[]
        {
            new RoomConfigEntry("lobby", RoomKind.Open, Array.Empty<string>()),
            new RoomConfigEntry("lobby", RoomKind.Invite, Array.Empty<string>()),
        }, catalog);

        Assert.Equal(new[] { "lobby" }, result.Created);
        Assert.Contains(result.Skipped, s => s.Contains("duplicate"));
        catalog.TryGet("lobby", out var rec);
        Assert.Equal(RoomKind.Open, rec.Kind); // first wins
    }

    [Fact]
    public void Reconcile_EmptyConfig_IsNoOp()
    {
        var catalog = RoomCatalog.Load(TempDir());
        var result = RoomConfig.Reconcile(Array.Empty<RoomConfigEntry>(), catalog);
        Assert.Same(ReconcileResult.Empty, result);
    }

    // ---- End-to-end: persistence is keyed by service name (data dir survives) ----

    [Fact]
    public void ConfigRooms_PersistInCatalog_AcrossReload()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "rooms.yaml"),
            "rooms:\n  - name: lobby\n    kind: open\n  - name: vip\n    kind: invite\n    members: [Ealice]\n");

        var catalog = RoomCatalog.Load(dir);
        RoomConfig.Reconcile(RoomConfig.Load(dir), catalog);

        // Simulate a restart: the catalog persists to rooms.json in the same dir.
        var reloaded = RoomCatalog.Load(dir);
        Assert.True(reloaded.Contains("lobby"));
        Assert.True(reloaded.TryGet("vip", out var vip));
        Assert.Equal(new[] { "Ealice" }, vip.Members);
    }
}
