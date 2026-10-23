using WindUpKey.Quest;
using WindUpKey.Services;

namespace WindUpKey.Tests;

/// <summary>The quest board: sticker lines, rewards, resets, duty pools, rolls and rerolls.</summary>
public sealed class QuestBoardTests
{
    private static ushort Cells(params int[] cells) => (ushort)cells.Aggregate(0, (mask, c) => mask | (1 << c));

    /// <summary>Puts each sticker in the given cell (cells index the whole board; converted to an empty-cell index).</summary>
    private static Func<int, int> InCells(QuestBoardState state, params int[] cells)
    {
        var queue = new Queue<int>(cells);
        return _ =>
        {
            var cell = queue.Dequeue();
            Assert.False(StickerBoard.HasSticker(state.Stickers, cell));
            return Enumerable.Range(0, cell).Count(c => !StickerBoard.HasSticker(state.Stickers, c));
        };
    }

    // ---------- the board ----------

    [Fact]
    public void TheBoard_HasTenLines_OfFourCellsEach()
    {
        Assert.Equal(10, StickerBoard.Lines.Count);
        Assert.All(StickerBoard.Lines, l => Assert.Equal(4, System.Numerics.BitOperations.PopCount(l)));
        Assert.Equal(10, StickerBoard.Lines.Distinct().Count());
        Assert.Contains(Cells(0, 5, 10, 15), StickerBoard.Lines);
        Assert.Contains(Cells(3, 6, 9, 12), StickerBoard.Lines);
        Assert.Contains(Cells(0, 1, 2, 3), StickerBoard.Lines);
        Assert.Contains(Cells(0, 4, 8, 12), StickerBoard.Lines);
    }

    [Fact]
    public void Place_FillsTheNthEmptyCell()
    {
        var board = StickerBoard.Place(Cells(0, 1), 0, out var cell);
        Assert.Equal(2, cell);
        Assert.Equal(Cells(0, 1, 2), board);

        StickerBoard.Place(Cells(0, 1), 13, out cell);
        Assert.Equal(15, cell);
        Assert.Throws<ArgumentOutOfRangeException>(() => StickerBoard.Place(Cells(0, 1), 14, out _));
    }

    [Fact]
    public void ACornerSticker_CanFinishThreeLinesAtOnce()
    {
        // Row 0, column 0 and the diagonal each miss only cell 0.
        var board = Cells(1, 2, 3, 4, 8, 12, 5, 10, 15);
        Assert.Equal(0, StickerBoard.CompletedLines(board));
        Assert.Equal(3, StickerBoard.CompletedLines((ushort)(board | 1)));
    }

    [Fact]
    public void TwelveStickers_CanSitWithoutALine_SoAFullBoardAlwaysHasOne()
    {
        // No row, column or diagonal full: one gap per row, on neither diagonal pattern completing.
        var board = (ushort)(0xFFFF & ~Cells(0, 6, 11, 13));
        Assert.Equal(0, StickerBoard.CompletedLines(board));
        Assert.True(StickerBoard.CompletedLines(0xFFFF) > 0);
    }

    // ---------- placing stickers ----------

    [Fact]
    public void AStickerThatFinishesALine_ClearsTheBoard_AndAdds24Hours()
    {
        var state = new QuestBoardState { Stickers = Cells(0, 1, 2) };
        var award = QuestBoardEngine.PlaceStickers(state, 1, 8, InCells(state, 3));

        Assert.Equal(0, state.Stickers);
        Assert.Equal(8 + 24, award.Hours);
        Assert.False(award.FillToCap);
        Assert.Equal(1, award.LinesCleared);
        Assert.Equal(Cells(0, 1, 2, 3), award.Placements[0].LineCells);
    }

    [Fact]
    public void TwoLinesAtOnce_Add72Hours()
    {
        // Cell 0 finishes row 0 and column 0.
        var state = new QuestBoardState { Stickers = Cells(1, 2, 3, 4, 8, 12) };
        var award = QuestBoardEngine.PlaceStickers(state, 1, 8, InCells(state, 0));

        Assert.Equal(8 + 72, award.Hours);
        Assert.False(award.TripleLine);
    }

