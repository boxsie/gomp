using Ensemble.Client;
using Gomp.Client;

namespace Gomp.Host;

/// <summary>
/// An <see cref="IRoomClientTransport"/> for the member core (<see cref="GompClient"/>)
/// that rides the unified backend's ONE base <see cref="RegisteredService"/> —
/// the same identity that hosts pubs. Outbound calls (dial / send / sign) map
/// straight onto the base service; inbound events are NOT pulled from the service
/// here (the orchestrator owns the single register-stream handler). Instead the
/// orchestrator's combined handler classifies each base-stream event and pushes
/// the member-bound ones in via <see cref="RaiseMessageReceived"/> /
/// <see cref="RaisePeerConnected"/> / <see cref="RaisePeerDisconnected"/>.
///
/// This is why one node can both host and join over a single identity: the base
/// service's outbound surface drives the member's dials, while the host's admin
/// traffic and the member's room traffic are demultiplexed by from_addr upstream.
/// </summary>
internal sealed class SharedBaseClientTransport : IRoomClientTransport
{
    private readonly EnsembleClient _client;
    private readonly RegisteredService _baseSvc;

    public SharedBaseClientTransport(EnsembleClient client, RegisteredService baseSvc)
    {
        _client = client;
        _baseSvc = baseSvc;
    }

    public string Address => _baseSvc.ServiceAddress;

    public Task ConnectAsync(string toAddr, CancellationToken ct = default)
        => _baseSvc.ConnectPeerAsync(toAddr, ct);

    public Task DisconnectAsync(string toAddr, CancellationToken ct = default)
        => _baseSvc.DisconnectPeerAsync(toAddr, ct);

    public Task SendAsync(string toAddr, byte[] payload, CancellationToken ct = default)
        => _baseSvc.SendBytesAsync(toAddr, payload, ct);

    public Task<SignedDocument> SignAsync(byte[] payload, CancellationToken ct = default)
        => _baseSvc.SignDocumentAsync(payload, ct);

    public Task<BindingVerification> VerifyBindingAsync(
        byte[] binding, byte[] dilithiumPub, string address, CancellationToken ct = default)
        => _client.VerifyBindingAsync(binding, dilithiumPub, address, ct);

    public event Func<string, Task>? PeerConnected;
    public event Func<string, Task>? PeerDisconnected;
    public event Func<string, byte[], Task>? MessageReceived;

    // ---- pushed in by the orchestrator's combined base-stream handler ----

    public Task RaiseMessageReceived(string from, byte[] payload)
        => MessageReceived is { } h ? h(from, payload) : Task.CompletedTask;

    public Task RaisePeerConnected(string addr)
        => PeerConnected is { } h ? h(addr) : Task.CompletedTask;

    public Task RaisePeerDisconnected(string addr)
        => PeerDisconnected is { } h ? h(addr) : Task.CompletedTask;
}
