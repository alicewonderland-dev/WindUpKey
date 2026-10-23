using System;
using System.Collections.Generic;
using System.Linq;

namespace WindUpKey.Quest;

/// <summary>How the quest board sees a duty (from the English ContentFinderCondition sheet).</summary>
public enum QuestDutyCategory : byte
{
    Other = 0,
    Dungeon = 1,
    Guildhest = 2,
    Trial = 3,
    NormalRaid = 4,
    AllianceRaid = 5,
    Extreme = 6,
    Savage = 7,
    Ultimate = 8,
}

/// <summary>A duty the board can offer.</summary>
/// <param name="ContentFinderConditionId">Its ContentFinderCondition row.</param>
/// <param name="Category">What kind of duty it is.</param>
/// <param name="Level">Its required level.</param>
/// <param name="InMentorPool">It can come up in the Mentor Roulette.</param>
public readonly record struct QuestDutyRow(uint ContentFinderConditionId, QuestDutyCategory Category, int Level, bool InMentorPool);

/// <summary>What the rolls need to know about the character.</summary>
/// <param name="HighestCombatLevel">The highest level of any combat job (limited jobs don't count).</param>
/// <param name="HasCombatJobBelowCap">A combat job (not a limited job) is unlocked and below the level cap.</param>
/// <param name="HasNextMsq">There is a main scenario quest left to do.</param>
/// <param name="UnlockedDuties">The duties (ContentFinderCondition rows) the character has unlocked.</param>
/// <param name="UnlockableDuties">The locked duties whose unlock quest the character can take now.</param>
/// <param name="ClearedUltimates">The Ultimate raids the character has cleared.</param>
public sealed record QuestCharacterFacts(
    int HighestCombatLevel,
    bool HasCombatJobBelowCap,
    bool HasNextMsq,
    IReadOnlySet<uint> UnlockedDuties,
    IReadOnlySet<uint> UnlockableDuties,
    IReadOnlySet<uint> ClearedUltimates);

/// <summary>Rolling and rerolling the board's slots. No Dalamud dependency.</summary>
public static class QuestRollRules
{
    public const int EasySlots = 5;

    public const int MediumSlots = 3;

    public const int HardSlots = 2;

    /// <summary>The highest level in <paramref name="levels"/> at or below <paramref name="highest"/>, or null.</summary>
    public static int? Bracket(IEnumerable<int> levels, int highest)
    {
        int? best = null;
        foreach (var level in levels)
        {
            if (level <= highest && (best is null || level > best))
                best = level;
        }

        return best;
    }

    /// <summary>An Easy trial or normal raid gives half wind.</summary>
    public static bool IsHalfWind(QuestDutyCategory category) => category is QuestDutyCategory.Trial or QuestDutyCategory.NormalRaid;

    /// <summary>The Easy pool: mentor-roulette dungeons, guildhests, trials and raids (never Extremes) the character's level allows.</summary>
    public static IEnumerable<QuestDutyRow> EasyPool(IEnumerable<QuestDutyRow> duties, QuestCharacterFacts facts) =>
        duties.Where(d => d.InMentorPool
                          && d.Category is QuestDutyCategory.Dungeon or QuestDutyCategory.Guildhest or QuestDutyCategory.Trial
                              or QuestDutyCategory.NormalRaid or QuestDutyCategory.AllianceRaid
                          && d.Level <= facts.HighestCombatLevel);

