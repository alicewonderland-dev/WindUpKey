using WindUpKey.Protocol;
using WindUpKey.Quest;
using WindUpKey.Services;

namespace WindUpKey.Tests;

/// <summary>0.2.9.0: the newest-tier Hard picks, cleared guarantees, first-clear and party bonuses, line wipes, Easy rerolls, vacation.</summary>
public sealed class QuestTuningTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    // Level 100: three Savage tiers, the newest marked current (as the game's HighEndDuty does), out of id order on purpose.
    private static readonly QuestDutyRow[] Duties =
    [
        new(986, QuestDutyCategory.Savage, 100, false, 190),
        new(988, QuestDutyCategory.Savage, 100, false, 191),
        new(990, QuestDutyCategory.Savage, 100, false, 192),
        new(992, QuestDutyCategory.Savage, 100, false, 193),
        new(1020, QuestDutyCategory.Savage, 100, false, 194),
        new(1022, QuestDutyCategory.Savage, 100, false, 195),
        new(1024, QuestDutyCategory.Savage, 100, false, 196),
        new(1026, QuestDutyCategory.Savage, 100, false, 197),
        new(1075, QuestDutyCategory.Savage, 100, false, 201, HighEnd: true),
        new(1069, QuestDutyCategory.Savage, 100, false, 198, HighEnd: true),
        new(1073, QuestDutyCategory.Savage, 100, false, 200, HighEnd: true),
        new(1071, QuestDutyCategory.Savage, 100, false, 199, HighEnd: true),
        // Level 90 (no current flag): Asphodelos then Abyssos, by sort order.
        new(809, QuestDutyCategory.Savage, 90, false, 146),
        new(811, QuestDutyCategory.Savage, 90, false, 147),
        new(807, QuestDutyCategory.Savage, 90, false, 148),
        new(801, QuestDutyCategory.Savage, 90, false, 149),
        new(873, QuestDutyCategory.Savage, 90, false, 150),
        new(881, QuestDutyCategory.Savage, 90, false, 151),
        new(877, QuestDutyCategory.Savage, 90, false, 152),
        new(884, QuestDutyCategory.Savage, 90, false, 153),
        new(1006, QuestDutyCategory.Ultimate, 100, false, 1006, HighEnd: true),
        new(1094, QuestDutyCategory.Ultimate, 100, false, 1007, HighEnd: true),
        new(996, QuestDutyCategory.Extreme, 100, false, 241),
        new(1031, QuestDutyCategory.Extreme, 100, false, 243),
        new(1044, QuestDutyCategory.Extreme, 100, false, 245),
        new(1077, QuestDutyCategory.Extreme, 100, false, 246),
        new(1116, QuestDutyCategory.Extreme, 100, false, 247, HighEnd: true),
    ];

    private static QuestCharacterFacts Facts(int level = 100, uint[]? cleared = null, uint[]? ultimates = null) =>
        new(level, false, false, new HashSet<uint>(), new HashSet<uint>(), (ultimates ?? []).ToHashSet(), (cleared ?? []).ToHashSet());

    private static readonly IReadOnlySet<uint> None = new HashSet<uint>();

    private static uint[] Ids(IEnumerable<QuestDutyRow> rows) => rows.Select(d => d.ContentFinderConditionId).ToArray();

    private static uint[] HardIds(QuestCharacterFacts facts, int seed = 1) =>
        Ids(QuestRollRules.PickHard(Duties, facts, QuestRollRules.HardSlots, None, None, needCleared: true, new Random(seed)));

    // ---------- the Hard pool ----------

    [Fact]
    public void TheNewestTier_IsTheCurrentFlaggedOne_FirstFightToLast()
    {
        Assert.Equal([1069u, 1071u, 1073u, 1075u], Ids(QuestRollRules.NewestSavageTier(Duties, Facts())));
    }

    [Fact]
    public void BelowTheCap_TheNewestTier_IsTheLastFourOfTheBracket()
    {
        Assert.Equal([873u, 881u, 877u, 884u], Ids(QuestRollRules.NewestSavageTier(Duties, Facts(level: 95))));
    }

    [Fact]
    public void NothingCleared_TheEarliestFightsComeFirst()
    {
        for (var seed = 0; seed < 20; seed++)
            Assert.Equal([1069u, 1071u], HardIds(Facts(), seed));
    }

    [Fact]
    public void OneSlot_IsAlwaysAClearedFight_AndTheOtherTheEarliestUncleared()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            Assert.Equal([1069u, 1071u], HardIds(Facts(cleared: [1069]), seed));
            Assert.Equal([1073u, 1069u], HardIds(Facts(cleared: [1073]), seed));
        }
    }

    [Fact]
    public void WithTwoCleared_OneClearedThenTheEarliestUncleared()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var ids = HardIds(Facts(cleared: [1069, 1071]), seed);
            Assert.Contains(ids[0], new uint[] { 1069, 1071 });
            Assert.Equal(1073u, ids[1]);
        }
    }

    [Fact]
    public void AllCleared_BothAreRandomClearedFights()
    {
        var all = new uint[] { 1069, 1071, 1073, 1075 };
        var seen = new HashSet<uint>();
        for (var seed = 0; seed < 60; seed++)
        {
            var ids = HardIds(Facts(cleared: all), seed);
            Assert.Equal(2, ids.Distinct().Count());
            Assert.All(ids, id => Assert.Contains(id, all));
            seen.UnionWith(ids);
        }

        Assert.Equal(all.Length, seen.Count);
    }

    [Fact]
    public void AClearedUltimate_ComesUpAtAQuarterOfASavagesWeight()
    {
        // One cleared Ultimate against three free Savage fights (one slot is the cleared fight): 0.25 / 3.25 ≈ 7.7%.
        var hits = 0;
        const int runs = 4000;
        for (var seed = 0; seed < runs; seed++)
        {
            var ids = HardIds(Facts(cleared: [1069], ultimates: [1006]), seed);
            Assert.Equal(1069u, ids[0]);
            if (ids[1] == 1006)
                hits++;
        }

        var share = hits / (double)runs;
        Assert.InRange(share, 0.05, 0.105);
    }

    [Fact]
    public void AnUncleareedUltimate_NeverComesUp()
    {
        for (var seed = 0; seed < 200; seed++)
            Assert.DoesNotContain(1094u, HardIds(Facts(ultimates: [1006]), seed));
    }

    [Fact]
    public void TheWeeklyRoll_MarksUltimatesAsUltimateSlots()
    {
        for (var seed = 0; seed < 400; seed++)
        {
            var hard = QuestRollRules.RollWeekly(Duties, Facts(ultimates: [1006]), new Random(seed))
                .Where(s => s.Difficulty == QuestDifficulty.Hard)
                .ToList();
            Assert.Equal(2, hard.Count);
            Assert.All(hard, s => Assert.Equal(s.ContentFinderConditionId == 1006 ? QuestSlotKind.Ultimate : QuestSlotKind.Savage, s.Kind));
        }
    }

    [Fact]
    public void AHardReroll_MovesOnToTheNextFights()
    {
        var state = new QuestBoardState();
        QuestRollRules.ReplaceWeekly(state, QuestRollRules.RollWeekly(Duties, Facts(), new Random(1)), Now);
        Assert.Equal([1069u, 1071u], state.SlotsOf(QuestDifficulty.Hard).Select(s => s.ContentFinderConditionId).ToArray());

        Assert.True(QuestRollRules.Reroll(state, QuestDifficulty.Hard, Duties, Facts(), new Random(1)));
        Assert.Equal([1073u, 1075u], state.SlotsOf(QuestDifficulty.Hard).Select(s => s.ContentFinderConditionId).ToArray());
    }

    [Fact]
    public void AHardReroll_KeepsAClearedFight_WhenNoCompletedSlotIsOne()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var state = new QuestBoardState();
            QuestRollRules.ReplaceWeekly(state, QuestRollRules.RollWeekly(Duties, Facts(cleared: [1069]), new Random(seed)), Now);
            Assert.True(QuestRollRules.Reroll(state, QuestDifficulty.Hard, Duties, Facts(cleared: [1069]), new Random(seed)));

            // 1069 is the only cleared fight, so it comes back; the other moves past 1071 to the next uncleared.
            Assert.Equal([1069u, 1073u], state.SlotsOf(QuestDifficulty.Hard).Select(s => s.ContentFinderConditionId).ToArray());
        }
    }

    // ---------- Medium ----------

    [Fact]
    public void OneMediumExtreme_IsAlwaysOneTheCharacterCleared()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var medium = QuestRollRules.RollWeekly(Duties, Facts(cleared: [1044]), new Random(seed))
                .Where(s => s.Difficulty == QuestDifficulty.Medium)
                .Select(s => s.ContentFinderConditionId)
                .ToList();
            Assert.Equal(3, medium.Count);
            Assert.Contains(1044u, medium);
            Assert.Equal(3, medium.Distinct().Count());
        }
    }

    [Fact]
    public void WithNothingCleared_MediumIsRandomFromThePool()
    {
        var seen = new HashSet<uint>();
        for (var seed = 0; seed < 60; seed++)
        {
            seen.UnionWith(QuestRollRules.RollWeekly(Duties, Facts(), new Random(seed))
                .Where(s => s.Difficulty == QuestDifficulty.Medium)
                .Select(s => s.ContentFinderConditionId));
        }

        Assert.Equal(5, seen.Count);
    }

    // ---------- rewards ----------

    private static QuestSlot Slot(QuestDifficulty d, QuestSlotKind k) => new() { Difficulty = d, Kind = k, ContentFinderConditionId = 5 };

    [Fact]
    public void AFirstClear_TriplesTheWind_ButNotTheStickers()
    {
        Assert.Equal(new QuestRewardRules.SlotReward(1, 24), QuestRewardRules.ForDutyClear(Slot(QuestDifficulty.Easy, QuestSlotKind.Unlock), true, firstClear: true));
        Assert.Equal(new QuestRewardRules.SlotReward(2, 48), QuestRewardRules.ForDutyClear(Slot(QuestDifficulty.Medium, QuestSlotKind.Extreme), true, firstClear: true));
        Assert.Equal(new QuestRewardRules.SlotReward(3, 72), QuestRewardRules.ForDutyClear(Slot(QuestDifficulty.Hard, QuestSlotKind.Savage), true, firstClear: true));
        // Unsynced: a quarter of the tripled wind, and no stickers.
        Assert.Equal(new QuestRewardRules.SlotReward(0, 18), QuestRewardRules.ForDutyClear(Slot(QuestDifficulty.Hard, QuestSlotKind.Savage), false, firstClear: true));
        // An unsynced Easy clear still gives nothing.
        Assert.Equal(new QuestRewardRules.SlotReward(0, 0), QuestRewardRules.ForDutyClear(Slot(QuestDifficulty.Easy, QuestSlotKind.Duty), false, firstClear: true));
    }

    [Fact]
    public void TheLevelingRoulette_NeverGetsTheFirstClearBonus()
    {
        Assert.Equal(new QuestRewardRules.SlotReward(1, 8), QuestRewardRules.ForDutyClear(Slot(QuestDifficulty.Easy, QuestSlotKind.LevelingRoulette), true, firstClear: true));
        Assert.False(QuestRewardRules.CanBeFirstClear(QuestSlotKind.Msq));
        Assert.False(QuestRewardRules.CanBeFirstClear(QuestSlotKind.LevelingRoulette));
    }

    [Fact]
    public void AnUltimate_TriplesTheWind_AndKeepsThreeStickers()
    {
        Assert.Equal(new QuestRewardRules.SlotReward(3, 72), QuestRewardRules.ForDutyClear(Slot(QuestDifficulty.Hard, QuestSlotKind.Ultimate), true));
        Assert.Equal(new QuestRewardRules.SlotReward(3, 216), QuestRewardRules.ForDutyClear(Slot(QuestDifficulty.Hard, QuestSlotKind.Ultimate), true, firstClear: true));
    }

    [Theory]
    [InlineData(QuestPartyBonus.None, 1)]
    [InlineData(QuestPartyBonus.Partner, 1.5)]
    [InlineData(QuestPartyBonus.Owner, 2)]
    public void ThePartyBonus_MultipliesTheWind(QuestPartyBonus bonus, double multiplier) =>
        Assert.Equal(multiplier, QuestRewardRules.PartyMultiplier(bonus));

    [Fact]
    public void AFirstClearFlaggedDuty_PaysTripleThroughTheEngine()
    {
        var state = new QuestBoardState { Slots = [Slot(QuestDifficulty.Hard, QuestSlotKind.Savage)] };
        var credit = QuestBoardEngine.CreditDuty(state, new QuestDutyClear(5, true, false, FirstClear: true));
        Assert.Equal(72, credit.Credits.Single().Reward.Hours);
    }

    [Fact]
    public void TheService_MultipliesOnlyTheSlotsWindByTheParty()
    {
        var service = ProfileSettingsTests.PluginSource(Path.Combine("Services", "QuestBoardService.cs"));
        var award = ProfileSettingsTests.MethodBody(service, "    private void Award(");
        Assert.Contains("var hours = credits.Sum(c => c.Reward.Hours) * QuestRewardRules.PartyMultiplier(party);", award);
        Assert.Contains("QuestBoardEngine.PlaceStickers(board, stickers, hours,", award);
        // The first clear is read when the duty starts, before the game marks it completed.
        var begin = ProfileSettingsTests.MethodBody(service, "    private void BeginObservedDuty(");
        Assert.Contains("firstClear = _facts.IsUncleared(cfcId);", begin);
    }

    // ---------- the board ----------

    private static ushort Cells(params int[] cells) => (ushort)cells.Aggregate(0, (mask, c) => mask | (1 << c));

    private static Func<int, int> InCells(QuestBoardState state, params int[] cells)
    {
        var queue = new Queue<int>(cells);
        return _ =>
        {
            var cell = queue.Dequeue();
            return Enumerable.Range(0, cell).Count(c => !StickerBoard.HasSticker(state.Stickers, c));
        };
    }

    [Fact]
    public void ALine_WipesOnlyItsOwnStickers()
    {
        // Row 0 (cells 0-3) less one, plus two stickers elsewhere.
        var state = new QuestBoardState { Stickers = Cells(0, 1, 2, 5, 10) };
        var award = QuestBoardEngine.PlaceStickers(state, 1, 0, InCells(state, 3));

        Assert.Equal(1, award.LinesCleared);
        Assert.Equal(Cells(5, 10), state.Stickers);
        Assert.Equal(Cells(0, 1, 2, 3), award.Placements[0].LineCells);
    }

    [Fact]
    public void OneSticker_CanFinishARowAColumnAndADiagonal()
    {
        // Cell 0 finishes row 0, column 0 and the main diagonal; only their cells go.
        var state = new QuestBoardState { Stickers = Cells(1, 2, 3, 4, 8, 12, 5, 10, 15, 6) };
        var award = QuestBoardEngine.PlaceStickers(state, 1, 0, InCells(state, 0));

        Assert.Equal(3, award.LinesCleared);
        Assert.True(award.FillToCap);
        Assert.Equal(Cells(6), state.Stickers);
    }

    [Fact]
    public void TheBoard_NeverFills()
    {
        var state = new QuestBoardState();
        var rng = new Random(3);
        for (var i = 0; i < 500; i++)
        {
            QuestBoardEngine.PlaceStickers(state, 1, 0, n => rng.Next(n));
            Assert.True(StickerBoard.EmptyCount(state.Stickers) > 0);
            Assert.Equal(0, StickerBoard.CompletedLines(state.Stickers));
        }
    }

    // ---------- Easy ----------

    [Fact]
    public void AllEasyDone_NeedsEasySlots_AndEveryOneDone()
    {
        Assert.False(QuestRollRules.AllEasyDone(new QuestBoardState()));
        var state = new QuestBoardState
        {
            Slots =
            [
                new QuestSlot { Difficulty = QuestDifficulty.Easy, Kind = QuestSlotKind.Duty, ContentFinderConditionId = 1, Completed = true },
                new QuestSlot { Difficulty = QuestDifficulty.Easy, Kind = QuestSlotKind.Duty, ContentFinderConditionId = 2 },
                new QuestSlot { Difficulty = QuestDifficulty.Medium, Kind = QuestSlotKind.Extreme, ContentFinderConditionId = 3 },
            ],
        };
        Assert.False(QuestRollRules.AllEasyDone(state));
        state.Slots[1].Completed = true;
        Assert.True(QuestRollRules.AllEasyDone(state));
    }

    [Fact]
    public void TheService_RerollsEasyAfterAnAward_KeepingTheDaysMsqSticker()
    {
        var service = ProfileSettingsTests.PluginSource(Path.Combine("Services", "QuestBoardService.cs"));
        Assert.Contains("RerollEasyIfAllDone();", ProfileSettingsTests.MethodBody(service, "    private void Award("));
        var reroll = ProfileSettingsTests.MethodBody(service, "    private void RerollEasyIfAllDone(");
        Assert.Contains("QuestRollRules.ReplaceEasy(board, easy, daily);", reroll);
        Assert.Contains("slot.Completed = true;", reroll);
    }

    // ---------- springs ----------

    [Fact]
    public void TenLinesADay_AreTheSpringLimit()
    {
        Assert.Equal(10, SpringRules.MaxQuestLinesPerDay);
        Assert.Equal(10, SpringRules.MaxLinesPerGrant);
        Assert.Equal(SpringRules.MaxLinesPerGrant, QuestBoardEngine.MaxLinesPerGrant);
    }

    // ---------- vacation ----------

    [Fact]
    public void Frozen_KeepsEveryLayer_AndABreaksTimeLeft()
    {
        var layers = new WindLayers(30, 10, 5, Now, Now.AddMinutes(20));
        var later = layers.Frozen(Now.AddDays(30));

        Assert.Equal((30.0, 10.0, 5.0), (later.Normal, later.Quest, later.Player));
        Assert.Equal(Now.AddDays(30), later.AsOf);
        Assert.Equal(Now.AddDays(30).AddMinutes(20), later.BrokenUntil);
        // Back from vacation, the break runs out its 20 minutes, then the drain carries on (5 minutes off player overwind).
        Assert.Equal(5, later.At(Now.AddDays(30).AddMinutes(20)).Player, 6);
        Assert.Equal(5 - 5 / 60.0, later.At(Now.AddDays(30).AddMinutes(25)).Player, 6);
    }

    [Fact]
    public void Vacation_LetsTheDollMove_AndRefusesEveryWindChange()
    {
        var timer = ProfileSettingsTests.PluginSource(Path.Combine("Services", "WindTimerService.cs"));
        Assert.Contains("public bool IsLocked => _config.IsDoll && !_config.IsOnVacation && IsTimerEmpty", timer);
        foreach (var method in new[] { "    public void AddHours(", "    public PlayerWindResult AddPlayerWind(", "    public TimeSpan AddCommendationWind(", "    public void AddQuestWind(", "    public void ClearWind(" })
            Assert.Contains("_config.IsOnVacation", ProfileSettingsTests.MethodBody(timer, method));
        Assert.Contains("var at = _config.IsOnVacation ? layers.Frozen(now) : layers.At(now);", ProfileSettingsTests.MethodBody(timer, "    private void WriteLayers("));
    }

    [Fact]
    public void Vacation_RefusesInboundPaidMessages_SoTheRelayRefunds()
    {
        var relay = ProfileSettingsTests.PluginSource(Path.Combine("Services", "RelayClient.cs"));
        foreach (var handler in new[] { "    private async Task HandleInboundWindAsync(", "    private async Task HandleInboundUnwindAsync(", "    private async Task HandleInboundRepairAsync(", "    private async Task HandleInboundCallAsync(" })
            Assert.Contains("RefuseIfOnVacationAsync(payload.RequestId, fromKey, ct)", ProfileSettingsTests.MethodBody(relay, handler));
        foreach (var send in new[] { "    public async Task SendWindByKeyAsync(", "    public async Task SendUnwindByKeyAsync(", "    public async Task SendPaidUnwindAsync(", "    public async Task SendRepairAsync(", "    public async Task SendSpringGiftAsync(" })
            Assert.Contains("RefuseOwnVacation()", ProfileSettingsTests.MethodBody(relay, send));
    }

    [Fact]
    public void ASyncedVacationChange_NeverStampsThisPcsWind()
    {
        var timer = ProfileSettingsTests.PluginSource(Path.Combine("Services", "WindTimerService.cs"));
        var set = ProfileSettingsTests.MethodBody(timer, "    public bool SetVacation(");
        var synced = set[set.IndexOf("if (updatedUtc is { } synced)", StringComparison.Ordinal)..set.IndexOf("        else\n", StringComparison.Ordinal)];
        Assert.DoesNotContain("Save(", synced.Replace("_config.Save();", string.Empty));
        Assert.DoesNotContain("SyncLockState();", synced);
        // Ending through a sync first moves the frozen row on undrained (the relay may send no newer wind).
        Assert.True(
            synced.IndexOf("WriteLayers(new WindLayers(", StringComparison.Ordinal)
            < synced.IndexOf("_config.VacationSinceUtc = on ? sinceUtc ?? now : null;", StringComparison.Ordinal));
        Assert.DoesNotContain("WindUpdatedUtc", synced);
        // The safeword pauses remote control on vacation but leaves the frozen wind as it is.
        var safeword = ProfileSettingsTests.MethodBody(timer, "    public void UseSafeword(");
        Assert.True(safeword.IndexOf("if (_config.IsOnVacation)", StringComparison.Ordinal) < safeword.IndexOf("Layers.AddSafeword(", StringComparison.Ordinal));
    }

    [Fact]
    public void ClosedPopups_NeverReopenThemselves()
    {
        // Dalamud calls OnClose on the next draw, after the tick's Sync: a Sync that reopens on state OnClose clears loops.
        var unwind = ProfileSettingsTests.PluginSource(Path.Combine("Ui", "UnwindConfirmWindow.cs"));
        Assert.Contains("!ReferenceEquals(quote, _shownQuote)", ProfileSettingsTests.MethodBody(unwind, "    public void Sync("));
        var discord = ProfileSettingsTests.PluginSource(Path.Combine("Ui", "DiscordPromptWindow.cs"));
        var sync = ProfileSettingsTests.MethodBody(discord, "    public void Sync(");
        Assert.Contains("_opened", sync);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(discord, @"MarkShown\(\)").Count - 1);
    }

    [Fact]
    public void Vacation_IsPerCharacter_AndADollsOnly()
    {
        var source = ProfileSettingsTests.PluginSource("Configuration.cs");
        Assert.Contains("public bool IsOnVacation => IsDoll && VacationSinceUtc is not null;", source);
        Assert.Contains("VacationSinceUtc = VacationSinceUtc,", source);
        Assert.Contains("VacationSinceUtc = profile.VacationSinceUtc;", source);
    }
}
