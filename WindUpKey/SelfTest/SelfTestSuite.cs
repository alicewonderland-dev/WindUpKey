#if WINDUP_TESTING || WINDUP_SELFTEST_UNITS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using WindUpKey.Protocol;
using WindUpKey.Services;

namespace WindUpKey.SelfTest;

/// <summary>The live connection as the opt-in verbs watch it (04-D7). Values-free.</summary>
public readonly record struct RelayLiveState(
    bool Connected,
    bool RegisterAccepted,
    int AcceptedConnections,
    bool ShouldSuspendMovementLocks,
    bool AuthBlocked,
    int SessionReplacedSeen,
    int CloseCount,
    WebSocketCloseStatus? LastCloseStatus,
    string? LastCloseDescription);

/// <summary>
/// 01-4's identity part, read once per run (04-D9): whether Copy has a valid identity, and what the real import check
/// says about importing the current value (expected Unchanged; null when it couldn't run).
/// </summary>
public readonly record struct IdentityCopyCheck(bool CopyAvailable, RelayIdentityImport? SameValueImport);

/// <summary>
/// What the suite needs from the plugin (04 Architecture "thin adapters"): framework-thread snapshots, the live-session
/// link and tap, and the controls the opt-in verbs use. The Testing build implements it over <c>RelayClient</c> and
/// Dalamud; tests implement it with fakes or a scripted relay session.
/// </summary>
public interface ISelfTestHost
{
    /// <summary>A framework-thread snapshot. Never takes the identity-file lock (04-D10).</summary>
    Task<SessionSnapshot> SnapshotAsync(CancellationToken ct);

    /// <summary>01-4 through the real import-check path, which re-reads the identity file under its lock: once per run (04-D9).</summary>
    Task<IdentityCopyCheck> IdentityCopyCheckAsync(CancellationToken ct);

    Task<PostPatchFacts> PostPatchFactsAsync(CancellationToken ct);

    /// <summary>A <see cref="ProbeSecret.FreshRandom"/> checked against this character's current and previous secret, or null.</summary>
    Task<ProbeSecret?> FreshRandomSecretAsync(CancellationToken ct);

    ISelfTestLink Link { get; }

    /// <summary>Installs (or, with null, removes) the tap the receive loop consults (04-D6).</summary>
    void SetTap(SelfTestTap? tap);

    Task<RelayLiveState> StateAsync(CancellationToken ct);

    /// <summary>04-D7 outage: hold the connect loop in memory only (and close the socket), or lift the hold.</summary>
    Task SetOutageHoldAsync(bool hold, CancellationToken ct);

    /// <summary>
    /// False once the plugin is unloading or the character logged out (04-D10): a verb and its restore then never take
    /// the Reconnect path; the restore only lifts the hold.
    /// </summary>
    bool ReconnectAllowed { get; }

    /// <summary>
    /// The Reconnect button's path: apply the compiled relay defaults, then <c>Start()</c>. Does nothing when
    /// <see cref="ReconnectAllowed"/> is false by the time it runs, or when <see cref="SelfTestSuite.TakeoverRisk"/>
    /// against <paramref name="before"/> (the state before the verb) says it could take the key from another PC (04-D11).
    /// With <paramref name="liftOutageHold"/>, first lifts the outage hold in the same framework tick, whether or not it
    /// then starts, so the connect loop can't reconnect between the lift and <c>Start()</c> (04-D12).
    /// </summary>
    Task ReconnectAsync(RelayLiveState before, bool liftOutageHold, CancellationToken ct);
}

/// <summary>
/// The safe tier and the opt-in verbs, in order (04-D2, 04-D4 to 04-D8). Everything Dalamud-touching goes through
/// <see cref="ISelfTestHost"/>; probes through <see cref="IProbeConnector"/> and <see cref="IStatsClient"/>. No Dalamud
/// dependency.
/// </summary>
public sealed class SelfTestSuite(ISelfTestHost host, IProbeConnector connector, IStatsClient stats, SelfTestTimings? timings = null)
{
    /// <summary>Results that are about the live session, turned into SKIP if it changed during the run (04-D6).</summary>
    public static readonly IReadOnlySet<string> SessionDependent = new HashSet<string>(StringComparer.Ordinal)
    {
        "01-2", "02-2", "02-5", "03-2", "X-auth-own-random", "X-auth-own-malformed",
    };

