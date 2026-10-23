using System;
using System.Collections.Generic;
using System.Linq;

namespace WindUpKey.Quest;

/// <summary>One sticker put on the board, and the lines it finished (the board clears when there are any).</summary>
/// <param name="Cell">The cell it went in (row * 4 + column).</param>
/// <param name="Lines">How many lines it finished.</param>
/// <param name="BoardBefore">The board with this sticker, before a line cleared it (for the clear's animation).</param>
/// <param name="LineCells">The cells of the finished lines (0 when none).</param>
public readonly record struct StickerPlacement(int Cell, int Lines, ushort BoardBefore, ushort LineCells);

/// <summary>What a set of stickers and wind added up to.</summary>
/// <param name="Hours">Wind to add (from slots and line bonuses).</param>
/// <param name="FillToCap">A sticker finished three or more lines: wind goes to the overwind cap.</param>
/// <param name="Placements">Each sticker, in order.</param>
public sealed record QuestAward(double Hours, bool FillToCap, IReadOnlyList<StickerPlacement> Placements)
{
    public int LinesCleared => Placements.Sum(p => p.Lines);

    /// <summary>Three or more lines finished by one sticker (the achievement flag).</summary>
    public bool TripleLine => Placements.Any(p => p.Lines >= 3);
}

/// <summary>A duty clear as the quest board sees it.</summary>
/// <param name="ContentFinderConditionId">The duty.</param>
/// <param name="Synced">Level-synced (or at or below the duty's sync level).</param>
/// <param name="LevelingRouletteBelowCap">Entered through the Leveling Roulette on a job below the level cap.</param>
public readonly record struct QuestDutyClear(uint ContentFinderConditionId, bool Synced, bool LevelingRouletteBelowCap);

/// <summary>A slot a clear completed (null for an MSQ turn-in on a day without an MSQ slot), and its reward.</summary>
public readonly record struct QuestSlotCredit(QuestSlot? Slot, QuestRewardRules.SlotReward Reward);

/// <summary>Slots completed by one clear, and whether an Easy slot matched but didn't count for being unsynced.</summary>
public sealed record QuestDutyCredit(IReadOnlyList<QuestSlotCredit> Credits, bool UnsyncedEasyMatch);

/// <summary>Crediting clears and placing stickers on a <see cref="QuestBoardState"/>. No Dalamud dependency.</summary>
public static class QuestBoardEngine
{
    /// <summary>
    /// Completes every open slot <paramref name="clear"/> matches. An Easy slot needs the clear synced (an unsynced one
    /// stays open); a Medium or Hard slot is used up either way, unsynced for a quarter of the wind and no stickers, so
    /// an unsynced clear can't be repeated for more wind.
    /// </summary>
    public static QuestDutyCredit CreditDuty(QuestBoardState state, QuestDutyClear clear)
    {
        var credits = new List<QuestSlotCredit>();
        var unsyncedEasy = false;
        foreach (var slot in state.Slots)
        {
            if (slot.Completed || !Matches(slot, clear))
                continue;

            if (slot.Difficulty == QuestDifficulty.Easy && !clear.Synced)
            {
                unsyncedEasy = true;
                continue;
            }

            slot.Completed = true;
            slot.CompletedUnsynced = !clear.Synced;
            credits.Add(new QuestSlotCredit(slot, QuestRewardRules.ForDutyClear(slot, clear.Synced)));
        }

        return new QuestDutyCredit(credits, unsyncedEasy);
    }

    /// <summary>
    /// The turn-in of main scenario quest <paramref name="questId"/>. Always wind, and a sticker when today's MSQ slot
    /// hasn't given one yet (the slot is marked completed, but stays open for wind; a day rolled without an MSQ slot gives
    /// wind only). Each quest is credited once ever, so replaying the story (New Game+) earns nothing. Null when
    /// <paramref name="questId"/> was already credited.
    /// </summary>
    public static QuestSlotCredit? CreditMsq(QuestBoardState state, uint questId)
    {
        if (questId == 0 || state.CreditedMsqQuestIds.Contains(questId))
            return null;

        state.CreditedMsqQuestIds.Add(questId);
        var slot = state.Slots.FirstOrDefault(s => s.Kind == QuestSlotKind.Msq);
        var sticker = slot is { Completed: false };
        if (slot is not null)
            slot.Completed = true;
        return new QuestSlotCredit(slot, QuestRewardRules.ForMsq(sticker));
    }

