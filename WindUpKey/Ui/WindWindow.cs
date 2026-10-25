using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using WindUpKey.Protocol;
using WindUpKey.Services;

namespace WindUpKey.Ui;

/// <summary>
/// Winding, as its own window: the partners on the left (search, online, a doll's wind as last seen), and on the right
/// what can be done for the one selected: wind with springs, give springs, repair, ask to be wound, and unwind (set apart,
/// and still behind the doll's cost quote). It only calls the relay's existing sends. A doll's own wind is never shown,
/// except on the debug-only Yourself row.
/// </summary>
public sealed class WindWindow : Window
{
    private static readonly int[] SpringPresets = [1, 2, 3];
    private static readonly Vector4 Green = new(0.35f, 0.92f, 0.45f, 1f);
    private static readonly Vector4 Amber = new(1f, 0.75f, 0.3f, 1f);

    /// <summary>A doll under this percentage is marked low in the list.</summary>
    private const int LowPercent = 25;

    private readonly Configuration _config;
    private readonly RelayClient _relay;
    private readonly WindTimerService _timer;
    private readonly ConfigWindow _host;
    private string _search = string.Empty;
    private string? _selectedKey;
    /// <summary>The gift amount typed for each partner, as the old Pairing tab kept it (never sent to another).</summary>
    private readonly Dictionary<string, int> _giftDrafts = new(StringComparer.Ordinal);

