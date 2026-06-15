using Ensemble.Client;

namespace Gomp.Host;

/// <summary>
/// The slice of a registered service a <see cref="Room"/> needs: its address,
/// sending bytes to a peer, and the runtime allowlist / disconnect actions used
/// for membership and kicks. Abstracted so the room's roster, fan-out, backfill
/// and anti-spoof logic is unit-testable without a live daemon.
/// </summary>
internal interface IRoomTransport
{
    /// <summary>The room sub-identity's E-address.</summary>
    string Address { get; }

    /// <summary>Send a raw room-ops payload to a connected member (fire-and-forget).</summary>
    Task SendAsync(string toAddr, byte[] payload, CancellationToken ct = default);

    /// <summary>Grant/revoke member addresses on the room's live allowlist.</summary>
    Task UpdateAllowlistAsync(IEnumerable<string>? add, IEnumerable<string>? remove, CancellationToken ct = default);

    /// <summary>Drop a connected member's link (the kick half; pair with a revoke to stick).</summary>
    Task DisconnectAsync(string addr, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IRoomTransport"/> backed by a live <see cref="RegisteredService"/>
/// (one room sub-identity). <c>SendBytesAsync</c> requires the RPC transport,
/// which the room manifest always uses.
/// </summary>
internal sealed class RegisteredServiceTransport : IRoomTransport
{
    private readonly RegisteredService _svc;

    public RegisteredServiceTransport(RegisteredService svc) => _svc = svc;

    public string Address => _svc.ServiceAddress;

    public Task SendAsync(string toAddr, byte[] payload, CancellationToken ct = default)
        => _svc.SendBytesAsync(toAddr, payload, ct);

    public Task UpdateAllowlistAsync(IEnumerable<string>? add, IEnumerable<string>? remove, CancellationToken ct = default)
        => _svc.UpdateAllowlistAsync(add, remove, ct);

    public Task DisconnectAsync(string addr, CancellationToken ct = default)
        => _svc.DisconnectPeerAsync(addr, ct);
}
