using WindUpKey.Protocol;
using WindUpKey.Services;

namespace WindUpKey.Tests;

/// <summary>The doll's three wind layers: drain order, the fills, breaking, unwinding and the spring rules they use.</summary>
public sealed class WindLayersTests
{
    private const double Max = 72;
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static WindLayers Layers(double normal, double quest = 0, double player = 0) => new(normal, quest, player, Now, null);

    [Fact]
    public void TimeDrainsPlayerOverwind_ThenQuestOverwind_ThenNormalWind()
    {
        var layers = Layers(72, quest: 10, player: 5);

        Assert.Equal((72.0, 10.0, 1.0), Parts(layers.At(Now.AddHours(4))));
        Assert.Equal((72.0, 7.0, 0.0), Parts(layers.At(Now.AddHours(8))));
        Assert.Equal((70.0, 0.0, 0.0), Parts(layers.At(Now.AddHours(17))));
        Assert.Equal((0.0, 0.0, 0.0), Parts(layers.At(Now.AddHours(200))));
        Assert.Equal(TimeSpan.FromHours(87), layers.RunningFor(Now));
        Assert.Equal(Now.AddHours(87), layers.StopsAt(Now));
    }

    [Fact]
    public void AtZeroNormalWind_QuestOverwindIsHeld_AndTheDollIsLocked()
    {
        var frozen = Layers(0, quest: 20);

        Assert.Equal((0.0, 20.0, 0.0), Parts(frozen.At(Now.AddDays(3))));
        Assert.True(frozen.IsLockedAt(Now));
        Assert.Null(frozen.StopsAt(Now));

        // Wound again: they move, and the quest overwind drains before the new normal wind.
        var wound = frozen.AddPlayer(8, Max, Now, 30).Layers;
        Assert.False(wound.IsLockedAt(Now));
        Assert.Equal((8.0, 0.0, 0.0), Parts(wound.At(Now.AddHours(20))));
    }

    [Fact]
    public void APlayerWind_FillsNormalWind_ThenPlayerOverwind()
    {
        var outcome = Layers(60).AddPlayer(24, Max, Now, 30);
        Assert.False(outcome.Broke);
        Assert.Equal((72.0, 0.0, 12.0), Parts(outcome.Layers));
        Assert.Equal(100, outcome.Layers.PercentOfMax(Now, Max));
    }

    [Fact]
    public void PlayerOverwindReachingMax_BreaksTheDoll_AndIsLost_ButQuestOverwindStays()
    {
        var outcome = Layers(72, quest: 30, player: 60).AddPlayer(12, Max, Now, 30);

        Assert.True(outcome.Broke);
        Assert.Equal((72.0, 30.0, 0.0), Parts(outcome.Layers));
        Assert.Equal(Now.AddMinutes(30), outcome.Layers.BrokenUntil);
        Assert.True(outcome.Layers.IsLockedAt(Now.AddMinutes(10)));
        Assert.Equal(TimeSpan.Zero, outcome.Layers.RunningFor(Now.AddMinutes(10)));
    }

