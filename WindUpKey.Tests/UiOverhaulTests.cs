using System.Text.RegularExpressions;

namespace WindUpKey.Tests;

/// <summary>
/// The UI overhaul: a main window with the doll's wind Moodles, and Wind, Pairs, Quests, Safeword and Settings windows.
/// The windows need Dalamud, so the checks on what they show and which sends they use read their source.
/// </summary>
public sealed class UiOverhaulTests
{
    private static string Ui(string file) => ProfileSettingsTests.PluginSource(Path.Combine("Ui", file));

    private static string MoodlesSource => ProfileSettingsTests.PluginSource(Path.Combine("Services", "MoodlesWindStatusService.cs"));

    [Theory]
    [InlineData("MainWindow.cs")]
    [InlineData("WindWindow.cs")]
    public void Windows_NeverShowTheDollTheirOwnWind(string file)
    {
        // The doll must never learn their remaining time, nor a percentage of it: only their Moodles.
        var source = Ui(file);
        if (file == "WindWindow.cs")
        {
            // The one exception: the debug Yourself row, which exists only in debug mode, shows your own bar.
            Assert.Single(Regex.Matches(source, @"\bPercentForWinder\b"));
            var actions = ProfileSettingsTests.MethodBody(source, "    private void DrawActions(Partner partner)");
            var self = actions.IndexOf("else if (_config.IsDoll)", StringComparison.Ordinal);
            Assert.True(self > actions.IndexOf("if (!partner.Self)", StringComparison.Ordinal));
            Assert.True(actions.IndexOf("DrawWindBar(_timer.PercentForWinder, _timer.IsBroken);", StringComparison.Ordinal) > self);
            Assert.Contains("if (_config.IsDebugEnabled && _config.IsDoll && _config.ShowsAsDoll && PairingKeyUtil.IsValid(_config.PairingKey))", source);
        }
        else
        {
            Assert.DoesNotMatch(@"\bPercentForWinder\b", source);
        }

        Assert.DoesNotMatch(@"\bFormatRemaining\b", source);
        Assert.DoesNotMatch(@"\bLayers\b", source);
        Assert.DoesNotMatch(@"\bUnwindCost\b", source);
    }

    [Fact]
    public void WindWindow_SaysSeen_AndDrawsTheBarAtZero()
    {
        var source = Ui("WindWindow.cs");
        Assert.DoesNotContain("Heard", source);
        Assert.Contains("return \"Seen just now\";", source);
        // An unwound doll (0%) still gets her bar, empty.
        var bar = ProfileSettingsTests.MethodBody(source, "    private static void DrawWindBar(int percent, bool broken)");
        Assert.Contains("ImGui.ProgressBar(clamped / 100f,", bar);
        Assert.DoesNotContain("if (", bar);
    }

    [Fact]
    public void MainWindow_QuickWind_OffersUpToFourLowDolls_ForOneSpring()
    {
        var source = Ui("MainWindow.cs");
        Assert.Contains("private const int QuickWindBelowPercent = 75;", source);
        Assert.Contains("private const int QuickWindMax = 4;", source);
        var quick = ProfileSettingsTests.MethodBody(source, "    private void DrawQuickWind(List<PairedPartner> partners)");
        Assert.Contains("wind.Percent < QuickWindBelowPercent", quick);
        Assert.Contains("p.Wind is { Broken: false } wind", quick);
        Assert.Contains("!_relay.IsPartnerOnVacation(p.Key)", quick);
        Assert.Contains(".OrderBy(p => p.Wind!.Value.Percent)", quick);
        Assert.Contains(".Take(QuickWindMax)", quick);
        Assert.Contains("_relay.SendWindByKeyAsync(key, 1)", quick);
    }

    [Fact]
    public void PresenceAnswers_ShareADollsWind_OnlyWithThoseWhoMayWindHer()
    {
        var relay = ProfileSettingsTests.PluginSource(Path.Combine("Services", "RelayClient.cs"));
        // The expression body alone, from after its signature to its ';' (it has no braces for MethodBody to end at).
        const string signature = "    private bool PresenceSharesWind(string fromKey, bool stillPaired) =>";
        var start = relay.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "PresenceSharesWind not found");
        start += signature.Length;
        var shares = relay[start..relay.IndexOf(';', start)];
        foreach (var rule in new[]
                 {
                     "\n        _config.IsDoll\n", "\n        && !_config.IsOnVacation\n", "\n        && !_timer.IsRemoteControlPaused\n",
                     "\n        && stillPaired\n", "\n        && !string.Equals(fromKey, _config.PairingKey, StringComparison.Ordinal)\n",
                     "\n        && _consent.CanReceiveWindFromKey(fromKey)",
                 })
            Assert.Contains(rule, shares);

