using System.Text.Json;
using Gomp.Protocol;

namespace Gomp.Host;

/// <summary>
/// A persisted record of one room — enough to re-register its sub-identity and
/// re-seed its allowlist after a host restart (ADR-0007 §9: identity + history
/// persist, members re-dial). The room's E-address is NOT stored: it is derived
/// deterministically from the node seed + the <c>&lt;host&gt;/&lt;name&gt;</c>
/// path, so re-registering the same name reproduces the same address.
/// </summary>
internal sealed record RoomRecord(
    string Name,
    RoomKind Kind,
    IReadOnlyList<string> Members,
    string DisplayName = "",
    string Topic = "",
    int RetentionMax = 0); // 0 = use the host's global ROOM_HISTORY_MAX default

/// <summary>
/// The set of rooms a host runs, persisted as JSON under the service data dir.
/// Mutations (create/close room, add/remove member) write through immediately so
/// a restart restores the same rooms with the same membership. In-memory access
/// is serialized; the host drives all mutations from its single base-stream
/// reader, but the lock keeps the file write and the map consistent.
/// </summary>
internal sealed class RoomCatalog
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, RoomRecord> _rooms;

    private RoomCatalog(string path, IEnumerable<RoomRecord> rooms)
    {
        _path = path;
        _rooms = new Dictionary<string, RoomRecord>(StringComparer.Ordinal);
        foreach (var r in rooms) _rooms[r.Name] = r;
    }

    public static RoomCatalog Load(string dataDir)
    {
        var path = Path.Combine(dataDir, "rooms.json");
        var rooms = new List<RoomRecord>();
        if (File.Exists(path))
        {
            try
            {
                var dto = JsonSerializer.Deserialize<CatalogDto>(File.ReadAllText(path));
                if (dto?.Rooms is { } list)
                {
                    foreach (var r in list)
                        rooms.Add(new RoomRecord(
                            r.Name, (RoomKind)r.Kind, r.Members ?? Array.Empty<string>(),
                            r.DisplayName ?? "", r.Topic ?? "", r.RetentionMax));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // A corrupt catalog must not strand the host; start empty.
            }
        }
        return new RoomCatalog(path, rooms);
    }

    public IReadOnlyList<RoomRecord> All()
    {
        lock (_gate) return _rooms.Values.ToList();
    }

    public bool Contains(string name)
    {
        lock (_gate) return _rooms.ContainsKey(name);
    }

    public bool TryGet(string name, out RoomRecord record)
    {
        lock (_gate) return _rooms.TryGetValue(name, out record!);
    }

    /// <summary>Insert or replace a room record and persist.</summary>
    public void Put(RoomRecord record)
    {
        lock (_gate)
        {
            _rooms[record.Name] = record;
            Save();
        }
    }

    /// <summary>Remove a room and persist. No-op if absent.</summary>
    public void Remove(string name)
    {
        lock (_gate)
        {
            if (_rooms.Remove(name)) Save();
        }
    }

    /// <summary>Add a member to a room's stored allowlist; persists. Returns the new record, or null if the room is unknown or already a member.</summary>
    public RoomRecord? AddMember(string name, string addr)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(name, out var rec)) return null;
            if (rec.Members.Contains(addr)) return null;
            var members = rec.Members.ToList();
            members.Add(addr);
            var updated = rec with { Members = members };
            _rooms[name] = updated;
            Save();
            return updated;
        }
    }

    /// <summary>Remove a member from a room's stored allowlist; persists. Returns the new record, or null if the room is unknown or not a member.</summary>
    public RoomRecord? RemoveMember(string name, string addr)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(name, out var rec)) return null;
            if (!rec.Members.Contains(addr)) return null;
            var members = rec.Members.Where(m => m != addr).ToList();
            var updated = rec with { Members = members };
            _rooms[name] = updated;
            Save();
            return updated;
        }
    }

    /// <summary>Change a room's kind (ACL tier); persists. Returns the new record, or null if unknown / unchanged.</summary>
    public RoomRecord? SetKind(string name, RoomKind kind)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(name, out var rec)) return null;
            if (rec.Kind == kind) return null;
            var updated = rec with { Kind = kind };
            _rooms[name] = updated;
            Save();
            return updated;
        }
    }

    /// <summary>Update a room's display name, topic and per-room retention; persists. Returns the new record, or null if unknown.</summary>
    public RoomRecord? UpdateMeta(string name, string displayName, string topic, int retentionMax)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(name, out var rec)) return null;
            var updated = rec with
            {
                DisplayName = displayName,
                Topic = topic,
                RetentionMax = retentionMax < 0 ? 0 : retentionMax,
            };
            _rooms[name] = updated;
            Save();
            return updated;
        }
    }

    private void Save()
    {
        var dto = new CatalogDto
        {
            Rooms = _rooms.Values
                .Select(r => new RoomDto
                {
                    Name = r.Name,
                    Kind = (int)r.Kind,
                    Members = r.Members.ToArray(),
                    DisplayName = r.DisplayName,
                    Topic = r.Topic,
                    RetentionMax = r.RetentionMax,
                })
                .ToArray(),
        };
        AtomicFile.Write(_path, JsonSerializer.Serialize(dto, JsonOpts));
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private sealed class CatalogDto
    {
        public RoomDto[] Rooms { get; set; } = Array.Empty<RoomDto>();
    }

    private sealed class RoomDto
    {
        public string Name { get; set; } = "";
        public int Kind { get; set; }
        public string[]? Members { get; set; }
        public string? DisplayName { get; set; }
        public string? Topic { get; set; }
        public int RetentionMax { get; set; }
    }
}
