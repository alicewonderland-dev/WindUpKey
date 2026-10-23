using System;

namespace WindUpKey.Services;

/// <summary>What a player's wind did.</summary>
/// <param name="Layers">The doll's wind after it.</param>
/// <param name="Broke">It took player overwind to max wind hours: the doll is broken, and their player overwind gone.</param>
public readonly record struct PlayerWindOutcome(WindLayers Layers, bool Broke);

/// <summary>
/// A doll's wind as three layers, drained from the top: player overwind (only other players add it; it breaks the doll
/// when it reaches max wind hours), quest overwind (quest wind past 100%, at most 1× max on top of normal wind), and normal
/// wind (0 to 100% of max wind hours). The doll moves while normal wind is above 0 and they aren't broken. Nothing drains
/// while broken, nor while normal wind is 0 (quest overwind is held until someone winds them). No Dalamud dependency.
/// </summary>
/// <param name="Normal">Normal wind hours at <paramref name="AsOf"/>.</param>
/// <param name="Quest">Quest overwind hours at <paramref name="AsOf"/>.</param>
/// <param name="Player">Player overwind hours at <paramref name="AsOf"/>.</param>
/// <param name="AsOf">When the amounts were true.</param>
/// <param name="BrokenUntil">While broken: when it ends by itself. Null when not broken.</param>
public readonly record struct WindLayers(double Normal, double Quest, double Player, DateTimeOffset AsOf, DateTimeOffset? BrokenUntil)
{
    /// <summary>The longest a break can be set to last, and the shortest.</summary>
    public const int MinRecoveryMinutes = 5;
    public const int MaxRecoveryMinutes = 240;
    public const int DefaultRecoveryMinutes = 30;

    /// <summary>Player overwind under this (a rounding remainder) doesn't count as overwound.</summary>
    private const double OverwoundEpsilon = 1e-6;

    public static WindLayers Empty(DateTimeOffset now) => new(0, 0, 0, now, null);

    public static int ClampRecoveryMinutes(int minutes) => Math.Clamp(minutes, MinRecoveryMinutes, MaxRecoveryMinutes);

    /// <summary>
    /// Wind from before the layers (one expiry): the time left becomes normal wind up to max, and anything past it quest
    /// overwind, as the only overwind there was came from quests or the safeword.
    /// </summary>
    public static WindLayers FromExpiry(DateTimeOffset? expiry, DateTimeOffset now, double maxWindHours)
    {
        var left = expiry is { } e && e > now ? (e - now).TotalHours : 0;
        var normal = Math.Min(left, Math.Max(0, maxWindHours));
        return new WindLayers(normal, left - normal, 0, now, null);
    }

    public bool IsBrokenAt(DateTimeOffset now) => BrokenUntil is { } until && now < until;

    /// <summary>Has player overwind: another player's wind may now break them.</summary>
    public bool IsOverwound => Player > OverwoundEpsilon;

    /// <summary>The layers at <paramref name="now"/>: elapsed time drained top-down, except while broken or frozen.</summary>
    public WindLayers At(DateTimeOffset now)
    {
        var start = AsOf;
        if (BrokenUntil is { } until)
        {
            if (now < until)
                return this with { AsOf = now };
            start = until > start ? until : start;
        }

        var elapsed = now > start ? (now - start).TotalHours : 0;
        var normal = Math.Max(0, Normal);
        var quest = Math.Max(0, Quest);
        var player = Math.Max(0, Player);

        // At 0 normal wind (after an unwind) nothing drains: quest overwind waits for the next wind.
        if (normal <= 0)
            return new WindLayers(0, quest, 0, now, null);

        var take = Math.Min(player, elapsed);
        player -= take;
        elapsed -= take;
        take = Math.Min(quest, elapsed);
        quest -= take;
        elapsed -= take;
        normal = Math.Max(0, normal - elapsed);
        return new WindLayers(normal, quest, player, now, null);
    }

    /// <summary>Locked like unwound: broken, or no normal wind.</summary>
    public bool IsLockedAt(DateTimeOffset now) => IsBrokenAt(now) || At(now).Normal <= 0;

    /// <summary>How long until the doll stops (0 while locked): everything above 0 drains before normal wind runs out.</summary>
    public TimeSpan RunningFor(DateTimeOffset now)
    {
        if (IsBrokenAt(now))
            return TimeSpan.Zero;
        var at = At(now);
        return at.Normal <= 0 ? TimeSpan.Zero : TimeSpan.FromHours(at.Normal + at.Quest + at.Player);
    }

    /// <summary>
    /// When the doll will stop if nothing changes, or null while stopped (unwound, or frozen with only quest overwind).
    /// A broken doll's time is paused, so it runs on from the end of the break. Older builds read this as the expiry.
    /// </summary>
    public DateTimeOffset? StopsAt(DateTimeOffset now)
    {
        var at = At(now);
        if (at.Normal <= 0)
            return null;
        var from = IsBrokenAt(now) ? BrokenUntil!.Value : now;
        return from.AddHours(at.Normal + at.Quest + at.Player);
    }

    /// <summary>Normal wind as a percentage of max wind hours, 0–100 (overwind reads 100).</summary>
    public int PercentOfMax(DateTimeOffset now, double maxWindHours)
    {
        var normal = At(now).Normal;
        if (maxWindHours <= 0)
            return normal > 0 ? 100 : 0;
        // A hair under a whole percent (64.8h of 72h is 89.99…) still reads as that percent.
        return (int)Math.Clamp(Math.Floor(normal / maxWindHours * 100 + 1e-9), 0, 100);
    }

    /// <summary>
    /// A wind from another player: normal wind up to max, then player overwind. The wind that first overwinds the doll
    /// never breaks them, however much it is (so a doll whose max is under a spring's hours can still be wound). Once
    /// overwound, a wind that takes player overwind to max wind hours breaks the doll: it is lost, and the break lasts
    /// <paramref name="recoveryMinutes"/>. Never call it on a broken doll.
    /// </summary>
    public PlayerWindOutcome AddPlayer(double hours, double maxWindHours, DateTimeOffset now, int recoveryMinutes)
    {
        var at = At(now);
        if (hours <= 0)
            return new PlayerWindOutcome(at, false);

        var room = Math.Max(0, maxWindHours - at.Normal);
        var toNormal = Math.Min(hours, room);
        var player = at.Player + (hours - toNormal);
        var next = at with { Normal = at.Normal + toNormal, Player = player };
        var wasOverwound = at.IsOverwound;
        if (wasOverwound && maxWindHours > 0 && player >= maxWindHours - 1e-9)
            return new PlayerWindOutcome(next with { Player = 0, BrokenUntil = now.AddMinutes(ClampRecoveryMinutes(recoveryMinutes)) }, true);
        return new PlayerWindOutcome(next, false);
    }

    /// <summary>Quest wind: normal wind up to max, then quest overwind, so normal and quest together reach at most 2× max.</summary>
    public WindLayers AddQuest(double hours, double maxWindHours, DateTimeOffset now)
    {
        var at = At(now);
        if (hours <= 0)
            return at;

        var room = Math.Max(0, maxWindHours - at.Normal);
        var toNormal = Math.Min(hours, room);
        var questRoom = Math.Max(0, 2 * maxWindHours - (at.Normal + toNormal) - at.Quest);
        return at with { Normal = at.Normal + toNormal, Quest = at.Quest + Math.Min(hours - toNormal, questRoom) };
    }

    /// <summary>Commendation wind: normal wind only, up to max.</summary>
    public WindLayers AddNormal(double hours, double maxWindHours, DateTimeOffset now)
    {
        var at = At(now);
        if (hours <= 0 || at.Normal >= maxWindHours)
            return at;
        return at with { Normal = Math.Min(maxWindHours, at.Normal + hours) };
    }

    /// <summary>The safeword: normal wind up to max, then quest overwind with no cap, so it can never break the doll.</summary>
    public WindLayers AddSafeword(double hours, double maxWindHours, DateTimeOffset now)
    {
        var at = At(now);
        if (hours <= 0)
            return at;

        var toNormal = Math.Min(hours, Math.Max(0, maxWindHours - at.Normal));
        return at with { Normal = at.Normal + toNormal, Quest = at.Quest + (hours - toNormal) };
    }

    /// <summary>The hours an unwind removes (normal wind and player overwind), which set its cost.</summary>
    public double UnwindableHours(DateTimeOffset now)
    {
        var at = At(now);
        return at.Normal + at.Player;
    }

    /// <summary>An unwind: normal wind and player overwind go; quest overwind is never touched.</summary>
    public WindLayers Unwind(DateTimeOffset now) => At(now) with { Normal = 0, Player = 0 };

    /// <summary>
    /// After max wind hours were raised: player overwind takes up the new room in normal wind, so it only remains past
    /// 100% (a player's wind always fills normal wind first, so overwind below a full normal layer means the max went
    /// up). Quest overwind stays where it is: below 100% it is legitimately held after an unwind, and an unwind must never
    /// take it. A lowered max changes nothing (a wind never shortens the timer, and the percentage reads 100).
    /// </summary>
    public WindLayers Relayer(double maxWindHours)
    {
        var room = maxWindHours - Normal;
        if (room <= 0 || Player <= 0 || Normal <= 0)
            return this;

        var moved = Math.Min(room, Player);
        return this with { Normal = Normal + moved, Player = Player - moved };
    }

    /// <summary>
    /// Vacation: the layers exactly as stored, moved to <paramref name="now"/> with nothing drained, and a break's end moved
    /// on by the time since <see cref="AsOf"/>, so the time it had left is kept too.
    /// </summary>
    public WindLayers Frozen(DateTimeOffset now)
    {
        var paused = now > AsOf ? now - AsOf : TimeSpan.Zero;
        return this with
        {
            Normal = Math.Max(0, Normal),
            Quest = Math.Max(0, Quest),
            Player = Math.Max(0, Player),
            AsOf = now,
            BrokenUntil = BrokenUntil is { } until ? until + paused : null,
        };
    }

    /// <summary>A repair: the break ends now, and the wind carries on from where it was.</summary>
    public WindLayers Repair(DateTimeOffset now) => At(now) with { BrokenUntil = null };
}
