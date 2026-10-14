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
    public void TheThresholdIs30Yalms()
    {
        // The owner chose 30 yalms; changing it should be deliberate.
        Assert.Equal(30f, CallHandoffRules.HandoffYalms);
    }

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

    private static string TravelSource => ProfileSettingsTests.PluginSource(Path.Combine("Services", "CallTravelService.cs"));

    /// <summary>The text from <paramref name="start"/> up to (not including) <paramref name="end"/>.</summary>
    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"missing: {start}");
        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to >= 0, $"missing after {start}: {end}");
        return source[from..to];
    }

    [Fact]
    public void TickTeleporting_AbortsLifestreamInTheHandoffBranch_BeforeTheBusyReturn()
    {
        var tick = Between(TravelSource, "private void TickTeleporting()", "private void TickPathing()");

        // The handoff runs ahead of the busy early-return, which would otherwise hold navmesh off until the plot.
        var branch = Between(
            tick,
            "if (_pending.IsHousingCall && IsLifestreamBusy() && ShouldHandOffToNavmesh())",
            "if (IsBetweenAreas() || IsLifestreamBusy() || IsPlayerCasting())");

        // The abort is in the branch itself, not only in StopAutomationIpc, and the branch returns for a tick.
        Assert.Contains("_lsAbort?.InvokeAction();", branch);
        Assert.Contains("_doorApproachActive = false;", branch);
        Assert.Matches(@"_lsAbort\?\.InvokeAction\(\);[\s\S]*return;\s*\}", branch);

        // Lifestream can stay busy for a few frames after the abort; the log line is keyed so it's throttled.
        Assert.Contains("key: \"teleport-handoff\"", branch);
        Assert.DoesNotContain("throttle: false", branch);
    }

    [Fact]
    public void ShouldHandOffToNavmesh_FeedsTheRuleThePendingCallAndPlayerState()
    {
        var method = Between(TravelSource, "private bool ShouldHandOffToNavmesh()", "private bool IsWithinSameInstancePathRange()");

        // Only a missing player or Call skips the rule.
        Assert.Contains("if (player is null || _pending is null)\n            return false;", method.Replace("\r\n", "\n"));
        Assert.Contains("housingCall: _pending.IsHousingCall,", method);
        Assert.Contains("indoor: _pending.HousingIndoor,", method);
        Assert.Contains("atHousingWard: IsAtHousingWard(),", method);
        Assert.Contains("betweenAreas: IsBetweenAreas(),", method);
        Assert.Contains("casting: IsPlayerCasting(),", method);

        // XZ distance from the player to the owner's position.
        Assert.Contains("var dx = player.Position.X - _pending.Position.X;", method);
        Assert.Contains("var dz = player.Position.Z - _pending.Position.Z;", method);
        Assert.Contains("distanceToOwnerYalms: MathF.Sqrt(dx * dx + dz * dz)", method);
    }
}
