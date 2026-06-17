using Gomp.Host;
using Gomp.Protocol;
using Xunit;

namespace Gomp.Host.Tests;

public sealed class RoomStoreTests
{
    private static string TempDir() => Directory.CreateTempSubdirectory("roomstore").FullName;

    [Fact]
    public void Delete_RemovesRoomDirectory()
    {
        var dir = TempDir();
        var store = RoomStore.Open(dir, "snug", 100);
        store.Append(Posts.Make("a", "snug", "hi"), 1);
        var roomDir = Path.Combine(dir, "rooms", "snug");
        Assert.True(Directory.Exists(roomDir));

        RoomStore.Delete(dir, "snug");

        Assert.False(Directory.Exists(roomDir));
        // Other rooms under the same data dir are untouched.
        RoomStore.Open(dir, "other", 100);
        RoomStore.Delete(dir, "snug"); // idempotent: no throw when already gone
        Assert.True(Directory.Exists(Path.Combine(dir, "rooms", "other")));
    }

    [Fact]
    public void Clear_EmptiesHistoryAndResetsCursor()
    {
        var dir = TempDir();
        var store = RoomStore.Open(dir, "r", 100);
        for (var i = 0; i < 4; i++) store.Append(Posts.Make("a", "r", $"m{i}"), i);
        Assert.Equal(4, store.LatestSeq);

        store.Clear();
        Assert.Equal(0, store.LatestSeq);
        Assert.Empty(store.Backfill(0, 0).Messages);

        // Survives reopen (the file was rewritten empty) and a new append starts at 1.
        var reopened = RoomStore.Open(dir, "r", 100);
        Assert.Equal(0, reopened.LatestSeq);
        Assert.Equal(1, reopened.Append(Posts.Make("a", "r", "fresh"), 9).Seq);
    }

    [Fact]
    public void SetRetention_LowersCap_TrimsTail()
    {
        var store = RoomStore.Open(TempDir(), "r", 100);
        for (var i = 0; i < 6; i++) store.Append(Posts.Make("a", "r", $"m{i}"), i);
        Assert.Equal(6, store.Backfill(0, 0).Messages.Count);

        store.SetRetention(2);
        Assert.Equal(2, store.MaxMessages);
        var kept = store.Backfill(0, 0).Messages;
        Assert.Equal(2, kept.Count);
        Assert.Equal(5, kept[0].Seq);   // 1..4 dropped
        Assert.Equal(6, kept[1].Seq);
        Assert.Equal(6, store.LatestSeq); // cursor unchanged

        store.SetRetention(0); // ignored — no-op
        Assert.Equal(2, store.MaxMessages);
    }

    [Fact]
    public void Append_AssignsMonotonicSeq()
    {
        var store = RoomStore.Open(TempDir(), "r", 100);
        var m1 = store.Append(Posts.Make("a", "r", "one"), 10);
        var m2 = store.Append(Posts.Make("a", "r", "two"), 20);
        Assert.Equal(1, m1.Seq);
        Assert.Equal(2, m2.Seq);
        Assert.Equal(20, m2.HostTs);
        Assert.Equal(2, store.LatestSeq);
    }

    [Fact]
    public void Backfill_SinceCursor_ReturnsTail()
    {
        var store = RoomStore.Open(TempDir(), "r", 100);
        for (var i = 0; i < 5; i++) store.Append(Posts.Make("a", "r", $"m{i}"), i);

        var all = store.Backfill(0, 0);
        Assert.Equal(5, all.Messages.Count);
        Assert.True(all.Complete);
        Assert.Equal(5, all.LatestSeq);

        var tail = store.Backfill(3, 0);
        Assert.Equal(2, tail.Messages.Count);
        Assert.Equal(4, tail.Messages[0].Seq);
        Assert.Equal(5, tail.Messages[1].Seq);
    }

    [Fact]
    public void Backfill_RespectsLimit_AndFlagsIncomplete()
    {
        var store = RoomStore.Open(TempDir(), "r", 100);
        for (var i = 0; i < 5; i++) store.Append(Posts.Make("a", "r", $"m{i}"), i);

        var page = store.Backfill(0, 2);
        Assert.Equal(2, page.Messages.Count);
        Assert.False(page.Complete);
        Assert.Equal(1, page.Messages[0].Seq);
        Assert.Equal(2, page.Messages[1].Seq);
    }

    [Fact]
    public void Retention_DropsOldest_KeepsSeqMonotonic()
    {
        var store = RoomStore.Open(TempDir(), "r", 3);
        for (var i = 0; i < 6; i++) store.Append(Posts.Make("a", "r", $"m{i}"), i);

        var all = store.Backfill(0, 0);
        Assert.Equal(3, all.Messages.Count);
        Assert.Equal(4, all.Messages[0].Seq);   // 1..3 dropped
        Assert.Equal(6, all.Messages[2].Seq);
        Assert.Equal(6, store.LatestSeq);
    }

    [Fact]
    public void Persistence_SurvivesReopen_RecoversCursor()
    {
        var dir = TempDir();
        var store = RoomStore.Open(dir, "r", 100);
        store.Append(Posts.Make("a", "r", "one"), 10);
        store.Append(Posts.Make("a", "r", "two"), 20);

        var reopened = RoomStore.Open(dir, "r", 100);
        Assert.Equal(2, reopened.LatestSeq);
        var all = reopened.Backfill(0, 0);
        Assert.Equal(2, all.Messages.Count);

        // A new append continues the sequence rather than colliding.
        var m3 = reopened.Append(Posts.Make("a", "r", "three"), 30);
        Assert.Equal(3, m3.Seq);
    }

    [Fact]
    public void Persistence_CompactsToRetainedTail()
    {
        var dir = TempDir();
        var store = RoomStore.Open(dir, "r", 2);
        for (var i = 0; i < 5; i++) store.Append(Posts.Make("a", "r", $"m{i}"), i);

        // Reopen: only the retained tail persisted, cursor still at 5.
        var reopened = RoomStore.Open(dir, "r", 2);
        var all = reopened.Backfill(0, 0);
        Assert.Equal(2, all.Messages.Count);
        Assert.Equal(4, all.Messages[0].Seq);
        Assert.Equal(5, reopened.LatestSeq);
    }
}
