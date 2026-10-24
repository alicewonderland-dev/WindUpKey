#if WINDUP_TESTING || WINDUP_SELFTEST_UNITS
using System;
using System.Net.WebSockets;

namespace WindUpKey.SelfTest;

/// <summary>A limit probe's verdict: the result, whether to retry once (a missing close description), and whether it proves a 03 relay.</summary>
public readonly record struct LimitVerdict(CheckResult Result, bool Retry, bool ProvesRelay03);

/// <summary>
/// Pure judgments of probe outcomes (04-D5). <c>pairSync</c> is never a pass for an auth probe, a close without its
/// description is never a pass for a limit probe, and a collision is never a pass. No Dalamud dependency.
/// </summary>
public static class ProbeJudge
{
    /// <summary>What to do when a probe was accepted as this character (04-D5).</summary>
    public const string RecoverySteps =
        "A self-test probe was accepted as this character, so the relay may now hold a wrong identity for it. " +
        "Ask the relay operator to run reset-account for this pairing key, then press Reconnect (Settings, Connection).";

    /// <summary>The clock skew over which <c>X-clock</c> warns (04-D4).</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 01-12 (P0): through the Funnel host, an upgrade without the token gets 401 and one with the Bearer header 101,
    /// and <c>/stats</c> without and with it 401 and 200.
    /// </summary>
    public static CheckResult Funnel(string host, ProbeOutcome noToken, ProbeOutcome withToken, StatsOutcome statsNoToken, StatsOutcome statsWithToken)
    {
        const string id = "01-12";
        if (withToken.Kind == ProbeOutcomeKind.Unreachable)
            return CheckResult.Fail(id, $"{host} unreachable");
        if (withToken.Kind == ProbeOutcomeKind.HttpRefused)
            return CheckResult.Fail(id, $"upgrade with the Bearer header got HTTP {withToken.HttpStatus} at {host}, not 101: the Authorization header didn't get through, or the token differs");
        if (withToken.Kind != ProbeOutcomeKind.Upgraded)
            return CheckResult.Fail(id, $"upgrade with the Bearer header: {withToken}");
        if (noToken.Kind == ProbeOutcomeKind.Upgraded)
            return CheckResult.Fail(id, $"upgrade without the token was accepted at {host} (no token configured on the relay?)");
        if (noToken.Kind != ProbeOutcomeKind.HttpRefused || noToken.HttpStatus != 401)
            return CheckResult.Fail(id, $"upgrade without the token: {noToken}, expected HTTP 401");
        if (statsNoToken.Status != 401)
            return CheckResult.Fail(id, $"/stats without the token: {Describe(statsNoToken)}, expected 401");
        if (statsWithToken.Status != 200)
            return CheckResult.Fail(id, $"/stats with the Bearer header: {Describe(statsWithToken)}, expected 200");
        return CheckResult.Pass(id, $"{host}: upgrade 401 without the token, 101 with the Bearer header; /stats 401 and 200");
    }

    /// <summary>
    /// A P1 limit probe: the relay must close with <paramref name="expected"/> and exactly
    /// <paramref name="expectedDescription"/>. A close with the right code but no description (Funnel may buffer the
    /// close frame) is a FAIL with a note, retried once. Any close with the right code proves a 03 relay.
    /// </summary>
    public static LimitVerdict Limit(string id, ProbeOutcome outcome, WebSocketCloseStatus expected, string expectedDescription, bool retried)
    {
        var expectedText = $"close {(int)expected} {expectedDescription}";
        switch (outcome.Kind)
        {
            case ProbeOutcomeKind.Closed when outcome.CloseStatus == expected
                                              && string.Equals(outcome.CloseDescription, expectedDescription, StringComparison.Ordinal):
                return new LimitVerdict(CheckResult.Pass(id, $"{expectedText}, description intact"), false, true);
            case ProbeOutcomeKind.Closed when outcome.CloseStatus == expected && string.IsNullOrEmpty(outcome.CloseDescription):
                return new LimitVerdict(
                    CheckResult.Fail(id, $"close {(int)expected} arrived without its description{(retried ? " twice" : string.Empty)} (Funnel may be buffering the close frame)"),
                    !retried,
                    true);
            case ProbeOutcomeKind.Closed when outcome.CloseStatus == expected:
                return new LimitVerdict(CheckResult.Fail(id, $"close {(int)expected} with description \"{outcome.CloseDescription}\", expected {expectedDescription}"), false, true);
            case ProbeOutcomeKind.Pong:
                return new LimitVerdict(CheckResult.Warn(id, "the relay answered instead of closing: it predates issue 03"), false, false);
            case ProbeOutcomeKind.Timeout:
                return new LimitVerdict(CheckResult.Warn(id, $"no close within the reply timeout, expected {expectedText} (relay older than 03?)"), false, false);
            case ProbeOutcomeKind.Unreachable or ProbeOutcomeKind.HttpRefused:
                return new LimitVerdict(CheckResult.Fail(id, $"probe couldn't connect: {outcome}"), false, false);
            default:
                return new LimitVerdict(CheckResult.Fail(id, $"{outcome}, expected {expectedText}"), false, false);
        }
    }