    private const string ReconnectRecovery =
        "Press Reconnect in Settings, under Connection, or log out and back in. The self-test's hold is memory-only and has been cleared.";

    private const string TakeoverRecovery =
        "The self-test did not reconnect, so it took nothing back from another PC. If this PC should hold the key, press " +
        "Reconnect in Settings, under Connection, yourself. The self-test's hold is memory-only and has been cleared.";

    private readonly SelfTestTimings _t = timings ?? new SelfTestTimings();

    /// <summary>The live 60 KiB ping's result, folded into 03-2 with P1's oversize result.</summary>
    private CheckResult? _ping;

    /// <summary>04-D11: the verb already reported a takeover risk, so its restore doesn't report it again.</summary>
    private bool _takeoverReported;

    public SelfTestRunner Runner { get; } = new();

    /// <summary>The safe tier and the post-patch section (04-D2). Results are in <see cref="Runner"/>.</summary>
    public async Task RunSafeAsync(CancellationToken ct)
    {
        var probe = new RelayProbe(connector, _t);
        // 01-4's import check once per run (04-D9), first, so the snapshot's values-free identity view is fresh.
        var identity = await host.IdentityCopyCheckAsync(ct).ConfigureAwait(false);
        var before = await host.SnapshotAsync(ct).ConfigureAwait(false) with
        {
            CopyAvailable = identity.CopyAvailable,
            SameValueImportCheck = identity.SameValueImport,
        };

        await Runner.StepAsync(["P-data-emote"], _t.Step, async token =>
            PostPatchJudge.Judge(await host.PostPatchFactsAsync(token).ConfigureAwait(false)), ct).ConfigureAwait(false);

        Runner.AddRange(SessionFactsJudge.Judge(before));

        var probeHost = ProbeHost(before);
        var relay03 = false;
        if (probeHost is null || !Uri.TryCreate(probeHost, UriKind.Absolute, out var wsUri))
        {
            foreach (var id in new[] { "01-12", "03-2", "X-limit-binary", "X-auth-canary", "X-auth-own-malformed", "X-auth-own-random", "02-5", "02-2", "X-clock" })
                Runner.Add(CheckResult.Skip(id, "no relay host to probe"));
        }
        else
        {
            var host = SelfTestRedact.Host(probeHost);

            // P0 (01-12) and X-clock.
            await Runner.StepAsync(["01-12"], _t.Step, async token =>
            {
                var noToken = await probe.UpgradeAsync(wsUri, false, token).ConfigureAwait(false);
                var withToken = await probe.UpgradeAsync(wsUri, true, token).ConfigureAwait(false);
                if (ProbePlan.StatsUri(probeHost) is not { } statsUri)
                    return [CheckResult.Fail("01-12", $"{host}: no /stats URL for this relay URL")];
                var statsNo = await stats.GetAsync(statsUri, false, _t.Reply, token).ConfigureAwait(false);
                var statsWith = await stats.GetAsync(statsUri, true, _t.Reply, token).ConfigureAwait(false);
                return
                [
                    ProbeJudge.Funnel(host, noToken, withToken, statsNo, statsWith),
                    ProbeJudge.Clock(statsWith.Date ?? statsNo.Date, DateTimeOffset.UtcNow),
                    CheckResult.Info("X-stats", statsWith.Accounts is { } accounts
                        ? $"/stats accounts = {accounts} (compare by hand for 01-1)"
                        : "/stats gave no accounts count"),
                ];
            }, ct).ConfigureAwait(false);

            // P1: before register, 4 KiB + 1 and binary. A 1009 proves a 03 relay and gates the wind probe.
            LimitVerdict oversize = default;
            await Runner.StepAsync(["X-limit-oversize"], _t.Step, async token =>
            {
                oversize = await LimitWithRetryAsync("X-limit-oversize", () => probe.OversizeBeforeRegisterAsync(wsUri, token),
                    WebSocketCloseStatus.MessageTooBig, WireCloseReasons.MessageTooBig).ConfigureAwait(false);
                relay03 = oversize.ProvesRelay03;
                return [];
            }, ct).ConfigureAwait(false);
            await Runner.StepAsync(["X-limit-binary"], _t.Step, async token =>
                [(await LimitWithRetryAsync("X-limit-binary", () => probe.BinaryBeforeRegisterAsync(wsUri, token),
                    WebSocketCloseStatus.InvalidMessageType, WireCloseReasons.BinaryNotSupported).ConfigureAwait(false)).Result], ct).ConfigureAwait(false);

            // P2 canary: a random unused key, malformed secret.
            var excluded = Excluded(before);
            var canary = AuthActivity.Unknown;
            await Runner.StepAsync(["X-auth-canary"], _t.Step, async token =>
            {
                if (ProbePlan.PickUnpairedKey(before.OwnKey, excluded) is not { } canaryKey)
                    return [CheckResult.Skip("X-auth-canary", "no random unused key could be picked")];
                var outcome = await probe.RegisterAsync(wsUri, canaryKey, ProbeSecret.Malformed, token).ConfigureAwait(false);
                canary = ProbeJudge.Canary(outcome);
                return [ProbeJudge.CanaryResult(outcome)];
            }, ct).ConfigureAwait(false);

            // P3′ then P3: own key, wrong secrets. Only with auth active, the register accepted, on the live host.
            await Runner.StepAsync(["X-auth-own-malformed", "X-auth-own-random"], _t.Step * 2, token =>
                OwnKeyProbesAsync(probe, wsUri, probeHost, canary, before, token), ct).ConfigureAwait(false);

            // Live session (04-D6), sized for every partner's presence query (04-D10).
            await Runner.StepAsync(["02-5", "02-2"], _t.LiveStep, token =>
                LiveChecksAsync(before, probeHost, relay03, excluded, token), ct).ConfigureAwait(false);
            FoldFunnelLimits(_ping, oversize.Result);

            // Standby: every other compiled host, upgrade only (INFO).
            await Runner.StepAsync(["X-standby"], _t.Step, async token =>
            {
                var results = new List<CheckResult>();
                foreach (var other in before.CompiledUrls.Where(u => !string.Equals(u, probeHost, StringComparison.OrdinalIgnoreCase)))
                {
                    results.Add(Uri.TryCreate(other, UriKind.Absolute, out var otherUri)
                        ? ProbeJudge.Standby(SelfTestRedact.Host(other), await probe.UpgradeAsync(otherUri, true, token).ConfigureAwait(false))
                        : CheckResult.Info("X-standby", "a compiled host isn't a valid URL"));
                }

                return results.Count > 0 ? results : [CheckResult.Info("X-standby", "no other compiled host")];
            }, ct).ConfigureAwait(false);
        }

        foreach (var entry in SelfTestCatalog.Checklist.Where(e => e.Coverage is CheckCoverage.Manual or CheckCoverage.OptIn))
            Runner.Add(SelfTestCatalog.ManualResult(entry));

        var after = await host.SnapshotAsync(ct).ConfigureAwait(false);
        Runner.ApplyInvariants(SessionInvariants.Changed(before.Invariants, after.Invariants), SessionDependent, causedByVerb: false);
    }

