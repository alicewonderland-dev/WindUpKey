using System;

namespace WindUpKey.Services;

/// <summary>A wind's new expiry, and the time it actually added (never negative).</summary>
/// <param name="Expiry">The expiry to store.</param>
/// <param name="Added">How much later it is than the current expiry (or now, for an empty timer).</param>
public readonly record struct WindResult(DateTimeOffset Expiry, TimeSpan Added);

/// <summary>How a wind changes a doll's expiry. No Dalamud dependency.</summary>
public static class WindMath
{
    /// <summary>The smallest cap a wind is clamped to, whatever <see cref="Configuration.MaxWindHours"/> says.</summary>
    public const double MinCapHours = 0.01;

    /// <summary>
    /// Adds <paramref name="amount"/> to the current expiry (or to <paramref name="now"/> when it is empty or past).
    /// With a cap (<paramref name="maxWindHours"/> not null) the result is at most <c>now + maxWindHours</c>, but
    /// 06-A8: a wind never shortens the timer, so an expiry already past the cap (after a quest wind, or after an owner
    /// lowered max hours) is kept as it is: <c>max(current, min(current + amount, cap))</c>. A null cap adds the full
    /// amount (the quest reward).
    /// </summary>
    public static WindResult Add(DateTimeOffset now, DateTimeOffset? currentExpiry, TimeSpan amount, double? maxWindHours)
    {
        var current = currentExpiry is { } expiry && expiry > now ? expiry : now;
        if (amount <= TimeSpan.Zero)
            return new WindResult(current, TimeSpan.Zero);

        var proposed = current.Add(amount);
        if (maxWindHours is { } hours)
        {
            var cap = now.AddHours(Math.Max(MinCapHours, hours));
            if (proposed > cap)
                proposed = cap;
        }

        if (proposed < current)
            proposed = current;

        return new WindResult(proposed, proposed - current);
    }
}
