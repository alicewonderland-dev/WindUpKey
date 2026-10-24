using WindUpKey.Protocol;
using WindUpKey.Quest;

namespace WindUpKey.Tests;

/// <summary>0.2.10.0: the Daily roulettes, a weekly Easy set with a daily reroll, completion springs, turns, hidden rules.</summary>
public sealed class QuestReworkTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 16, 0, 0, TimeSpan.Zero);

    // ---------- turns ----------

    [Theory]
    [InlineData(4, "½ turn")]
    [InlineData(8, "1 turn")]
    [InlineData(12, "1½ turns")]
    [InlineData(16, "2 turns")]
    [InlineData(24, "3 turns")]
    [InlineData(0, "no turns")]
    public void Wind_IsShownInTurns(double hours, string expected) => Assert.Equal(expected, QuestRewardRules.Turns(hours));

    [Fact]
    public void EachTier_ShowsItsStickersAndTurns()
    {
        Assert.Equal((1, "1½ turns"), (QuestRewardRules.StickersFor(QuestDifficulty.Daily), QuestRewardRules.Turns(QuestRewardRules.BaseHoursFor(QuestDifficulty.Daily))));
        Assert.Equal((1, "1½ turns"), (QuestRewardRules.StickersFor(QuestDifficulty.Easy), QuestRewardRules.Turns(QuestRewardRules.BaseHoursFor(QuestDifficulty.Easy))));
        Assert.Equal((2, "2 turns"), (QuestRewardRules.StickersFor(QuestDifficulty.Medium), QuestRewardRules.Turns(QuestRewardRules.BaseHoursFor(QuestDifficulty.Medium))));
        Assert.Equal((3, "3 turns"), (QuestRewardRules.StickersFor(QuestDifficulty.Hard), QuestRewardRules.Turns(QuestRewardRules.BaseHoursFor(QuestDifficulty.Hard))));
    }

    // ---------- Daily ----------

    [Theory]
    [InlineData("Duty Roulette: Leveling", false, true)]
    [InlineData("Duty Roulette: Expert", false, true)]
    [InlineData("Duty Roulette: Alliance Raids", false, true)]
    [InlineData("Duty Roulette: Mentor", false, true)]
    [InlineData("Duty Roulette: Trials", false, false)]
    [InlineData("Duty Roulette: Normal Raids", false, false)]
    [InlineData("Chocobo Race: Random", false, false)]
    [InlineData("Daily Challenge: Frontline", true, true)]
    [InlineData("Crystalline Conflict (Casual Match)", true, true)]
    public void TheDaily_CountsDutyRoulettesAndPvp_ButNotTrialsOrNormalRaids(string name, bool pvp, bool counts) =>
        Assert.Equal(counts, QuestRollRules.CountsForDaily(name, pvp));

    [Fact]
    public void TheDaily_FinishesOnItsSecondRoulette_TheSameOneTwiceIsFine()
    {
        var state = new QuestBoardState();
        QuestRollRules.ReplaceDaily(state, QuestRollRules.RollDaily(), QuestResetClock.DailyPeriodStart(Now));
        var daily = Assert.Single(state.SlotsOf(QuestDifficulty.Daily));
        Assert.Equal((0, 2), (daily.Progress, daily.Target));

        Assert.Null(QuestBoardEngine.CreditRoulette(state));
        Assert.Equal(1, daily.Progress);
        Assert.False(daily.Completed);

        var done = QuestBoardEngine.CreditRoulette(state);
        Assert.Equal(new QuestRewardRules.SlotReward(1, 12), done!.Value.Reward);
        Assert.True(daily.Completed);
        Assert.Equal(1, QuestBoardEngine.CompletionSprings([done.Value]));

        // A third roulette the same day gives nothing more.
        Assert.Null(QuestBoardEngine.CreditRoulette(state));
        Assert.Equal(2, daily.Progress);
    }

    [Fact]
    public void TheDaily_StaysFirst_AndIsReplacedEachDay()
    {
        var state = new QuestBoardState();
        QuestRollRules.ReplaceEasy(state, [new QuestSlot { Difficulty = QuestDifficulty.Easy, Kind = QuestSlotKind.Duty, ContentFinderConditionId = 1 }]);
        QuestRollRules.ReplaceDaily(state, QuestRollRules.RollDaily(), Now);
        state.Slots[0].Progress = 2;
        state.Slots[0].Completed = true;
        QuestRollRules.ReplaceDaily(state, QuestRollRules.RollDaily(), Now.AddDays(1));

        Assert.Equal([QuestDifficulty.Daily, QuestDifficulty.Easy], state.Slots.Select(s => s.Difficulty));
        Assert.False(state.Slots[0].Completed);
        Assert.Equal(Now.AddDays(1), state.DailyPeriodStartUtc);
    }

    // ---------- Easy ----------

    private static readonly QuestDutyRow[] Duties =
    [
        new(1, QuestDutyCategory.Dungeon, 15, true),
        new(2, QuestDutyCategory.Dungeon, 50, true),
        new(3, QuestDutyCategory.Trial, 50, true),
        new(4, QuestDutyCategory.NormalRaid, 60, true),
        new(5, QuestDutyCategory.AllianceRaid, 50, true),
        new(7, QuestDutyCategory.Guildhest, 10, true),
        new(9, QuestDutyCategory.Dungeon, 30, true),
        new(10, QuestDutyCategory.Dungeon, 40, true),
        new(11, QuestDutyCategory.Dungeon, 45, true),
        new(12, QuestDutyCategory.Dungeon, 55, true),
        new(13, QuestDutyCategory.Dungeon, 58, true),
    ];

    private static QuestCharacterFacts Facts(bool belowCap = true) =>
        new(60, belowCap, false, Duties.Select(d => d.ContentFinderConditionId).ToHashSet(), new HashSet<uint>(), new HashSet<uint>());

    private static QuestBoardState EasyState(int seed = 1, bool belowCap = true)
    {
        var state = new QuestBoardState();
        QuestRollRules.ReplaceEasy(state, QuestRollRules.RollEasy(Duties, Facts(belowCap), new Random(seed)));
        return state;
    }

    [Fact]
    public void Easy_IsDungeonsAndGuildhestsOnly()
    {
        for (var seed = 0; seed < 50; seed++)
            Assert.DoesNotContain(EasyState(seed).Slots, s => s.ContentFinderConditionId is 3 or 4 or 5);
    }

    [Fact]
    public void Easy_RerollsOnceADay_KeepingCompletedSlots()
    {
        var state = EasyState();
        var easy = state.SlotsOf(QuestDifficulty.Easy).ToList();
        Assert.Equal(QuestRollRules.EasySlots, easy.Count);
        easy[1].Completed = true;
        var kept = easy[1];

        Assert.True(QuestRollRules.CanReroll(state, QuestDifficulty.Easy, Now));
        Assert.True(QuestRollRules.Reroll(state, QuestDifficulty.Easy, Duties, Facts(), new Random(2), Now));
        var after = state.SlotsOf(QuestDifficulty.Easy).ToList();
        Assert.Contains(kept, after);
        Assert.Equal(QuestRollRules.EasySlots, after.Count);
        Assert.Equal(after.Count, after.Select(s => (s.Kind, s.ContentFinderConditionId)).Distinct().Count());

        // Once a day: not again today, again after the daily reset.
        Assert.False(QuestRollRules.CanReroll(state, QuestDifficulty.Easy, Now.AddHours(3)));
        Assert.False(QuestRollRules.Reroll(state, QuestDifficulty.Easy, Duties, Facts(), new Random(2), Now.AddHours(3)));
        Assert.True(QuestRollRules.CanReroll(state, QuestDifficulty.Easy, Now.AddDays(1)));
    }

    [Fact]
    public void AnEasyReroll_NeverBringsBackACompletedDutyOrLevelingRoulette()
    {
        for (var seed = 0; seed < 30; seed++)
        {
            var state = EasyState(seed);
            var easy = state.SlotsOf(QuestDifficulty.Easy).ToList();
            foreach (var slot in easy.Where(s => s.Kind == QuestSlotKind.LevelingRoulette))
                slot.Completed = true;
            var done = easy.First(s => s.Kind == QuestSlotKind.Duty);
            done.Completed = true;

            Assert.True(QuestRollRules.Reroll(state, QuestDifficulty.Easy, Duties, Facts(), new Random(seed), Now));
            var fresh = state.SlotsOf(QuestDifficulty.Easy).Where(s => !s.Completed).ToList();
            Assert.DoesNotContain(fresh, s => s.Kind == QuestSlotKind.LevelingRoulette);
            Assert.DoesNotContain(fresh, s => s.ContentFinderConditionId == done.ContentFinderConditionId);
        }
    }

    [Fact]
    public void ReplacingEasy_MarksTheBoardWithTheCurrentRules_AndKeepsTodaysReroll()
    {
        // A weekly reset inside the daily period doesn't give the day's Easy reroll back: still once a day.
        var state = new QuestBoardState { RulesVersion = 1, EasyRerollDailyPeriodUtc = QuestResetClock.DailyPeriodStart(Now) };
        QuestRollRules.ReplaceEasy(state, [new QuestSlot { Difficulty = QuestDifficulty.Easy, Kind = QuestSlotKind.Duty, ContentFinderConditionId = 1 }]);
        Assert.Equal(QuestBoardState.CurrentRules, state.RulesVersion);
        Assert.False(QuestRollRules.CanReroll(state, QuestDifficulty.Easy, Now));
        Assert.Equal((QuestBoardState.CurrentRules, state.EasyRerollDailyPeriodUtc), (state.Clone().RulesVersion, state.Clone().EasyRerollDailyPeriodUtc));
    }

    [Fact]
    public void AnUnsyncedCompletion_PaysNoSpring()
    {
        var slot = new QuestSlot { Difficulty = QuestDifficulty.Medium, Kind = QuestSlotKind.Extreme, ContentFinderConditionId = 5, Completed = true, CompletedUnsynced = true };
        Assert.Equal(0, QuestBoardEngine.CompletionSprings([new QuestSlotCredit(slot, new QuestRewardRules.SlotReward(0, 4))]));
    }

    [Fact]
    public void ARouletteCounts_ForTheDailyOfTheDayItEnds()
    {
        var service = ProfileSettingsTests.PluginSource(Path.Combine("Services", "QuestBoardService.cs"));
        var credit = ProfileSettingsTests.MethodBody(service, "    private void CreditClear(");
        Assert.True(
            credit.IndexOf("QuestRollRules.ReplaceDaily(board, QuestRollRules.RollDaily(), today);", StringComparison.Ordinal)
            < credit.IndexOf("QuestBoardEngine.CreditRoulette(board)", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOldBoardsMsqSlot_IsDropped()
    {
        var state = new QuestBoardState
        {
            Slots =
            [
                new QuestSlot { Difficulty = QuestDifficulty.Easy, Kind = QuestSlotKind.Msq },
                new QuestSlot { Difficulty = QuestDifficulty.Daily, Kind = QuestSlotKind.Roulettes, Target = 2 },
                new QuestSlot { Difficulty = QuestDifficulty.Daily, Kind = QuestSlotKind.Roulettes, Target = 0 },
            ],
        };
        Assert.True(state.Normalize());
        Assert.Single(state.Slots);
        Assert.Equal(QuestSlotKind.Roulettes, state.Slots[0].Kind);
    }

    // ---------- springs ----------

    [Fact]
    public void EveryCompletedSlot_PaysASpring_TwentyUnitsADay()
    {
        var slot = new QuestSlot { Difficulty = QuestDifficulty.Hard, Kind = QuestSlotKind.Savage, ContentFinderConditionId = 5 };
        Assert.Equal(2, QuestBoardEngine.CompletionSprings(
            [new QuestSlotCredit(slot, new QuestRewardRules.SlotReward(3, 24)), new QuestSlotCredit(slot, new QuestRewardRules.SlotReward(1, 12))]));
        Assert.Equal(20, SpringRules.MaxQuestLinesPerDay);
        Assert.Equal(20, SpringRules.MaxLinesPerGrant);
        Assert.Equal(SpringRules.MaxLinesPerGrant, QuestBoardEngine.MaxLinesPerGrant);
    }

    [Fact]
    public void TheService_PaysLinesAndCompletions_AndCountsRoulettes()
    {
        var service = ProfileSettingsTests.PluginSource(Path.Combine("Services", "QuestBoardService.cs"));
        var award = ProfileSettingsTests.MethodBody(service, "    private void Award(");
        Assert.Contains("var springUnits = award.LinesCleared + QuestBoardEngine.CompletionSprings(credits);", award);
        Assert.Contains("QuestBoardEngine.AddSpringLines(board, springUnits,", award);
        Assert.DoesNotContain("RerollEasyIfAllDone", service);

        Assert.Contains("QuestBoardEngine.CreditRoulette(board)", ProfileSettingsTests.MethodBody(service, "    private void CreditClear("));
        // A roulette (PvP too) counts only when the game reports the duty completed: leaving never counts one.
        Assert.Contains("private void OnLeftDuty() => _rouletteRun = null;", service);
        // A board from before 0.2.10.0 rolls its Easy set again.
        Assert.Contains("board.RulesVersion < QuestBoardState.CurrentRules", ProfileSettingsTests.MethodBody(service, "    private void EnsureRolled("));
    }

    // ---------- what players see ----------

    [Fact]
    public void TheBoard_NamesNoHours_NoHiddenRules_AndHasNoHoverText()
    {
        var tab = ProfileSettingsTests.PluginSource(Path.Combine("Ui", "QuestBoardTab.cs"));
        Assert.DoesNotContain(":0}h", tab);
        Assert.DoesNotContain("SetTooltip", tab);
        Assert.Contains("public void DrawInfoWindow()", tab);
        var info = ProfileSettingsTests.MethodBody(tab, "    public void DrawInfoWindow(");
        foreach (var hidden in new[] { "first clear", "triple", "Ultimate", "party", "owner", "synced", "quarter", "newest" })
            Assert.DoesNotContain(hidden, info, StringComparison.OrdinalIgnoreCase);

        var service = ProfileSettingsTests.PluginSource(Path.Combine("Services", "QuestBoardService.cs"));
        Assert.Contains("Quest board: the key turns more than usual.", service);
        Assert.Contains("Quest board: that clear didn't count this time.", service);
        Assert.DoesNotContain("extra wind for", service);
        Assert.DoesNotContain("wasn't synced", service);
    }

    [Fact]
    public void TheInGameChangelog_KeepsTheHiddenRulesHidden()
    {
        var changelog = ProfileSettingsTests.PluginSource(Path.Combine("Ui", "ChangelogWindow.cs"));
        foreach (var hidden in new[] { "first clear", "triple wind", "owner doubles", "unsynced", "quarter of the wind", "you've cleared" })
            Assert.DoesNotContain(hidden, changelog, StringComparison.OrdinalIgnoreCase);
    }
}