    /// <summary>The live session's host when connected; otherwise the saved last host if compiled, else the first compiled host.</summary>
    public static string? ProbeHost(SessionSnapshot s)
    {
        if (s.Connected && s.ConnectedUrl is { } live)
            return live;
        if (s.LastSuccessfulRelayUrl is { } sticky && s.CompiledUrls.Any(u => string.Equals(u, sticky, StringComparison.OrdinalIgnoreCase)))
            return sticky;
        return s.CompiledUrls.FirstOrDefault();
    }

    private static List<string> Excluded(SessionSnapshot s) =>
        s.PartnerKeys.Concat(s.PendingKeys).Concat(s.PendingRemovalKeys).Concat(s.OwnedDollKeys).Concat(s.RelayPeerKeys ?? [])
            .Append(s.RegisteredKey ?? string.Empty).ToList();

    private static async Task<LimitVerdict> LimitWithRetryAsync(
        string id,
        Func<Task<ProbeOutcome>> run,
        WebSocketCloseStatus expected,
        string description)
    {
        var verdict = ProbeJudge.Limit(id, await run().ConfigureAwait(false), expected, description, retried: false);
        if (verdict.Retry)
            verdict = ProbeJudge.Limit(id, await run().ConfigureAwait(false), expected, description, retried: true) is var again
                ? again with { ProvesRelay03 = again.ProvesRelay03 || verdict.ProvesRelay03 }
                : verdict;
        return verdict;
    }