    [Fact]
    public void WhileBroken_TheTimerIsPaused_AndRunsOnAfterTheBreak()
    {
        var broken = Layers(72, player: 70).AddPlayer(4, Max, Now, 30).Layers;

        Assert.Equal(72.0, broken.At(Now.AddMinutes(29)).Normal);
        Assert.False(broken.IsLockedAt(Now.AddMinutes(30)));
        Assert.Null(broken.At(Now.AddMinutes(31)).BrokenUntil);
        Assert.Equal(72 - 1.5, broken.At(Now.AddHours(2)).Normal, 6);
        Assert.Equal(Now.AddMinutes(30).AddHours(72), broken.StopsAt(Now.AddMinutes(10)));

        var repaired = broken.Repair(Now.AddMinutes(10));
        Assert.False(repaired.IsLockedAt(Now.AddMinutes(10)));
        Assert.Equal(71.0, repaired.At(Now.AddMinutes(70)).Normal, 6);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public void TheBreakLength_IsClamped(int minutes)
    {
        var broken = Layers(72, player: 72).AddPlayer(1, Max, Now, minutes).Layers;
        Assert.Equal(
            Now.AddMinutes(Math.Clamp(minutes, WindLayers.MinRecoveryMinutes, WindLayers.MaxRecoveryMinutes)),
            broken.BrokenUntil);
    }

    [Fact]
    public void QuestWind_FillsNormal_ThenQuestOverwind_UpToTwiceMax()
    {
        Assert.Equal((72.0, 12.0, 0.0), Parts(Layers(60).AddQuest(24, Max, Now)));
        Assert.Equal((72.0, 72.0, 0.0), Parts(Layers(72, quest: 70).AddQuest(24, Max, Now)));
        // Player overwind sits on top and doesn't eat into the quest cap.
        Assert.Equal((72.0, 72.0, 30.0), Parts(Layers(72, quest: 60, player: 30).AddQuest(24, Max, Now)));
    }

    [Fact]
    public void CommendationAndSafeword_NeverAddPlayerOverwind()
    {
        Assert.Equal((72.0, 0.0, 0.0), Parts(Layers(70).AddNormal(8, Max, Now)));
        Assert.Equal((72.0, 200.0, 0.0), Parts(Layers(72).AddSafeword(200, Max, Now)));
    }

    [Fact]
    public void AnUnwind_RemovesNormalAndPlayerOverwind_AndKeepsQuestOverwind()
    {
        var layers = Layers(40, quest: 20, player: 0);
        Assert.Equal(40.0, layers.UnwindableHours(Now));
        var unwound = Layers(72, quest: 20, player: 10).Unwind(Now);
        Assert.Equal((0.0, 20.0, 0.0), Parts(unwound));
        Assert.True(unwound.IsLockedAt(Now));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.5, 1)]
    [InlineData(8, 1)]
    [InlineData(8.01, 2)]
    [InlineData(16, 2)]
    [InlineData(80, 10)]
    public void TheUnwindCost_RoundsUpToWholeSprings(double hours, int springs) =>
        Assert.Equal(springs, SpringRules.UnwindCost(hours));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(7.1, 9)]
    [InlineData(36, 50)]
    [InlineData(64.8, 90)]
    [InlineData(72, 100)]
    public void ThePercent_IsOfNormalWind(double normal, int percent) =>
        Assert.Equal(percent, Layers(normal, quest: 50, player: 10).PercentOfMax(Now, Max));

    [Fact]
    public void OldWind_BecomesNormal_ThenQuestOverwind()
    {
        Assert.Equal((72.0, 28.0, 0.0), Parts(WindLayers.FromExpiry(Now.AddHours(100), Now, Max)));
        Assert.Equal((0.0, 0.0, 0.0), Parts(WindLayers.FromExpiry(null, Now, Max)));
        Assert.Equal((0.0, 0.0, 0.0), Parts(WindLayers.FromExpiry(Now.AddHours(-1), Now, Max)));
    }

    [Fact]
    public void RaisingMax_MovesPlayerOverwindIntoNormalWind_ButNeverQuestOverwind()
    {
        Assert.Equal((36.0, 0.0, 12.0), Parts(Layers(24, player: 24).Relayer(36)));
        Assert.Equal((48.0, 0.0, 0.0), Parts(Layers(24, player: 24).Relayer(72)));
        // Held after an unwind and a small wind: quest overwind stays put, so a later unwind still can't take it.
        Assert.Equal((8.0, 20.0, 0.0), Parts(Layers(8, quest: 20).Relayer(72)));
        Assert.Equal((80.0, 0.0, 5.0), Parts(Layers(80, player: 5).Relayer(72)));
    }

    [Fact]
    public void RaisingMax_AfterPlayerOverwindDrained_LeavesQuestOverwindWhereItIs()
    {
        // The timer reads the drain since the last save first, then relayers: relayering first would move the drained
        // player overwind into normal wind and take the drain from quest overwind instead.
        var saved = Layers(72, quest: 20, player: 24);

        Assert.Equal((72.0, 20.0, 0.0), Parts(saved.At(Now.AddHours(24)).Relayer(96)));
        Assert.Equal((92.0, 0.0, 0.0), Parts(saved.Relayer(96).At(Now.AddHours(24))));
    }

    [Fact]
    public void Timer_ReadsTheDrainBeforeRelayering()
    {
        var timer = ProfileSettingsTests.PluginSource(Path.Combine("Services", "WindTimerService.cs"));

        Assert.Contains("_config.BrokenUntilUtc).At(now).Relayer(_config.MaxWindHours);", timer);
    }

    [Fact]
    public void Migration_FillsLayersFromTheExpiry_OnlyWhereTheyAreMissing()
    {
        var old = new CharacterProfile { MaxWindHours = 72, ExpiryUtc = Now.AddHours(100) };
        var migrated = new CharacterProfile { MaxWindHours = 72, ExpiryUtc = Now.AddHours(100), NormalWindHours = 5, WindAsOfUtc = Now };

        ProfileMigration.FillWindLayers([old, migrated, null], Now);

        Assert.Equal((72.0, 28.0, 0.0, Now), (old.NormalWindHours, old.QuestOverwindHours, old.PlayerOverwindHours, old.WindAsOfUtc));
        Assert.Equal(5.0, migrated.NormalWindHours);
        Assert.Equal(WindLayers.DefaultRecoveryMinutes, new CharacterProfile().BrokenRecoveryMinutes);
    }

    [Fact]
    public void SpringRules_DoubleForWinders_AndOwnersWindHalfAgainAsMuch()
    {
        Assert.Equal((1, 2), (SpringRules.DailySprings(SpringRules.RoleDoll), SpringRules.DailySprings(SpringRules.RoleWinder)));
        Assert.Equal((3, 6), (SpringRules.QuestSprings(SpringRules.RoleDoll, 3), SpringRules.QuestSprings(SpringRules.RoleWinder, 3)));
        Assert.Equal((10, 20), (SpringRules.Cap(SpringRules.RoleDoll), SpringRules.Cap(SpringRules.RoleWinder)));
        Assert.Equal((16.0, 24.0), (SpringRules.WindHours(2, fromOwner: false), SpringRules.WindHours(2, fromOwner: true)));
        Assert.Equal(SpringRules.RoleDoll, SpringRules.NormalizeRole(null));
    }

    private static (double Normal, double Quest, double Player) Parts(WindLayers layers) =>
        (Math.Round(layers.Normal, 6), Math.Round(layers.Quest, 6), Math.Round(layers.Player, 6));
}