        var answer = ProfileSettingsTests.MethodBody(relay, "    private async Task HandleInboundPresenceQueryAsync(Envelope envelope, CancellationToken ct)");
        Assert.Contains("Percent = shareWind ? _timer.PercentForWinder : null,", answer);
        // Not before the pair list is synced (the local permissions may be stale), and not again and again for free.
        Assert.Contains("bool? windShared = _registerAccepted ? PresenceSharesWind(fromKey, stillPaired) : null;", answer);
        Assert.Contains("var shareWind = windShared == true && TakeWindShare(fromKey);", answer);
        Assert.Contains("WindShared = windShared,", answer);
        // A little under the main window's 2 minutes, so latency never makes its refresh miss.
        Assert.Contains("private static readonly TimeSpan WindShareInterval = TimeSpan.FromSeconds(105);", relay);

        var heard = ProfileSettingsTests.MethodBody(relay, "    private void HandlePresenceResult(Envelope envelope)");
        Assert.Contains(
            "if (payload.Online && payload.Percent is { } percent && payload.StillPaired != false && _consent.IsPairedByKey(peerKey))",
            heard);
        // A doll that stops sharing (permission, vacation, no longer a doll) drops out of quick wind.
        Assert.Contains("else if (payload.Online && payload.WindShared == false)\n            ForgetPartnerWind(peerKey);", heard);
    }

    [Fact]
    public void MainWindow_ShowsTheMoodleIcons_OnlyForADoll()
    {
        var source = Ui("MainWindow.cs");
        Assert.Contains("_moodles.CurrentIcons()", source);
        Assert.Contains("if (_config.ShowsAsDoll)\n            DrawMoodleIcons();", source);
    }

    [Fact]
    public void CurrentIcons_MatchTheLevelTheSyncApplies()
    {
        const string resolve = "ResolveLevel(_timer.PercentForWinder, _timer.IsTimerEmpty, _timer.IsBroken, _timer.IsOverwound)";
        var source = MoodlesSource;
        var icons = ProfileSettingsTests.MethodBody(source, "    public IReadOnlyList<WindMoodleIcon> CurrentIcons()");
        Assert.Contains(resolve, icons);
        Assert.Contains("LevelStatuses(level)", icons);
        // The sync resolves the level the same way, so the icons are the Moodles the doll has.
        Assert.True(Regex.Matches(source, Regex.Escape(resolve)).Count >= 2);
        // None on vacation (the sync removes them then) and none for a winder.
        Assert.Contains("if (!_config.IsDoll || _config.IsOnVacation)\n            return [];", icons);
    }

    [Theory]
    [InlineData("_relay.SendWindByKeyAsync(partner.Key, springs)")]
    [InlineData("_relay.SendSpringGiftAsync(partner.Key,")]
    [InlineData("_relay.SendRepairAsync(partner.Key)")]
    [InlineData("_relay.SendWindRequestAsync(partnerKey)")]
    [InlineData("_relay.SendUnwindByKeyAsync(partner.Key)")]
    public void WindWindow_UsesTheExistingSends(string send)
    {
        Assert.Contains(send, Ui("WindWindow.cs"));
    }

    [Fact]
    public void WindWindow_UnwindStillGoesThroughTheQuote()
    {
        // Only the confirm window, after the doll's quote, sends the paid unwind.
        Assert.DoesNotContain("SendPaidUnwindAsync", Ui("WindWindow.cs"));
        Assert.Contains("SendPaidUnwindAsync", Ui("UnwindConfirmWindow.cs"));
    }

    [Fact]
    public void WindWindow_ClampsTheGiftToTheWinderCap()
    {
        Assert.Contains("_giftDrafts[partner.Key] = Math.Clamp(gift, 1, SpringRules.WinderCap);", Ui("WindWindow.cs"));
        Assert.Contains("SendSpringGiftAsync(partner.Key, Math.Clamp(gift, 1, SpringRules.WinderCap))", Ui("WindWindow.cs"));
    }

    [Fact]
    public void Plugin_OpensTheMainWindow_AndSettingsFromTheirButtons()
    {
        var plugin = ProfileSettingsTests.PluginSource("Plugin.cs");
        Assert.Contains("PluginInterface.UiBuilder.OpenMainUi += OpenMain;", plugin);
        Assert.Contains("PluginInterface.UiBuilder.OpenConfigUi += OpenConfig;", plugin);
        Assert.Contains("private void OpenMain() => _mainWindow.IsOpen = true;", plugin);
    }

    [Fact]
    public void SafewordWindow_UsesTheSafeword_WithItsSetupLeftInSettings()
    {
        var window = Ui("SafewordWindow.cs");
        // One draft field shared by two windows lost a typed safeword; the setup is drawn by Settings alone.
        Assert.DoesNotContain("DrawSafewordTab", window);
        Assert.Contains("_useSafeword(_typed);", window);
        Assert.Contains("ImGuiInputTextFlags.Password", window);
        Assert.Contains("_host.DrawSafewordPause();", window);
        Assert.Contains("new SafewordWindow(Configuration, _configWindow, UseSafeword);", ProfileSettingsTests.PluginSource("Plugin.cs"));
    }

    [Theory]
    [InlineData("MainWindow.cs", "_relay.EnsurePresenceFresh(partner.PartnerKey, RelayClient.IdlePresenceAge);")]
    [InlineData("WindWindow.cs", "_relay.EnsurePresenceFresh(partner.Key, selected ? null : RelayClient.IdlePresenceAge);")]
    public void WindowsLeftOpen_AskPresenceRarely(string file, string call)
    {
        // Each presence query costs both sides a message from the relay's rate limit.
        Assert.Contains(call, Ui(file));
    }

    [Fact]
    public void WindWindow_ActsOnlyOnAShownRow()
    {
        var draw = ProfileSettingsTests.MethodBody(Ui("WindWindow.cs"), "    private void DrawWindow()");
        Assert.Contains("foreach (var partner in shown)", draw);
        Assert.Contains("if (shown.FirstOrDefault(", draw);
        Assert.DoesNotContain("partners.First(", draw);
    }

    [Fact]
    public void WindWindow_TheYourselfRow_IsADebugDollsAndEnsuresTheSelfPair()
    {
        var source = Ui("WindWindow.cs");
        Assert.Contains("if (_config.IsDebugEnabled && _config.IsDoll && _config.ShowsAsDoll && PairingKeyUtil.IsValid(_config.PairingKey))", source);
        Assert.Contains("_host.EnsureDebugSelfPair(canWind: true, canUnwind: false)", source);
        Assert.Contains("_host.EnsureDebugSelfPair(canWind: false, canUnwind: true)", source);
    }

    [Fact]
    public void PairsWindow_RepairsTheDebugSelfOwnership()
    {
        Assert.Contains("_host.SyncDebugSelfOwnership();", Ui("PairsWindow.cs"));
    }

    [Fact]
    public void CurrentIcons_AreNotReadFromDiskEveryFrame()
    {
        var icons = ProfileSettingsTests.MethodBody(MoodlesSource, "    public IReadOnlyList<WindMoodleIcon> CurrentIcons()");
        Assert.Contains("cached.Level == level", icons);
    }

    [Theory]
    [InlineData("WindWindow.cs")]
    [InlineData("PairsWindow.cs")]
    [InlineData("QuestsWindow.cs")]
    [InlineData("SafewordWindow.cs")]
    public void PopOuts_DoNothingUntilARoleIsChosen(string file)
    {
        // A pop-out left open across Change role, or a switch to an alt with no role, must not wind or pair.
        var draw = ProfileSettingsTests.MethodBody(Ui(file), "    public override void Draw()");
        Assert.Contains("if (RoleGate.Blocks(_config))\n            return;", draw);
    }

    [Fact]
    public void AWindRequest_IsRememberedAsItsPercentageAlone_ButNeverOverAKnownBreak()
    {
        // A request can't tell whether the doll is broken: it never claims she is, and never hides a break already seen
        // (which kept its own age), or quick wind would offer a broken doll.
        var relay = ProfileSettingsTests.PluginSource(Path.Combine("Services", "RelayClient.cs"));
        var request = ProfileSettingsTests.MethodBody(relay, "    private void HandleInboundWindRequest(Envelope envelope)");
        Assert.Contains(
            "if (LastKnownWind(fromKey) is not { Broken: true })\n            RememberPartnerWind(fromKey, percent, overwound: false, broken: false);",
            request);
    }

    [Fact]
    public void MainWindow_ModuleButtons_AreSizedExactly()
    {
        // Dalamud's IconButtonWithText scales the size it's given by the UI scale, squeezing the right column.
        var source = Ui("MainWindow.cs");
        Assert.DoesNotContain("ImGuiComponents.IconButtonWithText(", source);
        Assert.Contains("ImGui.Button($\"##module_{label}\", new Vector2(width, height));", source);
    }

    [Fact]
    public void QuestBoard_DrawsCheckBoxes_NotAsciiMarks()
    {
        var source = Ui("QuestBoardTab.cs");
        Assert.DoesNotContain("\"[x]\"", source);
        Assert.DoesNotContain("\"[ ]\"", source);
        Assert.Contains("DrawCheckBox(slot.Completed);", source);
    }

    [Fact]
    public void DiscordInvite_IsTheCurrentLink()
    {
        Assert.Contains("public const string Invite = \"https://discord.gg/5mRHH9gr6N\";", Ui("DiscordLink.cs"));
    }

    [Fact]
    public void Safeword_IsADollButton_OutsideHardcore_AndVacationAndChangelogAreInSettings()
    {
        var modules = ProfileSettingsTests.MethodBody(Ui("MainWindow.cs"), "    private void DrawModules()");
        Assert.Contains("if (_config.ShowsAsDoll && !_config.HardcoreMode)\n            modules.Add((FontAwesomeIcon.ShieldAlt, \"Safeword\"", modules);
        Assert.DoesNotContain("\"Vacation\"", modules);
        Assert.DoesNotContain("\"Changelog\"", modules);
    }
}
