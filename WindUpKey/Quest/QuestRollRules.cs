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
/// <param name="SortKey">The sheet's order (a Savage tier's fights run first to last).</param>
/// <param name="HighEnd">The sheet's current-content flag: today's Savage tier, Ultimates and the newest Extreme.</param>
public readonly record struct QuestDutyRow(
    uint ContentFinderConditionId, QuestDutyCategory Category, int Level, bool InMentorPool, uint SortKey = 0, bool HighEnd = false);

/// <summary>What the rolls need to know about the character.</summary>
/// <param name="HighestCombatLevel">The highest level of any combat job (limited jobs don't count).</param>
/// <param name="HasCombatJobBelowCap">A combat job (not a limited job) is unlocked and below the level cap.</param>
/// <param name="HasNextMsq">There is a main scenario quest left to do.</param>
/// <param name="UnlockedDuties">The duties (ContentFinderCondition rows) the character has unlocked.</param>
/// <param name="UnlockableDuties">The locked duties whose unlock quest the character can take now.</param>
/// <param name="ClearedUltimates">The Ultimate raids the character has cleared.</param>
/// <param name="ClearedHighEnd">The Extremes and Savage raids the character has cleared.</param>
public sealed record QuestCharacterFacts(
    int HighestCombatLevel,
    bool HasCombatJobBelowCap,
    bool HasNextMsq,
    IReadOnlySet<uint> UnlockedDuties,
    IReadOnlySet<uint> UnlockableDuties,
    IReadOnlySet<uint> ClearedUltimates,
    IReadOnlySet<uint>? ClearedHighEnd = null)
{
    private static readonly IReadOnlySet<uint> None = new HashSet<uint>();

    /// <summary>Whether the character has cleared Extreme or Savage <paramref name="contentFinderConditionId"/>.</summary>
    public bool HasCleared(uint contentFinderConditionId) => (ClearedHighEnd ?? None).Contains(contentFinderConditionId);
}

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

    /// <summary>A Savage tier's fights; older tiers are told apart by the sheet's order in runs of this many.</summary>
    public const int SavageFightsPerTier = 4;

    /// <summary>A cleared Ultimate's weight in a Hard draw, against 1 for each Savage fight.</summary>
    public const double UltimateWeight = 0.25;

    /// <summary>
    /// The newest Savage tier in the highest bracket the character's level reaches, first fight to last: the tier the game
    /// marks as current (<see cref="QuestDutyRow.HighEnd"/>), or else the last <see cref="SavageFightsPerTier"/> fights
    /// of the bracket in the sheet's order (a character below the level cap).
    /// </summary>
    public static List<QuestDutyRow> NewestSavageTier(IReadOnlyList<QuestDutyRow> duties, QuestCharacterFacts facts)
    {
        var savage = duties.Where(d => d.Category == QuestDutyCategory.Savage).ToList();
        if (Bracket(savage.Select(d => d.Level), facts.HighestCombatLevel) is not { } level)
            return [];

        var bracket = savage
            .Where(d => d.Level == level)
            .DistinctBy(d => d.ContentFinderConditionId)
            .OrderBy(d => d.SortKey)
            .ThenBy(d => d.ContentFinderConditionId)
            .ToList();
        var current = bracket.Where(d => d.HighEnd).ToList();
        return current.Count > 0 ? current : bracket.Skip(Math.Max(0, bracket.Count - SavageFightsPerTier)).ToList();
    }

    /// <summary>The Ultimate raids the character has cleared.</summary>
    public static List<QuestDutyRow> ClearedUltimates(IReadOnlyList<QuestDutyRow> duties, QuestCharacterFacts facts) =>
        duties.Where(d => d.Category == QuestDutyCategory.Ultimate && facts.ClearedUltimates.Contains(d.ContentFinderConditionId))
            .DistinctBy(d => d.ContentFinderConditionId)
            .ToList();

    /// <summary>The Hard pool: the newest Savage tier, plus every Ultimate the character has cleared.</summary>
    public static List<QuestDutyRow> HardPool(IReadOnlyList<QuestDutyRow> duties, QuestCharacterFacts facts)
    {
        var pool = NewestSavageTier(duties, facts);
        pool.AddRange(ClearedUltimates(duties, facts));
        return pool;
    }

    private static readonly IReadOnlySet<uint> NoDuties = new HashSet<uint>();

    /// <summary>The week's Medium and Hard slots.</summary>
    public static List<QuestSlot> RollWeekly(IReadOnlyList<QuestDutyRow> duties, QuestCharacterFacts facts, Random rng)
    {
        var slots = PickMedium(MediumPool(duties, facts), facts, MediumSlots, NoDuties, NoDuties, needCleared: true, rng)
            .Select(d => DutySlot(QuestDifficulty.Medium, QuestSlotKind.Extreme, d))
            .ToList();
        slots.AddRange(PickHard(duties, facts, HardSlots, NoDuties, NoDuties, needCleared: true, rng)
            .Select(d => DutySlot(QuestDifficulty.Hard, KindForHard(d.Category), d)));
        return slots;
    }

    /// <summary>
    /// Up to <paramref name="count"/> Extremes from <paramref name="pool"/>: one the character has cleared first when
    /// <paramref name="needCleared"/> (if any), then random others. Never one in <paramref name="taken"/>; one in
    /// <paramref name="avoid"/> only when nothing else is left.
    /// </summary>
    public static List<QuestDutyRow> PickMedium(
        List<QuestDutyRow> pool,
        QuestCharacterFacts facts,
        int count,
        IReadOnlySet<uint> taken,
        IReadOnlySet<uint> avoid,
        bool needCleared,
        Random rng)
    {
        var chosen = new List<QuestDutyRow>();
        bool Free(QuestDutyRow d) => !taken.Contains(d.ContentFinderConditionId) && chosen.All(c => c.ContentFinderConditionId != d.ContentFinderConditionId);

        if (needCleared && count > 0
            && PreferNotAvoided(pool.Where(d => Free(d) && facts.HasCleared(d.ContentFinderConditionId)), avoid) is { Count: > 0 } cleared)
            chosen.Add(cleared[rng.Next(cleared.Count)]);

        while (chosen.Count < count && PreferNotAvoided(pool.Where(Free), avoid) is { Count: > 0 } rest)
            chosen.Add(rest[rng.Next(rest.Count)]);

        return chosen;
    }

    /// <summary>
    /// Up to <paramref name="count"/> Hard duties. First, when <paramref name="needCleared"/>, a Savage of the newest tier
    /// the character has cleared (random among them, if any). Every other pick is one weighted draw: "a Savage" weighs 1
    /// for each tier fight still free, and each cleared Ultimate <see cref="UltimateWeight"/>. A Savage pick is the earliest
    /// fight not cleared yet, or a random one once all are cleared. Never one in <paramref name="taken"/>; one in
    /// <paramref name="avoid"/> only when nothing else is left.
    /// </summary>
    public static List<QuestDutyRow> PickHard(
        IReadOnlyList<QuestDutyRow> duties,
        QuestCharacterFacts facts,
        int count,
        IReadOnlySet<uint> taken,
        IReadOnlySet<uint> avoid,
        bool needCleared,
        Random rng)
    {
        var tier = NewestSavageTier(duties, facts);
        var ultimates = ClearedUltimates(duties, facts);
        var chosen = new List<QuestDutyRow>();
        bool Free(QuestDutyRow d) => !taken.Contains(d.ContentFinderConditionId) && chosen.All(c => c.ContentFinderConditionId != d.ContentFinderConditionId);

        if (needCleared && count > 0
            && PreferNotAvoided(tier.Where(d => Free(d) && facts.HasCleared(d.ContentFinderConditionId)), avoid) is { Count: > 0 } cleared)
            chosen.Add(cleared[rng.Next(cleared.Count)]);

        while (chosen.Count < count)
        {
            // Avoided duties come back only when neither kind has anything else left.
            var savages = tier.Where(d => Free(d) && !avoid.Contains(d.ContentFinderConditionId)).ToList();
            var ults = ultimates.Where(d => Free(d) && !avoid.Contains(d.ContentFinderConditionId)).ToList();
            if (savages.Count == 0 && ults.Count == 0)
            {
                savages = tier.Where(Free).ToList();
                ults = ultimates.Where(Free).ToList();
            }

            if (savages.Count == 0 && ults.Count == 0)
                break;

            var draw = rng.NextDouble() * (savages.Count + UltimateWeight * ults.Count);
            if (draw < savages.Count)
            {
                var uncleared = savages.FirstOrDefault(d => !facts.HasCleared(d.ContentFinderConditionId));
                chosen.Add(uncleared.ContentFinderConditionId != 0 ? uncleared : savages[rng.Next(savages.Count)]);
            }
            else
            {
                chosen.Add(ults[rng.Next(ults.Count)]);
            }
        }

        return chosen;
    }

    /// <summary>The candidates not in <paramref name="avoid"/>, or all of them when every one is.</summary>
    private static List<QuestDutyRow> PreferNotAvoided(IEnumerable<QuestDutyRow> candidates, IReadOnlySet<uint> avoid)
    {
        var all = candidates.DistinctBy(d => d.ContentFinderConditionId).ToList();
        var preferred = all.Where(d => !avoid.Contains(d.ContentFinderConditionId)).ToList();
        return preferred.Count > 0 ? preferred : all;
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
    /// slots stay as they are, so the week still allows at most 3 Medium and 2 Hard completions. The new duties follow the
    /// roll's rules (<see cref="PickMedium"/>, <see cref="PickHard"/>): a cleared one is kept on the board unless a
    /// completed slot already is one, and they avoid the replaced duties when the pool has others; when it doesn't, a
    /// replaced duty may come back, but a completed one never does. Returns false (and changes nothing) when no reroll is
    /// allowed.
    /// </summary>
    public static bool Reroll(
        QuestBoardState state, QuestDifficulty difficulty, IReadOnlyList<QuestDutyRow> duties, QuestCharacterFacts facts, Random rng)
    {
        if (difficulty is not (QuestDifficulty.Medium or QuestDifficulty.Hard) || !CanReroll(state, difficulty))
            return false;

        var current = state.SlotsOf(difficulty).ToList();
        var open = current.Where(s => !s.Completed).ToList();
        var completed = current.Where(s => s.Completed).ToList();
        var completedIds = completed.Select(s => s.ContentFinderConditionId).ToHashSet();
        var openIds = open.Select(s => s.ContentFinderConditionId).ToHashSet();
        var fresh = difficulty == QuestDifficulty.Medium
            ? PickMedium(
                MediumPool(duties, facts), facts, open.Count, completedIds, openIds,
                needCleared: !completed.Any(s => facts.HasCleared(s.ContentFinderConditionId)), rng)
            : PickHard(
                duties, facts, open.Count, completedIds, openIds,
                needCleared: !completed.Any(s => s.Kind == QuestSlotKind.Savage && facts.HasCleared(s.ContentFinderConditionId)), rng);

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

    /// <summary>The board has Easy slots and every one is done (a full clear rolls a new set before the daily reset).</summary>
    public static bool AllEasyDone(QuestBoardState state)
    {
        var easy = state.SlotsOf(QuestDifficulty.Easy).ToList();
        return easy.Count > 0 && easy.All(s => s.Completed);
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