    private async Task<IReadOnlyList<CheckResult>> OwnKeyProbesAsync(
        RelayProbe probe,
        Uri wsUri,
        string probeHost,
        AuthActivity canary,
        SessionSnapshot before,
        CancellationToken ct)
    {
        // Same host and same accepted session as at the start, checked again right before (04-D5).
        var now = await host.SnapshotAsync(ct).ConfigureAwait(false);
        var sameSession = now.Invariants.AcceptedConnections == before.Invariants.AcceptedConnections && now.Invariants.Open;
        if (!ProbePlan.OwnKeyProbesAllowed(canary, now.RegisterAccepted && sameSession, probeHost, now.ConnectedUrl))
        {
            var why = ProbePlan.OwnKeySkipReason(canary, now.RegisterAccepted && sameSession, probeHost, now.ConnectedUrl);
            return [CheckResult.Skip("X-auth-own-malformed", why), CheckResult.Skip("X-auth-own-random", why)];
        }

        // 04-D11: the key the live session registered with, never the current config key, and only when it is the
        // ContentId-derived one.
        if (ProbePlan.OwnProbeKey(now.RegisteredKey, now.ContentIdKey) is not { } ownKey)
        {
            var why = ProbePlan.OwnProbeKeySkipReason(now.RegisteredKey);
            return [CheckResult.Skip("X-auth-own-malformed", why), CheckResult.Skip("X-auth-own-random", why)];
        }

        // P3′ first: a malformed secret is refused in Bind and Enforce whatever the account state, so it is the safer one.
        var malformed = ProbeJudge.OwnKey("X-auth-own-malformed", await probe.RegisterAsync(wsUri, ownKey, ProbeSecret.Malformed, ct).ConfigureAwait(false));
        if (malformed.Status != CheckStatus.Pass)
            return [malformed, CheckResult.Skip("X-auth-own-random", "the malformed-secret probe didn't get auth_failed")];

        if (await host.FreshRandomSecretAsync(ct).ConfigureAwait(false) is not { } fresh)
            return [malformed, CheckResult.Skip("X-auth-own-random", "no fresh random secret distinct from the current and previous one")];
        var random = ProbeJudge.OwnKey("X-auth-own-random", await probe.RegisterAsync(wsUri, ownKey, fresh, ct).ConfigureAwait(false));
        return [malformed, random];
    }

    /// <summary>
    /// Why <paramref name="now"/> isn't the accepted session on <paramref name="probeHost"/> that <paramref name="before"/>
    /// saw, or null when it still is (04-D10: re-checked right before any wind, unwind or presence is sent).
    /// </summary>
    public static string? LiveSessionChanged(SessionSnapshot before, SessionSnapshot now, string probeHost)
    {
        if (now.Invariants.AcceptedConnections != before.Invariants.AcceptedConnections)
            return "the relay connection was re-established since the run started";
        if (!now.Invariants.Open || !now.RegisterAccepted)
            return "not connected with an accepted register any more";
        if (!string.Equals(now.ConnectedUrl, probeHost, StringComparison.OrdinalIgnoreCase))
            return "the live session is no longer on the probed host";
        return null;
    }