    /// <summary>
    /// The day's Easy slots, in order: the next MSQ (if any), one duty the character can unlock but hasn't (if any), a
    /// Leveling Roulette (if a combat job is below the cap), then random unlocked duties up to <see cref="EasySlots"/>.
    /// </summary>
    public static List<QuestSlot> RollEasy(IReadOnlyList<QuestDutyRow> duties, QuestCharacterFacts facts, Random rng)
    {
        var slots = new List<QuestSlot>();
        if (facts.HasNextMsq)
            slots.Add(new QuestSlot { Difficulty = QuestDifficulty.Easy, Kind = QuestSlotKind.Msq });

        var pool = EasyPool(duties, facts).ToList();
        var unlockable = pool
            .Where(d => !facts.UnlockedDuties.Contains(d.ContentFinderConditionId)
                        && facts.UnlockableDuties.Contains(d.ContentFinderConditionId))
            .ToList();
        uint unlockTarget = 0;
        if (unlockable.Count > 0)
        {
            var pick = unlockable[rng.Next(unlockable.Count)];
            unlockTarget = pick.ContentFinderConditionId;
            slots.Add(DutySlot(QuestDifficulty.Easy, QuestSlotKind.Unlock, pick));
        }

        if (facts.HasCombatJobBelowCap)
            slots.Add(new QuestSlot { Difficulty = QuestDifficulty.Easy, Kind = QuestSlotKind.LevelingRoulette });

        var unlocked = pool
            .Where(d => facts.UnlockedDuties.Contains(d.ContentFinderConditionId) && d.ContentFinderConditionId != unlockTarget)
            .ToList();
        foreach (var pick in Pick(unlocked, EasySlots - slots.Count, rng))
            slots.Add(DutySlot(QuestDifficulty.Easy, QuestSlotKind.Duty, pick));

        return slots;
    }

    /// <summary>The Medium pool: the Extremes (unreal trials excluded) of the highest bracket the character's level reaches.</summary>
    public static List<QuestDutyRow> MediumPool(IReadOnlyList<QuestDutyRow> duties, QuestCharacterFacts facts)
    {
        var extremes = duties.Where(d => d.Category == QuestDutyCategory.Extreme).ToList();
        var bracket = Bracket(extremes.Select(d => d.Level), facts.HighestCombatLevel);
        return bracket is { } level ? extremes.Where(d => d.Level == level).ToList() : [];
    }

    /// <summary>The Hard pool: the Savage raids of the highest bracket the character's level reaches, plus every Ultimate they've cleared.</summary>
    public static List<QuestDutyRow> HardPool(IReadOnlyList<QuestDutyRow> duties, QuestCharacterFacts facts)
    {
        var savage = duties.Where(d => d.Category == QuestDutyCategory.Savage).ToList();
        var bracket = Bracket(savage.Select(d => d.Level), facts.HighestCombatLevel);
        var pool = bracket is { } level ? savage.Where(d => d.Level == level).ToList() : [];
        pool.AddRange(duties.Where(d => d.Category == QuestDutyCategory.Ultimate
                                        && facts.ClearedUltimates.Contains(d.ContentFinderConditionId)));
        return pool;
    }

    /// <summary>The week's Medium and Hard slots.</summary>
    public static List<QuestSlot> RollWeekly(IReadOnlyList<QuestDutyRow> duties, QuestCharacterFacts facts, Random rng)
    {
        var slots = Pick(MediumPool(duties, facts), MediumSlots, rng)
            .Select(d => DutySlot(QuestDifficulty.Medium, QuestSlotKind.Extreme, d))
            .ToList();
        slots.AddRange(Pick(HardPool(duties, facts), HardSlots, rng)
            .Select(d => DutySlot(QuestDifficulty.Hard, KindForHard(d.Category), d)));
        return slots;
    }

    public static bool IsRerollUsed(QuestBoardState state, QuestDifficulty difficulty) => difficulty switch
    {
        QuestDifficulty.Medium => state.MediumRerollUsed,
        QuestDifficulty.Hard => state.HardRerollUsed,
        _ => true,
    };

    /// <summary>A reroll is left this week for <paramref name="difficulty"/> (Medium or Hard) and it has an open slot.</summary>
    public static bool CanReroll(QuestBoardState state, QuestDifficulty difficulty) =>
        !IsRerollUsed(state, difficulty) && state.SlotsOf(difficulty).Any(s => !s.Completed);

