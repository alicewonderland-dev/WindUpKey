#if WINDUP_TESTING || WINDUP_SELFTEST_UNITS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindUpKey.Protocol;

namespace WindUpKey.SelfTest;

/// <summary>
/// The self-test's way onto the live relay session (04-D6): a dedicated send that never touches the client's pending
/// wind or owner bookkeeping, and a raw send (for the oversize verb only) that skips the client's 64 KiB send cap.
/// </summary>
public interface ISelfTestLink
{
    /// <summary>Sends within the client's send cap. False when the socket isn't open or the message is over the cap.</summary>
    Task<bool> SendAsync(Envelope envelope, CancellationToken ct);

    /// <summary>Sends these UTF-8 bytes as one text message, whatever their size. False when the socket isn't open.</summary>
    Task<bool> SendRawAsync(byte[] utf8, CancellationToken ct);
}

/// <summary>What the live-session checks may do this run (04-D6).</summary>
/// <param name="OwnKey">This character's key: <c>not_allowed</c> must come back with <c>to</c> set to it.</param>
/// <param name="UnpairedKey">A random key that is never this one, a partner's or a pending one; null when none was found.</param>
/// <param name="PresencePartners">At most <see cref="ProbePlan.MaxPresencePartners"/> partners to query.</param>
/// <param name="WindProbeAllowed">Only after P1 proved the relay is 03 or newer (<see cref="ProbePlan.WindProbeAllowed"/>).</param>
/// <param name="WindSkipReason">Why the wind probe is skipped, when it is.</param>
public sealed record LiveCheckPlan(
    string OwnKey,
    string? UnpairedKey,
    IReadOnlyList<string> PresencePartners,
    bool WindProbeAllowed,
    string WindSkipReason);

/// <summary>
/// The checks over the live session (04-D6): a <c>wind</c> (1/3600 h) and an <c>unwind</c> to a random unpaired key
/// expect <c>not_allowed</c>; a 60 KiB padded <c>ping</c> expects <c>pong</c>; a <c>presenceQuery</c> to up to 10 partners
/// and to the random key, which must look offline. Replies are matched by <see cref="SelfTestTap"/>, so none reaches
/// normal handling or chat. At most <see cref="ProbePlan.RateBudget"/> rate-counted messages. No Dalamud dependency.
/// </summary>
public static class LiveSessionChecks
{
    /// <summary>The padded ping: 60 KiB, under the relay's 64 KiB limit.</summary>
    public const int PingBytes = 60 * 1024;

    /// <summary>The wind sent to the unpaired key: one second, so even a relay that forwarded it would do nothing visible.</summary>
    public const double ProbeWindHours = 1.0 / 3600;

    /// <summary>
    /// Runs the checks. Results: <c>02-5</c> (wind and unwind refused), <c>X-ping-60k</c> (internal, folded into 03-2 by
    /// the caller), <c>02-2</c> (presence). Each partner's presence reply gets <paramref name="presenceTimeout"/> (04-D10).
    /// </summary>
    public static async Task<IReadOnlyList<CheckResult>> RunAsync(
        ISelfTestLink link,
        SelfTestTap tap,
        LiveCheckPlan plan,
        SelfTestBudget budget,
        TimeSpan replyTimeout,
        TimeSpan presenceTimeout,
        CancellationToken ct)
    {
        var results = new List<CheckResult>
        {
            await WindProbeAsync(link, tap, plan, budget, replyTimeout, ct).ConfigureAwait(false),
            await PingAsync(link, tap, replyTimeout, ct).ConfigureAwait(false),
            await PresenceAsync(link, tap, plan, budget, replyTimeout, presenceTimeout, ct).ConfigureAwait(false),
        };
        return results;
    }

    public static async Task<CheckResult> WindProbeAsync(
        ISelfTestLink link,
        SelfTestTap tap,
        LiveCheckPlan plan,
        SelfTestBudget budget,
        TimeSpan replyTimeout,
        CancellationToken ct)
    {
        const string id = "02-5";
        if (!plan.WindProbeAllowed)
            return CheckResult.Skip(id, plan.WindSkipReason);
        if (plan.UnpairedKey is not { } target)
            return CheckResult.Skip(id, "no random unpaired key could be picked");
        if (!budget.TryTake(2))
            return CheckResult.Skip(id, "the run's message budget is used up");

        var wind = await RefusedAsync(link, tap, plan.OwnKey, replyTimeout, ct, requestId =>
            Envelope.Create(MessageTypes.Wind, new WindPayload { RequestId = requestId, To = target, Hours = ProbeWindHours, Springs = 1 })).ConfigureAwait(false);
        var unwind = await RefusedAsync(link, tap, plan.OwnKey, replyTimeout, ct, requestId =>
            Envelope.Create(MessageTypes.Unwind, new UnwindPayload { RequestId = requestId, To = target })).ConfigureAwait(false);

        if (wind is null && unwind is null)
            return CheckResult.Pass(id, "wind and unwind to a random unpaired key were refused with not_allowed");
        return CheckResult.Fail(id, string.Join("; ", new[] { wind is null ? null : "wind: " + wind, unwind is null ? null : "unwind: " + unwind }.Where(s => s is not null)));
    }

