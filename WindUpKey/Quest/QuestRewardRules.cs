using System;
using WindUpKey.Services;

namespace WindUpKey.Quest;

/// <summary>Stickers and wind for a completed slot, and the line bonuses. No Dalamud dependency.</summary>
public static class QuestRewardRules
{
    /// <summary>Wind per sticker (Easy 1 sticker, Medium 2, Hard 3).</summary>
    public const double HoursPerSticker = 8;

    /// <summary>An MSQ turn-in always gives this, sticker or not.</summary>
    public const double MsqHours = 8;

    public const double OneLineHours = 24;

    public const double TwoLineHours = 72;

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

    /// <summary>The wind multiplier for who was in the party.</summary>
    public static double PartyMultiplier(QuestPartyBonus bonus) => bonus switch
    {
        QuestPartyBonus.Owner => OwnerPartyMultiplier,
        QuestPartyBonus.Partner => PartnerPartyMultiplier,
        _ => 1,
    };

    /// <summary>Whether a slot can give the first-clear bonus: a duty slot (not an MSQ turn-in or the Leveling Roulette).</summary>
    public static bool CanBeFirstClear(QuestSlotKind kind) =>
        kind is QuestSlotKind.Duty or QuestSlotKind.Unlock or QuestSlotKind.Extreme or QuestSlotKind.Savage or QuestSlotKind.Ultimate;

    /// <summary>What one completed slot gives.</summary>
    public readonly record struct SlotReward(int Stickers, double Hours);

    /// <summary>The cap quest wind is clamped to: <see cref="OverwindFactor"/> × max wind hours.</summary>
    public static double OverwindCapHours(double maxWindHours) =>
        OverwindFactor * Math.Max(WindMath.MinCapHours, maxWindHours);

    public static int StickersFor(QuestDifficulty difficulty) => difficulty switch
    {
        QuestDifficulty.Easy => 1,
        QuestDifficulty.Medium => 2,
        QuestDifficulty.Hard => 3,
        _ => 0,
    };

    /// <summary>
    /// A duty clear that matched <paramref name="slot"/>: Easy needs it synced (else nothing), and an Easy trial or normal
    /// raid gives half wind; an unsynced Medium or Hard clear gives a quarter of the wind and no stickers. An Ultimate's
    /// wind is <see cref="UltimateMultiplier"/>×, and a <paramref name="firstClear"/> of a duty slot
    /// <see cref="FirstClearMultiplier"/>× on top (the multipliers apply to the wind only, and multiply together).
    /// </summary>
    public static SlotReward ForDutyClear(QuestSlot slot, bool synced, bool firstClear = false)
    {
        var stickers = StickersFor(slot.Difficulty);
        var hours = stickers * HoursPerSticker;
        if (slot.Kind == QuestSlotKind.Ultimate)
            hours *= UltimateMultiplier;
        if (firstClear && CanBeFirstClear(slot.Kind))
            hours *= FirstClearMultiplier;
        if (slot.Difficulty == QuestDifficulty.Easy)
        {
            if (!synced)
                return new SlotReward(0, 0);
            return new SlotReward(stickers, slot.HalfWind ? hours / 2 : hours);
        }

        return synced ? new SlotReward(stickers, hours) : new SlotReward(0, hours * UnsyncedShare);
    }

    /// <summary>An MSQ turn-in: always <see cref="MsqHours"/>, and a sticker only for the first one each day.</summary>
    public static SlotReward ForMsq(bool stickerAvailable) => new(stickerAvailable ? 1 : 0, MsqHours);

    /// <summary>The bonus for lines finished by one sticker: 1 line +24h, 2 lines +72h, 3 or more fill to the overwind cap.</summary>
    public static LineBonus ForLines(int lines) => lines switch
    {
        <= 0 => new LineBonus(0, false),
        1 => new LineBonus(OneLineHours, false),
        2 => new LineBonus(TwoLineHours, false),
        _ => new LineBonus(0, true),
    };

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
