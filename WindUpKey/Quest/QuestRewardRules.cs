using System;
using WindUpKey.Services;

namespace WindUpKey.Quest;

/// <summary>Stickers and wind for a completed slot, and the line bonuses. No Dalamud dependency.</summary>
public static class QuestRewardRules
{
    /// <summary>A turn of the key: the board shows wind in turns (and half turns), never hours.</summary>
    public const double HoursPerTurn = 8;

    /// <summary>A Daily or Easy completion's wind (1½ turns).</summary>
    public const double DailyHours = 12;

    public const double EasyHours = 12;

    /// <summary>Medium and Hard: a turn per sticker.</summary>
    public const double HoursPerSticker = 8;

    /// <summary>A main scenario turn-in always gives this (no sticker or spring).</summary>
    public const double MsqHours = 8;

    public const double OneLineHours = 24;

    public const double TwoLineHours = 72;

    /// <summary>Roulettes that finish the Daily slot.</summary>
    public const int DailyRoulettes = 2;

    /// <summary>Quest wind may overwind up to this many times the doll's max wind hours.</summary>
    public const double OverwindFactor = 2;

    /// <summary>A Medium or Hard clear done unsynced gives this share of its wind, and no stickers.</summary>
    public const double UnsyncedShare = 0.25;

    /// <summary>A duty slot the character had never cleared before gives this many times its wind.</summary>
    public const double FirstClearMultiplier = 3;

    /// <summary>An Ultimate slot gives this many times a Hard slot's wind (its stickers stay 3).</summary>
    public const double UltimateMultiplier = 3;

    /// <summary>A paired partner in the party when the duty or turn-in completes.</summary>
    public const double PartnerPartyMultiplier = 1.5;

    /// <summary>One of the doll's owners in the party (instead of <see cref="PartnerPartyMultiplier"/>).</summary>
    public const double OwnerPartyMultiplier = 2;

    /// <summary>Every completed slot pays this many spare springs (doubled for winders by the relay), as each line does.</summary>
    public const int SpringsPerCompletion = 1;

    /// <summary>The wind multiplier for who was in the party.</summary>
    public static double PartyMultiplier(QuestPartyBonus bonus) => bonus switch
    {
        QuestPartyBonus.Owner => OwnerPartyMultiplier,
        QuestPartyBonus.Partner => PartnerPartyMultiplier,
        _ => 1,
    };

    /// <summary>Whether a slot can give the first-clear bonus: a duty slot (not the Leveling Roulette or the Daily).</summary>
    public static bool CanBeFirstClear(QuestSlotKind kind) =>
        kind is QuestSlotKind.Duty or QuestSlotKind.Unlock or QuestSlotKind.Extreme or QuestSlotKind.Savage or QuestSlotKind.Ultimate;

    /// <summary>What one completed slot gives.</summary>
    public readonly record struct SlotReward(int Stickers, double Hours);

    /// <summary>The cap quest wind is clamped to: <see cref="OverwindFactor"/> × max wind hours.</summary>
    public static double OverwindCapHours(double maxWindHours) =>
        OverwindFactor * Math.Max(WindMath.MinCapHours, maxWindHours);

    public static int StickersFor(QuestDifficulty difficulty) => difficulty switch
    {
        QuestDifficulty.Daily => 1,
        QuestDifficulty.Easy => 1,
        QuestDifficulty.Medium => 2,
        QuestDifficulty.Hard => 3,
        _ => 0,
    };

    /// <summary>A tier's own wind for a completion (before any hidden multiplier): what the board shows, in turns.</summary>
    public static double BaseHoursFor(QuestDifficulty difficulty) => difficulty switch
    {
        QuestDifficulty.Daily => DailyHours,
        QuestDifficulty.Easy => EasyHours,
        QuestDifficulty.Medium or QuestDifficulty.Hard => StickersFor(difficulty) * HoursPerSticker,
        _ => 0,
    };

    /// <summary>
    /// A duty clear that matched <paramref name="slot"/>: Easy needs it synced (else nothing); an unsynced Medium or Hard
    /// clear gives a quarter of the wind and no stickers. An Ultimate's wind is <see cref="UltimateMultiplier"/>×, and a
    /// <paramref name="firstClear"/> of a duty slot <see cref="FirstClearMultiplier"/>× on top (the multipliers apply to
    /// the wind only, and multiply together).
    /// </summary>
    public static SlotReward ForDutyClear(QuestSlot slot, bool synced, bool firstClear = false)
    {
        var stickers = StickersFor(slot.Difficulty);
        var hours = BaseHoursFor(slot.Difficulty);
        if (slot.Kind == QuestSlotKind.Ultimate)
            hours *= UltimateMultiplier;
        if (firstClear && CanBeFirstClear(slot.Kind))
            hours *= FirstClearMultiplier;
        if (slot.Difficulty == QuestDifficulty.Easy)
            return synced ? new SlotReward(stickers, hours) : new SlotReward(0, 0);

        return synced ? new SlotReward(stickers, hours) : new SlotReward(0, hours * UnsyncedShare);
    }

    /// <summary>The Daily slot finished: its sticker and wind.</summary>
    public static SlotReward ForDaily() => new(StickersFor(QuestDifficulty.Daily), DailyHours);

    /// <summary>A main scenario turn-in: wind only.</summary>
    public static SlotReward ForMsq() => new(0, MsqHours);

    /// <summary>The bonus for lines finished by one sticker: 1 line +24h, 2 lines +72h, 3 or more fill to the overwind cap.</summary>
    public static LineBonus ForLines(int lines) => lines switch
    {
        <= 0 => new LineBonus(0, false),
        1 => new LineBonus(OneLineHours, false),
        2 => new LineBonus(TwoLineHours, false),
        _ => new LineBonus(0, true),
    };

    /// <summary>
    /// Wind as the board shows it, in turns of 8 hours rounded to the nearest half turn ("½ turn", "1 turn",
    /// "1½ turns", "3 turns"). Never hours.
    /// </summary>
    public static string Turns(double hours)
    {
        var halves = (int)Math.Round(hours / (HoursPerTurn / 2), MidpointRounding.AwayFromZero);
        if (halves <= 0)
            return "no turns";
        var whole = halves / 2;
        var half = halves % 2 == 1;
        var number = whole == 0 ? "½" : half ? $"{whole}½" : $"{whole}";
        return halves == 2 ? "1 turn" : halves == 1 ? "½ turn" : $"{number} turns";
    }

    /// <param name="Hours">Wind added (when not <paramref name="FillToCap"/>).</param>
    /// <param name="FillToCap">Three or more lines at once: wind goes to the overwind cap.</param>
    public readonly record struct LineBonus(double Hours, bool FillToCap);
}

/// <summary>Who was in the doll's party when a duty or turn-in completed.</summary>
public enum QuestPartyBonus : byte
{
    None = 0,

    /// <summary>A paired partner (doll or winder).</summary>
    Partner = 1,

    /// <summary>One of the doll's owners.</summary>
    Owner = 2,
}