    /// <param name="timer">Only for the debug Yourself row's bar.</param>
    /// <param name="host">Settings, for the debug self-pair the Yourself row winds.</param>
    public WindWindow(Configuration config, RelayClient relay, WindTimerService timer, ConfigWindow host)
        : base("Wind###WindUpKeyWind")
    {
        _config = config;
        _relay = relay;
        _timer = timer;
        _host = host;
        Size = new Vector2(500, 330);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>With tabs, this is drawn as the main window's Wind tab, never as its own window too.</summary>
    public override bool DrawConditions() => !_config.TabbedMainWindow;

    public override void Draw()
    {
        if (RoleGate.Blocks(_config))
            return;

        // A role preview can pick and search partners, but every action is off.
        RoleGate.DrawPreviewBanner(_config);
        DrawWindow();
    }

    private void DrawWindow()
    {
        var springs = _relay.SpringBalance is { } balance ? $"{balance} / {_relay.SpringCap} spare springs" : "… spare springs";
        ImGui.TextDisabled(springs);
        ImGui.Separator();

        var partners = Partners();
        if (partners.Length == 0)
        {
            ImGui.TextWrapped("No partners yet. Pair with someone under Pairs.");
            return;
        }

        // The actions are always for a row the list shows: a search that hides the selected partner selects the first
        // match, and one that matches nobody shows no actions.
        var shown = partners
            .Where(p => _search.Length == 0 || p.Label.Contains(_search, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (shown.Length > 0 && (_selectedKey is null || !shown.Any(p => string.Equals(p.Key, _selectedKey, StringComparison.Ordinal))))
            _selectedKey = shown[0].Key;

        var scale = ImGuiHelpers.GlobalScale;
        if (ImGui.BeginChild("##wind_list", new Vector2(190 * scale, 0), true))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##wind_search", "Search", ref _search, 32);
            foreach (var partner in shown)
                DrawListRow(partner);
            if (shown.Length == 0)
                ImGui.TextDisabled("No matches");
        }

        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild("##wind_actions", Vector2.Zero, false))
        {
            if (shown.FirstOrDefault(p => string.Equals(p.Key, _selectedKey, StringComparison.Ordinal)) is { Key: not null } selected)
            {
                ImGui.BeginDisabled(_config.IsPreviewingOtherRole);
                DrawActions(selected);
                ImGui.EndDisabled();
            }
        }

        ImGui.EndChild();
    }

    /// <param name="Self">The debug self-pair (winds and unwinds locally; debug mode only).</param>
    private readonly record struct Partner(string Key, string Label, bool Self);

    /// <summary>The paired partners (and, in debug mode, yourself), in the Pairs order.</summary>
    private Partner[] Partners()
    {
        var list = _config.PairedPartners
            .Where(p => PairingKeyUtil.IsValid(p.PartnerKey) && !string.Equals(p.PartnerKey, _config.PairingKey, StringComparison.Ordinal))
            .Select(p => new Partner(PairingKeyUtil.Normalize(p.PartnerKey), p.GetChosenName(), false))
            .ToList();
        // As the old Debug tab's Self wind: for a doll only (a winder has no wind of her own), and not in a winder preview.
        if (_config.IsDebugEnabled && _config.IsDoll && _config.ShowsAsDoll && PairingKeyUtil.IsValid(_config.PairingKey))
            list.Add(new Partner(PairingKeyUtil.Normalize(_config.PairingKey), "Yourself (debug)", true));
        return list.ToArray();
    }

    private void DrawListRow(Partner partner)
    {
        // The window can stay open: only the selected partner is kept as fresh as the Pairs window keeps them.
        var selected = string.Equals(partner.Key, _selectedKey, StringComparison.Ordinal);
        _relay.EnsurePresenceFresh(partner.Key, selected ? null : RelayClient.IdlePresenceAge);
        var mark = _config.FindPairByKey(partner.Key)?.IsOwner == true ? " (owner)"
            : _config.FindOwnedDoll(partner.Key) is not null ? " (yours)"
            : string.Empty;
        if (ImGui.Selectable($"{partner.Label}{mark}##row_{partner.Key}", selected))
            _selectedKey = partner.Key;

        var status = RowStatus(partner, out var color);
        if (status.Length == 0)
            return;
        ImGui.SameLine(ImGui.GetContentRegionMax().X - ImGui.CalcTextSize(status).X);
        ImGui.TextColored(color, status);
        if (ImGui.IsItemHovered() && _relay.LastKnownWind(partner.Key) is { } wind)
            ImGui.SetTooltip(SeenAgo(wind.SeenAt));
    }

    /// <summary>"Seen just now", "Seen 5m ago", "Seen 3h ago", "Seen 2d ago".</summary>
    internal static string SeenAgo(DateTimeOffset seenAt)
    {
        var age = DateTimeOffset.UtcNow - seenAt;
        if (age < TimeSpan.FromMinutes(1))
            return "Seen just now";
        if (age < TimeSpan.FromHours(1))
            return $"Seen {(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromDays(1))
            return $"Seen {(int)age.TotalHours}h ago";
        return $"Seen {(int)age.TotalDays}d ago";
    }

    /// <summary>The charge bar: always drawn, empty at 0% for an unwound doll.</summary>
    private static void DrawWindBar(int percent, bool broken)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        ImGui.ProgressBar(clamped / 100f, new Vector2(-1, 0), broken ? "Broken" : $"{clamped}%");
    }

    /// <summary>
    /// On the right of a row: a doll's wind as last seen, else "online" or "offline". The colour is the presence (green
    /// online, grey otherwise), or amber for a doll low or broken.
    /// </summary>
    private string RowStatus(Partner partner, out Vector4 color)
    {
        color = ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled];
        if (partner.Self)
            return string.Empty;
        if (_relay.IsPartnerOnVacation(partner.Key))
            return "away";

        var presence = _relay.GetPartnerPresence(partner.Key);
        if (presence == PartnerPresence.Online)
            color = Green;
        if (_relay.LastKnownWind(partner.Key) is { } wind)
        {
            color = wind.Broken || wind.Percent < LowPercent ? Amber : color;
            return wind.Broken ? "broken" : $"{wind.Percent}%";
        }

