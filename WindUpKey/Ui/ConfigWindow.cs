using System;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using WindUpKey.Protocol;
using WindUpKey.Services;

namespace WindUpKey.Ui;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Configuration _config;
    private readonly RelayClient _relay;
    private readonly WindTimerService _timer;
    private readonly ITargetManager _targets;
    private readonly string _lowWindMessagesPath;
    private string _pairKeyDraft = string.Empty;
    private bool _hardcoreConfirm;

    public ConfigWindow(
        Configuration config,
        RelayClient relay,
        WindTimerService timer,
        ITargetManager targets,
        string lowWindMessagesPath)
        : base("Wind-Up Key###WindUpKeyConfig")
    {
        _config = config;
        _relay = relay;
        _timer = timer;
        _targets = targets;
        _lowWindMessagesPath = lowWindMessagesPath;
        Size = new Vector2(520, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        if (!_config.HasChosenRole)
        {
            DrawRoleSetup();
            return;
        }

        if (!ImGui.BeginTabBar("WindUpKeySettingsTabs"))
            return;

        if (ImGui.BeginTabItem("General"))
        {
            DrawGeneralTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Pairing"))
        {
            DrawPairingTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Safeword"))
        {
            DrawSafewordTab();
            ImGui.EndTabItem();
        }

#if WINDUP_TESTING
        if (ImGui.BeginTabItem("Testing"))
        {
            DrawTestingTab();
            ImGui.EndTabItem();
        }
#endif

        ImGui.EndTabBar();
    }

    private void DrawRoleSetup()
    {
        if (_config.HardcoreMode)
        {
            _config.Role = PlayerRole.Doll;
            _config.Save();
            _timer.Tick();
            return;
        }

        ImGui.TextUnformatted("Welcome");
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("I am a Doll", new Vector2(200, 0)))
        {
            var grantStarterWind = !_config.HasCompletedInitialSetup;
            _config.Role = PlayerRole.Doll;
            _config.HasCompletedInitialSetup = true;
            _config.Save();
            _timer.Tick();
            if (grantStarterWind)
                _timer.AddHours(24);
        }

        ImGui.Spacing();
        if (ImGui.Button("I am a Winder", new Vector2(200, 0)))
        {
            _config.Role = PlayerRole.Winder;
            _config.HasCompletedInitialSetup = true;
            _timer.SuspendDollRestrictions();
            _config.Save();
        }
    }

    private void DrawGeneralTab()
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Connection");
        ImGui.Separator();

        ImGui.TextUnformatted(_relay.IsConnected ? "Status: Online" : "Status: Offline");
        if (!_relay.IsConnected && !string.IsNullOrEmpty(_relay.LastStatus))
            ImGui.TextUnformatted(_relay.LastStatus);

        if (ImGui.Button("Reconnect"))
        {
            _config.ApplyRelayDefaults();
            _relay.Start();
        }

        if (_config.IsDoll)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("Timer");
            ImGui.Separator();

            var maxHours = (int)Math.Round(_config.MaxWindHours);
            ImGui.SetNextItemWidth(80);
            if (ImGui.InputInt("Max wind hours", ref maxHours))
                _config.MaxWindHours = Math.Clamp(maxHours, 1, 168);

            var autoSit = _config.AutoGroundSit;
            if (ImGui.Checkbox("Auto groundsit when unwound", ref autoSit))
            {
                _config.AutoGroundSit = autoSit;
                _config.Save();
            }

            ImGui.Spacing();
            if (ImGui.Button("Open messages file"))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = _lowWindMessagesPath,
                        UseShellExecute = true,
                    });
                }
                catch
                {
                    // Ignore launch failures.
                }
            }
        }

        ImGui.Spacing();
        if (ImGui.Button("Save"))
            _config.Save();

        if (!_config.HardcoreMode)
        {
            ImGui.SameLine();
            if (ImGui.Button("Change role"))
            {
                if (_config.IsDoll)
                    _timer.SuspendDollRestrictions();
                _config.Role = PlayerRole.Unset;
                _config.Save();
            }
        }
        else
        {
            ImGui.TextWrapped("Hardcore is on. You are locked as a Doll and cannot switch to Winder.");
        }
    }

    private void DrawPairingTab()
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Your pairing key");

        // Larger green key; click-to-copy hitbox matches text only.
        var pairingKey = _config.PairingKey;
        ImGui.SetWindowFontScale(1.6f);
        var keySize = ImGui.CalcTextSize(pairingKey);
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.35f, 0.92f, 0.45f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Header, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, Vector4.Zero);
        if (ImGui.Selectable(pairingKey, false, ImGuiSelectableFlags.None, keySize))
            ImGui.SetClipboardText(pairingKey);
        if (ImGui.IsItemHovered())
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        ImGui.PopStyleColor(4);
        ImGui.SetWindowFontScale(1f);

        ImGui.SetNextItemWidth(120);
        ImGui.InputText("Partner key", ref _pairKeyDraft, 16);
        ImGui.SameLine();
        if (ImGui.Button("Pair") && !string.IsNullOrWhiteSpace(_pairKeyDraft))
        {
            var key = _pairKeyDraft;
            _pairKeyDraft = string.Empty;
            _ = _relay.SubmitPairKeyAsync(key);
        }

        if (_config.PendingPartnerKeys.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("Pending");
            foreach (var pending in _config.PendingPartnerKeys)
                ImGui.BulletText(pending);
        }

        if (_config.PairedPartners.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("Paired");
            for (var i = 0; i < _config.PairedPartners.Count; i++)
            {
                var partner = _config.PairedPartners[i];
                ImGui.PushID(i);

                var header = string.IsNullOrWhiteSpace(partner.Identity)
                    ? partner.PartnerKey
                    : $"{partner.Identity}  ({partner.PartnerKey})";

                _relay.EnsurePresenceFresh(partner.PartnerKey);

                // Collapsed by default (no DefaultOpen). Stable id keeps expand state per key.
                if (!ImGui.CollapsingHeader($"{header}###pair_{partner.PartnerKey}"))
                {
                    DrawPartnerPresenceLabel(partner.PartnerKey);
                    ImGui.PopID();
                    continue;
                }

                DrawPartnerPresenceLabel(partner.PartnerKey);

                var identity = partner.Identity ?? string.Empty;
                ImGui.SetNextItemWidth(220);
                var identityChanged = ImGui.InputText("##name_world", ref identity, 96);
                ImGui.SameLine();
                ImGui.TextUnformatted("Name@World");
                ImGui.SameLine();
                if (ImGui.SmallButton("From target") && TryReadTargetIdentity(out var fromTarget))
                {
                    identity = fromTarget;
                    identityChanged = true;
                }

                if (identityChanged)
                {
                    partner.Identity = identity.Trim();
                    _config.Save();
                }

                if (_config.IsDoll)
                {
                    var canWind = partner.CanWindMe;
                    if (ImGui.Checkbox("Can wind me", ref canWind))
                    {
                        partner.CanWindMe = canWind;
                        _config.Save();
                    }

                    var canUnwind = partner.CanUnwindMe;
                    if (ImGui.Checkbox("Can unwind me", ref canUnwind))
                    {
                        partner.CanUnwindMe = canUnwind;
                        _config.Save();
                    }
                }

                ImGui.TextUnformatted("Wind");
                ImGui.SameLine();
                foreach (var hours in new[] { 1.0, 6.0, 12.0, 24.0 })
                {
                    var h = hours;
                    var label = h == 1 ? "1h" : $"{h:0}h";
                    if (ImGui.SmallButton(label))
                        _ = _relay.SendWindByKeyAsync(partner.PartnerKey, h);
                    ImGui.SameLine();
                }

                if (ImGui.SmallButton("Unwind"))
                    _ = _relay.SendUnwindByKeyAsync(partner.PartnerKey);

                ImGui.Spacing();
                var danger = new Vector4(0.75f, 0.18f, 0.18f, 1f);
                var dangerHover = new Vector4(0.88f, 0.28f, 0.28f, 1f);
                var dangerActive = new Vector4(0.60f, 0.12f, 0.12f, 1f);
                ImGui.PushStyleColor(ImGuiCol.Button, danger);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, dangerHover);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, dangerActive);
                if (ImGui.Button("Unpair"))
                    ImGui.OpenPopup("unpair_confirm");
                ImGui.PopStyleColor(3);

                var popupOpen = true;
                if (ImGui.BeginPopupModal("unpair_confirm", ref popupOpen, ImGuiWindowFlags.AlwaysAutoResize))
                {
                    ImGui.TextUnformatted($"Unpair {partner.PartnerKey}?");
                    ImGui.Spacing();
                    ImGui.PushStyleColor(ImGuiCol.Button, danger);
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, dangerHover);
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, dangerActive);
                    if (ImGui.Button("Confirm", new Vector2(100, 0)))
                    {
                        var key = partner.PartnerKey;
                        _ = _relay.UnpairByKeyAsync(key);
                        ImGui.CloseCurrentPopup();
                        i--;
                    }

                    ImGui.PopStyleColor(3);
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel", new Vector2(100, 0)))
                        ImGui.CloseCurrentPopup();

                    ImGui.EndPopup();
                }

                ImGui.PopID();
                ImGui.Spacing();
            }
        }
    }

    private void DrawPartnerPresenceLabel(string partnerKey)
    {
        ImGui.SameLine();
        var presence = _relay.GetPartnerPresence(partnerKey);
        switch (presence)
        {
            case PartnerPresence.Online:
                ImGui.TextColored(new Vector4(0.35f, 0.92f, 0.45f, 1f), "Online");
                break;
            case PartnerPresence.Offline:
                ImGui.TextColored(new Vector4(0.75f, 0.18f, 0.18f, 1f), "Offline");
                break;
            default:
                ImGui.TextDisabled("…");
                break;
        }
    }

    private bool TryReadTargetIdentity(out string identity)
    {
        identity = string.Empty;
        if (_targets.Target is not IPlayerCharacter pc)
            return false;

        var name = pc.Name.TextValue;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        string? world = null;
        try
        {
            world = pc.HomeWorld.ValueNullable?.Name.ToString();
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrEmpty(world))
            return false;

        identity = PlayerIdentity.Format(name, world);
        return true;
    }

    private void DrawSafewordTab()
    {
        ImGui.Spacing();

        if (!_config.IsDoll)
            return;

        ImGui.TextUnformatted("Safeword");
        ImGui.Separator();

        var safewordOn = _config.SafewordEnabled;
        if (ImGui.Checkbox("Enable safeword", ref safewordOn))
            _config.SafewordEnabled = safewordOn;

        var safeword = _config.Safeword;
        if (ImGui.InputText("Safeword command arg", ref safeword, 64))
            _config.Safeword = safeword;

        var safewordHours = (int)Math.Round(_config.SafewordHours);
        if (ImGui.SliderInt("Safeword hours", ref safewordHours, 1, 24))
            _config.SafewordHours = Math.Clamp(safewordHours, 1, 24);

        ImGui.Spacing();
        ImGui.TextUnformatted("Hardcore");
        ImGui.Separator();

        if (_config.HardcoreMode)
        {
            ImGui.TextWrapped("Hardcore is on. You are locked as a Doll and cannot switch to Winder.");
            ImGui.TextDisabled("Use /windup unlock to clear Hardcore.");
        }
        else if (!_hardcoreConfirm)
        {
            if (ImGui.Button("Enable Hardcore"))
                _hardcoreConfirm = true;
        }
        else
        {
            ImGui.TextWrapped("This cannot be turned off in the plugin. Continue?");
            if (ImGui.Button("Confirm Hardcore"))
            {
                _config.HardcoreMode = true;
                _config.Role = PlayerRole.Doll;
                _config.Save();
                _hardcoreConfirm = false;
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                _hardcoreConfirm = false;
        }

        ImGui.Spacing();
        if (ImGui.Button("Save"))
            _config.Save();
    }

#if WINDUP_TESTING
    private void DrawTestingTab()
    {
        ImGui.Spacing();

        if (!_config.IsDoll)
            return;

        if (ImGui.Button("Unwind"))
            _timer.UnwindForTesting();
        ImGui.SameLine();
        if (ImGui.Button("Add 1h wind"))
            _timer.AddHours(1);
    }
#endif
}