    private async Task<IReadOnlyList<CheckResult>> LiveChecksAsync(
        SessionSnapshot before,
        string probeHost,
        bool relay03,
        List<string> excluded,
        CancellationToken ct)
    {
        if (!before.Connected || !before.RegisterAccepted)
        {
            const string why = "not connected with an accepted register";
            _ping = CheckResult.Skip("X-ping-60k", why);
            return [CheckResult.Skip("02-5", why), CheckResult.Skip("02-2", why)];
        }

        // The same accepted session on the same host as at the start, checked again right before (04-D10).
        if (LiveSessionChanged(before, await host.SnapshotAsync(ct).ConfigureAwait(false), probeHost) is { } changed)
        {
            _ping = CheckResult.Skip("X-ping-60k", changed);
            return [CheckResult.Skip("02-5", changed), CheckResult.Skip("02-2", changed)];
        }

        var tap = new SelfTestTap();
        host.SetTap(tap);
        try
        {
            var plan = new LiveCheckPlan(
                before.OwnKey,
                ProbePlan.PickUnpairedKey(before.OwnKey, excluded),
                ProbePlan.PresencePartners(before.OwnKey, before.PartnerKeys),
                ProbePlan.WindProbeAllowed(relay03, before.RegisterAccepted),
                relay03 ? "the register isn't accepted" : "P1 didn't prove the relay is 03 or newer, so a wind might be forwarded or queued");
            var results = await LiveSessionChecks.RunAsync(host.Link, tap, plan, new SelfTestBudget(ProbePlan.RateBudget), _t.Reply, _t.PresenceReply, ct)
                .ConfigureAwait(false);
            _ping = results.FirstOrDefault(r => r.Id == "X-ping-60k");
            return results.Where(r => r.Id != "X-ping-60k").ToList();
        }
        finally
        {
            tap.CancelAll();
            host.SetTap(null);
        }
    }

    /// <summary>
    /// 03-2's safe part: the 60 KiB pong on the live session and P1's 4 KiB + 1 close with its description. A part that
    /// didn't finish (its step timed out) counts as a FAIL. The parts stay in the log as INFO.
    /// </summary>
    private void FoldFunnelLimits(CheckResult? ping, CheckResult? oversize)
    {
        CheckResult folded;
        if (ping is null || oversize is null)
            folded = CheckResult.Fail("03-2", "the 60 KiB ping or the 4 KiB + 1 probe didn't finish");
        else if (ping.Status == CheckStatus.Fail || oversize.Status == CheckStatus.Fail)
            folded = CheckResult.Fail("03-2", string.Join("; ", new[] { ping, oversize }.Where(p => p.Status == CheckStatus.Fail).Select(p => p.Reason)));
        else if (ping.Status == CheckStatus.Pass && oversize.Status == CheckStatus.Pass)
            folded = CheckResult.Pass("03-2", $"{ping.Reason}; before register 4 KiB + 1: {oversize.Reason} (64 KiB + 1 after register: the oversize verb)");
        else
            folded = new CheckResult("03-2", ping.Status == CheckStatus.Warn || oversize.Status == CheckStatus.Warn ? CheckStatus.Warn : CheckStatus.Skip,
                string.Join("; ", new[] { ping, oversize }.Where(p => p.Status != CheckStatus.Pass).Select(p => p.Reason)));
        Runner.Add(folded);
        if (ping is not null)
            Runner.Add(CheckResult.Info("X-ping-60k", $"{ping.Status}: {ping.Reason}"));
        if (oversize is not null)
            Runner.Add(CheckResult.Info("X-limit-oversize", $"{oversize.Status}: {oversize.Reason}"));
    }

    // ---------- 04-D7 opt-in verbs ----------

