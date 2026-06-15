using System.Diagnostics;

namespace Gomp.Integration;

/// <summary>
/// A real <c>ensemble --headless --signaling=loopback</c> daemon spawned as a
/// child process for the live e2e. Mirrors the Go regression harness
/// (internal/integration/twodaemon_servicedial_test.go): loopback skips Tor, so
/// several daemons sharing one rendezvous dir discover + dial each other over
/// same-host unix sockets — fast and deterministic.
///
/// Data dirs live under /tmp (not the test working tree) so the per-service
/// loopback socket paths stay under SUN_LEN (~108 bytes on Linux).
/// </summary>
internal sealed class DaemonProcess : IAsyncDisposable
{
    private readonly Process _proc;
    private readonly string _dataDir;
    private readonly string _logPath;

    public string Name { get; }

    /// <summary>The daemon's gRPC unix socket path.</summary>
    private string SocketPath => Path.Combine(_dataDir, "ensemble.sock");

    /// <summary>The gRPC endpoint for <see cref="Ensemble.Client.EnsembleClient"/>.</summary>
    public string Endpoint => "unix://" + SocketPath;

    /// <summary>Run <c>ensemble debug --socket</c> against this daemon and return
    /// its routing-table + connection-state dump (route diagnosis).</summary>
    public async Task<string> DebugInfoAsync(string binary)
    {
        var psi = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("debug");
        psi.ArgumentList.Add("--socket");
        psi.ArgumentList.Add(SocketPath);
        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await p.WaitForExitAsync().ConfigureAwait(false);
        return stdout + (string.IsNullOrWhiteSpace(stderr) ? "" : "\n[stderr] " + stderr);
    }

    private DaemonProcess(string name, Process proc, string dataDir, string logPath)
    {
        Name = name;
        _proc = proc;
        _dataDir = dataDir;
        _logPath = logPath;
    }

    /// <summary>
    /// Locate the ensemble binary: <c>ENSEMBLE_BIN</c> if set and present, else
    /// <c>&lt;ENSEMBLE_REPO&gt;/bin/ensemble</c> (default repo
    /// <c>../ensemble</c> relative to this repo). Returns null if none resolves —
    /// the caller skips the test.
    /// </summary>
    public static string? LocateBinary()
    {
        var fromEnv = Environment.GetEnvironmentVariable("ENSEMBLE_BIN");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return fromEnv;

        var repo = Environment.GetEnvironmentVariable("ENSEMBLE_REPO");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(repo))
            candidates.Add(Path.Combine(repo, "bin", "ensemble"));
        // Default: sibling checkout next to the gomp repo.
        candidates.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "ensemble", "bin", "ensemble")));
        candidates.Add(Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "ensemble", "bin", "ensemble")));

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Start a loopback daemon sharing <paramref name="rendezvousDir"/> and block
    /// until it logs readiness (or <paramref name="timeout"/> elapses).
    /// </summary>
    public static async Task<DaemonProcess> StartAsync(
        string name, string binary, string rendezvousDir, TimeSpan timeout, CancellationToken ct = default)
    {
        var dataDir = Directory.CreateTempSubdirectory($"gomp-e2e-{name}-").FullName;
        var logPath = Path.Combine(dataDir, "daemon.log");

        var psi = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--headless");
        psi.ArgumentList.Add("--data-dir"); psi.ArgumentList.Add(dataDir);
        psi.ArgumentList.Add("--signaling"); psi.ArgumentList.Add("loopback");
        psi.ArgumentList.Add("--loopback-dir"); psi.ArgumentList.Add(rendezvousDir);

        var log = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };

        if (!proc.Start())
            throw new InvalidOperationException($"daemon {name}: failed to start {binary}");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var d = new DaemonProcess(name, proc, dataDir, logPath);
        await d.WaitReadyAsync(timeout, ct).ConfigureAwait(false);
        return d;
    }

    private async Task WaitReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_proc.HasExited)
                throw new InvalidOperationException($"daemon {Name} exited early (code {_proc.ExitCode}); log:\n{TailLog(20)}");
            if (ReadLog().Contains("loopback: signaling ready", StringComparison.Ordinal))
                return;
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        throw new TimeoutException($"daemon {Name} not ready in {timeout}; log:\n{TailLog(20)}");
    }

    private string ReadLog()
    {
        try
        {
            using var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var r = new StreamReader(fs);
            return r.ReadToEnd();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    public string TailLog(int lines)
    {
        var all = ReadLog().TrimEnd('\n').Split('\n');
        return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_proc.HasExited)
            {
                _proc.Kill(entireProcessTree: true);
                await _proc.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch { /* disposable daemon — best effort */ }
        finally
        {
            _proc.Dispose();
            try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
