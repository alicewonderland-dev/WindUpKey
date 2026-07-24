using System;
using System.Collections.Generic;
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
    private readonly GameCommandRunner _commands;
    private readonly string _lowWindMessagesPath;
    private readonly ChangelogWindow _changelogWindow;
    private readonly string _pluginVersion;
    private readonly Dictionary<string, string> _partnerKeyDrafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pairLabelDrafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OwnerSettingsDraft> _ownerDrafts = new(StringComparer.Ordinal);
    private string _pairKeyDraft = string.Empty;
    private bool _hardcoreConfirm;
    private static readonly double[] WindHourPresets = [1.0, 6.0, 12.0, 24.0];

    private sealed class OwnerSettingsDraft
    {
        public double MaxHours = 72;
        public bool AutoSit = true;
        public ushort EmoteId = 52;
        public bool Locked;
        public bool Synced;
    }

    public ConfigWindow(
        Configuration config,
        RelayClient relay,
        WindTimerService timer,
        ITargetManager targets,
        GameCommandRunner commands,
        string lowWindMessagesPath,
        ChangelogWindow changelogWindow,
        string pluginVersion)
        : base("Wind-Up Key###WindUpKeyConfig")
    {
        _config = config;
        _relay = relay;
        _timer = timer;
        _targets = targets;
        _commands = commands;
        _lowWindMessagesPath = lowWindMessagesPath;
        _changelogWindow = changelogWindow;
        _pluginVersion = pluginVersion;
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

        if (_config.IsDebugEnabled)
            _relay.SyncDebugSelfOwnership();

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

        if (_config.IsDoll && ImGui.BeginTabItem("Safeword"))
        {
            DrawSafewordTab();
            ImGui.EndTabItem();
        }

        if (_config.OwnedDolls.Count > 0 && ImGui.BeginTabItem("Owner"))
        {
            DrawOwnerTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("About"))
        {
            DrawAboutTab();
            ImGui.EndTabItem();
        }

        // Debug is intentionally last so conditional tabs never appear after it.
        if (_config.IsDebugEnabled && ImGui.BeginTabItem("Debug"))
        {
            DrawDebugTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawAboutTab()
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Wind-Up Key");
        ImGui.TextDisabled($"Version {_pluginVersion}");
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextWrapped(
            "A (hopefully) simple plugin. Become the wind-up doll of your dreams, " +
            "or assist dolls by helping to wind them.");
        ImGui.Spacing();

        if (ImGui.Button("Change Log"))
            _changelogWindow.IsOpen = true;
    }

    private void DrawRoleSetup()
    {
        if (_config.HardcoreMode)
        {
            var grantStarterWind = !_config.HasCompletedInitialSetup;
            _config.Role = PlayerRole.Doll;
            _config.HasCompletedInitialSetup = true;
            _config.Save();
            if (grantStarterWind)
                _timer.AddHours(24);
            else
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
            // Grant wind before syncing lock — Tick() first would lock+sit, then unlock.
            if (grantStarterWind)
                _timer.AddHours(24);
            else
                _timer.Tick();
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

            if (_config.OwnerSettingsLocked)
                ImGui.TextWrapped("An owner has locked max hours and unwound emote settings.");

            var settingsLocked = _config.OwnerSettingsLocked;
            if (settingsLocked)
                ImGui.BeginDisabled();

            var maxHours = (int)Math.Round(_config.MaxWindHours);
            ImGui.SetNextItemWidth(80);
            if (ImGui.InputInt("Max wind hours", ref maxHours))
                _config.MaxWindHours = Math.Clamp(maxHours, 1, 168);

            var autoSit = _config.AutoGroundSit;
            if (ImGui.Checkbox("Play emote when unwound", ref autoSit))
            {
                _config.AutoGroundSit = autoSit;
                _config.Save();
            }

            if (autoSit)
                DrawLockEmoteCombo();

            if (settingsLocked)
                ImGui.EndDisabled();

            var moodlesOn = _config.MoodlesStatusEnabled;
            if (ImGui.Checkbox("Show wind charge as Moodle (requires Moodles)", ref moodlesOn))
            {
                _config.MoodlesStatusEnabled = moodlesOn;
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
        ImGui.TextUnformatted("Audio");
        ImGui.Separator();
        var soundOn = _config.SoundEffectsEnabled;
        if (ImGui.Checkbox("Enable sound effects", ref soundOn))
        {
            _config.SoundEffectsEnabled = soundOn;
            _config.Save();
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

        if (_config.IsDebugOwner)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("Debug");
            ImGui.Separator();
            if (ImGui.Button(_config.DebugMode ? "Disable debug mode" : "Enable debug mode"))
            {
                _config.DebugMode = !_config.DebugMode;
                _config.Save();
            }
        }
    }

    private void DrawLockEmoteCombo()
    {
        var emotes = _commands.GetUnlockedLoopingEmotes();
        var selectedId = _config.EffectiveLockEmoteId;
        var selectedInList = TryFindEmoteName(emotes, selectedId, out var selectedName);

        if (!selectedInList)
        {
            selectedName = _commands.GetEmoteName(selectedId) ?? $"Emote #{selectedId}";
            if (selectedId != GameCommandRunner.GroundSitEmoteId)
            {
                _config.LockEmoteId = GameCommandRunner.GroundSitEmoteId;
                _config.Save();
                selectedId = GameCommandRunner.GroundSitEmoteId;
                selectedName = TryFindEmoteName(emotes, selectedId, out var fromList)
                    ? fromList
                    : (_commands.GetEmoteName(selectedId) ?? "Sit on Ground");
            }
        }

        DrawEmoteCombo(
            "Unwound emote",
            emotes,
            selectedId,
            selectedName ?? "Sit on Ground",
            "lock_emote",
            id =>
            {
                if (id == _config.EffectiveLockEmoteId)
                    return;
                _config.LockEmoteId = id;
                _config.Save();
            });
    }

    private void DrawPairingTab()
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Your pairing key");

        var pairingKey = _config.PairingKey;
        if (!PairingKeyUtil.IsValid(pairingKey))
        {
            ImGui.TextDisabled("Log in to see your pairing key.");
        }
        else
        {
            // Larger green key; click-to-copy hitbox matches text only.
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
        }

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
                var actionPartnerKey = PairingKeyUtil.Normalize(partner.PartnerKey);
                var hasValidPartnerKey = PairingKeyUtil.IsValid(actionPartnerKey);
                ImGui.PushID(i);

                var pairLabel = partner.GetMessageLabel();
                var header = string.IsNullOrWhiteSpace(pairLabel)
                             || string.Equals(pairLabel, partner.PartnerKey, StringComparison.Ordinal)
                    ? partner.PartnerKey
                    : $"{pairLabel}  ({partner.PartnerKey})";

                _relay.EnsurePresenceFresh(partner.PartnerKey);

                // Collapsed by default (no DefaultOpen). Stable id keeps expand state per key.
                if (!ImGui.CollapsingHeader($"{header}###pair_{partner.PartnerKey}"))
                {
                    DrawPartnerPresenceLabel(partner.PartnerKey);
                    ImGui.PopID();
                    continue;
                }

                DrawPartnerPresenceLabel(partner.PartnerKey);

                var draftKey = partner.PartnerKey ?? string.Empty;
                if (!_partnerKeyDrafts.TryGetValue(draftKey, out var partnerKeyDraft))
                    partnerKeyDraft = draftKey;

                ImGui.SetNextItemWidth(120);
                if (ImGui.InputText("##partner_key", ref partnerKeyDraft, 16))
                    _partnerKeyDrafts[draftKey] = partnerKeyDraft;
                else
                    _partnerKeyDrafts[draftKey] = partnerKeyDraft;

                ImGui.SameLine();
                ImGui.TextUnformatted("Pairing key");
                if (!string.Equals(
                        PairingKeyUtil.Normalize(partnerKeyDraft),
                        PairingKeyUtil.Normalize(partner.PartnerKey),
                        StringComparison.Ordinal))
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Apply key"))
                    {
                        var oldKey = partner.PartnerKey ?? string.Empty;
                        if (_relay.ReplacePartnerKey(oldKey, partnerKeyDraft))
                        {
                            _partnerKeyDrafts.Remove(oldKey);
                            var newKey = PairingKeyUtil.Normalize(partnerKeyDraft);
                            _partnerKeyDrafts[newKey] = newKey;
                        }
                        else
                        {
                            ImGui.OpenPopup("partner_key_invalid");
                        }
                    }
                }

                var keyPopup = true;
                if (ImGui.BeginPopupModal("partner_key_invalid", ref keyPopup, ImGuiWindowFlags.AlwaysAutoResize))
                {
                    ImGui.TextUnformatted("Could not update key (invalid or already paired).");
                    if (ImGui.Button("OK", new Vector2(100, 0)))
                        ImGui.CloseCurrentPopup();
                    ImGui.EndPopup();
                }

                DrawPairLabelField(
                    partner,
                    "Name@World",
                    "name_world",
                    partner.Identity,
                    partner.IsIdentitySaved,
                    96,
                    saved =>
                    {
                        partner.Identity = saved;
                        partner.IsIdentitySaved = true;
                        _config.Save();
                    },
                    allowTarget: true);

                DrawPairLabelField(
                    partner,
                    "Nickname",
                    "nickname",
                    partner.Nickname,
                    partner.IsNicknameSaved,
                    64,
                    saved =>
                    {
                        partner.Nickname = saved;
                        partner.IsNicknameSaved = true;
                        _config.Save();
                    });

                if (_config.IsDoll && partner.IsOwner)
                {
                    DrawPairLabelField(
                        partner,
                        "Title",
                        "title",
                        partner.Title,
                        partner.IsTitleSaved,
                        64,
                        saved =>
                        {
                            partner.Title = saved;
                            partner.IsTitleSaved = true;
                            _config.Save();
                        });
                }

                if (_config.IsDoll)
                {
                    if (partner.IsOwner)
                    {
                        ImGui.BeginDisabled();
                        var owner = true;
                        ImGui.Checkbox("Owner", ref owner);
                        ImGui.EndDisabled();
                        ImGui.TextDisabled("Owners always may wind/unwind. Clear with /windup unlock.");
                    }
                    else
                    {
                        var makeOwner = false;
                        if (ImGui.Checkbox("Owner", ref makeOwner) && makeOwner && hasValidPartnerKey)
                            _ = _relay.GrantOwnerAsync(actionPartnerKey);
                    }

                    if (partner.IsOwner)
                    {
                        ImGui.TextDisabled("Can wind me / Can unwind me: always allowed for owners.");
#if WINDUP_TESTING
                        if (_config.HardcoreMode)
                        {
                            ImGui.BeginDisabled();
                            var hardcoreCall = true;
                            ImGui.Checkbox("Can call me", ref hardcoreCall);
                            ImGui.EndDisabled();
                            ImGui.TextDisabled("Hardcore is on — owners may always call you.");
                        }
                        else
                        {
                            var canCall = partner.CanCallMe;
                            if (ImGui.Checkbox("Can call me", ref canCall))
                            {
                                partner.CanCallMe = canCall;
                                _config.Save();
                            }
                        }
#endif
                    }
                    else
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
                }

                ImGui.TextUnformatted("Wind");
                ImGui.SameLine();
                if (!hasValidPartnerKey)
                    ImGui.BeginDisabled();
                DrawWindHourButtons(
                    hours => _ = _relay.SendWindByKeyAsync(actionPartnerKey, hours),
                    smallButtons: true);

                if (ImGui.SmallButton("Unwind"))
                    _ = _relay.SendUnwindByKeyAsync(actionPartnerKey);

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
                if (!hasValidPartnerKey)
                    ImGui.EndDisabled();

                var popupOpen = true;
                if (ImGui.BeginPopupModal("unpair_confirm", ref popupOpen, ImGuiWindowFlags.AlwaysAutoResize))
                {
                    ImGui.TextUnformatted($"Unpair {actionPartnerKey}?");
                    ImGui.Spacing();
                    ImGui.PushStyleColor(ImGuiCol.Button, danger);
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, dangerHover);
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, dangerActive);
                    if (ImGui.Button("Confirm", new Vector2(100, 0)))
                    {
                        _ = _relay.UnpairByKeyAsync(actionPartnerKey);
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

    private void DrawPairLabelField(
        PairedPartner partner,
        string label,
        string id,
        string? value,
        bool isSaved,
        int maxLength,
        Action<string> onSave,
        bool allowTarget = false)
    {
        value ??= string.Empty;
        var draftKey = $"{partner.PartnerKey}\u001F{id}";

        ImGui.TextUnformatted($"{label}:");
        ImGui.SameLine();

        if (!isSaved)
        {
            if (!_pairLabelDrafts.TryGetValue(draftKey, out var draft))
                draft = value;

            ImGui.SetNextItemWidth(190);
            if (ImGui.InputText($"##{id}_new", ref draft, maxLength))
                _pairLabelDrafts[draftKey] = draft;
            else
                _pairLabelDrafts[draftKey] = draft;

            ImGui.SameLine();
            if (ImGui.SmallButton($"Save##{id}_new"))
            {
                onSave(draft.Trim());
                _pairLabelDrafts.Remove(draftKey);
            }

            if (allowTarget)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"From target##{id}_new") && TryReadTargetIdentity(out var fromTarget))
                    _pairLabelDrafts[draftKey] = fromTarget;
            }
        }
        else
        {
            if (string.IsNullOrEmpty(value))
                ImGui.TextDisabled("Not set");
            else
                ImGui.TextUnformatted(value);

            ImGui.SameLine();
            if (ImGui.SmallButton($"Edit##{id}"))
            {
                _pairLabelDrafts[draftKey] = value;
                ImGui.OpenPopup($"edit_{id}");
            }
        }

        if (!ImGui.BeginPopup($"edit_{id}"))
            return;

        if (!_pairLabelDrafts.TryGetValue(draftKey, out var popupDraft))
            popupDraft = value;

        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(240);
        if (ImGui.InputText($"##{id}_edit", ref popupDraft, maxLength))
            _pairLabelDrafts[draftKey] = popupDraft;
        else
            _pairLabelDrafts[draftKey] = popupDraft;

        if (allowTarget && ImGui.SmallButton($"From target##{id}_edit")
                        && TryReadTargetIdentity(out var popupTarget))
        {
            popupDraft = popupTarget;
            _pairLabelDrafts[draftKey] = popupDraft;
        }

        if (ImGui.Button($"Save##{id}_edit"))
        {
            onSave(popupDraft.Trim());
            _pairLabelDrafts.Remove(draftKey);
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button($"Cancel##{id}_edit"))
        {
            _pairLabelDrafts.Remove(draftKey);
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
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

    private void DrawOwnerTab()
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Owned dolls");
        ImGui.Separator();
        ImGui.TextWrapped("Manage max wind hours and unwound emote settings for dolls that designated you as an owner.");

        if (_config.OwnedDolls.Count == 0)
        {
            ImGui.TextDisabled("No owned dolls.");
            return;
        }

        for (var i = 0; i < _config.OwnedDolls.Count; i++)
        {
            var doll = _config.OwnedDolls[i];
            ImGui.PushID(i);
            _relay.EnsurePresenceFresh(doll.DollKey);

            var ownedPair = _config.FindPairByKey(doll.DollKey);
            var ownedLabel = ownedPair?.GetChosenName();
            if (string.IsNullOrWhiteSpace(ownedLabel))
                ownedLabel = string.IsNullOrWhiteSpace(doll.Identity) ? doll.DollKey : doll.Identity;
            var header = string.Equals(ownedLabel, doll.DollKey, StringComparison.Ordinal)
                ? doll.DollKey
                : $"{ownedLabel}  ({doll.DollKey})";

            // Collapsed by default. Stable id keeps expand state per doll.
            if (!ImGui.CollapsingHeader($"{header}###owned_{doll.DollKey}"))
            {
                DrawPartnerPresenceLabel(doll.DollKey);
                ImGui.PopID();
                continue;
            }

            DrawPartnerPresenceLabel(doll.DollKey);

            if (!_ownerDrafts.TryGetValue(doll.DollKey, out var draft))
            {
                draft = new OwnerSettingsDraft();
                _ownerDrafts[doll.DollKey] = draft;
                _ = _relay.QueryOwnerSettingsAsync(doll.DollKey);
            }

            var snap = _relay.GetOwnerSettings(doll.DollKey);
            if (snap?.LastError is { Length: > 0 } err)
                ImGui.TextColored(new Vector4(0.9f, 0.35f, 0.35f, 1f), err);

            if (ImGui.Button("Refresh settings"))
            {
                draft.Synced = false;
                _ = _relay.QueryOwnerSettingsAsync(doll.DollKey);
            }

            if (snap is not { HasData: true })
            {
                ImGui.TextDisabled("Waiting for settings (doll must be online)…");
                ImGui.PopID();
                ImGui.Spacing();
                continue;
            }

            if (!draft.Synced)
            {
                draft.MaxHours = snap.MaxWindHours;
                draft.AutoSit = snap.AutoGroundSit;
                draft.EmoteId = snap.LockEmoteId == 0 ? (ushort)52 : snap.LockEmoteId;
                draft.Locked = snap.SettingsLocked;
                draft.Synced = true;
            }

            ImGui.Spacing();
            if (draft.Locked)
                ImGui.BeginDisabled();

            var maxHours = (int)Math.Round(draft.MaxHours);
            ImGui.SetNextItemWidth(80);
            if (ImGui.InputInt("Max wind hours", ref maxHours))
            {
                draft.MaxHours = Math.Clamp(maxHours, 1, 168);
                if (!ImGui.IsItemActive())
                    PushOwnerSettings(doll.DollKey, draft);
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                draft.MaxHours = Math.Clamp(maxHours, 1, 168);
                PushOwnerSettings(doll.DollKey, draft);
            }

            if (ImGui.Checkbox("Play emote when unwound", ref draft.AutoSit))
                PushOwnerSettings(doll.DollKey, draft);

            if (draft.AutoSit)
            {
                var emoteId = draft.EmoteId;
                DrawOwnerEmoteCombo(snap.Emotes, ref emoteId);
                if (emoteId != draft.EmoteId)
                {
                    draft.EmoteId = emoteId;
                    PushOwnerSettings(doll.DollKey, draft);
                }
            }

            if (draft.Locked)
                ImGui.EndDisabled();

            if (DrawOwnerLockToggle(draft.Locked))
            {
                draft.Locked = !draft.Locked;
                PushOwnerSettings(doll.DollKey, draft, notify: false);
            }

            ImGui.TextDisabled("When locked, these settings cannot be changed until unlocked. Only owners can unlock.");

#if WINDUP_TESTING
            ImGui.Spacing();
            DrawOwnerCallButton(doll.DollKey, snap);
#endif

            ImGui.PopID();
            ImGui.Spacing();
        }
    }