    [Fact]
    public void ThreeLinesAtOnce_FillToTheCap_AndSetTheTripleFlag()
    {
        var state = new QuestBoardState { Stickers = Cells(1, 2, 3, 4, 8, 12, 5, 10, 15) };
        var award = QuestBoardEngine.PlaceStickers(state, 1, 8, InCells(state, 0));

        Assert.True(award.FillToCap);
        Assert.True(award.TripleLine);
        Assert.Equal(0, state.Stickers);
    }

    [Fact]
    public void LinesAreCheckedAfterEachSticker_AndLeftoversGoOnTheFreshBoard()
    {
        // A Hard clear's 3 stickers: the first finishes row 0, the next two land on the cleared board.
        var state = new QuestBoardState { Stickers = Cells(0, 1, 2) };
        var award = QuestBoardEngine.PlaceStickers(state, 3, 24, InCells(state, 3, 5, 6));

        Assert.Equal(24 + 24, award.Hours);
        Assert.Equal(3, award.Placements.Count);
        Assert.Equal(Cells(5, 6), state.Stickers);
        Assert.Equal(1, award.LinesCleared);
    }

    // ---------- rewards ----------

    private static QuestSlot Slot(QuestDifficulty d, QuestSlotKind k, uint cfc = 1, bool half = false) =>
        new() { Difficulty = d, Kind = k, ContentFinderConditionId = cfc, HalfWind = half };

    [Fact]
    public void SlotRewards_FollowTheTable()
    {
        Assert.Equal(new QuestRewardRules.SlotReward(1, 8), QuestRewardRules.ForDutyClear(Slot(QuestDifficulty.Easy, QuestSlotKind.Duty), true));
        Assert.Equal(new QuestRewardRules.SlotReward(1, 4), QuestRewardRules.ForDutyClear(Slot(QuestDifficulty.Easy, QuestSlotKind.Duty, half: true), true));
        Assert.Equal(new QuestRewardRules.SlotReward(0, 0), QuestRewardRules.ForDutyClear(Slot(QuestDifficulty.Easy, QuestSlotKind.Duty), false));
        Assert.Equal(new QuestRewardRules.SlotReward(2, 16), QuestRewardRules.ForDutyClear(Slot(QuestDifficulty.Medium, QuestSlotKind.Extreme), true));
        Assert.Equal(new QuestRewardRules.SlotReward(0, 4), QuestRewardRules.ForDutyClear(Slot(QuestDifficulty.Medium, QuestSlotKind.Extreme), false));
        Assert.Equal(new QuestRewardRules.SlotReward(3, 24), QuestRewardRules.ForDutyClear(Slot(QuestDifficulty.Hard, QuestSlotKind.Savage), true));
        Assert.Equal(new QuestRewardRules.SlotReward(0, 6), QuestRewardRules.ForDutyClear(Slot(QuestDifficulty.Hard, QuestSlotKind.Savage), false));
        Assert.Equal(new QuestRewardRules.SlotReward(1, 8), QuestRewardRules.ForMsq(true));
        Assert.Equal(new QuestRewardRules.SlotReward(0, 8), QuestRewardRules.ForMsq(false));
    }

    [Fact]
    public void QuestWind_OverwindsUpToTwiceTheMax_AndNeverShortens()
    {
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        var cap = QuestRewardRules.OverwindCapHours(72);
        Assert.Equal(144, cap);

        var fromSixty = WindMath.Add(now, now.AddHours(60), TimeSpan.FromHours(24), cap);
        Assert.Equal(now.AddHours(84), fromSixty.Expiry);
        var atCap = WindMath.Add(now, now.AddHours(140), TimeSpan.FromHours(24), cap);
        Assert.Equal(now.AddHours(144), atCap.Expiry);
        var fill = WindMath.Add(now, now.AddHours(10), TimeSpan.FromHours(cap), cap);
        Assert.Equal(now.AddHours(144), fill.Expiry);
        // A timer already past the cap (the safeword is uncapped) is kept.
        var past = WindMath.Add(now, now.AddHours(200), TimeSpan.FromHours(8), cap);
        Assert.Equal(now.AddHours(200), past.Expiry);
    }

    // ---------- crediting ----------

