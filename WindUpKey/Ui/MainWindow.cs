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
/// The main window (GagSpeak-style): springs and connection, who this character is, the doll's wind Moodles as icons,
/// notices, partners online, and a grid of buttons that open each feature's window. A doll never sees a time or a
/// percentage of their own wind here: only the Moodles they have.
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
    private readonly ChangelogWindow _changelog;

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
        ChangelogWindow changelog,
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
        _changelog = changelog;
        Size = new Vector2(330, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        if (!_config.HasChosenRole)
        {
            _settings.DrawRoleSetup();
            return;
        }

        DrawHeader();
        ImGui.Separator();
        ImGui.Spacing();
        DrawProfile();
        ImGui.Spacing();
        DrawNotices();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawModules();
    }

    /// <summary>Spare springs on the left, the connection on the right (click for its status and Reconnect).</summary>
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
        ImGui.SameLine(ImGui.GetContentRegionMax().X - width);
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

            if (ImGui.Button("Reconnect"))
            {
                _settings.Reconnect();
                ImGui.CloseCurrentPopup();
            }

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

        if (_config.IsDoll)
            DrawMoodleIcons();

        var partners = _config.PairedPartners
            .Where(p => PairingKeyUtil.IsValid(p.PartnerKey) && !string.Equals(p.PartnerKey, _config.PairingKey, StringComparison.Ordinal))
            .ToList();
        // The main window stays open, so presence is asked rarely here (each query costs both sides a message).
        foreach (var partner in partners)
            _relay.EnsurePresenceFresh(partner.PartnerKey, RelayClient.IdlePresenceAge);
        var online = partners.Count(p => _relay.GetPartnerPresence(p.PartnerKey) == PartnerPresence.Online);
        ImGui.TextUnformatted(partners.Count == 0 ? "No partners yet" : $"{online} of {partners.Count} partners online");
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
        if (!_config.IsDoll)
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
        if (_timer.IsOnVacation)
            ImGui.TextColored(Blue, "On vacation: your wind is frozen.");
        if (_config.IsDoll && _timer.SafewordPauseEndsAt is { } pauseEnd)
            ImGui.TextColored(Green, $"Remote control paused until {pauseEnd.ToLocalTime():g}.");
        if (_config.HardcoreMode)
            ImGui.TextColored(Amber, "Hardcore is on.");
    }

    /// <summary>Two columns of buttons, each opening its window. Winders see no Safeword or Vacation.</summary>
    private void DrawModules()
    {
        var modules = new List<(FontAwesomeIcon Icon, string Label, Action Open)>
        {
            (FontAwesomeIcon.Key, "Wind", () => Toggle(_wind)),
            (FontAwesomeIcon.UserFriends, "Pairs", () => Toggle(_pairs)),
            (FontAwesomeIcon.ThLarge, "Quests", () => Toggle(_quests)),
        };
        if (_config.IsDoll)
        {
            modules.Add((FontAwesomeIcon.ShieldAlt, "Safeword", () => Toggle(_safeword)));
            modules.Add((FontAwesomeIcon.UmbrellaBeach, "Vacation", () => _settings.Open(SettingsSection.Vacation)));
        }

        modules.Add((FontAwesomeIcon.Cog, "Settings", () => Toggle(_settings)));
        modules.Add((FontAwesomeIcon.FileAlt, "Changelog", () => Toggle(_changelog)));
        modules.Add((FontAwesomeIcon.Comments, "Discord", () => Dalamud.Utility.Util.OpenLink(DiscordLink.Invite)));

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
