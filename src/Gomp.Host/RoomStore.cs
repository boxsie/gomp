using Google.Protobuf;
using Gomp.Protocol;

namespace Gomp.Host;

/// <summary>
/// Per-room message history (ADR-0007 §7): a monotonic, append-only log the host
/// persists under the service data dir so late joiners and reconnectors backfill
/// via a since-cursor, and the room survives a host restart with its history
/// intact. Retention is count-based in v1 (<see cref="_maxMessages"/>): the
/// oldest messages drop once the cap is exceeded.
///
/// On disk the log is length-delimited <see cref="RoomMessage"/> protobuf
/// (<c>WriteDelimitedTo</c>), append-only, with a compaction pass on load and
/// whenever the live tail is trimmed — so the file never grows without bound.
/// The sequence counter is the backfill cursor and is recovered from the last
/// stored message on load, so cursors stay stable across restarts.
///
/// Access is serialized: the host drives append/backfill from a single
/// per-room stream reader, but the lock guards against a fan-out push racing a
/// concurrent backfill.
/// </summary>
internal sealed class RoomStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly int _maxMessages;
    private readonly LinkedList<RoomMessage> _messages = new();
    private long _seq;

    /// <summary>Default backfill batch when the client asks for 0.</summary>
    public const int DefaultBackfillLimit = 200;

    /// <summary>Hard cap on a single backfill batch regardless of the client's ask.</summary>
    public const int MaxBackfillLimit = 500;

    private RoomStore(string path, int maxMessages)
    {
        _path = path;
        _maxMessages = maxMessages;
    }

    /// <summary>
    /// Open (or create) the history for room <paramref name="name"/> under
    /// <paramref name="dataDir"/>. <paramref name="maxMessages"/> is the
    /// retention cap (older messages are dropped). Loads existing history and
    /// recovers the sequence counter; compacts the on-disk log to the retained
    /// tail.
    /// </summary>
    public static RoomStore Open(string dataDir, string name, int maxMessages)
    {
        if (maxMessages <= 0) maxMessages = 1000;
        var path = Path.Combine(dataDir, "rooms", name, "history.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new RoomStore(path, maxMessages);
        store.Load();
        return store;
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        var loaded = new List<RoomMessage>();
        try
        {
            using var fs = File.OpenRead(_path);
            while (fs.Position < fs.Length)
            {
                var msg = RoomMessage.Parser.ParseDelimitedFrom(fs);
                if (msg is null) break;
                loaded.Add(msg);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidProtocolBufferException)
        {
            // A truncated tail (crash mid-append) leaves earlier records intact;
            // keep whatever parsed cleanly and move on.
        }

        // Keep only the retained tail and recover the cursor from it.
        var keep = loaded.Count > _maxMessages ? loaded.GetRange(loaded.Count - _maxMessages, _maxMessages) : loaded;
        foreach (var m in keep) _messages.AddLast(m);
        _seq = _messages.Count > 0 ? _messages.Last!.Value.Seq : 0;

        // Compact if we dropped anything, so the file matches the retained tail.
        if (keep.Count != loaded.Count) Rewrite();
    }

    /// <summary>The highest sequence the store holds (0 when empty).</summary>
    public long LatestSeq
    {
        get { lock (_gate) return _seq; }
    }

    /// <summary>
    /// Append a member's verbatim <see cref="SignedPost"/>, stamping a fresh
    /// monotonic sequence and host receive time. Returns the stored
    /// <see cref="RoomMessage"/> ready to fan out. Enforces retention.
    /// </summary>
    public RoomMessage Append(SignedPost post, long hostTsMs)
    {
        lock (_gate)
        {
            var msg = new RoomMessage { Seq = ++_seq, HostTs = hostTsMs, Post = post };
            _messages.AddLast(msg);

            using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write))
                msg.WriteDelimitedTo(fs);

            if (_messages.Count > _maxMessages)
            {
                // Trim the live tail, then compact the file to match so it can't
                // grow unbounded across a long-lived room.
                while (_messages.Count > _maxMessages) _messages.RemoveFirst();
                Rewrite();
            }
            return msg;
        }
    }

    /// <summary>
    /// Messages with <c>seq &gt; sinceSeq</c>, oldest-first, capped by
    /// <paramref name="limit"/> (0 = <see cref="DefaultBackfillLimit"/>, clamped
    /// to <see cref="MaxBackfillLimit"/>). <c>complete</c> is false when the
    /// batch was truncated and the client should fetch again from the last seq.
    /// </summary>
    public BackfillResponse Backfill(long sinceSeq, int limit)
    {
        if (limit <= 0) limit = DefaultBackfillLimit;
        if (limit > MaxBackfillLimit) limit = MaxBackfillLimit;

        var resp = new BackfillResponse();
        lock (_gate)
        {
            resp.LatestSeq = _seq;
            var taken = 0;
            var truncated = false;
            foreach (var m in _messages)
            {
                if (m.Seq <= sinceSeq) continue;
                if (taken >= limit) { truncated = true; break; }
                resp.Messages.Add(m);
                taken++;
            }
            resp.Complete = !truncated;
        }
        return resp;
    }

    // Caller holds _gate.
    private void Rewrite()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        {
            foreach (var m in _messages) m.WriteDelimitedTo(fs);
        }
        File.Move(tmp, _path, overwrite: true);
    }
}
