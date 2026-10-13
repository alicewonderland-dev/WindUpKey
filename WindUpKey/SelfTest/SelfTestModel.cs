#if WINDUP_TESTING || WINDUP_SELFTEST_UNITS
using System;
using System.Collections.Generic;

namespace WindUpKey.SelfTest;

/// <summary>How one check came out (04-D4). <see cref="Info"/> is reported in the log only, never counted.</summary>
public enum CheckStatus
{
    Pass,
    Fail,
    Warn,
    Skip,
    Manual,
    Info,
}

/// <summary>
/// One reported line: a check id (a checklist id such as <c>01-12</c>, or an extra <c>X-*</c>/<c>P-*</c>/<c>V-*</c>
/// id), its status and a values-free reason (04-D3). <paramref name="Loud"/> marks a FAIL that comes with recovery
/// steps (a probe that took the session over, a restore that failed): it is printed first and in red.
/// </summary>
public sealed record CheckResult(string Id, CheckStatus Status, string Reason, bool Loud = false)
{
    public static CheckResult Pass(string id, string reason) => new(id, CheckStatus.Pass, reason);

    public static CheckResult Fail(string id, string reason) => new(id, CheckStatus.Fail, reason);

    public static CheckResult LoudFail(string id, string reason) => new(id, CheckStatus.Fail, reason, Loud: true);

    public static CheckResult Warn(string id, string reason) => new(id, CheckStatus.Warn, reason);

    public static CheckResult Skip(string id, string reason) => new(id, CheckStatus.Skip, reason);

    public static CheckResult Manual(string id, string reason) => new(id, CheckStatus.Manual, reason);

    public static CheckResult Info(string id, string reason) => new(id, CheckStatus.Info, reason);
}

/// <summary>What a run is (04-D2): the safe tier, or one opt-in disruptive verb.</summary>
public enum SelfTestTier
{
    Safe,
    Outage,
    Oversize,
    Reconnect,
}

/// <summary>
/// The live session's identity as the end-of-run invariants see it (04-D6): an accepted-connection counter bumped on
/// every accepted register, whether the socket is open, any auth block, and how many session_replaced were seen.
/// </summary>
public readonly record struct SessionInvariants(int AcceptedConnections, bool Open, bool AuthBlocked, int SessionReplacedSeen)
{
    /// <summary>Why <paramref name="after"/> no longer is the session the checks ran on, or null when it still is.</summary>
    public static string? Changed(SessionInvariants before, SessionInvariants after)
    {
        var why = new List<string>();
        if (after.AcceptedConnections != before.AcceptedConnections)
            why.Add("the relay connection was re-established during the run");
        if (!after.Open)
            why.Add("the relay socket is no longer open");
        if (after.AuthBlocked)
            why.Add("the relay rejected this session");
        if (after.SessionReplacedSeen != before.SessionReplacedSeen)
            why.Add("a session_replaced was seen");
        return why.Count == 0 ? null : string.Join("; ", why);
    }

    /// <summary>
    /// 04-D10: what changed that an opt-in verb never causes (it reconnects, so the counter and the socket may change):
    /// a new auth block or a <c>session_replaced</c>. Null when nothing like that happened.
    /// </summary>
    public static string? NotCausedByVerb(SessionInvariants before, SessionInvariants after)
    {
        var why = new List<string>();
        if (after.AuthBlocked && !before.AuthBlocked)
            why.Add("the relay rejected this session");
        if (after.SessionReplacedSeen != before.SessionReplacedSeen)
            why.Add("a session_replaced was seen");
        return why.Count == 0 ? null : string.Join("; ", why);
    }
}

/// <summary>
/// Per-run limits (04-D6, Architecture "Timeouts"): connect and reply timeouts for probes, and the message budget on the
/// live session. Settable for tests.
/// </summary>
public sealed record SelfTestTimings
{
    /// <summary>One probe connect (the client's own per-host connect timeout).</summary>
    public TimeSpan ProbeConnect { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>One reply, on a probe or the live session.</summary>
    public TimeSpan Reply { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// One partner's presence reply (04-D10): above the relay's 5 s send timeout to a partner, so a slow partner is
    /// answered before the wait ends.
    /// </summary>
    public TimeSpan PresenceReply { get; init; } = TimeSpan.FromSeconds(7);

    /// <summary>A whole safe-tier step (a check with its retries).</summary>
    public TimeSpan Step { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a restore may take before it counts as failed.</summary>
    public TimeSpan Restore { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>The outage verb: how long the relay must stay unreachable before locks are suspended (01 safety rule).</summary>
    public TimeSpan OutageGrace { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The outage verb: extra time after the grace for the flag to turn.</summary>
    public TimeSpan OutageSlack { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long an automatic or requested reconnect may take to be accepted.</summary>
    public TimeSpan Reconnect { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often state is polled while waiting.</summary>
    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The live-session step (04-D10): wind, unwind, the ping and the random key's presence at one reply each, every
    /// partner's presence at <see cref="PresenceReply"/>, and one more reply for the re-check and slack.
    /// </summary>
    public TimeSpan LiveStep => Reply * 5 + PresenceReply * ProbePlan.MaxPresencePartners;

    /// <summary>
    /// The safe tier's worst case (04-D11): the sum of its step budgets in <c>SelfTestSuite.RunSafeAsync</c>: the
    /// post-patch section, P0, both P1 limits, the P2 canary and the standby hosts at one <see cref="Step"/> each, the
    /// own-key probes at two, and <see cref="LiveStep"/>.
    /// </summary>
    public TimeSpan SafeTierBudget => Step * 6 + Step * 2 + LiveStep;

    /// <summary>An opt-in verb's step budget (04-D7): the outage waits out the grace before it reconnects.</summary>
    public TimeSpan VerbStep(SelfTestTier verb) =>
        verb == SelfTestTier.Outage ? OutageGrace + OutageSlack + Reconnect + Step : Reconnect + Step;

    /// <summary>An opt-in verb's worst case (04-D11): its step, then its restore.</summary>
    public TimeSpan VerbBudget(SelfTestTier verb) => VerbStep(verb) + Restore;
}
#endif