    [Fact]
    public void AnEasyClear_NeedsToBeSynced_AndStaysOpenWhenItIsnt()
    {
        var state = new QuestBoardState { Slots = [Slot(QuestDifficulty.Easy, QuestSlotKind.Duty, 10)] };

        var unsynced = QuestBoardEngine.CreditDuty(state, new QuestDutyClear(10, false, false));
        Assert.Empty(unsynced.Credits);
        Assert.True(unsynced.UnsyncedEasyMatch);
        Assert.False(state.Slots[0].Completed);

        var synced = QuestBoardEngine.CreditDuty(state, new QuestDutyClear(10, true, false));
        Assert.Single(synced.Credits);
        Assert.True(state.Slots[0].Completed);

        Assert.Empty(QuestBoardEngine.CreditDuty(state, new QuestDutyClear(10, true, false)).Credits);
    }

    [Fact]
    public void AnUnsyncedMediumClear_UsesUpTheSlot_ForAQuarterAndNoStickers()
    {
        var state = new QuestBoardState { Slots = [Slot(QuestDifficulty.Medium, QuestSlotKind.Extreme, 20)] };
        var credit = QuestBoardEngine.CreditDuty(state, new QuestDutyClear(20, false, false));

        var only = Assert.Single(credit.Credits);
        Assert.Equal(new QuestRewardRules.SlotReward(0, 4), only.Reward);
        Assert.True(state.Slots[0].Completed);
        Assert.True(state.Slots[0].CompletedUnsynced);
        Assert.Empty(QuestBoardEngine.CreditDuty(state, new QuestDutyClear(20, false, false)).Credits);
    }

    [Fact]
    public void OneClear_CreditsEverySlotItMatches()
    {
        var state = new QuestBoardState
        {
            Slots =
            [
                Slot(QuestDifficulty.Easy, QuestSlotKind.LevelingRoulette, 0),
                Slot(QuestDifficulty.Easy, QuestSlotKind.Duty, 30),
                Slot(QuestDifficulty.Easy, QuestSlotKind.Duty, 31),
            ],
        };

        var credit = QuestBoardEngine.CreditDuty(state, new QuestDutyClear(30, true, true));
        Assert.Equal(2, credit.Credits.Count);
        Assert.False(state.Slots[2].Completed);
    }

    [Fact]
    public void TheLevelingRouletteSlot_NeedsAJobBelowTheCap()
    {
        var state = new QuestBoardState { Slots = [Slot(QuestDifficulty.Easy, QuestSlotKind.LevelingRoulette, 0)] };
        Assert.Empty(QuestBoardEngine.CreditDuty(state, new QuestDutyClear(30, true, false)).Credits);
        Assert.Single(QuestBoardEngine.CreditDuty(state, new QuestDutyClear(30, true, true)).Credits);
    }

    [Fact]
    public void TheMsq_AlwaysGivesWind_ButOnlyOneStickerADay()
    {
        var state = new QuestBoardState { Slots = [Slot(QuestDifficulty.Easy, QuestSlotKind.Msq, 0)] };

        Assert.Equal(new QuestRewardRules.SlotReward(1, 8), QuestBoardEngine.CreditMsq(state, 70001)!.Value.Reward);
        Assert.Equal(new QuestRewardRules.SlotReward(0, 8), QuestBoardEngine.CreditMsq(state, 70002)!.Value.Reward);

        // The daily roll brings a fresh MSQ slot, so tomorrow's first turn-in gives a sticker again.
        QuestRollRules.ReplaceEasy(state, [Slot(QuestDifficulty.Easy, QuestSlotKind.Msq, 0)], DateTimeOffset.UnixEpoch);
        Assert.Equal(1, QuestBoardEngine.CreditMsq(state, 70003)!.Value.Reward.Stickers);
    }

    [Fact]
    public void ADayWithoutAnMsqSlot_StillGivesMsqWind_WithoutASticker()
    {
        // E.g. the story was done at the daily roll and a patch added more.
        var credit = QuestBoardEngine.CreditMsq(new QuestBoardState(), 70001);
        Assert.Equal(new QuestRewardRules.SlotReward(0, 8), credit!.Value.Reward);
        Assert.Null(credit.Value.Slot);
    }

