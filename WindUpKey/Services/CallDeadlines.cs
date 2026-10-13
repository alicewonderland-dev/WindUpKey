#if WINDUP_TESTING || WINDUP_CALL_UNITS
using System;

namespace WindUpKey.Services;

/// <summary>What an answered Call is doing, for <see cref="CallDeadlines.Check"/>.</summary>
public enum CallWaitKind
{
    /// <summary>Waiting for a duty, combat or crafting to end, or for her answer.</summary>
    Waiting,

    /// <summary>The failed prompt is open.</summary>
    FailedPrompt,

    /// <summary>A travel phase.</summary>
    Traveling,
}

/// <summary>Which limit ended a Call (none yet: <see cref="None"/>).</summary>
public enum CallExpiry
{
    None,
    WaitLimit,
    FailedPromptLimit,
    OverallCap,
}

/// <summary>
/// 07-A3 (D7): the safety deadlines of an answered Call, which apply in Hardcore too. The wait limit runs from the
/// Call's arrival, the failed-prompt limit from each failure, the overall cap from arrival. Nothing expires while the
/// doll is between areas, but a transit never holds a deadline more than <see cref="TransitCeilingMs"/> past its time.
/// Each limit fires exactly at its time. Monotonic milliseconds from an injected clock (<c>Environment.TickCount64</c>).
/// No Dalamud dependency.
/// </summary>
public sealed class CallDeadlines(Func<long> clock)
{
    /// <summary>Waiting for a gate or her answer, from arrival.</summary>
    public const long WaitLimitMs = 10 * 60_000;

    /// <summary>The failed prompt, from each failure.</summary>
    public const long FailedPromptLimitMs = 3 * 60_000;

    /// <summary>The whole Call, from arrival.</summary>
    public const long OverallCapMs = 20 * 60_000;

    /// <summary>How long past a deadline a zone transit can hold it.</summary>
    public const long TransitCeilingMs = 2 * 60_000;

    private long _arrivedMs;
    private long? _failedMs;

    /// <summary>A Call arrived (now).</summary>
    public void Start()
    {
        _arrivedMs = clock();
        _failedMs = null;
    }

    /// <summary>The failed prompt opened (now): its limit starts over.</summary>
    public void MarkFailure() => _failedMs = clock();

    /// <summary>The limit that has run out for a Call that is <paramref name="kind"/>, or <see cref="CallExpiry.None"/>.</summary>
    public CallExpiry Check(CallWaitKind kind, bool betweenAreas)
    {
        var now = clock();
        var expiry = CallExpiry.None;
        var deadline = long.MaxValue;

        void Consider(CallExpiry which, long at)
        {
            if (now >= at && at < deadline)
            {
                expiry = which;
                deadline = at;
            }
        }

        Consider(CallExpiry.OverallCap, _arrivedMs + OverallCapMs);
        if (kind == CallWaitKind.Waiting)
            Consider(CallExpiry.WaitLimit, _arrivedMs + WaitLimitMs);
        if (kind == CallWaitKind.FailedPrompt && _failedMs is { } failed)
            Consider(CallExpiry.FailedPromptLimit, failed + FailedPromptLimitMs);

        if (expiry != CallExpiry.None && betweenAreas && now < deadline + TransitCeilingMs)
            return CallExpiry.None;
        return expiry;
    }
}
#endif
