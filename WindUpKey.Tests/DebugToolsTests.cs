using WindUpKey.Protocol;

namespace WindUpKey.Tests;

/// <summary>
/// Debug tools: previewing the other role's UI (view only, never saved) and adding spare springs (granted by the relay
/// for the keys its config lists). The windows and the relay client need Dalamud, so their wiring is read from source.
/// </summary>
public sealed class DebugToolsTests
{
    private static string Source(params string[] path) => ProfileSettingsTests.PluginSource(Path.Combine(path));

    [Fact]
    public void ThePreview_IsNeverSaved_AndOnlyInDebugModeWithARole()
    {
        var config = Source("Configuration.cs");
        Assert.Contains("    [Newtonsoft.Json.JsonIgnore]\n    public bool DebugPreviewOtherRole { get; set; }", config);
        Assert.Contains("public bool IsPreviewingOtherRole => IsDebugEnabled && DebugPreviewOtherRole && HasChosenRole;", config);
        Assert.Contains("public bool ShowsAsDoll => IsPreviewingOtherRole ? !IsDoll : IsDoll;", config);
        // Not part of a character's saved profile either.
        Assert.DoesNotContain("DebugPreviewOtherRole", Source("CharacterProfile.cs"));
    }

    [Fact]
    public void Safeword_IsViewOnly_WhilePreviewing()
    {
        var draw = ProfileSettingsTests.MethodBody(Source("Ui", "SafewordWindow.cs"), "    public override void Draw()");
        var begin = draw.IndexOf("var preview = RoleGate.BeginPreview(_config);", StringComparison.Ordinal);
        Assert.True(begin > draw.IndexOf("if (RoleGate.Blocks(_config))", StringComparison.Ordinal));
        Assert.True(draw.IndexOf("RoleGate.EndPreview(preview);", StringComparison.Ordinal) > begin);
    }

    [Fact]
    public void Wind_LetsAPreviewPickPartners_ButNotAct()
    {
        var source = Source("Ui", "WindWindow.cs");
        Assert.Contains("RoleGate.DrawPreviewBanner(_config);", ProfileSettingsTests.MethodBody(source, "    public override void Draw()"));
        Assert.Contains(
            "ImGui.BeginDisabled(_config.IsPreviewingOtherRole);\n                DrawActions(selected);\n                ImGui.EndDisabled();",
            ProfileSettingsTests.MethodBody(source, "    private void DrawWindow()"));
    }

    [Fact]
    public void Pairs_LetsAPreviewOpenHeadersAndWindows_ButNotChangeThem()
    {
        Assert.Contains("_host.DrawPairsContent(viewOnly: _config.IsPreviewingOtherRole);", Source("Ui", "PairsWindow.cs"));
        var pairs = ProfileSettingsTests.MethodBody(Source("Ui", "ConfigWindow.cs"), "    public void DrawPairsContent(bool viewOnly = false)");
        // Pairing, Unpair, the Labels/Permissions/Owner windows' contents and the owner confirm are off.
        Assert.Contains("ImGui.BeginDisabled(viewOnly);\n        ImGui.SetNextItemWidth(120);", pairs);
        Assert.Contains("var unpairOff = !hasValidPartnerKey || viewOnly;", pairs);
        Assert.Contains("ImGui.BeginDisabled(viewOnly);\n                DrawPairLabelsWindow(partner);", pairs);
        Assert.Contains("ImGui.BeginDisabled(viewOnly);\n        DrawOwnerGrantConfirm();\n        ImGui.EndDisabled();", pairs);
    }

    [Fact]
    public void Quests_ShowsTheBoardAndInformation_ButCantReroll()
    {
        Assert.Contains("() => config.IsPreviewingOtherRole", Source("Ui", "QuestsWindow.cs"));
        var tab = Source("Ui", "QuestBoardTab.cs");
        Assert.Contains("var can = _quests.CanReroll(difficulty) && !_viewOnly();", tab);
        var confirm = ProfileSettingsTests.MethodBody(tab, "    private void DrawRerollConfirm()");
        Assert.Contains("if (_viewOnly())\n        {\n            _rerollConfirm = null;\n            return;\n        }", confirm);
    }

