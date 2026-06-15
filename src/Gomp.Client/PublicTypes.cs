namespace Gomp.Client;

/// <summary>
/// How much a received post's authorship can be trusted, given what the member
/// currently knows about the author (ADR-0007 §6).
/// </summary>
public enum PostTrust
{
    /// <summary>The author's binding was verified and the post's ed25519
    /// signature checks out against the bound key — unforgeable by the host.</summary>
    Verified,

    /// <summary>No verified binding held for the author yet (their Hello hasn't
    /// propagated, or the host is running host-trusted): the host's attribution
    /// is taken on trust. Upgrades to <see cref="Verified"/> once the binding
    /// arrives.</summary>
    Unverified,

    /// <summary>A verified binding IS held for the author but the post's
    /// signature does NOT verify against it — a forgery attempt (a malicious
    /// host fabricating a post under a member's name). Drop or flag.</summary>
    Forged,
}

/// <summary>
/// A post delivered to the member (a live fan-out or a backfilled history
/// entry), already authorship-checked.
/// </summary>
/// <param name="Seq">Host-assigned monotonic sequence (the backfill cursor).</param>
/// <param name="HostTs">Host receive time, unix millis (host-asserted).</param>
/// <param name="Sender">Author E-address (verified to match the signature when
///   <see cref="Trust"/> is <see cref="PostTrust.Verified"/>).</param>
/// <param name="Content">Application content bytes (chat text, etc.).</param>
/// <param name="ClientTs">Author wall-clock at compose time, unix millis.</param>
/// <param name="Nonce">Per-message unique token (dedup / message-id basis).</param>
/// <param name="Trust">Authorship trust level — see <see cref="PostTrust"/>.</param>
public sealed record ReceivedPost(
    long Seq,
    long HostTs,
    string Sender,
    byte[] Content,
    long ClientTs,
    string Nonce,
    PostTrust Trust);

/// <summary>A roster/presence entry: a member and whether it's currently online.</summary>
public sealed record RoomMemberPresence(string Addr, bool Online);

/// <summary>
/// Callbacks a room raises to its consumer (the UI). All are invoked from the
/// inbound stream in order; keep handlers quick or hand off. The default
/// adapter <see cref="DelegateRoomObserver"/> wraps plain lambdas.
/// </summary>
public interface IRoomObserver
{
    /// <summary>A post arrived (live or backfilled), with its trust level.</summary>
    Task OnPostAsync(ReceivedPost post);

    /// <summary>A member joined or left.</summary>
    Task OnPresenceAsync(RoomMemberPresence presence);

    /// <summary>A full roster snapshot arrived (on join, or on request).</summary>
    Task OnRosterAsync(IReadOnlyList<RoomMemberPresence> members);

    /// <summary>The room reported an error (e.g. <c>sender_mismatch</c>).</summary>
    Task OnErrorAsync(string code, string message);
}

/// <summary>Adapts plain delegates to <see cref="IRoomObserver"/>; any callback
/// left null is a no-op.</summary>
public sealed class DelegateRoomObserver : IRoomObserver
{
    private readonly Func<ReceivedPost, Task>? _onPost;
    private readonly Func<RoomMemberPresence, Task>? _onPresence;
    private readonly Func<IReadOnlyList<RoomMemberPresence>, Task>? _onRoster;
    private readonly Func<string, string, Task>? _onError;

    public DelegateRoomObserver(
        Func<ReceivedPost, Task>? onPost = null,
        Func<RoomMemberPresence, Task>? onPresence = null,
        Func<IReadOnlyList<RoomMemberPresence>, Task>? onRoster = null,
        Func<string, string, Task>? onError = null)
    {
        _onPost = onPost;
        _onPresence = onPresence;
        _onRoster = onRoster;
        _onError = onError;
    }

    public Task OnPostAsync(ReceivedPost post) => _onPost?.Invoke(post) ?? Task.CompletedTask;
    public Task OnPresenceAsync(RoomMemberPresence presence) => _onPresence?.Invoke(presence) ?? Task.CompletedTask;
    public Task OnRosterAsync(IReadOnlyList<RoomMemberPresence> members) => _onRoster?.Invoke(members) ?? Task.CompletedTask;
    public Task OnErrorAsync(string code, string message) => _onError?.Invoke(code, message) ?? Task.CompletedTask;
}