    /// <summary>
    /// Replaces every open slot of <paramref name="difficulty"/> (Medium or Hard) and uses up its weekly reroll. Completed
    /// slots stay as they are, so the week still allows at most 3 Medium and 2 Hard completions. The new duties avoid both
    /// the replaced and the completed ones when the pool has enough others; when it doesn't, a replaced duty may come back,
    /// but a completed one never does. Returns false (and changes nothing) when no reroll is allowed.
    /// </summary>
    public static bool Reroll(
        QuestBoardState state, QuestDifficulty difficulty, IReadOnlyList<QuestDutyRow> duties, QuestCharacterFacts facts, Random rng)
    {
        if (difficulty is not (QuestDifficulty.Medium or QuestDifficulty.Hard) || !CanReroll(state, difficulty))
            return false;

        var current = state.SlotsOf(difficulty).ToList();
        var open = current.Where(s => !s.Completed).ToList();
        var completedIds = current.Where(s => s.Completed).Select(s => s.ContentFinderConditionId).ToHashSet();
        var openIds = open.Select(s => s.ContentFinderConditionId).ToHashSet();
        var pool = (difficulty == QuestDifficulty.Medium ? MediumPool(duties, facts) : HardPool(duties, facts))
            .Where(d => !completedIds.Contains(d.ContentFinderConditionId))
            .ToList();

        var fresh = Pick(pool.Where(d => !openIds.Contains(d.ContentFinderConditionId)).ToList(), open.Count, rng);
        if (fresh.Count < open.Count)
        {
            var chosen = fresh.Select(d => d.ContentFinderConditionId).ToHashSet();
            fresh.AddRange(Pick(pool.Where(d => !chosen.Contains(d.ContentFinderConditionId)).ToList(), open.Count - fresh.Count, rng));
        }

        // Each open slot is replaced where it stands; one the pool can't refill is dropped.
        var next = 0;
        for (var i = 0; i < state.Slots.Count; i++)
        {
            var slot = state.Slots[i];
            if (slot.Difficulty != difficulty || slot.Completed)
                continue;
            if (next < fresh.Count)
            {
                var duty = fresh[next++];
                state.Slots[i] = DutySlot(difficulty, difficulty == QuestDifficulty.Medium ? QuestSlotKind.Extreme : KindForHard(duty.Category), duty);
            }
            else
            {
                state.Slots.RemoveAt(i--);
            }
        }

        if (difficulty == QuestDifficulty.Medium)
            state.MediumRerollUsed = true;
        else
            state.HardRerollUsed = true;
        return true;
    }

    /// <summary>Puts this reset's Easy slots in place of the old ones (Easy stays first).</summary>
    public static void ReplaceEasy(QuestBoardState state, IEnumerable<QuestSlot> easy, DateTimeOffset dailyPeriodStart)
    {
        state.Slots.RemoveAll(s => s.Difficulty == QuestDifficulty.Easy);
        state.Slots.InsertRange(0, easy);
        state.DailyPeriodStartUtc = dailyPeriodStart;
    }

    /// <summary>Puts this week's Medium and Hard slots in place of the old ones and gives the rerolls back.</summary>
    public static void ReplaceWeekly(QuestBoardState state, IEnumerable<QuestSlot> weekly, DateTimeOffset weeklyPeriodStart)
    {
        state.Slots.RemoveAll(s => s.Difficulty is QuestDifficulty.Medium or QuestDifficulty.Hard);
        state.Slots.AddRange(weekly);
        state.WeeklyPeriodStartUtc = weeklyPeriodStart;
        state.MediumRerollUsed = false;
        state.HardRerollUsed = false;
    }

    private static QuestSlotKind KindForHard(QuestDutyCategory category) =>
        category == QuestDutyCategory.Ultimate ? QuestSlotKind.Ultimate : QuestSlotKind.Savage;

    private static QuestSlot DutySlot(QuestDifficulty difficulty, QuestSlotKind kind, QuestDutyRow duty) => new()
    {
        Difficulty = difficulty,
        Kind = kind,
        ContentFinderConditionId = duty.ContentFinderConditionId,
        HalfWind = difficulty == QuestDifficulty.Easy && IsHalfWind(duty.Category),
    };

    /// <summary>Up to <paramref name="count"/> distinct items of <paramref name="source"/>, in random order.</summary>
    private static List<QuestDutyRow> Pick(List<QuestDutyRow> source, int count, Random rng)
    {
        var items = source.DistinctBy(d => d.ContentFinderConditionId).ToList();
        var take = Math.Clamp(count, 0, items.Count);
        for (var i = 0; i < take; i++)
        {
            var j = rng.Next(i, items.Count);
            (items[i], items[j]) = (items[j], items[i]);
        }

        return items.GetRange(0, take);
    }
}
