using System;
using System.Collections.Generic;
using System.Linq;

namespace WindUpKey.Quest;

/// <summary>
/// A doll character's quest board: the 4×4 sticker board, this reset's slots and the weekly rerolls. Saved per character
/// (<see cref="CharacterProfile.QuestBoard"/>, mirrored in <see cref="Configuration.QuestBoard"/>). No Dalamud dependency.
/// </summary>
[Serializable]
public sealed class QuestBoardState
{
    /// <summary>The sticker board: bit <c>row * 4 + column</c> is set when that cell has a sticker.</summary>
    public ushort Stickers { get; set; }

    /// <summary>The daily period (<see cref="QuestResetClock.DailyPeriodStart"/>) the Easy slots were rolled for.</summary>
    public DateTimeOffset? DailyPeriodStartUtc { get; set; }

    /// <summary>The weekly period (<see cref="QuestResetClock.WeeklyPeriodStart"/>) the Medium and Hard slots were rolled for.</summary>
    public DateTimeOffset? WeeklyPeriodStartUtc { get; set; }

    /// <summary>This reset's slots, all tiers (Easy first, then Medium, then Hard).</summary>
    public List<QuestSlot> Slots { get; set; } = [];

    /// <summary>The Medium reroll was used this week.</summary>
    public bool MediumRerollUsed { get; set; }

    /// <summary>The Hard reroll was used this week.</summary>
    public bool HardRerollUsed { get; set; }

    /// <summary>The main scenario quest the board is waiting on (Quest row), or 0. Its turn-in is the MSQ slot's credit.</summary>
    public uint MsqTrackedQuestId { get; set; }

    /// <summary>
    /// Main scenario quests that can't give wind again: every one credited, plus every one already done when the board
    /// was first set up (<see cref="MsqHistorySeeded"/>), so New Game+ replays of the story earn nothing.
    /// </summary>
    public List<uint> CreditedMsqQuestIds { get; set; } = [];

    /// <summary>The main scenario quests done before the board existed were added to <see cref="CreditedMsqQuestIds"/>.</summary>
    public bool MsqHistorySeeded { get; set; }

    public QuestBoardState Clone() => new()
    {
        Stickers = Stickers,
        DailyPeriodStartUtc = DailyPeriodStartUtc,
        WeeklyPeriodStartUtc = WeeklyPeriodStartUtc,
        Slots = (Slots ?? []).Where(s => s is not null).Select(s => s.Clone()).ToList(),
        MediumRerollUsed = MediumRerollUsed,
        HardRerollUsed = HardRerollUsed,
        MsqTrackedQuestId = MsqTrackedQuestId,
        CreditedMsqQuestIds = (CreditedMsqQuestIds ?? []).ToList(),
        MsqHistorySeeded = MsqHistorySeeded,
    };

    /// <summary>Drops null or invalid slots (a damaged or hand-edited config). Returns true when anything changed.</summary>
    public bool Normalize()
    {
        Slots ??= [];
        CreditedMsqQuestIds ??= [];
        return Slots.RemoveAll(s => s is null || !s.IsValid) > 0;
    }

    public IEnumerable<QuestSlot> SlotsOf(QuestDifficulty difficulty) => Slots.Where(s => s.Difficulty == difficulty);
}