    /// <summary>Null when the request was refused exactly as 02-D2 says; otherwise what happened instead.</summary>
    private static async Task<string?> RefusedAsync(
        ISelfTestLink link,
        SelfTestTap tap,
        string ownKey,
        TimeSpan replyTimeout,
        CancellationToken ct,
        Func<string, Envelope> build)
    {
        var requestId = SelfTestTap.NewRequestId();
        var reply = tap.ExpectAsync(requestId, replyTimeout, ct);
        // An unsent request's wait just times out unused.
        if (!await link.SendAsync(build(requestId), ct).ConfigureAwait(false))
            return "not sent (the socket isn't open)";

        var envelope = await reply.ConfigureAwait(false);
        if (envelope is null)
            return "no reply within the timeout (it may have been forwarded or queued)";
        if (envelope.Type != MessageTypes.Error)
            return $"got {envelope.Type}, expected error not_allowed";
        var error = envelope.GetPayload<ErrorPayload>();
        if (error?.Code != ErrorCodes.NotAllowed)
            return $"error {error?.Code}, expected not_allowed";
        if (!string.Equals(WireLimits.NormalizeKey(error.To), WireLimits.NormalizeKey(ownKey), StringComparison.Ordinal))
            return "not_allowed was not addressed to this key";
        return null;
    }

    /// <summary>The 60 KiB ping (03-2's live part). Reported as <c>X-ping-60k</c>; the caller folds it into 03-2.</summary>
    public static async Task<CheckResult> PingAsync(ISelfTestLink link, SelfTestTap tap, TimeSpan replyTimeout, CancellationToken ct)
    {
        const string id = "X-ping-60k";
        var pong = tap.ExpectPongAsync(replyTimeout, ct);
        var envelope = Envelope.TryParse(System.Text.Encoding.UTF8.GetString(RelayProbe.PaddedPing(PingBytes)))!;
        if (!await link.SendAsync(envelope, ct).ConfigureAwait(false))
            return CheckResult.Fail(id, "the 60 KiB ping wasn't sent (socket closed, or over the client's send cap)");

        return await pong.ConfigureAwait(false) is not null
            ? CheckResult.Pass(id, "a 60 KiB ping got a pong")
            : CheckResult.Fail(id, "no pong to a 60 KiB ping within the timeout");
    }

    /// <summary>
    /// 02-2's automated part. A partner's query waits <paramref name="presenceTimeout"/> (above the relay's 5 s send
    /// timeout); an unanswered partner is a WARN, since a partner's client can be slow (04-D10). The random unpaired key
    /// is answered by the relay itself, so an unanswered or online-looking one is a FAIL.
    /// </summary>
    public static async Task<CheckResult> PresenceAsync(
        ISelfTestLink link,
        SelfTestTap tap,
        LiveCheckPlan plan,
        SelfTestBudget budget,
        TimeSpan replyTimeout,
        TimeSpan presenceTimeout,
        CancellationToken ct)
    {
        const string id = "02-2";
        var problems = new List<string>();
        var warnings = new List<string>();
        int online = 0, offline = 0, unanswered = 0, queried = 0;
        foreach (var partner in plan.PresencePartners)
        {
            if (!budget.TryTake())
                break;
            queried++;
            var reply = await QueryAsync(link, tap, partner, observe: true, presenceTimeout, ct).ConfigureAwait(false);
            if (reply is null)
                unanswered++;
            else if (reply.Online)
                online++;
            else
                offline++;
        }

        if (unanswered > 0)
            warnings.Add($"{unanswered} partner presence quer{(unanswered == 1 ? "y" : "ies")} unanswered");

        string unpaired;
        if (plan.UnpairedKey is not { } target)
        {
            unpaired = "no random key to query";
        }
        else if (!budget.TryTake())
        {
            unpaired = "random key not queried (budget)";
        }
        else
        {
            var reply = await QueryAsync(link, tap, target, observe: false, replyTimeout, ct).ConfigureAwait(false);
            if (reply is null)
            {
                problems.Add("the random unpaired key's presence query got no answer");
                unpaired = "random key unanswered";
            }
            else if (reply.Online || reply.StillPaired is not null)
            {
                problems.Add("the random unpaired key didn't look offline (02-D3)");
                unpaired = "random key not offline";
            }
            else
            {
                unpaired = "a random unpaired key looks offline";
            }
        }

        var summary = $"{queried} partner(s) queried: {online} online, {offline} offline; {unpaired}";
        return problems.Count > 0 ? CheckResult.Fail(id, $"{string.Join("; ", problems.Concat(warnings))} ({summary})")
            : warnings.Count > 0 ? CheckResult.Warn(id, $"{string.Join("; ", warnings)} ({summary})")
            : CheckResult.Pass(id, summary);
    }

    private static async Task<PresenceResultPayload?> QueryAsync(
        ISelfTestLink link,
        SelfTestTap tap,
        string target,
        bool observe,
        TimeSpan replyTimeout,
        CancellationToken ct)
    {
        var requestId = SelfTestTap.NewRequestId();
        var reply = tap.ExpectAsync(requestId, replyTimeout, ct, observe ? [target] : null);
        if (!await link.SendAsync(Envelope.Create(MessageTypes.PresenceQuery, new PresenceQueryPayload { RequestId = requestId, To = target }), ct)
                .ConfigureAwait(false))
            return null;
        var envelope = await reply.ConfigureAwait(false);
        return envelope?.Type == MessageTypes.PresenceResult ? envelope.GetPayload<PresenceResultPayload>() : null;
    }
}
#endif
