namespace WindUpKey.Tests;

/// <summary>
/// The main window's two layouts: buttons that open pop-out windows, or icon tabs holding the same drawing. The windows
/// need Dalamud, so their wiring is read from source.
/// </summary>
public sealed class TabbedLayoutTests
{
    private static string Ui(string file) => ProfileSettingsTests.PluginSource(Path.Combine("Ui", file));

    private static string Main => Ui("MainWindow.cs");

    [Fact]
    public void TheLayout_IsOneSettingForThePc_NotPerCharacter()
    {
        Assert.Contains("public bool TabbedMainWindow { get; set; }", ProfileSettingsTests.PluginSource("Configuration.cs"));
        Assert.DoesNotContain("TabbedMainWindow", ProfileSettingsTests.PluginSource("CharacterProfile.cs"));
        var section = ProfileSettingsTests.MethodBody(Ui("ConfigWindow.cs"), "    private void DrawInterfaceSection()");
        Assert.Contains("SetTabbedMainWindow(true);", section);
        Assert.Contains("SetTabbedMainWindow(false);", section);

        // Leaving tabs (from the Settings tab) reopens Settings as its own window rather than losing it.
        var set = ProfileSettingsTests.MethodBody(Ui("ConfigWindow.cs"), "    private void SetTabbedMainWindow(bool tabbed)");
        Assert.Contains("if (!tabbed)\n        {\n            _section = SettingsSection.Interface;\n            IsOpen = true;\n        }", set);
    }

    [Theory]
    [InlineData("WindWindow.cs")]
    [InlineData("PairsWindow.cs")]
    [InlineData("QuestsWindow.cs")]
    [InlineData("SafewordWindow.cs")]
    [InlineData("ConfigWindow.cs")]
    public void PopOuts_NeverDrawThemselves_WithTabs(string file)
    {
        // The same drawing (and its drafts) must never run twice in a frame: with tabs it runs only in the tab.
        Assert.Contains("public override bool DrawConditions() => !_config.TabbedMainWindow;", Ui(file));
    }

    [Fact]
    public void Tabs_DrawWhatEachPopOutDraws()
    {
        var tab = ProfileSettingsTests.MethodBody(Main, "    private void DrawTab(MainTab tab)");
        foreach (var call in new[] { "DrawHome();", "_wind.Draw();", "_pairs.Draw();", "_quests.Draw();", "_safeword.Draw();", "_settings.Draw();" })
            Assert.Contains(call, tab);
    }

    [Fact]
    public void Tabs_AreIcons_NamedWhenHovered()
    {
        var tabs = ProfileSettingsTests.MethodBody(Main, "    private void DrawTabs()");
        Assert.Contains("ImGui.PushFont(UiBuilder.IconFont);\n            var open = ImGui.BeginTabItem($\"{TabIcon(tab).ToIconString()}##tab_{tab}\", flags);\n            ImGui.PopFont();", tabs);
        Assert.Contains("if (ImGui.IsItemHovered())\n                ImGui.SetTooltip(tab.ToString());", tabs);
    }

    [Fact]
    public void SafewordTab_IsADollsOutsideHardcore_AsTheButtonIs()
    {
        Assert.Contains("if (_config.ShowsAsDoll && !_config.HardcoreMode)\n            yield return MainTab.Safeword;", Main);
    }

    [Fact]
    public void LeavingATab_DoesWhatClosingItsWindowDoes()
    {
        var leave = ProfileSettingsTests.MethodBody(Main, "    private void LeaveTab(MainTab tab)");
        Assert.Contains("_pairs.OnClose();", leave);
        Assert.Contains("_safeword.OnClose();", leave);
        Assert.Contains("LeaveTab(_activeTab);", ProfileSettingsTests.MethodBody(Main, "    public override void OnClose()"));

        // Switching tabs reaches it: each drawn tab is entered, which leaves the one before.
        Assert.Contains("EnterTab(tab);", ProfileSettingsTests.MethodBody(Main, "    private void DrawTabs()"));
        var enter = ProfileSettingsTests.MethodBody(Main, "    private void EnterTab(MainTab tab)");
        Assert.Contains("LeaveTab(_activeTab);\n        _activeTab = tab;", enter);
    }

    [Fact]
    public void WithTabs_OpeningAPopOut_OpensItsTab()
    {
        var route = ProfileSettingsTests.MethodBody(Main, "    public void RouteToTabs()");
        Assert.Contains("if (!_config.TabbedMainWindow)\n            return;", route);
        foreach (var entry in new[]
                 {
                     "(_wind, MainTab.Wind)", "(_pairs, MainTab.Pairs)", "(_quests, MainTab.Quests)", "(_safeword, MainTab.Safeword)",
                     "(_settings, MainTab.Settings)",
                 })
            Assert.Contains(entry, route);
        Assert.Contains("window.IsOpen = false;", route);
        // A collapsed main window is expanded, or the tab would never show; Draw lets it go again.
        Assert.Contains("_selectTab = tab;\n            Show();", route);
        Assert.Contains("Collapsed = null;", ProfileSettingsTests.MethodBody(Main, "    public override void Draw()"));
        // Routed before the windows draw, every frame.
        var draw = ProfileSettingsTests.MethodBody(ProfileSettingsTests.PluginSource("Plugin.cs"), "    private void DrawUi()");
        Assert.True(draw.IndexOf("_mainWindow.RouteToTabs();", StringComparison.Ordinal) < draw.IndexOf("_windowSystem.Draw();", StringComparison.Ordinal));
        Assert.Contains("PluginInterface.UiBuilder.Draw += DrawUi;", ProfileSettingsTests.PluginSource("Plugin.cs"));
    }

    [Fact]
    public void Discord_IsAnIconLeftOfTheConnection_InBothLayouts()
    {
        var header = ProfileSettingsTests.MethodBody(Main, "    private void DrawHeader()");
        var discord = header.IndexOf("DiscordLink.IconButton(_textures, \"##discord_main\");", StringComparison.Ordinal);
        Assert.True(discord >= 0 && discord < header.IndexOf("ImGui.SmallButton($\"{label}##connection\")", StringComparison.Ordinal));
        Assert.DoesNotContain("\"Discord\"", ProfileSettingsTests.MethodBody(Main, "    private void DrawModules()"));
        // The header is drawn before the layouts part ways, so both have it.
        var draw = ProfileSettingsTests.MethodBody(Main, "    public override void Draw()");
        Assert.True(draw.IndexOf("DrawHeader();", StringComparison.Ordinal) < draw.IndexOf("if (_config.TabbedMainWindow)", StringComparison.Ordinal));
    }
}
