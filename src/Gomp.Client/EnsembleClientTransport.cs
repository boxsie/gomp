using Ensemble.Client;

namespace Gomp.Client;

/// <summary>
/// The live <see cref="IRoomClientTransport"/> over a real daemon: wraps an
/// <see cref="EnsembleClient"/> plus the member's one registered rooms-client
/// service. Outbound calls map straight onto the SDK; the service's event stream
/// is translated into the seam's <see cref="PeerConnected"/> /
/// <see cref="PeerDisconnected"/> / <see cref="MessageReceived"/> events.
///
/// This is the un-unit-tested glue (no daemon in tests) — the orchestration it
/// feeds is exercised against a fake transport instead.
/// </summary>
internal sealed class EnsembleClientTransport : IRoomClientTransport, IAsyncDisposable
{
    private readonly EnsembleClient _client;
    private RegisteredService? _svc;

    private EnsembleClientTransport(EnsembleClient client) => _client = client;

    /// <summary>
    /// Register the member's rooms-client service and return a ready transport.
    /// The manifest should use <see cref="ServiceTransport.Rpc"/> (room-ops are
    /// raw protobuf); a pure dialer needs no permissive inbound ACL.
    /// </summary>
    public static async Task<EnsembleClientTransport> CreateAsync(
        EnsembleClient client, ServiceManifest manifest, CancellationToken ct = default)
    {
        var t = new EnsembleClientTransport(client);
        t._svc = await client.RegisterServiceAsync(manifest, t.OnServiceEventAsync, onError: null, ct).ConfigureAwait(false);
        return t;
    }

    public string Address => _svc!.ServiceAddress;

    public Task ConnectAsync(string toAddr, CancellationToken ct = default)
        => _svc!.ConnectPeerAsync(toAddr, ct);

    public Task DisconnectAsync(string toAddr, CancellationToken ct = default)
        => _svc!.DisconnectPeerAsync(toAddr, ct);

    public Task SendAsync(string toAddr, byte[] payload, CancellationToken ct = default)
        => _svc!.SendBytesAsync(toAddr, payload, ct);

    public Task<SignedDocument> SignAsync(byte[] payload, CancellationToken ct = default)
        => _svc!.SignDocumentAsync(payload, ct);

    public Task<BindingVerification> VerifyBindingAsync(
        byte[] binding, byte[] dilithiumPub, string address, CancellationToken ct = default)
        => _client.VerifyBindingAsync(binding, dilithiumPub, address, ct);

    public event Func<string, Task>? PeerConnected;
    public event Func<string, Task>? PeerDisconnected;
    public event Func<string, byte[], Task>? MessageReceived;

    private async ValueTask OnServiceEventAsync(ServiceEvent ev)
    {
        switch (ev)
        {
            case ServiceEvent.ConnectionEstablished e when PeerConnected is { } h:
                await h(e.FromAddr).ConfigureAwait(false);
                break;
            case ServiceEvent.ConnectionClosed e when PeerDisconnected is { } h:
                await h(e.FromAddr).ConfigureAwait(false);
                break;
            case ServiceEvent.RpcMessage m when MessageReceived is { } h:
                await h(m.FromAddr, m.Payload).ConfigureAwait(false);
                break;
        }
    }

    public ValueTask DisposeAsync() => _svc?.DisposeAsync() ?? ValueTask.CompletedTask;
}