#if WINDUP_TESTING
    private void DrawOwnerCallButton(string dollKey, OwnerSettingsSnapshot snap)
    {
        var online = _relay.GetPartnerPresence(dollKey) == PartnerPresence.Online;
        var canCall = snap.CanCall;
        var travelReady = snap.TravelReady;
        var inWorld = Plugin.ObjectTable.LocalPlayer is not null && Plugin.ClientState.IsLoggedIn;
        var enabled = online && canCall && travelReady && inWorld && _relay.IsConnected;

        if (!enabled)
            ImGui.BeginDisabled();

        if (ImGui.Button("Call"))
            _ = _relay.SendCallAsync(dollKey);

        if (!enabled)
            ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextDisabled("?");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            if (!travelReady)
            {
                ImGui.TextUnformatted("Requires the doll to have these plugins installed and enabled:");
                ImGui.BulletText("Lifestream");
                ImGui.BulletText("vnavmesh");
            }
            else if (!canCall)
                ImGui.TextUnformatted("This doll has not allowed you to call them (or Hardcore is off without Can call me).");
            else if (!online)
                ImGui.TextUnformatted("Doll must be online.");
            else if (!inWorld)
                ImGui.TextUnformatted("You must be in the world to call.");
            else if (!_relay.IsConnected)
                ImGui.TextUnformatted("Relay is not connected yet.");
            else
                ImGui.TextUnformatted("Call the doll to travel near your current position.");
            ImGui.EndTooltip();
        }
    }
