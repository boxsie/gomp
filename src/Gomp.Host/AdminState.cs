using System.Text.Json;

namespace Gomp.Host;

/// <summary>
/// The host's authority set (ADR-0007 §4): a single <see cref="Owner"/> seeded
/// from the <c>ROOM_HOST_OWNER</c> env overlay (ADR-0009 §11) plus a mutable set
/// of designated <see cref="Admins"/> the owner promotes. Admins are per-host
/// (global across rooms) in v1 and persist under the service data dir so they
/// survive a restart; the owner never persists — it is re-read from the
/// environment each boot, so rotating the operator address can't be overridden
/// by stale state.
///
/// An admin operation is authorized by checking the connection's
/// daemon-verified <c>from_addr</c> against <see cref="IsAuthorized"/>. The host
/// cannot be spoofed about who is calling (ADR-0002 attribution).
/// </summary>
internal sealed class AdminState
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly HashSet<string> _admins;

    /// <summary>The local operator's address; full authority, immutable at runtime.</summary>
    public string Owner { get; }

    private AdminState(string owner, string path, IEnumerable<string> admins)
    {
        Owner = owner;
        _path = path;
        _admins = new HashSet<string>(admins, StringComparer.Ordinal);
    }

    /// <summary>
    /// Load admin state from <paramref name="dataDir"/>, seeding the owner from
    /// <paramref name="owner"/>. An owner that somehow lingers in the persisted
    /// admin set is dropped (it is authority by being the owner, not by being a
    /// listed admin).
    /// </summary>
    public static AdminState Load(string dataDir, string owner)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("ROOM_HOST_OWNER is required (the host has no operator)", nameof(owner));

        var path = Path.Combine(dataDir, "admins.json");
        var admins = new List<string>();
        if (File.Exists(path))
        {
            try
            {
                var raw = File.ReadAllText(path);
                var dto = JsonSerializer.Deserialize<AdminsDto>(raw);
                if (dto?.Admins is { } a) admins.AddRange(a);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // A corrupt admin file must not strand the host; start with the
                // owner only. The owner is always authority.
            }
        }
        admins.RemoveAll(a => a == owner);
        return new AdminState(owner, path, admins);
    }

    /// <summary>True when <paramref name="addr"/> is the owner or a designated admin.</summary>
    public bool IsAuthorized(string addr)
    {
        if (addr == Owner) return true;
        lock (_gate) return _admins.Contains(addr);
    }

    /// <summary>Every address with authority — the owner plus all admins.</summary>
    public IReadOnlyList<string> AuthorizedAddresses()
    {
        lock (_gate)
        {
            var list = new List<string>(_admins.Count + 1) { Owner };
            list.AddRange(_admins);
            return list;
        }
    }

    /// <summary>
    /// Promote <paramref name="addr"/> to admin. Returns false (no-op) if it is
    /// already the owner or already an admin. Persists on change.
    /// </summary>
    public bool Promote(string addr)
    {
        if (addr == Owner) return false;
        lock (_gate)
        {
            if (!_admins.Add(addr)) return false;
            Save();
            return true;
        }
    }

    /// <summary>
    /// Demote <paramref name="addr"/>. Returns false if it was not an admin (the
    /// owner can never be demoted). Persists on change.
    /// </summary>
    public bool Demote(string addr)
    {
        lock (_gate)
        {
            if (!_admins.Remove(addr)) return false;
            Save();
            return true;
        }
    }

    private void Save()
    {
        var dto = new AdminsDto { Admins = _admins.ToArray() };
        var json = JsonSerializer.Serialize(dto, JsonOpts);
        AtomicFile.Write(_path, json);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private sealed class AdminsDto
    {
        public string[] Admins { get; set; } = Array.Empty<string>();
    }
}