    [Fact]
    public void EachMsqQuest_IsCreditedOnce_SoNewGamePlusEarnsNothing()
    {
        var state = new QuestBoardState { Slots = [Slot(QuestDifficulty.Easy, QuestSlotKind.Msq, 0)] };
        Assert.NotNull(QuestBoardEngine.CreditMsq(state, 70001));
        Assert.Null(QuestBoardEngine.CreditMsq(state, 70001));
        Assert.Null(QuestBoardEngine.CreditMsq(state, 0));

        // The list survives a reset and a clone (the profile copy).
        QuestRollRules.ReplaceEasy(state, [Slot(QuestDifficulty.Easy, QuestSlotKind.Msq, 0)], DateTimeOffset.UnixEpoch);
        Assert.Null(QuestBoardEngine.CreditMsq(state.Clone(), 70001));
    }

    [Fact]
    public void TheMsqDoneBeforeTheBoard_NeverGivesWind()
    {
        // The story was finished years ago; New Game+ makes a chapter's quests incomplete again.
        var state = new QuestBoardState { CreditedMsqQuestIds = [70005] };
        Assert.True(QuestBoardEngine.SeedMsqHistory(state, [70001, 70002, 70005, 0]));
        Assert.Equal([70005u, 70001u, 70002u], state.CreditedMsqQuestIds);
        Assert.Null(QuestBoardEngine.CreditMsq(state, 70001));
        Assert.NotNull(QuestBoardEngine.CreditMsq(state, 70003));

        // Only once per character: a later seed changes nothing.
        Assert.False(QuestBoardEngine.SeedMsqHistory(state, [70009]));
        Assert.DoesNotContain(70009u, state.CreditedMsqQuestIds);
        Assert.True(state.Clone().MsqHistorySeeded);
    }

    [Fact]
    public void MsqTurnIns_CreditEveryNewQuest_NotOnlyTheTrackedOne()
    {
        // The guide showed the Twin Adder path; the doll did the Maelstrom one (70011), then one more before a poll.
        var state = new QuestBoardState { Slots = [Slot(QuestDifficulty.Easy, QuestSlotKind.Msq, 0)], CreditedMsqQuestIds = [70001] };
        var credits = QuestBoardEngine.CreditMsqTurnIns(state, [70001, 70011, 70012], tracked: 70010);

        Assert.Equal(2, credits.Count);
        Assert.Equal(1, credits.Sum(c => c.Reward.Stickers));
        Assert.Equal(16, credits.Sum(c => c.Reward.Hours));
        Assert.Empty(QuestBoardEngine.CreditMsqTurnIns(state, [70001, 70011, 70012], tracked: 70013));
    }

    [Fact]
    public void ManyMsqTurnInsAtOnce_AreRecordedWithoutWind_ExceptTheTrackedOne()
    {
        // The history was seeded before the game's quest data loaded, so the whole story looks new.
        var state = new QuestBoardState();
        var story = Enumerable.Range(70001, 50).Select(i => (uint)i).ToList();
        var credits = QuestBoardEngine.CreditMsqTurnIns(state, story, tracked: 70050);

        var only = Assert.Single(credits);
        Assert.Equal(8, only.Reward.Hours);
        Assert.Equal(50, state.CreditedMsqQuestIds.Distinct().Count());
        Assert.Empty(QuestBoardEngine.CreditMsqTurnIns(state, story, tracked: 70050));
    }

    [Fact]
    public void SpringLines_WaitInBatches_UntilTheRelayAnswersForEach()
    {
        var state = new QuestBoardState();
        var ids = 0;
        string NewId() => $"g{++ids}";

        QuestBoardEngine.AddSpringLines(state, 2, NewId);
        QuestBoardEngine.AddSpringLines(state, 1, NewId);
        var first = Assert.Single(state.PendingSpringGrants);
        Assert.Equal(("g1", 3), (first.RequestId, first.Lines));

        // Once sent, a batch takes no more lines: the relay's answer for it covers exactly what it carried.
        first.Sent = true;
        QuestBoardEngine.AddSpringLines(state, 2, NewId);
        Assert.Equal(["g1", "g2"], state.PendingSpringGrants.Select(g => g.RequestId));

        // A batch never carries more than the relay accepts.
        QuestBoardEngine.AddSpringLines(state, 12, NewId);
        Assert.All(state.PendingSpringGrants, g => Assert.InRange(g.Lines, 1, QuestBoardEngine.MaxLinesPerGrant));
        Assert.Equal(17, state.PendingSpringGrants.Sum(g => g.Lines));

        Assert.True(QuestBoardEngine.SettleSpringGrant(state, "g1"));
        Assert.False(QuestBoardEngine.SettleSpringGrant(state, "g1"));
        Assert.False(QuestBoardEngine.SettleSpringGrant(state, null));
        Assert.Equal(14, state.Clone().PendingSpringGrants.Sum(g => g.Lines));
    }