    [Fact]
    public void ThePreview_EndsOnACharacterSwitch_AndBlocksReconnect()
    {
        var apply = ProfileSettingsTests.MethodBody(Source("Configuration.cs"), "    public void ApplyProfile(CharacterProfile profile)");
        Assert.Contains("DebugPreviewOtherRole = false;", apply);
        Assert.Contains(
            "ImGui.BeginDisabled(_config.IsPreviewingOtherRole);\n            if (ImGui.Button(\"Reconnect\"))",
            Source("Ui", "MainWindow.cs"));
    }

    [Fact]
    public void ThePreview_DisablesWhatFollowsItsBanner()
    {
        var gate = ProfileSettingsTests.MethodBody(Source("Ui", "RoleGate.cs"), "    public static bool BeginPreview(Configuration config)");
        Assert.Contains("if (!config.IsPreviewingOtherRole)\n            return false;", gate);
        Assert.Contains("ImGui.BeginDisabled();", gate);
    }

    [Fact]
    public void Settings_AreViewOnly_WhilePreviewing_ExceptDebug()
    {
        var draw = ProfileSettingsTests.MethodBody(Source("Ui", "ConfigWindow.cs"), "    public override void Draw()");
        Assert.Contains("var preview = _section != SettingsSection.Debug && RoleGate.BeginPreview(_config);", draw);
        Assert.Contains("RoleGate.EndPreview(preview);", draw);
    }

    [Fact]
    public void MainWindow_ShowsTheBanner_AndQuickWindIsOffWhilePreviewing()
    {
        var main = Source("Ui", "MainWindow.cs");
        Assert.Contains("RoleGate.DrawPreviewBanner(_config);", main);
        Assert.Contains("var canWind = _relay.IsConnected && _relay.SpringBalance is not 0 && !_config.IsPreviewingOtherRole;", main);
    }

    [Fact]
    public void ThePreview_ChangesWhatIsShown_NotTheRulesOrConfirms()
    {
        // Confirms and role changes keep the real role.
        var settings = Source("Ui", "ConfigWindow.cs");
        Assert.Contains("_hardcoreConfirm.StillValid(_config.ActiveContentId, _config.IsDoll, _ => true)", settings);
        Assert.Contains("ImGui.TextUnformatted(_config.IsDoll ? \"You're a doll.\" : \"You're a winder.\");", settings);
        // No service reads the preview, and the only source that does turns its menu off during it.
        var services = Path.GetDirectoryName(ProfileSettingsTests.PluginPath(Path.Combine("Services", "RelayClient.cs")))!;
        var sources = Path.GetDirectoryName(ProfileSettingsTests.PluginPath(Path.Combine("Sources", "ContextMenuWindSource.cs")))!;
        foreach (var file in Directory.GetFiles(services, "*.cs").Concat(Directory.GetFiles(sources, "*.cs")))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("ShowsAsDoll", text);
            Assert.DoesNotContain("DebugPreviewOtherRole", text);
            if (!file.EndsWith("ContextMenuWindSource.cs", StringComparison.Ordinal))
                Assert.DoesNotContain("IsPreviewingOtherRole", text);
        }
    }

    [Fact]
    public void ThePreview_OffersNoMenu_AndCantPayAnUnwind()
    {
        Assert.Contains(
            "if (!_config.HasChosenRole || _config.IsPreviewingOtherRole)\n            return;",
            Source("Sources", "ContextMenuWindSource.cs"));
        Assert.Contains("var canConfirm = (quote.Free || quote.Springs > 0) && affordable && !previewing;", Source("Ui", "UnwindConfirmWindow.cs"));
    }

    [Fact]
    public void DebugSprings_AreSentOnlyInDebugMode()
    {
        var relay = Source("Services", "RelayClient.cs");
        var send = ProfileSettingsTests.MethodBody(relay, "    public async Task SendDebugSpringGrantAsync(int springs)");
        Assert.Contains("if (!_config.IsDebugEnabled)\n            return;", send);
        Assert.Contains("Springs = Math.Clamp(springs, 1, SpringRules.WinderCap),", send);
    }

    [Fact]
    public void DebugSpringGrant_IsAKnownMessage()
    {
        Assert.True(MessageTypes.IsKnown(MessageTypes.DebugSpringGrant));
        Assert.Equal("debug", SpringReasons.Debug);
    }
}
