using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Ensemble.Client;
using Gomp.Host;

// Ensemble room host (ADR-0007 / ADR-0009): a daemon-supervised service. The
// supervisor execs this binary with the spawn contract in the environment
// (ENSEMBLE_SOCKET / ENSEMBLE_SERVICE_NAME / ENSEMBLE_SERVICE_TOKEN /
// ENSEMBLE_DATA_DIR) and the owner via the per-service env overlay
// (ROOM_HOST_OWNER). Optional ROOM_HISTORY_MAX caps per-room retention.

using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Information));
var log = loggerFactory.CreateLogger("ensemble-room-host");

var ctx = SpawnContext.FromEnv();
if (string.IsNullOrEmpty(ctx.SocketPath) || string.IsNullOrEmpty(ctx.ServiceName) || string.IsNullOrEmpty(ctx.DataDir))
{
    log.LogError("missing spawn contract: ENSEMBLE_SOCKET / ENSEMBLE_SERVICE_NAME / ENSEMBLE_DATA_DIR must all be set");
    return 1;
}

var owner = Environment.GetEnvironmentVariable("ROOM_HOST_OWNER");
if (string.IsNullOrWhiteSpace(owner))
{
    log.LogError("ROOM_HOST_OWNER is required (the operator address with admin authority)");
    return 1;
}

var historyMax = 1000;
if (int.TryParse(Environment.GetEnvironmentVariable("ROOM_HISTORY_MAX"), out var parsed) && parsed > 0)
    historyMax = parsed;

// SIGTERM (supervisor stop) and SIGINT (Ctrl-C) both trigger a clean shutdown.
using var shutdown = new CancellationTokenSource();
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; shutdown.Cancel(); });
using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; shutdown.Cancel(); });

await using var client = EnsembleClient.FromEnv(loggerFactory.CreateLogger<EnsembleClient>());
await using var host = new RoomHost(client, ctx.ServiceName, ctx.DataDir, owner, historyMax, loggerFactory);

try
{
    await host.StartAsync(shutdown.Token);
}
catch (Exception ex)
{
    log.LogError(ex, "room host failed to start");
    return 1;
}

log.LogInformation("room host running; base address {Addr}", host.BaseAddress);

try
{
    await Task.Delay(Timeout.Infinite, shutdown.Token);
}
catch (OperationCanceledException)
{
    // expected on SIGTERM/SIGINT
}

log.LogInformation("room host shutting down");
return 0;
