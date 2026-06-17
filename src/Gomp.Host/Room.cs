using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Gomp.Protocol;

namespace Gomp.Host;

/// <summary>
/// One live room: a host sub-identity (<c>&lt;host&gt;/&lt;name&gt;</c>) that
/// members dial. Owns the room's roster (presence), fans member posts out with
/// sender-signed attribution relayed verbatim (ADR-0007 §6), serves since-cursor
/// backfill (§7), and pushes presence updates (§8).
///
/// All inbound handling is driven from the host's single per-room stream reader,
/// so the methods here are invoked serially; the roster lock only guards against
/// a fan-out enumeration racing a presence mutation.
/// </summary>
internal sealed class Room
{
    private readonly IRoomTransport _transport;
    private readonly RoomStore _store;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly HashSet<string> _online = new(StringComparer.Ordinal);
    // Per-member identity binding, learned from each member's Hello. Stored and
    // relayed VERBATIM — the host never verifies a binding (recipients do); it
    // is just the distribution vehicle so peers can verify-once + pin (§6).
    private readonly Dictionary<string, IdentityBinding> _bindings = new(StringComparer.Ordinal);

    public string Name { get; }
    public RoomKind Kind { get; }

    /// <summary>The room's dialable E-address (share this to invite members).</summary>
    public string Address => _transport.Address;

    public Room(string name, RoomKind kind, IRoomTransport transport, RoomStore store, ILogger log)
    {
        Name = name;
        Kind = kind;
        _transport = transport;
        _store = store;
        _log = log;
    }

    /// <summary>Count of currently connected members (presence).</summary>
    public int OnlineCount
    {
        get { lock (_gate) return _online.Count; }
    }

    /// <summary>Snapshot of the currently-connected member addresses (for the management roster).</summary>
    public IReadOnlyList<string> OnlineMembers()
    {
        lock (_gate) return _online.ToList();
    }

    /// <summary>Wipe the room's stored history (admin "clear history").</summary>
    public void ClearHistory() => _store.Clear();

    /// <summary>Apply a per-room retention override at runtime (admin settings).</summary>
    public void SetRetention(int maxMessages) => _store.SetRetention(maxMessages);

    // ---- lifecycle events (roster + presence, ADR-0007 §5/§8) ----

    /// <summary>A member's connection became established: add to roster, send them
    /// the current roster, and announce their arrival to everyone else.</summary>
    public async Task OnConnectionEstablishedAsync(string addr)
    {
        List<string> others;
        lock (_gate)
        {
            _online.Add(addr);
            others = _online.Where(a => a != addr).ToList();
        }
        // Give the joiner the current roster up front (saves a round trip).
        await SendAsync(addr, new RoomEnvelope { Roster = BuildRoster() }).ConfigureAwait(false);
        // Announce the arrival to the rest.
        await PushPresenceAsync(others, addr, online: true).ConfigureAwait(false);
    }

    /// <summary>A member's connection dropped: remove from roster, announce departure.</summary>
    public async Task OnConnectionClosedAsync(string addr)
    {
        List<string> others;
        lock (_gate)
        {
            if (!_online.Remove(addr)) return;
            // Drop the binding too: a reconnect re-announces via Hello (and the
            // member may have rotated its key), so we never relay a stale one.
            _bindings.Remove(addr);
            others = _online.ToList();
        }
        await PushPresenceAsync(others, addr, online: false).ConfigureAwait(false);
    }

    // ---- inbound room-ops (ADR-0007 §6/§7) ----

