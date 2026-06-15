using Google.Protobuf;
using Gomp.Protocol;

namespace Gomp.Client;

/// <summary>
/// One joined room from the member's side — the mirror of the host's
/// <c>Room</c>. Speaks <see cref="RoomEnvelope"/> to a single room sub-identity:
/// announces the member's binding on connect (Hello), submits signed posts,
/// pulls since-cursor history, and routes inbound fan-out / roster / presence /
/// errors to an <see cref="IRoomObserver"/>. Authorship verification rides on a
/// per-room <see cref="MemberDirectory"/>.
///
/// Connection lifecycle is owned by <see cref="GompClient"/> (which dials and
/// routes events); this type is the room-ops speaker only. Inbound methods are
/// driven from <see cref="GompClient"/>'s single event router, so they run
/// serially for a given room.
/// </summary>
public sealed class RoomSession
{
    private readonly IRoomClientTransport _tx;
    private readonly PostSigner _signer;
    private readonly MemberDirectory _directory;
    private readonly IdentityBinding _ownBinding;
    private readonly IRoomObserver _observer;
    private readonly bool _requestHistory;
    private bool _historyRequested;

    /// <summary>The room's E-address.</summary>
    public string RoomAddress { get; }

    internal RoomSession(
        string roomAddress,
        IRoomClientTransport tx,
        IdentityBinding ownBinding,
        IRoomObserver observer,
        bool requestHistory,
        Func<long>? clock = null,
        Func<string>? nonce = null)
    {
        RoomAddress = roomAddress;
        _tx = tx;
        _ownBinding = ownBinding;
        _observer = observer;
        _requestHistory = requestHistory;
        _signer = new PostSigner(tx, clock, nonce);
        _directory = new MemberDirectory(tx);
    }

    /// <summary>Compose and submit a chat post (UTF-8 text) to the room.</summary>
    public async Task SendChatAsync(string text, CancellationToken ct = default)
    {
        var post = await _signer.SignAsync(RoomAddress, System.Text.Encoding.UTF8.GetBytes(text), ct).ConfigureAwait(false);
        await SendAsync(new RoomEnvelope { Submit = new SubmitPost { Post = post } }, ct).ConfigureAwait(false);
    }

    /// <summary>Request history after <paramref name="sinceSeq"/> (0 = from the
    /// earliest the host still retains).</summary>
    public Task BackfillAsync(long sinceSeq, int limit = 0, CancellationToken ct = default)
        => SendAsync(new RoomEnvelope { BackfillReq = new BackfillRequest { SinceSeq = sinceSeq, Limit = limit } }, ct);

    /// <summary>Ask the host for a fresh roster snapshot.</summary>
    public Task RequestRosterAsync(CancellationToken ct = default)
        => SendAsync(new RoomEnvelope { RosterReq = new RosterRequest() }, ct);

    // ---- lifecycle (driven by GompClient) ----

    /// <summary>The link to the room became usable (initial connect or reconnect):
    /// (re)announce our binding via Hello so the host can distribute it, and pull
    /// history once. Re-announcing on reconnect is safe — the host stash + roster
    /// distribution is idempotent.</summary>
    internal async Task OnConnectedAsync(CancellationToken ct = default)
    {
        await SendAsync(new RoomEnvelope { Hello = new Hello { Binding = _ownBinding } }, ct).ConfigureAwait(false);
        if (_requestHistory && !_historyRequested)
        {
            _historyRequested = true;
            await BackfillAsync(0, 0, ct).ConfigureAwait(false);
        }
    }

    // ---- inbound room-ops (from == RoomAddress) ----

    internal async Task OnRpcMessageAsync(byte[] payload)
    {
        RoomEnvelope env;
        try
        {
            env = RoomEnvelope.Parser.ParseFrom(payload);
        }
        catch (InvalidProtocolBufferException)
        {
            return;
        }

        switch (env.BodyCase)
        {
            case RoomEnvelope.BodyOneofCase.Message:
                await HandleMessageAsync(env.Message).ConfigureAwait(false);
                break;
            case RoomEnvelope.BodyOneofCase.BackfillResp:
                foreach (var m in env.BackfillResp.Messages)
                    await HandleMessageAsync(m).ConfigureAwait(false);
                break;
            case RoomEnvelope.BodyOneofCase.Roster:
                await HandleRosterAsync(env.Roster).ConfigureAwait(false);
                break;
            case RoomEnvelope.BodyOneofCase.Presence:
                await HandlePresenceAsync(env.Presence).ConfigureAwait(false);
                break;
            case RoomEnvelope.BodyOneofCase.Error:
                await _observer.OnErrorAsync(env.Error.Code, env.Error.Message).ConfigureAwait(false);
                break;
            // member->room bodies echoed back, or unset: nothing to do.
        }
    }

    private async Task HandleMessageAsync(RoomMessage m)
    {
        var post = m.Post;
        if (post is null || post.Body.IsEmpty)
            return;

        SignedPostBody body;
        try
        {
            body = SignedPostBody.Parser.ParseFrom(post.Body);
        }
        catch (InvalidProtocolBufferException)
        {
            return;
        }

        var trust = _directory.VerifyPost(body.Sender, post.Body.ToByteArray(), post.Sig.ToByteArray());
        await _observer.OnPostAsync(new ReceivedPost(
            m.Seq, m.HostTs, body.Sender, body.Content.ToByteArray(), body.ClientTs, body.Nonce, trust))
            .ConfigureAwait(false);
    }

    private async Task HandleRosterAsync(Roster roster)
    {
        var members = new List<RoomMemberPresence>(roster.Members.Count);
        foreach (var m in roster.Members)
        {
            // Verify-once any binding the host distributed for this peer.
            await _directory.LearnBindingAsync(m.Addr, m.Binding).ConfigureAwait(false);
            members.Add(new RoomMemberPresence(m.Addr, m.Online));
        }
        await _observer.OnRosterAsync(members).ConfigureAwait(false);
    }

    private async Task HandlePresenceAsync(PresenceUpdate p)
    {
        // A join carries the arriving member's binding once the host holds it.
        if (p.Online)
            await _directory.LearnBindingAsync(p.Addr, p.Binding).ConfigureAwait(false);
        await _observer.OnPresenceAsync(new RoomMemberPresence(p.Addr, p.Online)).ConfigureAwait(false);
    }

    private Task SendAsync(RoomEnvelope env, CancellationToken ct)
        => _tx.SendAsync(RoomAddress, env.ToByteArray(), ct);

    /// <summary>Verified-authorship view for this room (test/inspection seam).</summary>
    internal MemberDirectory Directory => _directory;
}
