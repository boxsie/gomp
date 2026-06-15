using Ensemble.Client;

namespace Gomp.Client;

/// <summary>
/// The slice of the daemon the gomp client needs, abstracted so the room-ops
/// orchestration (<see cref="RoomSession"/>, <see cref="PostSigner"/>,
/// <see cref="MemberDirectory"/>, <see cref="GompClient"/>) is unit-testable
/// without a live daemon — the mirror of the host's <c>IRoomTransport</c>.
///
/// One member runs ONE rooms-client service identity; every method here maps to
/// that identity:
/// <list type="bullet">
///   <item><see cref="Address"/> / <see cref="ConnectAsync"/> /
///   <see cref="SendAsync"/> / <see cref="DisconnectAsync"/> →
///   <c>RegisteredService.ServiceAddress</c> / <c>ConnectPeerAsync</c> /
///   <c>SendBytesAsync</c> / <c>DisconnectPeerAsync</c>.</item>
///   <item><see cref="SignAsync"/> → <c>RegisteredService.SignDocumentAsync</c>
///   (the daemon signs with the member's op key; the member never holds it).</item>
///   <item><see cref="VerifyBindingAsync"/> → <c>EnsembleClient.VerifyBindingAsync</c>
///   (the one post-quantum op, keyless over the local socket).</item>
/// </list>
///
/// Connection lifecycle is event-driven to match the SDK: <see cref="ConnectAsync"/>
/// merely issues the dial (fire-and-forget); the room/host link becoming usable
/// surfaces as <see cref="PeerConnected"/>, and inbound room-ops bytes arrive as
/// <see cref="MessageReceived"/>. The implementation may raise
/// <see cref="PeerConnected"/> again after a transient drop+reconnect (the
/// daemon's watchdog re-dials), so consumers must treat it as idempotent.
/// </summary>
internal interface IRoomClientTransport
{
    /// <summary>The member's own rooms-client service E-address — the
    /// <c>from_addr</c> the host attributes, and the value posts are signed
    /// under (<c>SignedPostBody.sender</c>).</summary>
    string Address { get; }

    /// <summary>Issue a dial to <paramref name="toAddr"/> (a room sub-identity or
    /// a host base identity). Fire-and-forget: readiness surfaces as
    /// <see cref="PeerConnected"/>. Idempotent for an already-connected peer.</summary>
    Task ConnectAsync(string toAddr, CancellationToken ct = default);

    /// <summary>Drop the link to <paramref name="toAddr"/> — leaving a room.</summary>
    Task DisconnectAsync(string toAddr, CancellationToken ct = default);

    /// <summary>Send a room-ops payload (a serialized <c>RoomEnvelope</c> /
    /// <c>AdminEnvelope</c>) to a connected peer.</summary>
    Task SendAsync(string toAddr, byte[] payload, CancellationToken ct = default);

    /// <summary>Sign <paramref name="payload"/> with the member's identity and
    /// return the signature plus the rotatable binding material (the member's
    /// own binding to hand the host at join).</summary>
    Task<SignedDocument> SignAsync(byte[] payload, CancellationToken ct = default);

    /// <summary>Verify a peer's identity binding against a claimed address (the
    /// one ML-DSA-65 check), returning the bound ed25519 key on success.</summary>
    Task<BindingVerification> VerifyBindingAsync(
        byte[] binding, byte[] dilithiumPub, string address, CancellationToken ct = default);

    /// <summary>A dialed peer's link became usable (initial connect OR reconnect).</summary>
    event Func<string, Task>? PeerConnected;

    /// <summary>A peer's link dropped.</summary>
    event Func<string, Task>? PeerDisconnected;

    /// <summary>Inbound room-ops bytes from a peer (<c>fromAddr</c>, payload).</summary>
    event Func<string, byte[], Task>? MessageReceived;
}
