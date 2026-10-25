namespace WindUpKey.Tests;

/// <summary>
/// The spare springs on Dalamud's server info bar: a setting under Settings, Interface; clicking the entry opens the main
/// window. The service and plugin need Dalamud, so their wiring is read from source.
/// </summary>
public sealed class ServerInfoBarTests
{
    private static string Service => ProfileSettingsTests.PluginSource(Path.Combine("Services", "ServerInfoBarService.cs"));

    private static string Plugin => ProfileSettingsTests.PluginSource("Plugin.cs");

    [Fact]
    public void TheEntry_IsOneSettingForThePc_UnderInterface_OffByDefault()
    {
        Assert.Contains("public bool ShowSpringsInServerInfoBar { get; set; }", ProfileSettingsTests.PluginSource("Configuration.cs"));
        Assert.DoesNotContain("ShowSpringsInServerInfoBar", ProfileSettingsTests.PluginSource("CharacterProfile.cs"));
        var section = ProfileSettingsTests.MethodBody(
            ProfileSettingsTests.PluginSource(Path.Combine("Ui", "ConfigWindow.cs")), "    private void DrawInterfaceSection()");
        Assert.Contains("_config.ShowSpringsInServerInfoBar = serverInfoBar;\n            _config.Save();", section);
    }

    [Fact]
    public void TheEntry_ShowsOnlyWhenOn_AndOnceARoleIsChosen()
    {
        var update = ProfileSettingsTests.MethodBody(Service, "    public void Update()");
        Assert.Contains("var show = _config.ShowSpringsInServerInfoBar && _config.HasChosenRole;", update);
        // Made once, then only shown or hidden (re-adding a removed title in one session isn't reliable).
        Assert.Contains("if (_entry is null)", update);
        Assert.Contains("_entry.Shown = false;", update);
        Assert.Contains("_entry.Shown = true;", update);
        Assert.DoesNotContain(".Remove()", update);
    }

    [Fact]
    public void TheEntry_ShowsTheSprings_AndChangesOnlyWithThem()
    {
        Assert.Contains("balance is { } b ? $\"Springs {b}/{cap}\" : \"Springs …\"", Service);
        var update = ProfileSettingsTests.MethodBody(Service, "    public void Update()");
        Assert.Contains("var text = SpringsText(_relay.SpringBalance, _relay.SpringCap);", update);
        Assert.Contains("if (text == _shownText)\n            return;", update);
    }

    [Fact]
    public void ClickingIt_OpensTheMainWindow()
    {
        // A left click only.
        Assert.Contains("if (click.ClickType == MouseClickType.Left)\n                    _toggleMainWindow();", Service);
        Assert.Contains("new ServerInfoBarService(DtrBar, Configuration, _relay, ToggleMain);", Plugin);
        Assert.Contains("private void ToggleMain() => _mainWindow.ShowOrHide();", Plugin);

        // A collapsed window counts as not showing: the click expands it rather than closing it.
        var main = ProfileSettingsTests.PluginSource(Path.Combine("Ui", "MainWindow.cs"));
        var toggle = ProfileSettingsTests.MethodBody(main, "    public void ShowOrHide()");
        Assert.Contains("var showing = IsOpen && _lastDrawnFrame >= ImGui.GetFrameCount() - 1;", toggle);
        Assert.Contains("Show();", toggle);
        var show = ProfileSettingsTests.MethodBody(main, "    private void Show()");
        Assert.Contains("Collapsed = false;\n        CollapsedCondition = ImGuiCond.Always;\n        BringToFront();", show);
        Assert.Contains("_lastDrawnFrame = ImGui.GetFrameCount();", ProfileSettingsTests.MethodBody(main, "    public override void Draw()"));
    }

    [Fact]
    public void TheEntry_IsKeptCurrentEachTick_AndRemovedOnUnload()
    {
        Assert.Contains("_serverInfoBar.Update();", ProfileSettingsTests.MethodBody(Plugin, "    private void OnFrameworkUpdate(IFramework framework)"));
        Assert.Contains("_serverInfoBar.Dispose();", ProfileSettingsTests.MethodBody(Plugin, "    public void Dispose()"));
        Assert.Contains("_entry?.Remove();", ProfileSettingsTests.MethodBody(Service, "    public void Dispose()"));
    }
}