    [Fact]
    public void UnpaidSpringBatches_AreCapped_DroppingTheOldest()
    {
        // A relay that never answers (springs off): the list stops growing.
        var state = new QuestBoardState();
        var ids = 0;
        for (var i = 0; i < 30; i++)
        {
            QuestBoardEngine.AddSpringLines(state, 1, () => $"g{++ids}");
            state.PendingSpringGrants[^1].Sent = true;
        }

        Assert.Equal(QuestBoardEngine.MaxPendingGrants, state.PendingSpringGrants.Count);
        Assert.Equal("g30", state.PendingSpringGrants[^1].RequestId);
    }

    // ---------- resets ----------

    [Fact]
    public void TheDailyReset_IsAt1500Utc()
    {
        var before = new DateTimeOffset(2026, 10, 23, 14, 59, 59, TimeSpan.Zero);
        var at = new DateTimeOffset(2026, 10, 23, 15, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 15, 0, 0, TimeSpan.Zero), QuestResetClock.DailyPeriodStart(before));
        Assert.Equal(at, QuestResetClock.DailyPeriodStart(at));
        Assert.Equal(at, QuestResetClock.NextDailyReset(before));
        // Another offset is the same instant.
        Assert.Equal(at, QuestResetClock.DailyPeriodStart(new DateTimeOffset(2026, 10, 24, 0, 30, 0, TimeSpan.FromHours(9))));
    }

    [Fact]
    public void TheWeeklyReset_IsTuesdayAt0800Utc()
    {
        // 2026-10-20 is a Tuesday.
        var tuesday = new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
        Assert.Equal(DayOfWeek.Tuesday, tuesday.DayOfWeek);
        Assert.Equal(tuesday.AddDays(-7), QuestResetClock.WeeklyPeriodStart(tuesday.AddSeconds(-1)));
        Assert.Equal(tuesday, QuestResetClock.WeeklyPeriodStart(tuesday));
        Assert.Equal(tuesday, QuestResetClock.WeeklyPeriodStart(new DateTimeOffset(2026, 10, 26, 23, 0, 0, TimeSpan.Zero)));
        Assert.Equal(tuesday.AddDays(7), QuestResetClock.NextWeeklyReset(tuesday.AddDays(6)));
    }

    // ---------- pools and rolls ----------

    private static readonly QuestDutyRow[] Duties =
    [
        new(1, QuestDutyCategory.Dungeon, 15, true),
        new(2, QuestDutyCategory.Dungeon, 50, true),
        new(3, QuestDutyCategory.Trial, 50, true),
        new(4, QuestDutyCategory.NormalRaid, 60, true),
        new(5, QuestDutyCategory.AllianceRaid, 50, true),
        new(6, QuestDutyCategory.Dungeon, 90, true),
        new(7, QuestDutyCategory.Guildhest, 10, true),
        new(8, QuestDutyCategory.Dungeon, 100, false),
        // Extremes: ARR (50), ShB (80), EW (90), DT (100). Old Extremes are in the Mentor Roulette too.
        new(50, QuestDutyCategory.Extreme, 50, true),
        new(80, QuestDutyCategory.Extreme, 80, true),
        new(81, QuestDutyCategory.Extreme, 80, true),
        new(82, QuestDutyCategory.Extreme, 80, true),
        new(83, QuestDutyCategory.Extreme, 80, true),
        new(90, QuestDutyCategory.Extreme, 90, true),
        new(100, QuestDutyCategory.Extreme, 100, false),
        new(250, QuestDutyCategory.Savage, 50, false),
        new(280, QuestDutyCategory.Savage, 80, false),
        new(281, QuestDutyCategory.Savage, 80, false),
        new(282, QuestDutyCategory.Savage, 80, false),
        new(290, QuestDutyCategory.Savage, 90, false),
        new(370, QuestDutyCategory.Ultimate, 70, false),
        new(390, QuestDutyCategory.Ultimate, 90, false),
    ];

    private static QuestCharacterFacts Facts(
        int level, bool belowCap = true, bool msq = true, uint[]? unlocked = null, uint[]? unlockable = null, uint[]? ultimates = null) =>
        new(level, belowCap, msq,
            (unlocked ?? Duties.Select(d => d.ContentFinderConditionId).ToArray()).ToHashSet(),
            (unlockable ?? []).ToHashSet(),
            (ultimates ?? []).ToHashSet());

    [Theory]
    [InlineData(49, null)]
    [InlineData(50, 50)]
    [InlineData(86, 80)]
    [InlineData(99, 90)]
    [InlineData(100, 100)]
    public void TheBracket_IsTheHighestLevelAtOrBelowTheCharacter(int level, int? expected) =>
        Assert.Equal(expected, QuestRollRules.Bracket([50, 60, 70, 80, 90, 100], level));

    [Fact]
    public void TheMediumPool_IsTheCharactersExtremeBracket()
    {
        Assert.Equal([80, 81, 82, 83], QuestRollRules.MediumPool(Duties, Facts(86)).Select(d => (int)d.ContentFinderConditionId).Order());
        Assert.Empty(QuestRollRules.MediumPool(Duties, Facts(40)));
    }

    [Fact]
    public void TheHardPool_IsTheSavageBracket_PlusClearedUltimates()
    {
        var pool = QuestRollRules.HardPool(Duties, Facts(86, ultimates: [390])).Select(d => (int)d.ContentFinderConditionId).Order();
        Assert.Equal([280, 281, 282, 390], pool);
    }

    [Fact]
    public void TheEasyRoll_FillsInOrder_AndNeverOffersExtremesOrUnreachableDuties()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var slots = QuestRollRules.RollEasy(Duties, Facts(60, unlocked: [1, 2, 3, 4, 5, 7, 50], unlockable: [6]), new Random(seed));

            Assert.Equal(QuestRollRules.EasySlots, slots.Count);
            Assert.Equal(QuestSlotKind.Msq, slots[0].Kind);
            // Duty 6 (level 90) is above the character, so nothing is unlockable at 60.
            Assert.Equal(QuestSlotKind.LevelingRoulette, slots[1].Kind);
            Assert.All(slots.Skip(2), s => Assert.Equal(QuestSlotKind.Duty, s.Kind));
            Assert.DoesNotContain(slots, s => s.ContentFinderConditionId is 50 or 6 or 8);
            Assert.Equal(slots.Count, slots.Select(s => (s.Kind, s.ContentFinderConditionId)).Distinct().Count());
            Assert.All(slots.Where(s => s.ContentFinderConditionId is 3 or 4), s => Assert.True(s.HalfWind));
            Assert.All(slots.Where(s => s.ContentFinderConditionId is 1 or 2 or 5 or 7), s => Assert.False(s.HalfWind));
        }
    }

    [Fact]
    public void TheEasyRoll_OffersOneUnlockableDuty_AndSkipsWhatDoesntApply()
    {
        var slots = QuestRollRules.RollEasy(Duties, Facts(100, belowCap: false, msq: false, unlocked: [1, 2, 3], unlockable: [6]), new Random(1));

        Assert.Equal(QuestSlotKind.Unlock, slots[0].Kind);
        Assert.Equal(6u, slots[0].ContentFinderConditionId);
        Assert.DoesNotContain(slots, s => s.Kind is QuestSlotKind.Msq or QuestSlotKind.LevelingRoulette);
        // Only three unlocked duties: four slots in all.
        Assert.Equal(4, slots.Count);
    }

    [Fact]
    public void TheWeeklyRoll_IsThreeMediumAndTwoHard()
    {
        var slots = QuestRollRules.RollWeekly(Duties, Facts(86, ultimates: [390]), new Random(3));
        Assert.Equal(3, slots.Count(s => s.Difficulty == QuestDifficulty.Medium && s.Kind == QuestSlotKind.Extreme));
        Assert.Equal(2, slots.Count(s => s.Difficulty == QuestDifficulty.Hard));
        Assert.All(slots.Where(s => s.ContentFinderConditionId == 390), s => Assert.Equal(QuestSlotKind.Ultimate, s.Kind));
        Assert.Empty(QuestRollRules.RollWeekly(Duties, Facts(40), new Random(3)));
    }

    // ---------- rerolls ----------

    private static QuestBoardState WeeklyState(int seed = 7)
    {
        var state = new QuestBoardState();
        QuestRollRules.ReplaceWeekly(state, QuestRollRules.RollWeekly(Duties, Facts(86), new Random(seed)), DateTimeOffset.UnixEpoch);
        return state;
    }

    [Fact]
    public void ARerollReplacesOnlyOpenSlots_AndKeepsTheWeeklyLimit()
    {
        for (var seed = 0; seed < 30; seed++)
        {
            var state = WeeklyState(seed);
            var medium = state.SlotsOf(QuestDifficulty.Medium).ToList();
            medium[0].Completed = true;
            var kept = medium[0].ContentFinderConditionId;
            var replaced = medium.Skip(1).Select(s => s.ContentFinderConditionId).ToHashSet();

            Assert.True(QuestRollRules.Reroll(state, QuestDifficulty.Medium, Duties, Facts(86), new Random(seed)));

            var after = state.SlotsOf(QuestDifficulty.Medium).ToList();
            Assert.Equal(3, after.Count);
            Assert.Same(medium[0], after[0]);
            Assert.Equal(1, after.Count(s => s.Completed));
            // Four Extremes in the bracket: the one left over replaces a replaced duty, which may come back once.
            Assert.DoesNotContain(after.Skip(1), s => s.ContentFinderConditionId == kept);
            Assert.Contains(after.Skip(1), s => !replaced.Contains(s.ContentFinderConditionId));
            Assert.Equal(3, after.Select(s => s.ContentFinderConditionId).Distinct().Count());
            Assert.True(state.MediumRerollUsed);
            Assert.False(state.HardRerollUsed);
        }
    }

    [Fact]
    public void ARerollIsOncePerWeek_AndNeedsAnOpenSlot()
    {
        var state = WeeklyState();
        Assert.True(QuestRollRules.Reroll(state, QuestDifficulty.Hard, Duties, Facts(86), new Random(1)));
        Assert.False(QuestRollRules.CanReroll(state, QuestDifficulty.Hard));
        Assert.False(QuestRollRules.Reroll(state, QuestDifficulty.Hard, Duties, Facts(86), new Random(1)));

        foreach (var slot in state.SlotsOf(QuestDifficulty.Medium))
            slot.Completed = true;
        Assert.False(QuestRollRules.CanReroll(state, QuestDifficulty.Medium));
        Assert.False(QuestRollRules.Reroll(state, QuestDifficulty.Medium, Duties, Facts(86), new Random(1)));
        Assert.False(QuestRollRules.CanReroll(state, QuestDifficulty.Easy));

        QuestRollRules.ReplaceWeekly(state, [], DateTimeOffset.UnixEpoch);
        Assert.False(state.MediumRerollUsed || state.HardRerollUsed);
    }

    [Fact]
    public void ARerollInASmallPool_NeverBringsBackACompletedDuty()
    {
        // Hard at 86: three Savage raids, two slots. One done leaves two others for the one open slot.
        for (var seed = 0; seed < 30; seed++)
        {
            var state = WeeklyState(seed);
            var hard = state.SlotsOf(QuestDifficulty.Hard).ToList();
            hard[1].Completed = true;
            var openBefore = hard[0].ContentFinderConditionId;

            Assert.True(QuestRollRules.Reroll(state, QuestDifficulty.Hard, Duties, Facts(86), new Random(seed)));
            var after = state.SlotsOf(QuestDifficulty.Hard).ToList();
            Assert.Equal(2, after.Count);
            Assert.NotEqual(hard[1].ContentFinderConditionId, after.Single(s => !s.Completed).ContentFinderConditionId);
            Assert.NotEqual(openBefore, after.Single(s => !s.Completed).ContentFinderConditionId);
        }
    }

    // ---------- the saved state ----------

    [Fact]
    public void Normalize_DropsInvalidSlots_AndCloneIsDeep()
    {
        var state = new QuestBoardState
        {
            Slots =
            [
                Slot(QuestDifficulty.Easy, QuestSlotKind.Duty, 1),
                Slot(QuestDifficulty.Medium, QuestSlotKind.Duty, 1),
                Slot(QuestDifficulty.Hard, QuestSlotKind.Savage, 0),
                null!,
            ],
        };

        Assert.True(state.Normalize());
        Assert.Single(state.Slots);
        var clone = state.Clone();
        clone.Slots[0].Completed = true;
        Assert.False(state.Slots[0].Completed);
    }
}