    /// <summary>Runs one opt-in verb. Results are in <see cref="Runner"/>.</summary>
    public async Task RunVerbAsync(SelfTestTier verb, CancellationToken ct)
    {
        var before = await host.StateAsync(ct).ConfigureAwait(false);
        var beforeInvariants = Invariants(before);
        var id = verb switch
        {
            SelfTestTier.Outage => "01-9",
            SelfTestTier.Oversize => "03-2",
            _ => "V-reconnect",
        };

        if (!before.Connected || !before.RegisterAccepted)
        {
            Runner.Add(CheckResult.Skip(id, "not connected with an accepted register, so nothing was changed"));
            return;
        }

        Func<CancellationToken, Task<IReadOnlyList<CheckResult>>> step = verb switch
        {
            SelfTestTier.Outage => token => OutageAsync(id, before, token),
            SelfTestTier.Oversize => token => OversizeAsync(id, before, token),
            _ => token => ReconnectVerbAsync(id, before, token),
        };
        await Runner.RunWithRestoreAsync(id, _t.VerbStep(verb), step, token => RestoreAsync(id, before, token), _t.Restore, ReconnectRecovery, ct)
            .ConfigureAwait(false);

        var after = await host.StateAsync(CancellationToken.None).ConfigureAwait(false);
        Runner.ApplyVerbInvariants(beforeInvariants, Invariants(after));
    }

    private static SessionInvariants Invariants(RelayLiveState s) => new(s.AcceptedConnections, s.Connected, s.AuthBlocked, s.SessionReplacedSeen);

    /// <summary>
    /// 04-D11: why taking the Reconnect path now could take this key back from another PC: a <c>session_replaced</c>
    /// since <paramref name="before"/>, or an auth block (<c>auth_failed</c>/<c>auth_required</c>) that appeared since.
    /// Null when there is neither. Start() would re-register with the real secret and clear the block.
    /// </summary>
    public static string? TakeoverRisk(RelayLiveState before, RelayLiveState now)
    {
        if (now.SessionReplacedSeen != before.SessionReplacedSeen)
            return "another PC took this key over during the verb (session_replaced)";
        if (now.AuthBlocked && !before.AuthBlocked)
            return "the relay rejected this session during the verb (auth_failed or auth_required)";
        return null;
    }

    private CheckResult TakeoverFail(string id, string risk)
    {
        _takeoverReported = true;
        return CheckResult.LoudFail(id, $"{risk}. {TakeoverRecovery}");
    }

    /// <summary>
    /// The Reconnect path, or the result to report instead: a SKIP when the plugin is unloading or the character logged
    /// out (04-D10), a loud FAIL when reconnecting could take the key from another PC (04-D11). Null when it reconnected.
    /// With <paramref name="liftOutageHold"/>, the hold is lifted in the same framework tick as <c>Start()</c> (04-D12);
    /// when it doesn't reconnect, the restore lifts it.
    /// </summary>
    private async Task<CheckResult?> TryReconnectAsync(string id, RelayLiveState before, bool liftOutageHold, string unloadingReason, CancellationToken ct)
    {
        if (!host.ReconnectAllowed)
            return CheckResult.Skip(id, unloadingReason);
        if (TakeoverRisk(before, await host.StateAsync(ct).ConfigureAwait(false)) is { } risk)
            return TakeoverFail(id, risk);
        await host.ReconnectAsync(before, liftOutageHold, ct).ConfigureAwait(false);
        return null;
    }

