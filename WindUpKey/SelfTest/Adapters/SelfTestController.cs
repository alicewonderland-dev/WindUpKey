#if WINDUP_TESTING
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using WindUpKey.Services;

namespace WindUpKey.SelfTest;

/// <summary>
/// <c>/windup selftest</c> (04-D2): one run at a time with a 2-minute cooldown between runs, cancelled on
/// <c>/windup selftest stop</c>, plugin Dispose, logout and a character switch (connect-policy epoch change other than
/// the run's own Reconnect). Prints the 04-D3 summary on the framework thread and writes the values-free log. Testing
/// builds only (04-D1); the caller checks <see cref="Configuration.IsDebugEnabled"/>.
/// </summary>
internal sealed class SelfTestController(
    SelfTestHost host,
    RelayClient relay,
    IFramework framework,
    IChatGui chat,
    IPluginLog log,
    string pluginConfigDirectory,
    string pluginVersion,
    string? relayToken) : IDisposable
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(2);

    /// <summary>04-D11: how long <see cref="Dispose"/> waits for a cancelled run to finish. Never longer.</summary>
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(2);

    private const string Usage =
        "Usage: /windup selftest | stop | manual [id] | outage [confirm] | oversize [confirm] | reconnect [confirm]";

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _run;

    /// <summary>04-D11: the current run was cancelled already; later cancels (every frame while it winds down) do nothing.</summary>
    private bool _cancelRequested;

    private int _expectedEpoch;
    private DateTime _lastRunEndUtc = DateTime.MinValue;
    private volatile bool _disposed;

    /// <summary>04-D10: set by Dispose and logout; a verb's restore then only lifts the hold and never calls Start().</summary>
    private volatile bool _noReconnect;

    private bool Running
    {
        get
        {
            lock (_gate)
                return _cts is not null;
        }
    }

    /// <summary>Framework thread. <paramref name="rest"/> is what follows <c>/windup selftest</c>.</summary>
    public void OnCommand(string rest)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;
        var confirmed = parts.Length > 1 && string.Equals(parts[1], "confirm", StringComparison.OrdinalIgnoreCase);
        switch (verb)
        {
            case "":
                StartRun(SelfTestTier.Safe);
                return;
            case "stop":
                if (Running)
                {
                    Cancel("stopped");
                    PluginChat.Print(chat, "Self-test stopping; restoring anything it changed.", PluginChat.Grey);
                }
                else
                {
                    PluginChat.Print(chat, "No self-test is running.", PluginChat.Grey);
                }

                return;
            case "manual":
                var lines = SelfTestCatalog.ManualText(parts.Length > 1 ? parts[1] : null);
                if (lines.Count == 0)
                    PluginChat.PrintError(chat, $"No check with id {parts[1]}. Ids look like 01-1 … 03-2, X-…, P-….");
                foreach (var line in lines)
                    PluginChat.Print(chat, line, PluginChat.White);
                return;
            case "outage" or "oversize" or "reconnect":
                var tier = verb switch
                {
                    "outage" => SelfTestTier.Outage,
                    "oversize" => SelfTestTier.Oversize,
                    _ => SelfTestTier.Reconnect,
                };
                if (!confirmed)
                {
                    PluginChat.Print(chat, Describe(tier), PluginChat.Yellow);
                    PluginChat.Print(chat, $"Nothing was changed. To run it: /windup selftest {verb} confirm", PluginChat.White);
                    return;
                }

                StartRun(tier);
                return;
            default:
                PluginChat.PrintError(chat, Usage);
                return;
        }
    }

    /// <summary>What a verb will do (04-D2: printed without <c>confirm</c>).</summary>
    public static string Describe(SelfTestTier tier) => tier switch
    {
        SelfTestTier.Outage =>
            $"outage (01-9, {SelfTestReport.Duration(SelfTestTier.Outage, new SelfTestTimings())}): holds the relay connection in memory and closes it, so you show offline and messages " +
            "for you queue; after 60 s movement locks on an unwound doll are suspended (the safety rule). Then the hold is " +
            "lifted and the client reconnects. The hold is never saved, and is lifted on stop, logout or unload. " +
            "While the outage holds the connection, another PC using this character can connect; it will be taken over " +
            "when the outage ends.",
        SelfTestTier.Oversize =>
            "oversize (03-2): sends one 64 KiB + 1 message on the live relay session. The relay closes it with 1009 " +
            "message_too_big and the client reconnects by itself.",
        _ =>
            "reconnect: does what the Reconnect button does (the session reconnects and re-registers), and checks the " +
            "register is accepted.",
    };

    /// <summary>04-D10: false once the plugin is unloading or the character logged out, until the next run starts.</summary>
    public bool ReconnectAllowed => !_noReconnect && !_disposed;

    /// <summary>Logout (04-D2, 04-D10): cancels a run, and its restore doesn't reconnect a logged-out client.</summary>
    public void OnLogout()
    {
        _noReconnect = true;
        Cancel("logout");
    }

    /// <summary>Framework thread: the run's own Reconnect changed the epoch, which mustn't cancel it.</summary>
    public void ExpectEpoch(int epoch) => Volatile.Write(ref _expectedEpoch, epoch);

    /// <summary>Framework thread, every frame: a character switch (or login, import, revert) cancels the run.</summary>
    public void Tick()
    {
        if (Running && relay.ConnectEpoch != Volatile.Read(ref _expectedEpoch))
            Cancel("the character or connection cycle changed");
    }

    /// <summary>
    /// Cancels a run (stop, logout). Lifts the outage hold right away; the run's own restore follows. Once per run
    /// (04-D11): a later cancel of the same run, such as <see cref="Tick"/> every frame until it ends, does nothing.
    /// </summary>
    public void Cancel(string reason)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            if (cts is null || _cancelRequested)
                return;
            _cancelRequested = true;
        }

        log.Information("WindUpKey self-test cancelled: {Reason}", reason);
        relay.SetSelfTestOutageHold(false);
        relay.SetSelfTestTap(null);
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run just ended.
        }
    }

    private void StartRun(SelfTestTier tier)
    {
        if (!relay.CanManageRelayIdentity)
        {
            PluginChat.PrintError(chat, "Log in with a character first.");
            return;
        }

        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_cts is not null)
            {
                PluginChat.PrintError(chat, "A self-test is already running. /windup selftest stop cancels it.");
                return;
            }

            var wait = _lastRunEndUtc + Cooldown - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                PluginChat.PrintError(chat, $"Self-test cooldown: try again in {Math.Ceiling(wait.TotalSeconds):0} s.");
                return;
            }

            cts = _cts = new CancellationTokenSource();
            _cancelRequested = false;
            _noReconnect = false;
        }

        // Everything from here is inside the try/finally that ends the run (04-D11): until RunAsync owns it, a throw
        // here ends it, so the controller can never stay "already running".
        var started = false;
        try
        {
            Volatile.Write(ref _expectedEpoch, relay.ConnectEpoch);
            var token = cts.Token;
            var duration = SelfTestReport.Duration(tier, new SelfTestTimings()) is { } d ? $" ({d})" : string.Empty;
            PluginChat.Print(chat, tier == SelfTestTier.Safe
                ? $"Self-test started{duration}. /windup selftest stop cancels it."
                : $"Self-test {tier.ToString().ToLowerInvariant()} started{duration}. /windup selftest stop cancels it and restores.", PluginChat.Grey);
            log.Information("WindUpKey self-test started: {Tier}", tier);
            var run = Task.Run(() => RunAsync(tier, cts, token));
            lock (_gate)
                _run = run;
            started = true;
        }
        finally
        {
            if (!started)
                EndRun(cts);
        }
    }

    /// <summary>Ends the run that owns <paramref name="cts"/>: the controller is free again, and the cooldown starts.</summary>
    private void EndRun(CancellationTokenSource cts)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
                _lastRunEndUtc = DateTime.UtcNow;
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// The run, its report and its chat lines. Whatever throws (log writing and reporting included), the outer
    /// <c>finally</c> ends the run, so the controller can never stay "already running" (04-D10).
    /// </summary>
    private async Task RunAsync(SelfTestTier tier, CancellationTokenSource cts, CancellationToken ct)
    {
        try
        {
            await RunAndReportAsync(tier, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The type only: messages can hold URLs or paths.
            log.Warning("WindUpKey self-test report failed with {Type}", ex.GetType().Name);
        }
        finally
        {
            EndRun(cts);
        }
    }

    private async Task RunAndReportAsync(SelfTestTier tier, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var cancelled = false;
        IReadOnlyList<string> sensitive = [];
        SelfTestSuite? suite = null;
        try
        {
            sensitive = await host.SensitiveValuesAsync(ct).ConfigureAwait(false);
            using var stats = new HttpStatsClient(relayToken);
            suite = new SelfTestSuite(host, new ClientWebSocketProbeConnector(relayToken), stats);
            if (tier == SelfTestTier.Safe)
                await suite.RunSafeAsync(ct).ConfigureAwait(false);
            else
                await suite.RunVerbAsync(tier, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            // The type only: messages can hold URLs.
            suite?.Runner.Add(CheckResult.Fail("X-session", $"the run failed with {ex.GetType().Name}"));
            log.Warning("WindUpKey self-test failed with {Type}", ex.GetType().Name);
        }
        finally
        {
            relay.SetSelfTestTap(null);
            relay.SetSelfTestOutageHold(false);
        }

        var results = suite?.Runner.Results ?? [];
        var title = tier == SelfTestTier.Safe ? "Self-test" : $"Self-test {tier.ToString().ToLowerInvariant()}";
        var logName = SelfTestReport.LogFileName(started);
        var text = SelfTestReport.LogText(title, started, DateTime.UtcNow, pluginVersion, results, sensitive, cancelled);
        var written = SelfTestReport.WriteLog(pluginConfigDirectory, logName, text);
        if (written is null)
            log.Warning("WindUpKey self-test could not write its log file");
        var lines = SelfTestReport.ChatLines(title, results, written ?? "(not written)", sensitive, cancelled);
        log.Information(
            "WindUpKey self-test finished: {Pass} pass, {Fail} fail, {Warn} warn, {Skip} skip, {Manual} manual{Stopped}",
            SelfTestReport.Count(results, CheckStatus.Pass),
            SelfTestReport.Count(results, CheckStatus.Fail),
            SelfTestReport.Count(results, CheckStatus.Warn),
            SelfTestReport.Count(results, CheckStatus.Skip),
            SelfTestReport.Count(results, CheckStatus.Manual),
            cancelled ? " (stopped)" : string.Empty);

        try
        {
            await framework.RunOnTick(() =>
            {
                if (_disposed)
                    return;
                for (var i = 0; i < lines.Count; i++)
                {
                    // FAIL lines (loud ones, with recovery steps, come first) go to the error channel.
                    if (i > 0 && lines[i].StartsWith("FAIL", StringComparison.Ordinal))
                        PluginChat.PrintError(chat, lines[i]);
                    else
                        PluginChat.Print(chat, lines[i], i == 0 ? PluginChat.White : PluginChat.Yellow);
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Unloading: the log file has the result.
        }
    }

    /// <summary>
    /// Cancels a run (its restore then only lifts the hold, 04-D10) and waits at most <see cref="DisposeWait"/> for it to
    /// finish (04-D11). A restore waiting on the framework thread can't finish while Dispose holds it, so this never
    /// blocks longer; the hold was already lifted by <see cref="Cancel"/>.
    /// </summary>
    public void Dispose()
    {
        Task? run;
        lock (_gate)
        {
            _disposed = true;
            _noReconnect = true;
            run = _cts is null ? null : _run;
        }

        Cancel("plugin unloading");
        if (run is not null && !run.Wait(DisposeWait))
            log.Information("WindUpKey self-test still winding down after {Seconds} s; unloading anyway", DisposeWait.TotalSeconds);
    }
}
#endif