        return presence switch
        {
            PartnerPresence.Online => "online",
            PartnerPresence.Offline => "offline",
            _ => string.Empty,
        };
    }

    private void DrawActions(Partner partner)
    {
        ImGui.SetWindowFontScale(1.2f);
        ImGui.TextUnformatted(partner.Label);
        ImGui.SetWindowFontScale(1f);

        if (!partner.Self)
        {
            var presence = _relay.GetPartnerPresence(partner.Key) switch
            {
                PartnerPresence.Online => "Online",
                PartnerPresence.Offline => "Offline",
                _ => "…",
            };
            ImGui.TextDisabled(_relay.IsPartnerOnVacation(partner.Key) ? $"{presence} · on vacation" : presence);

            // A doll's wind as last seen: a percentage of normal wind, never a time. An unwound doll's bar is empty, at 0%.
            if (_relay.LastKnownWind(partner.Key) is { } wind)
            {
                DrawWindBar(wind.Percent, wind.Broken);
                ImGui.TextDisabled(SeenAgo(wind.SeenAt));
            }
        }
        else if (_config.IsDoll)
        {
            // Debug only (the Yourself row exists only in debug mode): your own wind as a winder would see it, so the
            // self wind and unwind can be checked. Outside debug a doll never sees this.
            DrawWindBar(_timer.PercentForWinder, _timer.IsBroken);
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Wind with springs");
        foreach (var springs in SpringPresets)
        {
            if (springs != SpringPresets[0])
                ImGui.SameLine();
            // The Yourself row winds locally as a player's wind (no springs spent), so it can test overwind and breaking.
            if (ImGui.Button($"{springs} spring{(springs == 1 ? string.Empty : "s")}##wind{springs}")
                && (!partner.Self || _host.EnsureDebugSelfPair(canWind: true, canUnwind: false)))
                _relay.Forget(_relay.SendWindByKeyAsync(partner.Key, springs), "wind");
        }

        if (!partner.Self)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("Give springs");
            ImGui.SetNextItemWidth(80);
            var gift = _giftDrafts.GetValueOrDefault(partner.Key, 1);
            if (ImGui.InputInt("##gift", ref gift))
                _giftDrafts[partner.Key] = Math.Clamp(gift, 1, SpringRules.WinderCap);
            ImGui.SameLine();
            if (ImGui.Button("Give"))
                _relay.Forget(_relay.SendSpringGiftAsync(partner.Key, Math.Clamp(gift, 1, SpringRules.WinderCap)), "spring gift");

            ImGui.Spacing();
            if (ImGui.Button("Repair"))
                _relay.Forget(_relay.SendRepairAsync(partner.Key), "repair");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Repair a doll who was broken by being wound too far. Free.");

            if (_config.ShowsAsDoll)
            {
                ImGui.SameLine();
                DrawWindRequest(partner.Key);
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        // Unwinding asks the doll for its cost first; the confirm popup shows it.
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.15f, 0.15f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.75f, 0.2f, 0.2f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.5f, 0.1f, 0.1f, 1f));
        if (ImGui.Button("Unwind") && (!partner.Self || _host.EnsureDebugSelfPair(canWind: false, canUnwind: true)))
            _relay.Forget(_relay.SendUnwindByKeyAsync(partner.Key), "unwind");
        ImGui.PopStyleColor(3);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Costs a spring per 8 hours of their wind (free for their owner). You'll see the cost first.");
    }

    /// <summary>A doll asks a partner to wind them (once an hour).</summary>
    private void DrawWindRequest(string partnerKey)
    {
        var cooldown = _relay.WindRequestCooldownRemaining;
        var available = _relay.IsConnected && cooldown <= TimeSpan.Zero;
        if (!available)
            ImGui.BeginDisabled();
        if (ImGui.Button("Ask to be wound"))
            _relay.Forget(_relay.SendWindRequestAsync(partnerKey), "wind request");
        if (!available)
            ImGui.EndDisabled();

        if (cooldown > TimeSpan.Zero)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling(cooldown.TotalMinutes));
            ImGui.SameLine();
            ImGui.TextDisabled($"in {minutes}m");
        }
    }
}