    private async Task<IReadOnlyList<CheckResult>> OutageAsync(string id, RelayLiveState before, CancellationToken ct)
    {
        await host.SetOutageHoldAsync(true, ct).ConfigureAwait(false);
        var held = Stopwatch.StartNew();
        RelayLiveState state;
        while (true)
        {
            state = await host.StateAsync(ct).ConfigureAwait(false);
            if (state.ShouldSuspendMovementLocks || held.Elapsed > _t.OutageGrace + _t.OutageSlack)
                break;
            await Task.Delay(_t.Poll, ct).ConfigureAwait(false);
        }

        var suspendedAfter = held.Elapsed;
        if (!state.ShouldSuspendMovementLocks)
            return [CheckResult.Fail(id, $"locks were not suspended {held.Elapsed.TotalSeconds:0} s after the connection was held")];
        if (suspendedAfter < _t.OutageGrace - _t.Poll)
            return [CheckResult.Fail(id, $"locks were suspended after only {suspendedAfter.TotalSeconds:0} s, before the {_t.OutageGrace.TotalSeconds:0} s grace")];

        // 04-D12: the hold is lifted in the same framework tick as Start(), so the connect loop can't reconnect in between.
        if (await TryReconnectAsync(id, before, liftOutageHold: true,
                    "locks were suspended, but the plugin is unloading or logged out, so it didn't reconnect; the hold is lifted", ct)
                .ConfigureAwait(false) is { } notReconnected)
            return [notReconnected];
        // 08-D13: a session_replaced after this verb's own Start() is someone else taking over, not a reconnect that
        // didn't happen, so it doesn't stop the wait here; the restore reports it, as before 08.
        var (back, _) = await WaitForAcceptedAsync(before, stopOnTakeover: false, ct).ConfigureAwait(false);
        if (back is not { } reconnected)
            return [CheckResult.LoudFail(id, $"locks were suspended after {suspendedAfter.TotalSeconds:0} s, but the client didn't reconnect within {_t.Reconnect.TotalSeconds:0} s: the relay may really be down. {ReconnectRecovery}")];
        return reconnected.ShouldSuspendMovementLocks
            ? [CheckResult.Fail(id, "reconnected, but locks are still suspended")]
            : [CheckResult.Pass(id, $"locks suspended after {suspendedAfter.TotalSeconds:0} s; reconnected, register accepted, locks no longer suspended")];
    }

    private async Task<IReadOnlyList<CheckResult>> OversizeAsync(string id, RelayLiveState before, CancellationToken ct)
    {
        if (!await host.Link.SendRawAsync(RelayProbe.PaddedPing(WireLimits.RelayMessageBytes + 1), ct).ConfigureAwait(false))
            return [CheckResult.Fail(id, "the 64 KiB + 1 message wasn't sent (socket closed)")];

        var watch = Stopwatch.StartNew();
        RelayLiveState state;
        while (true)
        {
            state = await host.StateAsync(ct).ConfigureAwait(false);
            if (state.CloseCount > before.CloseCount || watch.Elapsed > _t.Reply + _t.Reply)
                break;
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
        }

        // 04-D12: another PC took the key, or an auth block: the client waits for the user and won't reconnect by
        // itself, so report the takeover once, without waiting for a reconnect.
        if (TakeoverRisk(before, state) is { } risk)
            return [TakeoverFail(id, risk)];
        if (state.CloseCount == before.CloseCount)
            return [CheckResult.Fail(id, "the relay didn't close the session after 64 KiB + 1")];
        var close = ProbeJudge.Limit(id, new ProbeOutcome(ProbeOutcomeKind.Closed, CloseStatus: state.LastCloseStatus, CloseDescription: state.LastCloseDescription),
            WebSocketCloseStatus.MessageTooBig, WireCloseReasons.MessageTooBig, retried: true).Result;

        // 08-P4 (T3): a session_replaced (or auth block) that arrives only after the close stops the wait too, and is
        // reported once instead of an extra "didn't reconnect" FAIL. 08-D13: only here, where the self-test hasn't
        // reconnected itself, so the client really won't come back by itself.
        var (back, lateRisk) = await WaitForAcceptedAsync(before, stopOnTakeover: true, ct).ConfigureAwait(false);
        if (lateRisk is not null)
            return [TakeoverFail(id, lateRisk)];
        if (back is null)
            return [close, CheckResult.LoudFail(id, $"the client didn't reconnect within {_t.Reconnect.TotalSeconds:0} s after the 1009 close. {ReconnectRecovery}")];
        return close.Status == CheckStatus.Pass
            ? [CheckResult.Pass(id, $"after register, 64 KiB + 1: {close.Reason}; the automatic reconnect was accepted")]
            : [close];
    }

