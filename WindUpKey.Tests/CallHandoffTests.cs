using System.Text.RegularExpressions;
using WindUpKey.Services;

namespace WindUpKey.Tests;

/// <summary>
/// Outdoor housing Calls hand the last stretch from Lifestream to vnavmesh near the owner, so the doll doesn't drive
/// past them to the plot and come back. <see cref="CallHandoffRules"/> is Dalamud-free; the wiring in
/// <c>CallTravelService</c> needs Dalamud, so those checks read its source.
/// </summary>
public sealed class CallHandoffTests
{
    private static bool Handoff(
        bool housingCall = true,
        bool indoor = false,
        bool atWard = true,
        bool betweenAreas = false,
        bool casting = false,
        float distance = 10f) =>
        CallHandoffRules.ShouldHandOffToNavmesh(housingCall, indoor, atWard, betweenAreas, casting, distance);

    [Fact]
    public void NearOwnerInWard_HandsOff()
    {
        Assert.True(Handoff(distance: 10f));
    }

    [Fact]
    public void ExactlyAtTheThreshold_HandsOff()
    {
        Assert.True(Handoff(distance: CallHandoffRules.HandoffYalms));
    }

    [Fact]
    public void JustBeyondTheThreshold_DoesNotHandOff()
    {
        Assert.False(Handoff(distance: CallHandoffRules.HandoffYalms + 0.5f));
    }

    [Fact]
    public void FarFromOwner_KeepsLifestreamDriving()
    {
        Assert.False(Handoff(distance: 150f));
    }

    [Fact]
    public void NotAHousingCall_DoesNotHandOff()
    {
        Assert.False(Handoff(housingCall: false));
    }

    [Fact]
    public void IndoorCall_DoesNotHandOff()
    {
        Assert.False(Handoff(indoor: true));
    }

    [Fact]
    public void NotInTheOwnersWard_DoesNotHandOff()
    {
        Assert.False(Handoff(atWard: false));
    }

    [Fact]
    public void BetweenAreas_DoesNotHandOff()
    {
        Assert.False(Handoff(betweenAreas: true));
    }

    [Fact]
    public void Casting_DoesNotHandOff()
    {
        Assert.False(Handoff(casting: true));
    }

    [Fact]
    public void TravelServiceChecksTheRuleBeforeLifestreamBusyReturns()
    {
        var source = ProfileSettingsTests.PluginSource(Path.Combine("Services", "CallTravelService.cs"));

        // The handoff runs ahead of the busy early-return, which would otherwise hold navmesh off until the plot.
        var handoff = Regex.Match(source, @"IsLifestreamBusy\(\) && ShouldHandOffToNavmesh\(\)");
        var busyReturn = Regex.Match(source, @"if \(IsBetweenAreas\(\) \|\| IsLifestreamBusy\(\) \|\| IsPlayerCasting\(\)\)");
        Assert.True(handoff.Success, "the handoff check is missing from TickTeleporting");
        Assert.True(busyReturn.Success, "the busy early-return moved; re-check the handoff order");
        Assert.True(handoff.Index < busyReturn.Index, "the handoff must run before the busy early-return");
        Assert.Contains("_lsAbort?.InvokeAction()", source);
    }
}