#endif

    private void PushOwnerSettings(string dollKey, OwnerSettingsDraft draft, bool notify = true)
    {
        _ = _relay.UpdateOwnerSettingsAsync(
            dollKey,
            maxWindHours: draft.MaxHours,
            autoGroundSit: draft.AutoSit,
            lockEmoteId: draft.EmoteId,
            settingsLocked: draft.Locked,
            notify: notify);
    }

    private static bool DrawOwnerLockToggle(bool locked)
    {
        Vector4 color, hover, active;
        if (locked)
        {
            color = new Vector4(0.75f, 0.18f, 0.18f, 1f);
            hover = new Vector4(0.88f, 0.28f, 0.28f, 1f);
            active = new Vector4(0.60f, 0.12f, 0.12f, 1f);
        }
        else
        {
            color = new Vector4(0.25f, 0.65f, 0.30f, 1f);
            hover = new Vector4(0.35f, 0.78f, 0.40f, 1f);
            active = new Vector4(0.18f, 0.52f, 0.22f, 1f);
        }

        ImGui.PushStyleColor(ImGuiCol.Button, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, active);
        var clicked = ImGui.Button(locked ? "Locked" : "Unlocked");
        ImGui.PopStyleColor(3);
        return clicked;
    }

    private void DrawOwnerEmoteCombo(IReadOnlyList<(ushort Id, string Name)> emotes, ref ushort emoteId)
    {
        var selectedId = emoteId == 0 ? (ushort)52 : emoteId;
        var selectedInList = TryFindEmoteName(emotes, selectedId, out var selectedName);
        if (!selectedInList)
            selectedName = _commands.GetEmoteName(selectedId) ?? $"Emote {selectedId}";

        var chosen = selectedId;
        DrawEmoteCombo(
            "Unwound emote",
            emotes,
            selectedId,
            selectedName ?? "Select emote",
            "owner_emote",
            id => chosen = id,
            showMissingEntry: !selectedInList,
            missingName: selectedName);
        emoteId = chosen;
    }

    private static bool TryFindEmoteName(
        IReadOnlyList<(ushort Id, string Name)> emotes,
        ushort id,
        out string name)
    {
        foreach (var (eid, n) in emotes)
        {
            if (eid != id)
                continue;
            name = n;
            return true;
        }

        name = string.Empty;
        return false;
    }

    private static void DrawEmoteCombo(
        string label,
        IReadOnlyList<(ushort Id, string Name)> emotes,
        ushort selectedId,
        string preview,
        string idPrefix,
        Action<ushort> onSelected,
        bool showMissingEntry = false,
        string? missingName = null)
    {
        ImGui.SetNextItemWidth(220);
        if (!ImGui.BeginCombo(label, preview))
            return;

        foreach (var (id, name) in emotes)
        {
            var isSelected = id == selectedId;
            if (ImGui.Selectable($"{name}##{idPrefix}_{id}", isSelected))
                onSelected(id);
            if (isSelected)
                ImGui.SetItemDefaultFocus();
        }

        if (showMissingEntry && missingName is not null)
        {
            if (ImGui.Selectable($"{missingName}##{idPrefix}_missing", true))
                onSelected(selectedId);
        }

        ImGui.EndCombo();
    }

    private static void DrawWindHourButtons(Action<double> onClick, bool smallButtons)
    {
        foreach (var hours in WindHourPresets)
        {
            var label = hours == 1 ? "1h" : $"{hours:0}h";
            var clicked = smallButtons ? ImGui.SmallButton(label) : ImGui.Button(label);
            if (clicked)
                onClick(hours);
            ImGui.SameLine();
        }
    }

    private void DrawSafewordTab()
    {
        ImGui.Spacing();

        if (!_config.IsDoll)
            return;

        ImGui.TextUnformatted("Safeword");
        ImGui.Separator();

        if (_config.HardcoreMode)
        {
            ImGui.TextDisabled("Safeword is disabled while Hardcore is on.");
        }
        else
        {
            var safewordOn = _config.SafewordEnabled;
            if (ImGui.Checkbox("Enable safeword", ref safewordOn))
                _config.SafewordEnabled = safewordOn;

            var safeword = _config.Safeword;
            if (ImGui.InputText("Safeword (/windup safeword …)", ref safeword, 64))
                _config.Safeword = safeword;

            var safewordHours = (int)Math.Round(_config.SafewordHours);
            if (ImGui.SliderInt("Safeword hours", ref safewordHours, 1, 24))
                _config.SafewordHours = Math.Clamp(safewordHours, 1, 24);
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Hardcore");
        ImGui.Separator();

        if (_config.HardcoreMode)
        {
            ImGui.TextWrapped("Hardcore is on. You are locked as a Doll and cannot switch to Winder.");
            ImGui.TextDisabled("Use /windup unlock to begin clearing Hardcore (also clears all owners).");
        }
        else if (!_hardcoreConfirm)
        {
            if (ImGui.Button("Enable Hardcore"))
                _hardcoreConfirm = true;
        }
        else
        {
            ImGui.TextWrapped("This cannot be turned off in the plugin. Safeword will also be disabled. Continue?");
            if (ImGui.Button("Confirm Hardcore"))
            {
                _config.HardcoreMode = true;
                _config.SafewordEnabled = false;
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

    private void DrawDebugTab()
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Debug tools");
        ImGui.Separator();

        ImGui.Spacing();
        ImGui.TextUnformatted("Hardcore");
        var cooldownActive = _config.HardcoreLastClearedUtc is { } last
            && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(72);
        if (cooldownActive)
            ImGui.TextDisabled("Unlock cooldown is active (72h).");
        else
            ImGui.TextDisabled("Unlock cooldown is not active.");

        if (ImGui.Button("Unblock hardcore clear"))
        {
            _config.HardcoreLastClearedUtc = null;
            _config.Save();
        }

        if (!_config.IsDoll)
            return;

        ImGui.Spacing();
        ImGui.TextUnformatted("Self wind");
        DrawWindHourButtons(
            hours =>
            {
                if (EnsureDebugSelfPair(canWind: true, canUnwind: false))
                    _ = _relay.SendWindByKeyAsync(_config.PairingKey, hours);
            },
            smallButtons: false);

        if (ImGui.Button("Unwind") && EnsureDebugSelfPair(canWind: false, canUnwind: true))
            _ = _relay.SendUnwindByKeyAsync(_config.PairingKey);
    }

    /// <summary>
    /// Ensures a local self-pair exists for debug relay wind/unwind.
    /// Enables the needed consent flags so buttons work without manual checkbox toggles.
    /// </summary>
    private bool EnsureDebugSelfPair(bool canWind, bool canUnwind)
    {
        if (!PairingKeyUtil.IsValid(_config.PairingKey))
            return false;

        var key = PairingKeyUtil.Normalize(_config.PairingKey);
        var pair = _config.FindPairByKey(key);
        if (pair is null)
        {
            _config.PairedPartners.Add(new PairedPartner
            {
                Identity = string.Empty,
                PartnerKey = key,
                CanWindMe = canWind,
                CanUnwindMe = canUnwind,
            });
            _config.Save();
            return true;
        }

        var changed = false;
        if (canWind && !pair.CanWindMe)
        {
            pair.CanWindMe = true;
            changed = true;
        }

        if (canUnwind && !pair.CanUnwindMe)
        {
            pair.CanUnwindMe = true;
            changed = true;
        }

        if (changed)
            _config.Save();
        return true;
    }
}