    /// <summary>Dispatch an inbound room-ops payload from member <paramref name="from"/>.</summary>
    public async Task OnRpcMessageAsync(string from, byte[] payload)
    {
        RoomEnvelope env;
        try
        {
            env = RoomEnvelope.Parser.ParseFrom(payload);
        }
        catch (InvalidProtocolBufferException)
        {
            await ReplyErrorAsync(from, "bad_envelope", "could not parse room envelope").ConfigureAwait(false);
            return;
        }

        switch (env.BodyCase)
        {
            case RoomEnvelope.BodyOneofCase.Submit:
                await HandleSubmitAsync(from, env.Submit).ConfigureAwait(false);
                break;
            case RoomEnvelope.BodyOneofCase.BackfillReq:
                await HandleBackfillAsync(from, env.BackfillReq).ConfigureAwait(false);
                break;
            case RoomEnvelope.BodyOneofCase.RosterReq:
                await SendAsync(from, new RoomEnvelope { Roster = BuildRoster() }).ConfigureAwait(false);
                break;
            case RoomEnvelope.BodyOneofCase.Hello:
                await HandleHelloAsync(from, env.Hello).ConfigureAwait(false);
                break;
            default:
                // room->member variants arriving from a member, or unset — ignore.
                await ReplyErrorAsync(from, "unexpected", $"unexpected room body {env.BodyCase}").ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleSubmitAsync(string from, SubmitPost submit)
    {
        var post = submit.Post;
        if (post is null || post.Body.IsEmpty)
        {
            await ReplyErrorAsync(from, "empty_post", "post carried no signed body").ConfigureAwait(false);
            return;
        }

        SignedPostBody body;
        try
        {
            body = SignedPostBody.Parser.ParseFrom(post.Body);
        }
        catch (InvalidProtocolBufferException)
        {
            await ReplyErrorAsync(from, "bad_post", "signed body did not parse").ConfigureAwait(false);
            return;
        }

        // Anti-spoof: the transport-verified sender must match the claimed author,
        // and the post must name THIS room. The host never forges — it relays the
        // SignedPost verbatim; recipients verify `sig` over `body` against the
        // author's key (the host has no author pubkey and does not verify it).
        if (body.Sender != from)
        {
            await ReplyErrorAsync(from, "sender_mismatch", "post sender does not match the connection").ConfigureAwait(false);
            return;
        }
        if (body.Room != Address)
        {
            await ReplyErrorAsync(from, "wrong_room", "post is addressed to a different room").ConfigureAwait(false);
            return;
        }

        var stored = _store.Append(post, NowMs());

        // Fan out the sequenced message to every connected member — including the
        // sender, for whom it is the authoritative seq confirmation.
        List<string> recipients;
        lock (_gate) recipients = _online.ToList();
        var fanout = new RoomEnvelope { Message = stored };
        var bytes = fanout.ToByteArray();
        foreach (var member in recipients)
            await SendRawAsync(member, bytes).ConfigureAwait(false);
    }

    private Task HandleBackfillAsync(string from, BackfillRequest req)
    {
        var resp = _store.Backfill(req.SinceSeq, req.Limit);
        return SendAsync(from, new RoomEnvelope { BackfillResp = resp });
    }

    /// <summary>A member handed us its identity binding (§6): stash it (verbatim,
    /// unverified) and push it to everyone already in the room via a presence
    /// update so they can verify-once. Future joiners pick it up from the roster.</summary>
    private async Task HandleHelloAsync(string from, Hello hello)
    {
        if (hello.Binding is null || hello.Binding.Binding.IsEmpty)
        {
            await ReplyErrorAsync(from, "bad_hello", "hello carried no binding").ConfigureAwait(false);
            return;
        }

        List<string> others;
        lock (_gate)
        {
            // Ignore a Hello from a connection we don't consider online (mid-teardown).
            if (!_online.Contains(from)) return;
            _bindings[from] = hello.Binding;
            others = _online.Where(a => a != from).ToList();
        }

        var env = new RoomEnvelope
        {
            Presence = new PresenceUpdate { Addr = from, Online = true, Binding = hello.Binding },
        };
        var bytes = env.ToByteArray();
        foreach (var member in others)
            await SendRawAsync(member, bytes).ConfigureAwait(false);
    }

    // ---- membership (driven by admin ops on the host) ----

    /// <summary>Grant a member address on the room's live allowlist (invite rooms).</summary>
    public Task AddMemberAsync(string addr) => _transport.UpdateAllowlistAsync(add: new[] { addr }, remove: null);

    /// <summary>Revoke a member and drop their live link — the kick (ADR-0007 §5).</summary>
    public async Task RemoveMemberAsync(string addr)
    {
        // Revoke first so the kicked peer's reconnect watchdog hits a closed gate,
        // then drop the live link.
        if (Kind == RoomKind.Invite)
            await _transport.UpdateAllowlistAsync(add: null, remove: new[] { addr }).ConfigureAwait(false);
        await _transport.DisconnectAsync(addr).ConfigureAwait(false);
    }

    // ---- helpers ----

    private Roster BuildRoster()
    {
        var roster = new Roster();
        lock (_gate)
        {
            foreach (var a in _online)
            {
                var member = new Member { Addr = a, Online = true };
                if (_bindings.TryGetValue(a, out var binding))
                    member.Binding = binding;
                roster.Members.Add(member);
            }
        }
        return roster;
    }

    private async Task PushPresenceAsync(IEnumerable<string> recipients, string addr, bool online)
    {
        var env = new RoomEnvelope { Presence = new PresenceUpdate { Addr = addr, Online = online } };
        var bytes = env.ToByteArray();
        foreach (var member in recipients)
            await SendRawAsync(member, bytes).ConfigureAwait(false);
    }

    private Task ReplyErrorAsync(string to, string code, string message)
        => SendAsync(to, new RoomEnvelope { Error = new RoomError { Code = code, Message = message } });

    private Task SendAsync(string to, RoomEnvelope env) => SendRawAsync(to, env.ToByteArray());

    private async Task SendRawAsync(string to, byte[] bytes)
    {
        try
        {
            await _transport.SendAsync(to, bytes).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A send to a member who just dropped is expected and harmless; the
            // roster self-heals on the connection_closed sweep.
            _log.LogDebug(ex, "room {Room}: send to {Member} failed", Name, to);
        }
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