    /// <summary>More main scenario quests than this done between two polls means the history is out of step.</summary>
    public const int MaxMsqCreditsAtOnce = 3;

    /// <summary>
    /// The main scenario quests done since the last poll: every one in <paramref name="completedMsqQuestIds"/> not
    /// credited yet. The first may give today's sticker. More than <see cref="MaxMsqCreditsAtOnce"/> at once means the
    /// history was seeded before the game's quest data loaded: those are recorded without wind, and only
    /// <paramref name="tracked"/> (the quest the board was waiting on) counts.
    /// </summary>
    public static List<QuestSlotCredit> CreditMsqTurnIns(QuestBoardState state, IEnumerable<uint> completedMsqQuestIds, uint tracked)
    {
        var known = state.CreditedMsqQuestIds.ToHashSet();
        var fresh = completedMsqQuestIds.Where(id => id != 0 && known.Add(id)).ToList();
        if (fresh.Count > MaxMsqCreditsAtOnce)
        {
            var keep = fresh.Contains(tracked) ? tracked : 0;
            state.CreditedMsqQuestIds.AddRange(fresh.Where(id => id != keep));
            fresh = keep == 0 ? [] : [keep];
        }

        var credits = new List<QuestSlotCredit>();
        foreach (var id in fresh)
        {
            if (CreditMsq(state, id) is { } credit)
                credits.Add(credit);
        }

        return credits;
    }

    /// <summary>
    /// The first time a character's board is set up: every main scenario quest they've already done can't give wind
    /// later, so replaying the story in New Game+ earns nothing. Returns false when it was already done.
    /// </summary>
    public static bool SeedMsqHistory(QuestBoardState state, IEnumerable<uint> completedMsqQuestIds)
    {
        if (state.MsqHistorySeeded)
            return false;

        var known = state.CreditedMsqQuestIds.ToHashSet();
        state.CreditedMsqQuestIds.AddRange(completedMsqQuestIds.Where(id => id != 0 && known.Add(id)));
        state.MsqHistorySeeded = true;
        return true;
    }

    /// <summary>
    /// Puts <paramref name="stickers"/> on the board one at a time in random empty cells (<paramref name="nextIndex"/>
    /// returns 0 ≤ i &lt; n for n empty cells). Lines are checked after each sticker: any line clears the board at once,
    /// and the stickers left go on the fresh board. <paramref name="baseHours"/> is the slots' wind; line bonuses add to it.
    /// </summary>
    public static QuestAward PlaceStickers(QuestBoardState state, int stickers, double baseHours, Func<int, int> nextIndex)
    {
        var hours = baseHours;
        var fill = false;
        var placements = new List<StickerPlacement>();
        for (var i = 0; i < stickers; i++)
        {
            // Can't happen (a full board always has a line), but never loop on a damaged config.
            if (StickerBoard.EmptyCount(state.Stickers) == 0)
                state.Stickers = 0;

            var board = StickerBoard.Place(state.Stickers, nextIndex(StickerBoard.EmptyCount(state.Stickers)), out var cell);
            var lines = StickerBoard.CompletedLines(board);
            placements.Add(new StickerPlacement(cell, lines, board, StickerBoard.CompletedLineCells(board)));
            state.Stickers = lines == 0 ? board : (ushort)0;
            if (lines == 0)
                continue;

            var bonus = QuestRewardRules.ForLines(lines);
            hours += bonus.Hours;
            fill |= bonus.FillToCap;
        }

        return new QuestAward(hours, fill, placements);
    }

    private static bool Matches(QuestSlot slot, QuestDutyClear clear) => slot.Kind switch
    {
        QuestSlotKind.LevelingRoulette => clear.LevelingRouletteBelowCap,
        QuestSlotKind.Duty or QuestSlotKind.Unlock or QuestSlotKind.Extreme or QuestSlotKind.Savage or QuestSlotKind.Ultimate
            => slot.ContentFinderConditionId != 0 && slot.ContentFinderConditionId == clear.ContentFinderConditionId,
        _ => false,
    };
}
