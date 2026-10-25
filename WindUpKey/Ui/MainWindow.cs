using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using WindUpKey.Services;

namespace WindUpKey.Ui;

/// <summary>
/// The main window (GagSpeak-style): springs, the Discord icon and connection, who this character is, the doll's wind
/// Moodles as icons, notices, partners online and quick wind, then either a grid of buttons that open each feature's
/// window or (Settings, Interface) icon tabs holding the same drawing. A doll never sees a time or a percentage of their
/// own wind here: only the Moodles they have.
/// </summary>
public sealed class MainWindow : Window
{
    private static readonly Vector4 Green = new(0.35f, 0.92f, 0.45f, 1f);
    private static readonly Vector4 Amber = new(1f, 0.75f, 0.3f, 1f);
    private static readonly Vector4 Red = new(0.9f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 Blue = new(0.45f, 0.75f, 1f, 1f);

    /// <summary>A Moodle icon's size (the game's status icons are 24×32).</summary>
    private static readonly Vector2 MoodleIconSize = new(24, 32);

    private readonly Configuration _config;
    private readonly RelayClient _relay;
    private readonly WindTimerService _timer;
    private readonly MoodlesWindStatusService _moodles;
    private readonly ITextureProvider _textures;
    private readonly ConfigWindow _settings;
    private readonly WindWindow _wind;
    private readonly PairsWindow _pairs;
    private readonly QuestsWindow _quests;
    private readonly SafewordWindow _safeword;

    public MainWindow(
        Configuration config,
        RelayClient relay,
        WindTimerService timer,
        MoodlesWindStatusService moodles,
        ITextureProvider textures,
        ConfigWindow settings,
        WindWindow wind,
        PairsWindow pairs,
        QuestsWindow quests,
        SafewordWindow safeword,
        string pluginVersion)
        : base($"Wind-Up Key {pluginVersion}###WindUpKeyMain")
    {
        _config = config;
        _relay = relay;
        _timer = timer;
        _moodles = moodles;
        _textures = textures;
        _settings = settings;
        _wind = wind;
        _pairs = pairs;
        _quests = quests;
        _safeword = safeword;
        Size = new Vector2(330, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>With tabs the window needs room for Settings' sidebar and the Wind window's two panes.</summary>
    private static readonly Vector2 TabbedMinimumSize = new(560, 420);

    /// <summary>With buttons, room for the header's springs, Discord icon and connection side by side.</summary>
    private static readonly Vector2 ButtonsMinimumSize = new(280, 200);

    public override void PreDraw()
    {
        SizeConstraints = new WindowSizeConstraints { MinimumSize = _config.TabbedMainWindow ? TabbedMinimumSize : ButtonsMinimumSize };
    }

    public override void Draw()
    {
        // Expanded once by Show; from here the player may collapse it again.
        Collapsed = null;
        _lastDrawnFrame = ImGui.GetFrameCount();

        if (!_config.HasChosenRole)
        {
            _settings.DrawRoleSetup();
            return;
        }

        DrawHeader();
        ImGui.Separator();
        if (_config.TabbedMainWindow)
        {
            DrawTabs();
            return;
        }

        ImGui.Spacing();
        DrawHome();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawModules();
    }

    /// <summary>
    /// Who this character is, their wind Moodles, notices and quick wind: under the header with buttons, or the Home tab.
    /// A debug role preview shows the other role here (its banner first).
    /// </summary>
    private void DrawHome()
    {
        RoleGate.DrawPreviewBanner(_config);
        DrawProfile();
        ImGui.Spacing();
        DrawNotices();
    }

    private enum MainTab
    {
        Home,
        Wind,
        Pairs,
        Quests,
        Safeword,
        Settings,
    }

    /// <summary>The tab to select on the next draw (a request to open its window, in tabs mode).</summary>
    private MainTab? _selectTab;

    /// <summary>The tab last drawn, so leaving it can do what closing its window does.</summary>
    private MainTab _activeTab = MainTab.Home;

    /// <summary>The tabs this character sees: as the buttons, Safeword only for a doll outside Hardcore.</summary>
    private IEnumerable<MainTab> VisibleTabs()
    {
        yield return MainTab.Home;
        yield return MainTab.Wind;
        yield return MainTab.Pairs;
        yield return MainTab.Quests;
        if (_config.ShowsAsDoll && !_config.HardcoreMode)
            yield return MainTab.Safeword;
        yield return MainTab.Settings;
    }

    private static FontAwesomeIcon TabIcon(MainTab tab) => tab switch
    {
        MainTab.Home => FontAwesomeIcon.Home,
        MainTab.Wind => FontAwesomeIcon.Key,
        MainTab.Pairs => FontAwesomeIcon.UserFriends,
        MainTab.Quests => FontAwesomeIcon.ThLarge,
        MainTab.Safeword => FontAwesomeIcon.ShieldAlt,
        _ => FontAwesomeIcon.Cog,
    };

    /// <summary>Icon tabs, each named when hovered, holding what the matching pop-out window would.</summary>
    private void DrawTabs()
    {
        if (!ImGui.BeginTabBar("##main_tabs"))
            return;

        foreach (var tab in VisibleTabs())
        {
            var flags = _selectTab == tab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            ImGui.PushFont(UiBuilder.IconFont);
            var open = ImGui.BeginTabItem($"{TabIcon(tab).ToIconString()}##tab_{tab}", flags);
            ImGui.PopFont();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(tab.ToString());
            if (!open)
                continue;

            EnterTab(tab);
            if (ImGui.BeginChild($"##tab_body_{tab}", Vector2.Zero, false))
                DrawTab(tab);
            ImGui.EndChild();
            ImGui.EndTabItem();
        }

        _selectTab = null;
        ImGui.EndTabBar();
    }

    /// <summary>The same drawing as each pop-out window (which doesn't draw itself in tabs mode).</summary>
    private void DrawTab(MainTab tab)
    {
        switch (tab)
        {
            case MainTab.Home:
                DrawHome();
                break;
            case MainTab.Wind:
                _wind.Draw();
                break;
            case MainTab.Pairs:
                _pairs.Draw();
                break;
            case MainTab.Quests:
                _quests.Draw();
                break;
            case MainTab.Safeword:
                _safeword.Draw();
                break;
            case MainTab.Settings:
                _settings.Draw();
                break;
        }
    }

    private void EnterTab(MainTab tab)
    {
        if (tab == _activeTab)
            return;

        LeaveTab(_activeTab);
        _activeTab = tab;
    }

    /// <summary>Leaving a tab (or closing the window on it) does what closing its window does.</summary>
    private void LeaveTab(MainTab tab)
    {
        if (tab == MainTab.Pairs)
            _pairs.OnClose();
        else if (tab == MainTab.Safeword)
            _safeword.OnClose();
    }

    public override void OnClose()
    {
        if (_config.TabbedMainWindow)
            LeaveTab(_activeTab);
    }

    /// <summary>
    /// Every frame, before the windows draw: in tabs mode, a request to open a pop-out window (a link, the plugin
    /// installer's settings button, a command) opens the main window on that tab instead.
    /// </summary>
    public void RouteToTabs()
    {
        if (!_config.TabbedMainWindow)
            return;

        foreach (var (window, tab) in new (Window Window, MainTab Tab)[]
                 {
                     (_wind, MainTab.Wind), (_pairs, MainTab.Pairs), (_quests, MainTab.Quests),
                     (_safeword, MainTab.Safeword), (_settings, MainTab.Settings),
                 })
        {
            if (!window.IsOpen)
                continue;

            window.IsOpen = false;
            _selectTab = tab;
            Show();
        }
    }

    /// <summary>Opens the window in front; a collapsed one draws nothing, so it is expanded once (Draw lets it go).</summary>
    private void Show()
    {
        IsOpen = true;
        Collapsed = false;
        CollapsedCondition = ImGuiCond.Always;
        BringToFront();
    }

    /// <summary>ImGui's frame count when Draw last ran: an open window that isn't drawn is collapsed (or hidden).</summary>
    private int _lastDrawnFrame = -1;

    /// <summary>
    /// The server info bar's click: closes the window when it is showing, otherwise opens it (expanding it from
    /// collapsed) in front.
    /// </summary>
    public void ShowOrHide()
    {
        var showing = IsOpen && _lastDrawnFrame >= ImGui.GetFrameCount() - 1;
        if (showing)
            IsOpen = false;
        else
            Show();
    }

    /// <summary>Spare springs on the left; on the right the Discord icon, then the connection (click for its status and Reconnect).</summary>
    private void DrawHeader()
    {
        ImGui.AlignTextToFramePadding();
        var springs = _relay.SpringBalance is { } balance ? $"{balance} / {_relay.SpringCap} springs" : "… springs";
        ImGui.TextColored(Blue, springs);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Spare springs wind your partners. You get free ones on your first login each day, and more from the quest board.");
        }

        var connected = _relay.IsConnected;
        var label = connected ? "Connected" : "Offline";
        var width = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2;
        var right = ImGui.GetContentRegionMax().X;
        // The Discord icon sits just left of the connection.
        ImGui.SameLine(right - width - ImGui.GetStyle().ItemSpacing.X - ImGui.GetFrameHeight());
        DiscordLink.IconButton(_textures, "##discord_main");
        ImGui.SameLine(right - width);
        ImGui.PushStyleColor(ImGuiCol.Text, connected ? Green : Red);
        if (ImGui.SmallButton($"{label}##connection"))
            ImGui.OpenPopup("connection_popup");
        ImGui.PopStyleColor();

        if (ImGui.BeginPopup("connection_popup"))
        {
            ImGui.TextUnformatted(connected ? "Status: Online" : "Status: Offline");
            if (!connected && !string.IsNullOrEmpty(_relay.LastStatus))
            {
                ImGui.PushTextWrapPos(ImGui.GetFontSize() * 20);
                ImGui.TextWrapped(_relay.LastStatus);
                ImGui.PopTextWrapPos();
            }

            // A role preview changes nothing, the connection included.
            ImGui.BeginDisabled(_config.IsPreviewingOtherRole);
            if (ImGui.Button("Reconnect"))
            {
                _settings.Reconnect();
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndDisabled();

            ImGui.EndPopup();
        }
    }

    /// <summary>Name, role and owners, the doll's wind Moodles as icons, and partners online.</summary>
    private void DrawProfile()
    {
        ImGui.SetWindowFontScale(1.35f);
        ImGui.TextUnformatted(CharacterName());
        ImGui.SetWindowFontScale(1f);
        ImGui.TextDisabled(RoleLine());

        if (_config.ShowsAsDoll)
            DrawMoodleIcons();

        var partners = _config.PairedPartners
            .Where(p => PairingKeyUtil.IsValid(p.PartnerKey) && !string.Equals(p.PartnerKey, _config.PairingKey, StringComparison.Ordinal))
            .ToList();
        // The main window stays open, so presence is asked rarely here (each query costs both sides a message).
        foreach (var partner in partners)
            _relay.EnsurePresenceFresh(partner.PartnerKey, RelayClient.IdlePresenceAge);
        var online = partners.Count(p => _relay.GetPartnerPresence(p.PartnerKey) == PartnerPresence.Online);
        ImGui.TextUnformatted(partners.Count == 0 ? "No partners yet" : $"{online} of {partners.Count} partners online");

        DrawQuickWind(partners);
    }

    /// <summary>Dolls under this percentage of their wind are offered for a quick wind.</summary>
    private const int QuickWindBelowPercent = 75;

    private const int QuickWindMax = 4;

    /// <summary>
    /// Up to four paired dolls lowest on wind (under 75%, as last seen; not broken or on vacation), each with their
    /// percentage. Clicking one winds them with a single spring, as the Wind window's "1 spring" does.
    /// </summary>
    private void DrawQuickWind(List<PairedPartner> partners)
    {
        var low = partners
            .Select(p => (Partner: p, Key: PairingKeyUtil.Normalize(p.PartnerKey)))
            .Select(p => (p.Partner, p.Key, Wind: _relay.LastKnownWind(p.Key)))
            .Where(p => p.Wind is { Broken: false } wind && wind.Percent < QuickWindBelowPercent && !_relay.IsPartnerOnVacation(p.Key))
            .OrderBy(p => p.Wind!.Value.Percent)
            .Take(QuickWindMax)
            .ToList();
        if (low.Count == 0)
            return;

        ImGui.Spacing();
        ImGui.TextDisabled("Low on wind");
        var canWind = _relay.IsConnected && _relay.SpringBalance is not 0 && !_config.IsPreviewingOtherRole;
        if (!canWind)
            ImGui.BeginDisabled();
        foreach (var (partner, key, wind) in low)
        {
            var percent = wind!.Value.Percent;
            var status = $"{percent}%";
            if (ImGui.Selectable($"{partner.GetChosenName()}##quick_{key}"))
                _relay.Forget(_relay.SendWindByKeyAsync(key, 1), "quick wind");
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(canWind ? "Wind with 1 spring." : _config.IsPreviewingOtherRole ? "View only while previewing." : "You need a spare spring and a connection to wind.");
            ImGui.SameLine(ImGui.GetContentRegionMax().X - ImGui.CalcTextSize(status).X);
            ImGui.TextColored(percent < 25 ? Amber : Green, status);
        }

        if (!canWind)
            ImGui.EndDisabled();
    }

    private string CharacterName()
    {
        var identity = _config.LastKnownIdentity;
        if (string.IsNullOrWhiteSpace(identity))
            return "Wind-Up Key";
        var at = identity.IndexOf('@');
        return at > 0 ? identity[..at] : identity;
    }

    private string RoleLine()
    {
        if (!_config.ShowsAsDoll)
            return "Winder";

        var owners = _config.PairedPartners.Where(p => p.IsOwner).Select(p => p.GetChosenName()).ToList();
        return owners.Count == 0 ? "Doll" : $"Doll · owned by {string.Join(", ", owners)}";
    }

    /// <summary>
    /// The wind Moodles the doll has now, with their titles; hover for the description, as in Moodles. They show whether
    /// or not the Moodles plugin is installed.
    /// </summary>
    private void DrawMoodleIcons()
    {
        var icons = _moodles.CurrentIcons();
        if (icons.Count == 0)
            return;

        var size = MoodleIconSize * ImGuiHelpers.GlobalScale;
        var first = true;
        foreach (var icon in icons)
        {
            if (!first)
                ImGui.SameLine();
            first = false;
            if (_textures.GetFromGameIcon(new GameIconLookup((uint)icon.IconId)).TryGetWrap(out var wrap, out _) && wrap is not null)
                ImGui.Image(wrap.Handle, size);
            else
                ImGui.Dummy(size);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{PlainText(icon.Title)}\n{PlainText(icon.Description)}");
        }

        ImGui.SameLine();
        var y = ImGui.GetCursorPosY();
        ImGui.SetCursorPosY(y + (size.Y - ImGui.GetTextLineHeight()) / 2);
        ImGui.TextUnformatted(string.Join(" · ", icons.Select(i => PlainText(i.Title))));
    }

    /// <summary>Moodles text can carry its own colour and glow tags; they're dropped here.</summary>
    private static string PlainText(string text) => Regex.Replace(text, @"\[/?[a-zA-Z]+(=[^\]]*)?\]", string.Empty);

    /// <summary>What changes how the plugin behaves for this character right now.</summary>
    private void DrawNotices()
    {
        if (_config.ShowsAsDoll && _timer.IsOnVacation)
            ImGui.TextColored(Blue, "On vacation: your wind is frozen.");
        if (_config.ShowsAsDoll && _timer.SafewordPauseEndsAt is { } pauseEnd)
            ImGui.TextColored(Green, $"Remote control paused until {pauseEnd.ToLocalTime():g}.");
        if (_config.ShowsAsDoll && _config.HardcoreMode)
            ImGui.TextColored(Amber, "Hardcore is on.");
    }

    /// <summary>Two columns of buttons, each opening its window. Only a doll outside Hardcore sees Safeword.</summary>
    private void DrawModules()
    {
        var modules = new List<(FontAwesomeIcon Icon, string Label, Action Open)>
        {
            (FontAwesomeIcon.Key, "Wind", () => Toggle(_wind)),
            (FontAwesomeIcon.UserFriends, "Pairs", () => Toggle(_pairs)),
            (FontAwesomeIcon.ThLarge, "Quests", () => Toggle(_quests)),
        };
        // Hardcore has no safeword. Vacation and the changelog are in Settings (its Vacation section and header link).
        if (_config.ShowsAsDoll && !_config.HardcoreMode)
            modules.Add((FontAwesomeIcon.ShieldAlt, "Safeword", () => Toggle(_safeword)));

        modules.Add((FontAwesomeIcon.Cog, "Settings", () => Toggle(_settings)));

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = (ImGui.GetContentRegionAvail().X - spacing) / 2;
        for (var i = 0; i < modules.Count; i++)
        {
            if (i % 2 == 1)
                ImGui.SameLine();
            var (icon, label, open) = modules[i];
            if (ModuleButton(icon, label, width))
                open();
        }
    }

    /// <summary>
    /// An icon and a label on a button of exactly <paramref name="width"/>. Dalamud's IconButtonWithText scales the size
    /// it is given by the UI scale, which pushed the right-hand column off the window at scales above 100%.
    /// </summary>
    private static bool ModuleButton(FontAwesomeIcon icon, string label, float width)
    {
        var start = ImGui.GetCursorScreenPos();
        var height = ImGui.GetFrameHeight();
        var clicked = ImGui.Button($"##module_{label}", new Vector2(width, height));

        var drawList = ImGui.GetWindowDrawList();
        var color = ImGui.GetColorU32(ImGuiCol.Text);
        var padding = ImGui.GetStyle().FramePadding.X;
        var iconText = icon.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var iconSize = ImGui.CalcTextSize(iconText);
        drawList.AddText(new Vector2(start.X + padding, start.Y + (height - iconSize.Y) / 2), color, iconText);
        ImGui.PopFont();

        var textSize = ImGui.CalcTextSize(label);
        var textX = start.X + padding + iconSize.X + 6 * ImGuiHelpers.GlobalScale;
        drawList.AddText(new Vector2(textX, start.Y + (height - textSize.Y) / 2), color, label);
        return clicked;
    }

    private static void Toggle(Window window)
    {
        window.IsOpen = !window.IsOpen;
        if (window.IsOpen)
            window.BringToFront();
    }
}
