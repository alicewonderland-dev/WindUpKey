using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindUpRelay.Host;

internal sealed class HostController : IDisposable
{
    private static readonly Regex TsNetHttpsUrl = new(
        @"https://[a-zA-Z0-9.-]+\.ts\.net",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _repoRoot;
    private readonly string _relayProject;
    private readonly string _productionSettings;

    private Process? _relay;
    private CancellationTokenSource? _outputCts;
    private int _relayListening;
    private bool _funnelEnabled;
    private const int RelayPort = 8787;

    public HostController()
    {
        _repoRoot = FindRepoRoot();
        _relayProject = Path.Combine(_repoRoot, "WindUpRelay", "WindUpRelay.csproj");
        _productionSettings = Path.Combine(_repoRoot, "WindUpRelay", "appsettings.Production.json");
    }

    public string RepoRoot => _repoRoot;
    public bool IsRunning => IsAlive(_relay) || _funnelEnabled;
    public bool RelayRunning => IsAlive(_relay);
    public bool FunnelRunning => _funnelEnabled;
    public string? PublicHttpsUrl { get; private set; }
    public string? LastError { get; private set; }

    public string PluginWssUrl =>
        string.IsNullOrEmpty(PublicHttpsUrl)
            ? string.Empty
            : PublicHttpsUrl.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase).TrimEnd('/') + "/ws";

    public event Action? StateChanged;
    public event Action<string>? LogLine;
    public event Action<string>? TunnelFailed;

