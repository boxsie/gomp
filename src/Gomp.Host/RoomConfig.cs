using Gomp.Protocol;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Gomp.Host;

/// <summary>One room as declared by the operator in <c>data/rooms.yaml</c>.</summary>
internal sealed record RoomConfigEntry(string Name, RoomKind Kind, IReadOnlyList<string> Members);

/// <summary>Thrown when <c>data/rooms.yaml</c> exists but cannot be parsed.</summary>
internal sealed class RoomConfigException : Exception
{
    public RoomConfigException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// The outcome of reconciling a <see cref="RoomCatalog"/> from operator config.
/// Pure data so the reconcile step is unit-testable without a daemon or logger;
/// the host turns it into log lines.
/// </summary>
internal sealed record ReconcileResult(
    IReadOnlyList<string> Created,
    IReadOnlyList<string> MembersAdded,
    IReadOnlyList<string> Skipped)
{
    public static readonly ReconcileResult Empty =
        new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>
/// The operator-authored room definitions for a <b>headless</b> host. Drop a
/// <c>rooms.yaml</c> into the service data dir and (re)start: on boot the host
/// reconciles its <see cref="RoomCatalog"/> from this file, so rooms can be
/// defined with no frontend and no live command channel (Dan's call, 2026-06-16:
/// "config + restart" is always preferred over come-up-then-issue-commands).
///
/// <para><b>Ensure-exists semantics.</b> Config only ever <i>adds</i>: a declared
/// room missing from the catalog is created; declared members missing from an
/// existing invite room are added. Config <b>never deletes</b> a room and never
/// changes a room's kind — removing a line from the file does not nuke a room
/// with history, and rooms created live via admin ops coexist with config ones.
/// </para>
///
/// Schema (all keys lower-case):
/// <code>
/// rooms:
///   - name: lobby
///     kind: open            # open | friends | invite
///   - name: vip
///     kind: invite
///     members: [Ealice, Ebob]
/// </code>
/// </summary>
internal static class RoomConfig
{
    public const string FileName = "rooms.yaml";

    /// <summary>
    /// Parse <c><paramref name="dataDir"/>/rooms.yaml</c>. Returns an empty list
    /// when the file is absent (the common headless case with no config yet).
    /// Throws <see cref="RoomConfigException"/> when the file exists but is not
    /// valid YAML — an operator who fat-fingers the config should see it fail
    /// loudly rather than have rooms silently vanish.
    /// </summary>
    public static IReadOnlyList<RoomConfigEntry> Load(string dataDir)
    {
        var path = Path.Combine(dataDir, FileName);
        if (!File.Exists(path))
            return Array.Empty<RoomConfigEntry>();

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            throw new RoomConfigException($"cannot read {FileName}: {ex.Message}", ex);
        }

        return Parse(text);
    }

    /// <summary>Parse rooms config from raw YAML text (the testable seam).</summary>
    public static IReadOnlyList<RoomConfigEntry> Parse(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return Array.Empty<RoomConfigEntry>();

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        ConfigDto? dto;
        try
        {
            dto = deserializer.Deserialize<ConfigDto>(yaml);
        }
        catch (YamlException ex)
        {
            throw new RoomConfigException($"malformed {FileName}: {ex.Message}", ex);
        }

        if (dto?.Rooms is not { } rooms)
            return Array.Empty<RoomConfigEntry>();

        var entries = new List<RoomConfigEntry>(rooms.Count);
        foreach (var r in rooms)
        {
            var name = (r.Name ?? string.Empty).Trim();
            var members = (IReadOnlyList<string>?)r.Members?.Where(m => !string.IsNullOrWhiteSpace(m)).ToList()
                          ?? Array.Empty<string>();
            // Kind defaults to Unspecified when unparseable; reconcile decides
            // whether to skip. Carry the raw entry through so the skip reason can
            // name the offending room.
            entries.Add(new RoomConfigEntry(name, ParseKind(r.Kind), members));
        }
        return entries;
    }

    /// <summary>
    /// Apply <paramref name="config"/> to <paramref name="catalog"/> with
    /// ensure-exists semantics (create-if-missing, add-only members, never delete,
    /// never change kind). Returns a <see cref="ReconcileResult"/> describing what
    /// changed and what was skipped. Pure apart from the catalog write-through.
    /// </summary>
    public static ReconcileResult Reconcile(IReadOnlyList<RoomConfigEntry> config, RoomCatalog catalog)
    {
        if (config.Count == 0)
            return ReconcileResult.Empty;

        var created = new List<string>();
        var membersAdded = new List<string>();
        var skipped = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in config)
        {
            if (entry.Name.Length == 0)
            {
                skipped.Add("(unnamed): empty name");
                continue;
            }
            if (!seen.Add(entry.Name))
            {
                skipped.Add($"{entry.Name}: duplicate in config");
                continue;
            }
            if (entry.Kind == RoomKind.Unspecified)
            {
                skipped.Add($"{entry.Name}: missing or unknown kind (want open|friends|invite)");
                continue;
            }

            if (!catalog.TryGet(entry.Name, out var existing))
            {
                catalog.Put(new RoomRecord(entry.Name, entry.Kind, entry.Members.ToList()));
                created.Add(entry.Name);
                continue;
            }

            // Room already exists: ensure-exists means we touch nothing destructive.
            if (existing.Kind != entry.Kind)
                skipped.Add($"{entry.Name}: kind differs (config={entry.Kind}, existing={existing.Kind}); keeping existing");

            // Add-only member merge, and only where the *existing* room is invite —
            // open/friends rooms don't carry an allowlist.
            if (existing.Kind == RoomKind.Invite)
            {
                foreach (var addr in entry.Members)
                {
                    if (catalog.AddMember(entry.Name, addr) is not null)
                        membersAdded.Add($"{entry.Name}: {addr}");
                }
            }
        }

        return new ReconcileResult(created, membersAdded, skipped);
    }

    private static RoomKind ParseKind(string? raw) => (raw ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "open" => RoomKind.Open,
        "friends" => RoomKind.Friends,
        "invite" => RoomKind.Invite,
        _ => RoomKind.Unspecified,
    };

    private sealed class ConfigDto
    {
        public List<RoomDto>? Rooms { get; set; }
    }

    private sealed class RoomDto
    {
        public string? Name { get; set; }
        public string? Kind { get; set; }
        public List<string>? Members { get; set; }
    }
}