    private async Task<IReadOnlyList<CheckResult>> ReconnectVerbAsync(string id, RelayLiveState before, CancellationToken ct)
    {
        if (await TryReconnectAsync(id, before, liftOutageHold: false, "the plugin is unloading or logged out, so nothing was changed", ct).ConfigureAwait(false) is { } notReconnected)
            return [notReconnected];
        // 08-D13: as in the outage, a session_replaced after this Start() is someone else's takeover; the restore reports it.
        var (back, _) = await WaitForAcceptedAsync(before, stopOnTakeover: false, ct).ConfigureAwait(false);
        return back is not null
            ? [CheckResult.Pass(id, "the Reconnect path reconnected, and the register was accepted")]
            : [CheckResult.Fail(id, $"no accepted reconnect within {_t.Reconnect.TotalSeconds:0} s")];
    }

    /// <summary>
    /// A newer accepted connection than <paramref name="before"/>'s that is open, or neither within
    /// <see cref="SelfTestTimings.Reconnect"/>. 08-P4 (T3): with <paramref name="stopOnTakeover"/>, stops as soon as
    /// <see cref="TakeoverRisk"/> appears, with the risk, since the client then waits for the user and won't reconnect by
    /// itself. 08-D13: only the oversize step asks for that; after the verb's own Start(), a takeover is someone else's.
    /// </summary>
    private async Task<(RelayLiveState? Accepted, string? Risk)> WaitForAcceptedAsync(RelayLiveState before, bool stopOnTakeover, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed <= _t.Reconnect)
        {
            var state = await host.StateAsync(ct).ConfigureAwait(false);
            if (stopOnTakeover && TakeoverRisk(before, state) is { } risk)
                return (null, risk);
            if (state.Connected && state.RegisterAccepted && state.AcceptedConnections > before.AcceptedConnections)
                return (state, null);
            await Task.Delay(_t.Poll, ct).ConfigureAwait(false);
        }

        return (null, null);
    }

    /// <summary>
    /// 04-D7 restore, always: lift the hold (never persisted), and when the client isn't connected with an accepted
    /// register, take the Reconnect path and wait for it. After an unload or a logout (04-D10) it only lifts the hold:
    /// the next login starts the relay again, and an unloaded plugin stays stopped. After a <c>session_replaced</c> or a
    /// new auth block during the verb (04-D11) it also only lifts the hold, and reports a loud FAIL: reconnecting would
    /// re-register with the real secret and take the key from the other PC. 08-P4 (T4): the hold is lifted only after the
    /// state is read, so a reconnect lifts it in the same framework tick as <c>Start()</c> (04-D12) and the connect loop
    /// can't register once more in between; the controller's <c>finally</c> still lifts it if that tick never runs.
    /// </summary>
    private async Task<bool> RestoreAsync(string id, RelayLiveState before, CancellationToken ct)
    {
        if (!host.ReconnectAllowed)
        {
            await host.SetOutageHoldAsync(false, ct).ConfigureAwait(false);
            Runner.Add(CheckResult.Info("X-restore", "the hold was lifted; no reconnect, as the plugin is unloading or logged out"));
            return true;
        }

        var state = await host.StateAsync(ct).ConfigureAwait(false);
        if (TakeoverRisk(before, state) is { } risk)
        {
            await host.SetOutageHoldAsync(false, ct).ConfigureAwait(false);
            if (!_takeoverReported)
                Runner.Add(TakeoverFail(id, risk));
            return true;
        }

        if (state.Connected && state.RegisterAccepted)
        {
            await host.SetOutageHoldAsync(false, ct).ConfigureAwait(false);
            return true;
        }

        await host.ReconnectAsync(before, liftOutageHold: true, ct).ConfigureAwait(false);
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed <= _t.Reconnect)
        {
            state = await host.StateAsync(ct).ConfigureAwait(false);
            if (state.Connected && state.RegisterAccepted)
                return true;
            await Task.Delay(_t.Poll, ct).ConfigureAwait(false);
        }

        return false;
    }
}
#endif