    public bool HasRelayTokenConfigured()
    {
        try
        {
            if (!File.Exists(_productionSettings))
                return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(_productionSettings));
            if (!doc.RootElement.TryGetProperty("Relay", out var relay))
                return false;
            if (!relay.TryGetProperty("Token", out var token))
                return false;
            var value = token.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value)
                   && value != "CHANGE_ME_TO_A_LONG_RANDOM_SECRET";
        }
        catch
        {
            return false;
        }
    }

    public async Task StartAsync()
    {
        LastError = null;
        PublicHttpsUrl = null;
        _funnelEnabled = false;

        if (!File.Exists(_relayProject))
            throw new InvalidOperationException($"Relay project not found:\n{_relayProject}");

        if (!HasRelayTokenConfigured())
            throw new InvalidOperationException(
                "Set Relay:Token in WindUpRelay\\appsettings.Production.json before hosting.");

        var tailscale = FindTailscale()
                        ?? throw new InvalidOperationException(
                            "tailscale.exe not found.\nInstall Tailscale from https://tailscale.com/download\nThen enable Funnel in the admin console.");

        await EnsureTailscaleReadyAsync(tailscale).ConfigureAwait(false);

        await StopAsync().ConfigureAwait(false);
        FreeRelayPort();
        Interlocked.Exchange(ref _relayListening, 0);
        _outputCts = new CancellationTokenSource();

        AppendLog("Starting relay...");
        _relay = StartProcess(
            "dotnet",
            $"run --project \"{_relayProject}\" -c Release --no-launch-profile",
            _repoRoot,
            new Dictionary<string, string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
            });

        PumpOutput(_relay, "relay", _outputCts.Token);
        await WaitForRelayReadyAsync(TimeSpan.FromSeconds(45), _outputCts.Token).ConfigureAwait(false);

        if (!IsAlive(_relay) || Volatile.Read(ref _relayListening) == 0)
            throw new InvalidOperationException(
                "Relay failed to start. Port 8787 may still be in use — click Stop, wait a few seconds, then Start again.");

        AppendLog("Relay is up on http://127.0.0.1:8787");
        RaiseState();

        AppendLog("Enabling Tailscale Funnel on port 8787...");
        await EnableFunnelAsync(tailscale).ConfigureAwait(false);
        await RefreshFunnelUrlAsync(tailscale).ConfigureAwait(false);

        if (string.IsNullOrEmpty(PublicHttpsUrl))
        {
            await DisableFunnelAsync(tailscale).ConfigureAwait(false);
            throw new InvalidOperationException(
                "Funnel started but no https://….ts.net URL was found.\n" +
                "Run: tailscale funnel status\n" +
                "Rename this machine to 'dollhome' for a stable hostname, then retry.");
        }

        _funnelEnabled = true;
        AppendLog($"Funnel public URL: {PublicHttpsUrl}");
        AppendLog($"Plugin RelayDefaults URL should be: {PluginWssUrl}");
        AppendLog("(One-time) Put that wss URL into WindUpKey/RelayDefaults.cs and rebuild the plugin.");
        RaiseState();
    }

    public async Task StopAsync()
    {
        _outputCts?.Cancel();
        _outputCts?.Dispose();
        _outputCts = null;

        var tailscale = FindTailscale();
        if (tailscale is not null && _funnelEnabled)
            await DisableFunnelAsync(tailscale).ConfigureAwait(false);

        await Task.Run(() =>
        {
            KillTree(_relay);
            FreeRelayPort();
        }).ConfigureAwait(false);

        _relay = null;
        PublicHttpsUrl = null;
        _funnelEnabled = false;
        Interlocked.Exchange(ref _relayListening, 0);
        AppendLog("Stopped.");
        RaiseState();
    }

    public void Dispose()
    {
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // ignore on shutdown
        }
    }

    private async Task EnsureTailscaleReadyAsync(string tailscale)
    {
        AppendLog("Checking Tailscale status...");
        var (exit, stdout, stderr) = await RunCaptureAsync(tailscale, "status", TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                "Tailscale is not ready. Open the Tailscale app, sign in, then retry.\n" +
                TrimOutput(stderr.Length > 0 ? stderr : stdout));
        }

        AppendLog("Tailscale is online.");
    }

    private async Task EnableFunnelAsync(string tailscale)
    {
        // Background Funnel persists until turned off; maps public HTTPS → local 8787.
        var (exit, stdout, stderr) = await RunCaptureAsync(
                tailscale,
                $"funnel --bg --yes {RelayPort}",
                TimeSpan.FromSeconds(60))
            .ConfigureAwait(false);

        var combined = stdout + "\n" + stderr;
        foreach (var line in combined.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            AppendLog("[funnel] " + line);

        TryCaptureUrl(combined);

        if (exit != 0 && string.IsNullOrEmpty(PublicHttpsUrl))
        {
            throw new InvalidOperationException(
                "Failed to enable Tailscale Funnel.\n" +
                "Enable Funnel in the Tailscale admin ACL, ensure HTTPS certs are on, then retry.\n" +
                TrimOutput(combined));
        }
    }

    private async Task DisableFunnelAsync(string tailscale)
    {
        try
        {
            AppendLog("Disabling Tailscale Funnel...");
            var (exit, stdout, stderr) = await RunCaptureAsync(
                    tailscale,
                    "funnel --yes reset",
                    TimeSpan.FromSeconds(30))
                .ConfigureAwait(false);
            if (exit != 0)
            {
                // Older CLIs use "funnel reset" / "funnel off"
                (_, stdout, stderr) = await RunCaptureAsync(tailscale, "funnel reset", TimeSpan.FromSeconds(30))
                    .ConfigureAwait(false);
                _ = stdout;
                _ = stderr;
            }
        }
        catch (Exception ex)
        {
            AppendLog("Funnel disable warning: " + ex.Message);
        }
        finally
        {
            _funnelEnabled = false;
        }
    }

    private async Task RefreshFunnelUrlAsync(string tailscale)
    {
        var (exit, stdout, stderr) = await RunCaptureAsync(tailscale, "funnel status", TimeSpan.FromSeconds(20))
            .ConfigureAwait(false);
        var combined = stdout + "\n" + stderr;
        foreach (var line in combined.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            AppendLog("[funnel] " + line);

        TryCaptureUrl(combined);
        if (exit != 0 && string.IsNullOrEmpty(PublicHttpsUrl))
            LastError = TrimOutput(combined);
    }

    private void TryCaptureUrl(string text)
    {
        var match = TsNetHttpsUrl.Match(text);
        if (!match.Success)
            return;

        PublicHttpsUrl = match.Value.TrimEnd('/');
        RaiseState();
    }

    private async Task WaitForRelayReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsAlive(_relay))
                return;
            if (Volatile.Read(ref _relayListening) == 1)
                return;

            await Task.Delay(300, ct).ConfigureAwait(false);
        }
    }

    private void FreeRelayPort()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments =
                    $"-NoProfile -Command \"Get-NetTCPConnection -LocalPort {RelayPort} -State Listen -ErrorAction SilentlyContinue | ForEach-Object {{ Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
            AppendLog($"Cleared any listeners on port {RelayPort}.");
        }
        catch (Exception ex)
        {
            AppendLog($"Could not auto-free port {RelayPort}: {ex.Message}");
        }
    }

    private void PumpOutput(Process process, string tag, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested && !process.HasExited)
                {
                    var line = await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line is null)
                        break;
                    HandleRelayLine(tag, line);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppendLog($"[{tag}] reader ended: {ex.Message}");
            }
            finally
            {
                RaiseState();
            }
        }, ct);

        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested && !process.HasExited)
                {
                    var line = await process.StandardError.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line is null)
                        break;
                    HandleRelayLine(tag, line);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                // ignore
            }
        }, ct);
    }

    private void HandleRelayLine(string tag, string line)
    {
        AppendLog($"[{tag}] {line}");

        if (line.Contains("WindUpKey relay listening", StringComparison.OrdinalIgnoreCase)
            || line.Contains($"listening on http://127.0.0.1:{RelayPort}", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Exchange(ref _relayListening, 1);
            RaiseState();
        }

        if (line.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
        {
            LastError = line;
            AppendLog("Relay port is already in use.");
            try
            {
                TunnelFailed?.Invoke("Relay port 8787 is already in use. Stop leftover processes and retry.");
            }
            catch
            {
                // ignore
            }
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCaptureAsync(
        string fileName,
        string arguments,
        TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException($"Failed to start {fileName}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }

            throw new TimeoutException($"{fileName} {arguments} timed out after {timeout.TotalSeconds:0}s");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return (process.ExitCode, stdout, stderr);
    }

    private Process StartProcess(
        string fileName,
        string arguments,
        string workingDirectory,
        Dictionary<string, string>? extraEnv)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (extraEnv is not null)
        {
            foreach (var (key, value) in extraEnv)
                psi.Environment[key] = value;
        }

        var process = Process.Start(psi)
                      ?? throw new InvalidOperationException($"Failed to start {fileName}");
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => RaiseState();
        return process;
    }

    private static void KillTree(Process? process)
    {
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            process.Dispose();
        }
    }

    private static bool IsAlive(Process? process)
    {
        try
        {
            return process is { HasExited: false };
        }
        catch
        {
            return false;
        }
    }

    private static string? FindTailscale()
    {
        var fromPath = FindOnPath("tailscale.exe") ?? FindOnPath("tailscale");
        if (fromPath is not null)
            return fromPath;

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tailscale", "tailscale.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tailscale", "tailscale.exe"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var relay = Path.Combine(dir.FullName, "WindUpRelay", "WindUpRelay.csproj");
            if (File.Exists(relay))
                return dir.FullName;
            dir = dir.Parent;
        }

        dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var relay = Path.Combine(dir.FullName, "WindUpRelay", "WindUpRelay.csproj");
            if (File.Exists(relay))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate WindUpRelay project (run from the solution folder).");
    }

    private static string TrimOutput(string text)
    {
        text = text.Trim();
        if (text.Length > 800)
            return text[..800] + "…";
        return text;
    }

    private void AppendLog(string line)
    {
        try
        {
            LogLine?.Invoke(line);
        }
        catch
        {
            // ignore
        }
    }

    private void RaiseState()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch
        {
            // ignore
        }
    }
}
