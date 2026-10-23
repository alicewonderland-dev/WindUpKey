using System;

namespace WindUpKey.Quest;

/// <summary>The game's resets the quest board follows: daily at 15:00 UTC, weekly on Tuesday at 08:00 UTC.</summary>
public static class QuestResetClock
{
    public static readonly TimeSpan DailyResetUtc = TimeSpan.FromHours(15);

    public const DayOfWeek WeeklyResetDay = DayOfWeek.Tuesday;

    public static readonly TimeSpan WeeklyResetUtc = TimeSpan.FromHours(8);

    /// <summary>The last daily reset at or before <paramref name="now"/>.</summary>
    public static DateTimeOffset DailyPeriodStart(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        var today = new DateTimeOffset(utc.Date, TimeSpan.Zero) + DailyResetUtc;
        return utc >= today ? today : today.AddDays(-1);
    }

    /// <summary>The last weekly reset at or before <paramref name="now"/>.</summary>
    public static DateTimeOffset WeeklyPeriodStart(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        var daysBack = ((int)utc.DayOfWeek - (int)WeeklyResetDay + 7) % 7;
        var candidate = new DateTimeOffset(utc.Date, TimeSpan.Zero).AddDays(-daysBack) + WeeklyResetUtc;
        return utc >= candidate ? candidate : candidate.AddDays(-7);
    }

    public static DateTimeOffset NextDailyReset(DateTimeOffset now) => DailyPeriodStart(now).AddDays(1);

    public static DateTimeOffset NextWeeklyReset(DateTimeOffset now) => WeeklyPeriodStart(now).AddDays(7);
}