    /// <summary>
    /// What the P2 canary (a random unused key, malformed secret) says about relay auth. Only <c>auth_failed</c> makes it
    /// active; pairSync or a collision (a legacy register) make it inactive; anything else is unknown.
    /// </summary>
    public static AuthActivity Canary(ProbeOutcome outcome) => outcome.Kind switch
    {
        ProbeOutcomeKind.AuthFailed => AuthActivity.Active,
        ProbeOutcomeKind.Accepted or ProbeOutcomeKind.Collision => AuthActivity.Inactive,
        _ => AuthActivity.Unknown,
    };

    public static CheckResult CanaryResult(ProbeOutcome outcome) => Canary(outcome) switch
    {
        AuthActivity.Active => CheckResult.Pass("X-auth-canary", "auth_failed for a malformed secret: relay auth is active (Bind or Enforce)"),
        AuthActivity.Inactive => CheckResult.Warn("X-auth-canary", "the canary register was accepted: relay auth is Off, or the relay predates 01; own-key probes skipped"),
        _ => CheckResult.Warn("X-auth-canary", $"no clear answer ({outcome}); own-key probes skipped"),
    };

    /// <summary>
    /// P3/P3′: an own-key register with a wrong secret must get <c>auth_failed</c>. <c>pairSync</c> or
    /// <c>session_replaced</c> means the probe was accepted as this character: a loud FAIL with recovery steps.
    /// </summary>
    public static CheckResult OwnKey(string id, ProbeOutcome outcome) => outcome.Kind switch
    {
        ProbeOutcomeKind.AuthFailed => CheckResult.Pass(id, "auth_failed: a wrong secret for this key is refused"),
        ProbeOutcomeKind.Accepted => CheckResult.LoudFail(id, $"the relay ACCEPTED a wrong secret for this key (pairSync); the probe closed at once. {RecoverySteps}"),
        ProbeOutcomeKind.SessionReplaced => CheckResult.LoudFail(id, $"the probe got session_replaced, so it held this key; it closed at once. {RecoverySteps}"),
        ProbeOutcomeKind.Collision => CheckResult.Fail(id, "pair_key_collision: the relay treated the register as legacy, so auth isn't active for this key (a collision isn't a pass)"),
        ProbeOutcomeKind.AuthRequired => CheckResult.Fail(id, "auth_required for a register that carried a secret"),
        ProbeOutcomeKind.Timeout => CheckResult.Warn(id, "no answer within the reply timeout"),
        _ => CheckResult.Fail(id, $"{outcome}, expected auth_failed"),
    };

    /// <summary>X-standby: the other compiled host answers an upgrade (no register). INFO either way.</summary>
    public static CheckResult Standby(string host, ProbeOutcome outcome) => outcome.Kind == ProbeOutcomeKind.Upgraded
        ? CheckResult.Info("X-standby", $"{host} is reachable (upgrade 101, closed without registering)")
        : CheckResult.Info("X-standby", $"{host} not reachable: {outcome}");

    /// <summary>X-clock: this PC's clock against the relay's <c>Date</c> header; WARN over <see cref="MaxClockSkew"/>.</summary>
    public static CheckResult Clock(DateTimeOffset? relayDate, DateTimeOffset localNow)
    {
        if (relayDate is not { } date)
            return CheckResult.Skip("X-clock", "the relay's /stats sent no Date header");
        var skew = (localNow - date).Duration();
        var text = $"{skew.TotalSeconds:0} s from the relay's clock";
        return skew > MaxClockSkew
            ? CheckResult.Warn("X-clock", $"{text}: over 2 minutes, so sync stamps may be judged as from the future (02-D6)")
            : CheckResult.Pass("X-clock", text);
    }

    private static string Describe(StatsOutcome stats) =>
        stats.Status is { } status ? $"HTTP {status}" : stats.Failure ?? "no answer";
}
#endif
