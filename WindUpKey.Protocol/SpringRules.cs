using System;

namespace WindUpKey.Protocol;

/// <summary>
/// Spare springs: the wind currency. The relay holds every balance and applies these rules; clients use them only to
/// show what a spring is worth. A spring is an 8h wind (12h from the doll's owner).
/// </summary>
public static class SpringRules
{
    public const string RoleDoll = "doll";
    public const string RoleWinder = "winder";

    public const int DollCap = 10;
    public const int WinderCap = 20;

    /// <summary>Wind hours per spring.</summary>
    public const double HoursPerSpring = 8;

    /// <summary>An owner winding an owned doll: +50%.</summary>
    public const double OwnerMultiplier = 1.5;

    /// <summary>The most springs one wind may spend.</summary>
    public const int MaxSpringsPerWind = 10;

    /// <summary>
    /// The most springs one unwind may cost (a 168h doll with player overwind needs more than any cap allows; the
    /// balance check is what stops an unwind the unwinder can't pay for).
    /// </summary>
    public const int MaxSpringsPerUnwind = 100;

    /// <summary>Quest board lines a character can be paid for per day: one full board (all ten lines).</summary>
    public const int MaxQuestLinesPerDay = 10;

    /// <summary>Lines a single grant may claim: one clear can finish a whole board.</summary>
    public const int MaxLinesPerGrant = 10;

    /// <summary>The game's daily reset, 15:00 UTC: the free spring and the quest line limit follow it.</summary>
    public static readonly TimeSpan DailyResetUtc = TimeSpan.FromHours(15);

    /// <summary>A role the relay knows, or the doll default for anything else (older clients send none).</summary>
    public static string NormalizeRole(string? role) =>
        string.Equals(role, RoleWinder, StringComparison.Ordinal) ? RoleWinder : RoleDoll;

    public static int Cap(string role) => NormalizeRole(role) == RoleWinder ? WinderCap : DollCap;

    /// <summary>Winders always get double what a doll gets.</summary>
    public static int Multiplier(string role) => NormalizeRole(role) == RoleWinder ? 2 : 1;

    /// <summary>The free springs for logging in on a new day.</summary>
    public static int DailySprings(string role) => Multiplier(role);

    /// <summary>Springs paid for <paramref name="lines"/> quest board lines.</summary>
    public static int QuestSprings(string role, int lines) => Math.Max(0, lines) * Multiplier(role);

    /// <summary>The hours <paramref name="springs"/> wind for, from the doll's owner or anyone else.</summary>
    public static double WindHours(int springs, bool fromOwner) =>
        Math.Max(0, springs) * HoursPerSpring * (fromOwner ? OwnerMultiplier : 1);

    /// <summary>The springs an unwind of <paramref name="hours"/> (normal wind plus player overwind) costs: rounded up.</summary>
    public static int UnwindCost(double hours)
    {
        if (hours <= 0)
            return 0;
        // A hair over a block boundary (a float from time arithmetic) must not cost a whole extra spring.
        return (int)Math.Ceiling(hours / HoursPerSpring - 1e-9);
    }

    /// <summary>The last daily reset at or before <paramref name="now"/>.</summary>
    public static DateTimeOffset DailyPeriodStart(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        var today = new DateTimeOffset(utc.Date, TimeSpan.Zero) + DailyResetUtc;
        return utc >= today ? today : today.AddDays(-1);
    }
}
