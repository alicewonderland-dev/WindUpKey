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
    /// raid gives half wind; an unsynced Medium or Hard clear gives a quarter of the wind and no stickers.
    /// </summary>
    public static SlotReward ForDutyClear(QuestSlot slot, bool synced)
    {
        var stickers = StickersFor(slot.Difficulty);
        var hours = stickers * HoursPerSticker;
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
