using System.Text.Json;

namespace Gomp.Host;

/// <summary>
/// The host's authority set (ADR-0007 §4 / ticket a5fbf64b, option B). gomp runs
/// under its OWN first-class account: the base service address is BOTH the
/// membership identity AND the <see cref="Owner"/> with full authority over the
/// host and its rooms. That address isn't known until the base identity is
/// registered, so it is bound via <see cref="SetOwner"/> once registration
/// returns — and is what seeds an Invite room's allowlist, so the operator can
/// always enter their own room (the identity they connect as == the owner).
///
/// Alongside the owner sit the delegated admins: a non-persisted
/// <see cref="_seedAdmin"/> read from the <c>ROOM_HOST_OWNER</c> env overlay each
/// boot (the operator's node account by default, or a remote operator on a
/// headless host) plus the operator-promoted <see cref="_admins"/> persisted
/// under the service data dir. Neither the owner nor the env seed persists — both
/// are re-derived each boot, so rotating either can't be overridden by stale
/// state.
///
/// An admin op is authorized by checking the connection's daemon-verified
/// <c>from_addr</c> against <see cref="IsAuthorized"/>. The host cannot be spoofed
/// about who is calling (ADR-0002 attribution).
/// </summary>
internal sealed class AdminState
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _seedAdmin;       // env ROOM_HOST_OWNER: a delegated admin, never persisted
    private readonly HashSet<string> _admins;  // operator-promoted admins, persisted

    /// <summary>
    /// The host owner — gomp's own base service address (option B). Empty until
    /// <see cref="SetOwner"/> runs after the base identity is registered. The owner
    /// is authority by role: never a listed admin and never persisted.
    /// </summary>
    public string Owner { get; private set; } = "";

    private AdminState(string seedAdmin, string path, IEnumerable<string> admins)
    {
        _seedAdmin = seedAdmin;
        _path = path;
        _admins = new HashSet<string>(admins, StringComparer.Ordinal);
        _admins.Remove(seedAdmin); // the env seed is authority by env, not a persisted admin
    }

    /// <summary>
    /// Load admin state from <paramref name="dataDir"/>, seeding the delegated
    /// admin from <paramref name="seedAdmin"/> (the <c>ROOM_HOST_OWNER</c> env
    /// value). The owner is bound later via <see cref="SetOwner"/> once the base
    /// identity is registered.
    /// </summary>
    public static AdminState Load(string dataDir, string seedAdmin)
    {
        if (string.IsNullOrWhiteSpace(seedAdmin))
            throw new ArgumentException(
                "ROOM_HOST_OWNER is required (the initial admin delegated authority over this host)",
                nameof(seedAdmin));

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
                // A corrupt admin file must not strand the host; start with the env
                // seed only. The seed is always authority.
            }
        }
        return new AdminState(seedAdmin, path, admins);
    }

    /// <summary>
    /// Bind the owner identity — gomp's base service address — once the base is
    /// registered (option B). Drops the address from the admin set if it somehow
    /// lingered there (the owner is authority by role, not by being listed).
    /// </summary>
    public void SetOwner(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("owner address is required", nameof(owner));
        lock (_gate)
        {
            Owner = owner;
            _admins.Remove(owner);
        }
    }

    /// <summary>True when <paramref name="addr"/> is the owner, the env seed admin, or a promoted admin.</summary>
    public bool IsAuthorized(string addr)
    {
        if (string.IsNullOrEmpty(addr)) return false;
        lock (_gate) return addr == Owner || addr == _seedAdmin || _admins.Contains(addr);
    }

    /// <summary>
    /// The base-identity manifest allowlist: who may DIAL the base to drive admin
    /// remotely — the env seed admin plus the promoted admins. The owner is gomp's
    /// own base address, never in its own allowlist, so it is excluded here.
    /// Always non-empty (the env seed is required).
    /// </summary>
    public IReadOnlyList<string> RemoteAdminAddresses()
    {
        lock (_gate)
        {
            var list = new List<string>(_admins.Count + 1) { _seedAdmin };
            list.AddRange(_admins);
            return list;
        }
    }

    /// <summary>
    /// Promote <paramref name="addr"/> to admin. Returns false (no-op) if it is
    /// already authority (owner or env seed) or already an admin. Persists on change.
    /// </summary>
    public bool Promote(string addr)
    {
        lock (_gate)
        {
            if (addr == Owner || addr == _seedAdmin) return false;
            if (!_admins.Add(addr)) return false;
            Save();
            return true;
        }
    }

    /// <summary>
    /// Demote <paramref name="addr"/>. Returns false if it was not a promoted admin
    /// (the owner and the env seed can never be demoted). Persists on change.
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
