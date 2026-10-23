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
    private readonly QuestBoardTab _questTab;
    private readonly ITargetManager _targets;
    private readonly GameCommandRunner _commands;
    private readonly MoodlesWindStatusService _moodlesStatus;
    private readonly string _lowWindMessagesPath;
    private readonly ChangelogWindow _changelogWindow;
    private readonly ITextureProvider _textures;
    private readonly string _pluginVersion;

    /// <summary>The Discord's invite (shared with LookingGlass), opened by the Discord button on the About tab.</summary>
    private const string DiscordInvite = "https://discord.gg/WY6hPnrNJ3";
    private readonly Dictionary<string, string> _pairLabelDrafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OwnerSettingsDraft> _ownerDrafts = new(StringComparer.Ordinal);
    private string _pairKeyDraft = string.Empty;
    private string _relaySecretDraft = string.Empty;
    private string? _relaySecretImportError;

    /// <summary>The character the import confirmation was opened for; Confirm is refused if it changed.</summary>
    private string _relaySecretImportContentId = string.Empty;
    /// <summary>06-A7: the open Hardcore confirm, tied to the character it was opened for (null when closed).</summary>
    private CharacterBoundConfirm? _hardcoreConfirm;

    /// <summary>06-A7: the open ownership confirm, tied to the character and the partner it was opened for.</summary>
    private CharacterBoundConfirm? _ownerConfirm;
    private string _ownerConfirmLabel = string.Empty;
    private bool _ownerConfirmOpenRequested;
    private string? _ownerConfirmError;

    /// <summary>06-A12: the character <see cref="_ownerConfirmError"/> was shown for; another one dismisses it.</summary>
    private string _ownerConfirmErrorContentId = string.Empty;

    /// <summary>06-A12: the lock emote whose name is cached in <see cref="_lockEmoteName"/> (null: nothing cached yet).</summary>
    private ushort? _lockEmoteNameId;
    private string? _lockEmoteName;

    /// <summary>06-A4: the safeword being typed, until the edit ends (null when not editing).</summary>
    private string? _safewordDraft;
    private string? _pairLabelsKey;
    private Vector2 _pairLabelsPos;
    private string? _pairPermsKey;
    private Vector2 _pairPermsPos;
    private string? _pairOwnerKey;
    private Vector2 _pairOwnerPos;
    private static readonly Vector2 PairSettingsWindowOffset = new(12f, 12f);
    private static readonly int[] WindSpringPresets = [1, 2, 3];

    /// <summary>How many springs each partner's Give button sends, while editing (by partner key).</summary>
    private readonly Dictionary<string, int> _springGiftDrafts = new(StringComparer.Ordinal);

    /// <summary>Max wind hours while typed, committed when the edit ends (null when not editing).</summary>
    private int? _maxHoursDraft;

    private sealed class OwnerSettingsDraft
    {
        public double MaxHours = 72;

        /// <summary>The doll's break recovery minutes; null for a doll before protocol 6.</summary>
        public int? BreakMinutes;

        public bool AutoSit = true;
        public ushort EmoteId = CharacterProfile.GroundSitEmoteId;
        public bool Locked;
        public bool Synced;
        public long RestoreSeq;
    }

    public ConfigWindow(
        Configuration config,
        RelayClient relay,
        WindTimerService timer,
        QuestBoardService quests,
        ITargetManager targets,
        GameCommandRunner commands,
        MoodlesWindStatusService moodlesStatus,
        string lowWindMessagesPath,
        ChangelogWindow changelogWindow,
        ITextureProvider textures,
        string pluginVersion)
        : base("Wind-Up Key###WindUpKeyConfig")
    {
        _config = config;
        _relay = relay;
        _timer = timer;
        _questTab = new QuestBoardTab(quests, textures);
        _targets = targets;
        _commands = commands;
        _moodlesStatus = moodlesStatus;
        _lowWindMessagesPath = lowWindMessagesPath;
        _changelogWindow = changelogWindow;
        _textures = textures;
        _pluginVersion = pluginVersion;
        Size = new Vector2(520, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        DropStaleConfirms();

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

        var pairingDrawn = false;
        if (ImGui.BeginTabItem("Pairing"))
        {
            DrawPairingTab();
            pairingDrawn = true;
            ImGui.EndTabItem();
        }

        if (_config.IsDoll && ImGui.BeginTabItem("Safeword"))
        {
            DrawSafewordTab();
            ImGui.EndTabItem();
        }

        if (_config.HasChosenRole && ImGui.BeginTabItem("Quests"))
        {
            _questTab.Draw();
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

        // 06-A12: the ownership confirm's refusal belongs to the Pairing tab: another tab dismisses it.
        if (!pairingDrawn)
            _ownerConfirmError = null;
    }

    public override void OnClose()
    {
        // 06-A12: closing the window dismisses the ownership confirm's refusal too.
        _ownerConfirmError = null;
    }

    /// <summary>
    /// 06-A12: every frame, whichever tab is drawn. The Hardcore confirm is dropped once it no longer belongs to the
    /// active character or she is no longer a doll (the Safeword tab, which draws it, is hidden then), and the ownership
    /// confirm's refusal once another character is active.
    /// </summary>
    private void DropStaleConfirms()
    {
        if (_hardcoreConfirm is not null && !_hardcoreConfirm.StillValid(_config.ActiveContentId, _config.IsDoll, _ => true))
            _hardcoreConfirm = null;

        if (_ownerConfirmError is not null
            && !string.Equals(_ownerConfirmErrorContentId, _config.ActiveContentId, StringComparison.Ordinal))
            _ownerConfirmError = null;
    }

    private void DrawAboutTab()
    {
        ImGui.Spacing();
        var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Wind-Up Key");
        ImGui.SameLine(right - ImGui.GetFrameHeight());
        if (DiscordButton())
            Dalamud.Utility.Util.OpenLink(DiscordInvite);
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

    /// <summary>A square button with no background until hovered, showing the embedded Discord icon (as in LookingGlass).</summary>
    private bool DiscordButton()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        var clicked = ImGui.Button("##discord", new Vector2(ImGui.GetFrameHeight()));
        ImGui.PopStyleColor();

        var texture = _textures.GetFromManifestResource(typeof(ConfigWindow).Assembly, "WindUpKey.discord-symbol.png");
        if (texture.TryGetWrap(out var wrap, out _) && wrap.Width > 0 && wrap.Height > 0)
        {
            var height = ImGui.GetFontSize();
            var fitted = new Vector2(height * wrap.Width / wrap.Height, height);
            var min = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax() - fitted) / 2;
            ImGui.GetWindowDrawList().AddImage(wrap.Handle, min, min + fitted);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Join the Discord");

        return clicked;
    }

    private void DrawRoleSetup()
    {
        if (_config.HardcoreMode)
        {
            var grantStarterWind = !_config.HasCompletedInitialSetup;
            _config.Role = PlayerRole.Doll;
            _config.HasCompletedInitialSetup = true;
            _config.Save();
            _relay.ResyncRole();
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
            // Register again, so the relay applies a doll's spring cap and grants.
            _relay.ResyncRole();
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
            _relay.ResyncRole();
        }
    }

    private void CommitMaxWindHours()
    {
        if (_maxHoursDraft is not { } hours)
            return;

        _maxHoursDraft = null;
        _config.MaxWindHours = hours;
        _config.Save();
        _timer.OnMaxWindHoursChanged();
    }

    private void DrawGeneralTab()
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Connection");
        ImGui.Separator();

        ImGui.TextUnformatted(_relay.IsConnected ? "Status: Online" : "Status: Offline");
        if (!_relay.IsConnected && !string.IsNullOrEmpty(_relay.LastStatus))
            ImGui.TextWrapped(_relay.LastStatus);

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

            // 06-A4: saved when committed (Enter, focus leaving, or a +/- step), so a crash can't lose it.
            // Typed digits are held here until the edit ends: a part-typed value (1, then 10…) read as the max would break
            // the doll on a partner's wind or cap quest wind low.
            if (settingsLocked)
                _maxHoursDraft = null;
            var maxHours = _maxHoursDraft ?? (int)Math.Round(_config.MaxWindHours);
            ImGui.SetNextItemWidth(80);
            if (ImGui.InputInt("Max wind hours", ref maxHours))
            {
                _maxHoursDraft = Math.Clamp(maxHours, 1, 168);
                if (!ImGui.IsItemActive())
                    CommitMaxWindHours();
            }

            if (_maxHoursDraft is not null && !ImGui.IsItemActive())
                CommitMaxWindHours();

            // How long a break by careless winding lasts before the doll recovers by itself.
            var breakMinutes = _config.BrokenRecoveryMinutes;
            ImGui.SetNextItemWidth(80);
            if (ImGui.InputInt("Break recovery (minutes)", ref breakMinutes))
            {
                _config.BrokenRecoveryMinutes = WindLayers.ClampRecoveryMinutes(breakMinutes);
                if (!ImGui.IsItemActive())
                    _config.Save();
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
                _config.Save();

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
                // 06-A13: turning it off clears a leftover wind moodle once (on the framework thread, next tick).
                if (!moodlesOn)
                    _moodlesStatus.OnStatusTurnedOff();
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
        ImGui.TextUnformatted("Moodles");
        ImGui.Separator();
        var reapplyAutomation = _config.ReapplyMoodlesAutomationOnAreaChange;
        if (ImGui.Checkbox("Re-apply Moodles automation after area changes", ref reapplyAutomation))
        {
            _config.ReapplyMoodlesAutomationOnAreaChange = reapplyAutomation;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Moodles only runs automation on login and job change.\nThis also runs it after every zone change, so automation moodles you removed come back.");

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
        // 06-A1: show the stored emote as it is, even when this character hasn't unlocked it (Ground Sit is used then);
        // only picking another one writes.
        var emotes = _commands.GetUnlockedLoopingEmotes();
        var storedId = _config.EffectiveLockEmoteId;
        // 06-A12: the sheet lookup runs when the stored emote changes, not every frame.
        if (_lockEmoteNameId != storedId)
        {
            _lockEmoteNameId = storedId;
            _lockEmoteName = _commands.GetEmoteName(storedId);
        }

        var view = LockEmoteDisplay.Resolve(storedId, emotes, _lockEmoteName);

        DrawEmoteCombo(
            "Unwound emote",
            emotes,
            view.SelectedId,
            view.Preview,
            "lock_emote",
            id =>
            {
                if (id == _config.EffectiveLockEmoteId)
                    return;
                _config.LockEmoteId = id;
                _config.Save();
            },
            showMissingEntry: !view.Unlocked,
            missingName: view.Preview);
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

        // Spare springs: the wind currency, held by the relay.
        ImGui.Spacing();
        ImGui.TextUnformatted(_relay.SpringBalance is { } springs
            ? $"Spare springs: {springs} / {_relay.SpringCap}"
            : "Spare springs: …");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Each spare spring winds a partner for 8 hours (12 from a doll's owner). You get free springs on your " +
                "first login each day, and more for each quest board line. You can't wind yourself with them.");
        }

        DrawRelayIdentitySection();

        ImGui.Spacing();
        ImGui.SetNextItemWidth(120);
        ImGui.InputText("Partner key", ref _pairKeyDraft, 16);
        ImGui.SameLine();
        if (ImGui.Button("Pair") && !string.IsNullOrWhiteSpace(_pairKeyDraft))
        {
            var key = _pairKeyDraft;
            _pairKeyDraft = string.Empty;
            // 08-P1: a send nobody awaits goes through Forget, so a failure is logged rather than left unobserved.
            _relay.Forget(_relay.SubmitPairKeyAsync(key), "pair submit");
        }

        if (_config.PendingPartnerKeys.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("Pending");
            string? cancelPendingKey = null;
            foreach (var pending in _config.PendingPartnerKeys)
            {
                ImGui.BulletText(pending);
                ImGui.SameLine();
                ImGui.PushID($"cancel_pending_{pending}");
                if (ImGui.SmallButton("Cancel"))
                    cancelPendingKey = pending;
                ImGui.PopID();
            }

            if (cancelPendingKey is not null)
                _relay.Forget(_relay.CancelPendingPairAsync(cancelPendingKey), "pair cancel");
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

                var isOwnedDoll = _config.FindOwnedDoll(actionPartnerKey) is not null;
                var expanded = ImGui.CollapsingHeader($"{header}###pair_{partner.PartnerKey}");
                DrawPartnerPresenceLabel(partner.PartnerKey);

                if (expanded)
                {
                    if (ImGui.Button("Labels"))
                        TogglePairSettingsWindow(ref _pairLabelsKey, ref _pairLabelsPos, partner.PartnerKey);

                    if (_config.IsDoll)
                    {
                        ImGui.SameLine();
                        if (ImGui.Button("Permissions"))
                            TogglePairSettingsWindow(ref _pairPermsKey, ref _pairPermsPos, partner.PartnerKey);
                    }

                    if (isOwnedDoll)
                    {
                        ImGui.SameLine();
                        if (ImGui.Button("Owner"))
                        {
                            if (!string.Equals(_pairOwnerKey, partner.PartnerKey, StringComparison.Ordinal))
                                EnsureOwnerDraft(actionPartnerKey);
                            TogglePairSettingsWindow(ref _pairOwnerKey, ref _pairOwnerPos, partner.PartnerKey);
                        }
                    }

                    ImGui.TextUnformatted("Wind");
                    ImGui.SameLine();
                    if (!hasValidPartnerKey)
                        ImGui.BeginDisabled();
                    DrawWindSpringButtons(
                        springs => _relay.Forget(_relay.SendWindByKeyAsync(actionPartnerKey, springs), "wind"),
                        fromOwner: isOwnedDoll,
                        smallButtons: true);

                    // Unwinding asks the doll for its cost first; the confirm popup shows it.
                    if (ImGui.SmallButton("Unwind"))
                        _relay.Forget(_relay.SendUnwindByKeyAsync(actionPartnerKey), "unwind");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Costs a spring per 8 hours of their wind (free for their owner). You'll see the cost first.");

                    ImGui.SameLine();
                    if (ImGui.SmallButton("Repair"))
                        _relay.Forget(_relay.SendRepairAsync(actionPartnerKey), "repair");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Repair a doll who was broken by being wound too far. Free.");

                    ImGui.TextUnformatted("Give springs");
                    ImGui.SameLine();
                    var gift = _springGiftDrafts.TryGetValue(actionPartnerKey, out var draftGift) ? draftGift : 1;
                    ImGui.SetNextItemWidth(70);
                    if (ImGui.InputInt("##gift", ref gift))
                        _springGiftDrafts[actionPartnerKey] = Math.Clamp(gift, 1, SpringRules.WinderCap);
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Give"))
                        _relay.Forget(_relay.SendSpringGiftAsync(actionPartnerKey, Math.Clamp(gift, 1, SpringRules.WinderCap)), "spring gift");

                    if (_config.IsDoll)
                    {
                        var requestCooldown = _relay.WindRequestCooldownRemaining;
                        var requestAvailable = hasValidPartnerKey
                                               && _relay.IsConnected
                                               && requestCooldown <= TimeSpan.Zero;
                        if (!requestAvailable)
                            ImGui.BeginDisabled();
                        if (ImGui.SmallButton("Request winding"))
                            _relay.Forget(_relay.SendWindRequestAsync(actionPartnerKey), "wind request");
                        if (!requestAvailable)
                            ImGui.EndDisabled();

                        if (requestCooldown > TimeSpan.Zero)
                        {
                            var minutes = Math.Max(1, (int)Math.Ceiling(requestCooldown.TotalMinutes));
                            ImGui.SameLine();
                            ImGui.TextDisabled($"Available in {minutes}m");
                        }
                    }

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
                            _relay.Forget(_relay.UnpairByKeyAsync(actionPartnerKey), "unpair");
                            ImGui.CloseCurrentPopup();
                            i--;
                        }

                        ImGui.PopStyleColor(3);
                        ImGui.SameLine();
                        if (ImGui.Button("Cancel", new Vector2(100, 0)))
                            ImGui.CloseCurrentPopup();

                        ImGui.EndPopup();
                    }
                }

                // Keep settings windows alive even if the pair row is collapsed.
                DrawPairLabelsWindow(partner);
                if (_config.IsDoll)
                    DrawPairPermissionsWindow(partner, actionPartnerKey, hasValidPartnerKey);
                if (isOwnedDoll)
                    DrawPairOwnerWindow(actionPartnerKey, partner.PartnerKey);

                ImGui.PopID();
                ImGui.Spacing();
            }
        }

        DrawOwnerGrantConfirm();
    }

    /// <summary>
    /// 06-A7: granting ownership asks first. The modal belongs to the character and partner it was opened for: it
    /// closes without granting if another character became active, she is no longer a doll, or that partner was
    /// unpaired. Cancel (or closing it) changes nothing.
    /// </summary>
    private void DrawOwnerGrantConfirm()
    {
        const string popupId = "Make owner?###owner_grant_confirm";
        if (_ownerConfirmOpenRequested)
        {
            _ownerConfirmOpenRequested = false;
            ImGui.OpenPopup(popupId);
        }

        if (_ownerConfirmError is { } error)
            ImGui.TextColored(new Vector4(0.9f, 0.35f, 0.35f, 1f), error);

        var popupOpen = true;
        if (!ImGui.BeginPopupModal(popupId, ref popupOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            // Closed (Cancel, Confirm, its title-bar button, or never drawn since): nothing is pending any more.
            _ownerConfirm = null;
            return;
        }

        var pending = _ownerConfirm;
        if (pending?.PartnerKey is not { } partnerKey
            || !pending.StillValid(_config.ActiveContentId, _config.IsDoll, k => _config.FindPairByKey(k) is not null))
        {
            _ownerConfirmError = "The character or the pairing changed, so no owner was added.";
            _ownerConfirmErrorContentId = _config.ActiveContentId;
            _ownerConfirm = null;
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted($"Make {_ownerConfirmLabel} your owner?");
        ImGui.Spacing();
        ImGui.TextUnformatted("Owners can always wind and unwind you, and can lock your max hours and unwound emote settings.");
        ImGui.TextUnformatted("Owners can also call you to them: always while Hardcore is on,");
        ImGui.TextUnformatted("otherwise only if you allow it with Can call me.");
        ImGui.TextUnformatted("You can remove this owner by unpairing them, or every owner with /windup unlock.");
        ImGui.Spacing();
        if (ImGui.Button("Confirm", new Vector2(100, 0)))
        {
            _relay.Forget(_relay.GrantOwnerAsync(partnerKey), "owner grant");
            _ownerConfirm = null;
            _ownerConfirmError = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(100, 0)))
        {
            _ownerConfirm = null;
            _ownerConfirmError = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawRelayIdentitySection()
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Relay identity");
        ImGui.TextWrapped(
            "A private code that proves to the relay that this character owns its pairing key. " +
            "Never share it with partners. To use this character on another PC, copy it here and import it there.");

        if (_relay.AuthBlockStatus is { } blocked)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.9f, 0.35f, 0.35f, 1f));
            ImGui.TextWrapped(blocked);
            ImGui.PopStyleColor();
            // The message says to press Reconnect: same as the General tab's button.
            if (ImGui.Button("Reconnect##relay_identity_block"))
            {
                _config.ApplyRelayDefaults();
                _relay.Start();
            }
        }

        // Title screen (or the first zone load after login): the active profile may be a stale character, so don't
        // act on it. Later zone loads and teleports keep the character.
        var canManage = _relay.CanManageRelayIdentity;
        if (!canManage)
            ImGui.TextDisabled("Log in to manage your relay identity.");

        var secret = _relay.ActiveRelaySecret;
        var storeStatus = _relay.RelayIdentityStatus;
        if (canManage && string.IsNullOrEmpty(secret))
        {
            ImGui.TextWrapped(storeStatus switch
            {
                RelayIdentityStoreStatus.Unavailable =>
                    "The relay identity file can't be read or saved right now (see the Dalamud log). Retrying.",
                RelayIdentityStoreStatus.ReadOnly =>
                    "The relay identity file is from a newer version of Wind-Up Key and has no identity for this " +
                    "character. This version won't change that file; update the plugin.",
                RelayIdentityStoreStatus.NotWritable =>
                    "The relay identity file or its .lock file in pluginConfigs is read-only or not writable, so no " +
                    "identity can be created for this character. Make them writable (see the Dalamud log).",
                _ => "Created the first time this character connects.",
            });
        }
        else if (canManage && storeStatus == RelayIdentityStoreStatus.ReadOnly)
        {
            ImGui.TextDisabled("The relay identity file is from a newer version of Wind-Up Key, so it can't be changed here.");
        }
        else if (canManage && storeStatus == RelayIdentityStoreStatus.NotWritable)
        {
            ImGui.TextDisabled("The relay identity file or its .lock file is read-only, so it can't be changed here.");
        }

        if (!canManage)
            ImGui.BeginDisabled();

        if (!string.IsNullOrEmpty(secret) && ImGui.Button("Copy relay identity"))
            ImGui.SetClipboardText(secret);

        ImGui.SetNextItemWidth(220);
        ImGui.InputText("##relay_identity_import", ref _relaySecretDraft, 128, ImGuiInputTextFlags.Password);
        ImGui.SameLine();
        if (ImGui.Button("Import") && !string.IsNullOrWhiteSpace(_relaySecretDraft))
        {
            var check = _relay.CheckRelaySecretImport(_relaySecretDraft);
            if (check == RelayIdentityImport.Replace)
            {
                _relaySecretImportError = null;
                _relaySecretImportContentId = _relay.RelayIdentityContentId;
                ImGui.OpenPopup("relay_identity_import_confirm");
            }
            else
            {
                if (check == RelayIdentityImport.Unchanged)
                {
                    // Nothing to replace, but the session may not be using it (blocked, or saved by another client).
                    check = _relay.ImportRelaySecret(_relaySecretDraft, _relay.RelayIdentityContentId);
                }

                // The pasted secret isn't kept once the import is decided, whatever the outcome.
                _relaySecretDraft = string.Empty;
                _relaySecretImportError = RelayIdentityImportMessage(check);
            }
        }

        if (!canManage)
            ImGui.EndDisabled();

        // Outside the disabled block, so the modal can always be closed. It belongs to the character it was opened
        // for: if that character logged out or another one became active, it closes without importing. A zone load
        // or teleport doesn't close it.
        var popupOpen = true;
        if (ImGui.BeginPopupModal("relay_identity_import_confirm", ref popupOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var forCharacter = _relaySecretImportContentId;
            if (string.IsNullOrEmpty(forCharacter)
                || !string.Equals(_relay.RelayIdentityContentId, forCharacter, StringComparison.Ordinal))
            {
                _relaySecretImportError = "The character changed, so the relay identity wasn't imported.";
                _relaySecretDraft = string.Empty;
                _relaySecretImportContentId = string.Empty;
                ImGui.CloseCurrentPopup();
            }
            else
            {
                ImGui.TextUnformatted("Replace this character's relay identity?");
                ImGui.TextUnformatted("The current one is kept, so you can revert to it afterwards.");
                ImGui.Spacing();
                if (ImGui.Button("Confirm", new Vector2(100, 0)))
                {
                    var result = _relay.ImportRelaySecret(_relaySecretDraft, forCharacter);
                    _relaySecretImportError = result == RelayIdentityImport.Replace ? null : RelayIdentityImportMessage(result);
                    _relaySecretDraft = string.Empty;
                    _relaySecretImportContentId = string.Empty;
                    ImGui.CloseCurrentPopup();
                }

                ImGui.SameLine();
                if (ImGui.Button("Cancel", new Vector2(100, 0)))
                {
                    _relaySecretDraft = string.Empty;
                    _relaySecretImportContentId = string.Empty;
                    ImGui.CloseCurrentPopup();
                }
            }

            ImGui.EndPopup();
        }
        else if (!popupOpen)
        {
            // Closed with its title-bar button: same as Cancel.
            _relaySecretDraft = string.Empty;
            _relaySecretImportContentId = string.Empty;
        }

        // CanRevertRelaySecret is false unless a character is logged in, and while the file can't be changed (from a
        // newer build or not writable, which the text above explains, or damaged and not copied aside yet), so this
        // is hidden on the title screen and in those states.
        if (_relay.CanRevertRelaySecret && ImGui.Button("Revert to previous identity"))
        {
            _relaySecretImportError = _relay.RevertRelaySecret()
                ? null
                : "Couldn't revert the relay identity. Check the Dalamud log.";
        }

        if (_relaySecretImportError is { } importError)
            ImGui.TextColored(new Vector4(0.9f, 0.35f, 0.35f, 1f), importError);
    }

    private static string RelayIdentityImportMessage(RelayIdentityImport result) => result switch
    {
        RelayIdentityImport.Unchanged => "That is already this character's relay identity.",
        RelayIdentityImport.Reconnected => "That is already this character's saved relay identity. Reconnecting with it.",
        RelayIdentityImport.NoCharacter => "Log in with the character first, then import its relay identity.",
        RelayIdentityImport.StoreUnavailable => "Couldn't read or save the relay identity file. Check the Dalamud log.",
        RelayIdentityImport.ReadOnly =>
            "The relay identity file is from a newer version of Wind-Up Key, so it can't be changed here.",
        RelayIdentityImport.NotWritable =>
            "The relay identity file or its .lock file is read-only or not writable. Make them writable, then try again.",
        _ => "That isn't a valid relay identity.",
    };

    private void TogglePairSettingsWindow(ref string? openKey, ref Vector2 openPos, string partnerKey)
    {
        if (string.Equals(openKey, partnerKey, StringComparison.Ordinal))
        {
            openKey = null;
            return;
        }

        // Only one settings window at a time.
        _pairLabelsKey = null;
        _pairPermsKey = null;
        _pairOwnerKey = null;

        openKey = partnerKey;
        openPos = ImGui.GetMousePos() + PairSettingsWindowOffset;
    }

    private static bool BeginPairSettingsWindow(string title, string id, Vector2 appearPos, ref bool open)
    {
        ImGui.SetNextWindowPos(appearPos, ImGuiCond.Appearing);
        return ImGui.Begin($"{title}###{id}", ref open, ImGuiWindowFlags.AlwaysAutoResize);
    }

    private void DrawPairLabelsWindow(PairedPartner partner)
    {
        if (!string.Equals(_pairLabelsKey, partner.PartnerKey, StringComparison.Ordinal))
            return;

        var open = true;
        if (!BeginPairSettingsWindow("Labels", $"pair_labels_{partner.PartnerKey}", _pairLabelsPos, ref open))
        {
            ImGui.End();
            if (!open)
                _pairLabelsKey = null;
            return;
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

        ImGui.Spacing();
        if (ImGui.Button("Close", new Vector2(100, 0)))
            open = false;

        ImGui.End();
        if (!open)
            _pairLabelsKey = null;
    }

    private void DrawPairPermissionsWindow(
        PairedPartner partner,
        string actionPartnerKey,
        bool hasValidPartnerKey)
    {
        if (!string.Equals(_pairPermsKey, partner.PartnerKey, StringComparison.Ordinal))
            return;

        var open = true;
        if (!BeginPairSettingsWindow("Permissions", $"pair_perms_{partner.PartnerKey}", _pairPermsPos, ref open))
        {
            ImGui.End();
            if (!open)
                _pairPermsKey = null;
            return;
        }

        if (partner.IsOwner)
        {
            ImGui.BeginDisabled();
            var owner = true;
            ImGui.Checkbox("Owner", ref owner);
            ImGui.EndDisabled();
            // 06-A7: unpairing removes just this owner; /windup unlock is no longer the only way.
            ImGui.TextDisabled("Owners always may wind/unwind. Remove this owner by unpairing them,");
            ImGui.TextDisabled("or every owner with /windup unlock.");
        }
        else
        {
            // 06-A7: ticking it only asks; the owner is granted from the confirm (DrawOwnerGrantConfirm).
            var makeOwner = false;
            if (ImGui.Checkbox("Owner", ref makeOwner) && makeOwner && hasValidPartnerKey)
            {
                _ownerConfirm = CharacterBoundConfirm.Open(_config.ActiveContentId, actionPartnerKey);
                var label = partner.GetMessageLabel();
                _ownerConfirmLabel = string.IsNullOrWhiteSpace(label) ? actionPartnerKey : label;
                _ownerConfirmOpenRequested = _ownerConfirm is not null;
                _ownerConfirmError = null;
            }
        }

        if (partner.IsOwner)
        {
            ImGui.TextDisabled("Can wind me / Can unwind me: always allowed for owners.");
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
                    _relay.NotifyConsentChanged(partner);
                }
            }
        }
        else
        {
            var canWind = partner.CanWindMe;
            if (ImGui.Checkbox("Can wind me", ref canWind))
            {
                partner.CanWindMe = canWind;
                _relay.NotifyConsentChanged(partner);
            }

            var canUnwind = partner.CanUnwindMe;
            if (ImGui.Checkbox("Can unwind me", ref canUnwind))
            {
                partner.CanUnwindMe = canUnwind;
                _relay.NotifyConsentChanged(partner);
            }
        }

        ImGui.Spacing();
        if (ImGui.Button("Close", new Vector2(100, 0)))
            open = false;

        ImGui.End();
        if (!open)
            _pairPermsKey = null;
    }

    private void EnsureOwnerDraft(string dollKey)
    {
        if (_ownerDrafts.ContainsKey(dollKey))
            return;

        _ownerDrafts[dollKey] = new OwnerSettingsDraft();
        _relay.Forget(_relay.QueryOwnerSettingsAsync(dollKey), "owner settings query");
    }

    private void DrawPairOwnerWindow(string dollKey, string partnerKey)
    {
        if (!string.Equals(_pairOwnerKey, partnerKey, StringComparison.Ordinal))
            return;

        var open = true;
        if (!BeginPairSettingsWindow("Owner Settings", $"pair_owner_{partnerKey}", _pairOwnerPos, ref open))
        {
            ImGui.End();
            if (!open)
                _pairOwnerKey = null;
            return;
        }

        EnsureOwnerDraft(dollKey);
        var draft = _ownerDrafts[dollKey];

        var snap = _relay.GetOwnerSettings(dollKey);
        if (snap?.LastError is { Length: > 0 } err)
            ImGui.TextColored(new Vector4(0.9f, 0.35f, 0.35f, 1f), err);

        if (ImGui.Button("Refresh settings"))
        {
            draft.Synced = false;
            _relay.Forget(_relay.QueryOwnerSettingsAsync(dollKey), "owner settings query");
        }

        if (snap is not { HasData: true })
        {
            ImGui.TextDisabled("Waiting for settings (doll must be online)…");
            ImGui.Spacing();
            if (ImGui.Button("Close", new Vector2(100, 0)))
                open = false;
            ImGui.End();
            if (!open)
                _pairOwnerKey = null;
            return;
        }

        // 06-B3: a change the doll's pause refused is dropped, so the window shows what's actually set.
        if (!draft.Synced || draft.RestoreSeq != snap.RestoreSeq)
        {
            draft.MaxHours = snap.MaxWindHours;
            draft.AutoSit = snap.AutoGroundSit;
            draft.EmoteId = snap.LockEmoteId == 0 ? CharacterProfile.GroundSitEmoteId : snap.LockEmoteId;
            draft.Locked = snap.SettingsLocked;
            draft.BreakMinutes = snap.BrokenRecoveryMinutes;
            draft.Synced = true;
            draft.RestoreSeq = snap.RestoreSeq;
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
                PushOwnerSettings(dollKey, draft);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            draft.MaxHours = Math.Clamp(maxHours, 1, 168);
            PushOwnerSettings(dollKey, draft);
        }

        // Only a doll on protocol 6 reports it (older dolls can't break).
        if (draft.BreakMinutes is { } breakMinutes)
        {
            ImGui.SetNextItemWidth(80);
            if (ImGui.InputInt("Break recovery (minutes)", ref breakMinutes))
            {
                draft.BreakMinutes = WindLayers.ClampRecoveryMinutes(breakMinutes);
                if (!ImGui.IsItemActive())
                    PushOwnerSettings(dollKey, draft);
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
                PushOwnerSettings(dollKey, draft);
        }

        if (ImGui.Checkbox("Play emote when unwound", ref draft.AutoSit))
            PushOwnerSettings(dollKey, draft);

        if (draft.AutoSit)
        {
            var emoteId = draft.EmoteId;
            DrawOwnerEmoteCombo(snap.Emotes, ref emoteId);
            if (emoteId != draft.EmoteId)
            {
                draft.EmoteId = emoteId;
                PushOwnerSettings(dollKey, draft);
            }
        }

        if (draft.Locked)
            ImGui.EndDisabled();

        if (DrawOwnerLockToggle(draft.Locked))
        {
            draft.Locked = !draft.Locked;
            PushOwnerSettings(dollKey, draft, notify: false);
        }

        ImGui.TextDisabled("When locked, these settings cannot be changed until unlocked. Only owners can unlock.");

        ImGui.Spacing();
        DrawOwnerCallButton(dollKey, snap);

        ImGui.Spacing();
        if (ImGui.Button("Close", new Vector2(100, 0)))
            open = false;

        ImGui.End();
        if (!open)
            _pairOwnerKey = null;
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

    private const string CallTravelNotReadyTooltip =
        "This doll can't answer Calls yet. They need Lifestream and vnavmesh, and a current version of Wind-Up Key. Press Refresh settings to check again.";
    private const string CallLocationTooltip = "Sends your current location to this doll.";

    private void DrawOwnerCallButton(string dollKey, OwnerSettingsSnapshot? snap)
    {
        var online = _relay.GetPartnerPresence(dollKey) == PartnerPresence.Online;
        var canCall = snap is { HasData: true, CanCall: true };
        // 11-D3: from the doll's latest snapshot; older dolls (0.2.4.0) always report false. Travel plugins are
        // required only on the receiving doll, never to initiate a Call.
        var travelReady = snap is { HasData: true, TravelReady: true };
        var inWorld = Plugin.ObjectTable.LocalPlayer is not null && Plugin.ClientState.IsLoggedIn;
        var enabled = online && canCall && inWorld && _relay.IsConnected && travelReady;

        if (!enabled)
            ImGui.BeginDisabled();

        if (ImGui.Button("Call"))
            _relay.Forget(_relay.SendCallAsync(dollKey), "call");

        if (!enabled)
            ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextDisabled("?");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            if (!canCall)
                ImGui.TextUnformatted("This doll has not allowed you to call them (or Hardcore is off without Can call me).");
            else if (!online)
                ImGui.TextUnformatted("Doll must be online.");
            else if (!inWorld)
                ImGui.TextUnformatted("You must be in the world to call.");
            else if (!_relay.IsConnected)
                ImGui.TextUnformatted("Relay is not connected yet.");
            else if (!travelReady)
                ImGui.TextUnformatted(CallTravelNotReadyTooltip);
            else
                ImGui.TextUnformatted("Call the doll to travel near your current position.");
            ImGui.TextUnformatted(CallLocationTooltip);
            ImGui.EndTooltip();
        }
    }

    private void PushOwnerSettings(string dollKey, OwnerSettingsDraft draft, bool notify = true)
    {
        _relay.Forget(
            _relay.UpdateOwnerSettingsAsync(
                dollKey,
                maxWindHours: draft.MaxHours,
                autoGroundSit: draft.AutoSit,
                lockEmoteId: draft.EmoteId,
                settingsLocked: draft.Locked,
                notify: notify,
                brokenRecoveryMinutes: draft.BreakMinutes),
            "owner settings update");
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
        var selectedId = emoteId == 0 ? CharacterProfile.GroundSitEmoteId : emoteId;
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
            // 03-D12: "###" makes the ID only the part after it, so a (remote) name can never change or collide IDs.
            if (ImGui.Selectable($"{name}###{idPrefix}_{id}", isSelected))
                onSelected(id);
            if (isSelected)
                ImGui.SetItemDefaultFocus();
        }

        if (showMissingEntry && missingName is not null)
        {
            if (ImGui.Selectable($"{missingName}###{idPrefix}_missing", true))
                onSelected(selectedId);
        }

        ImGui.EndCombo();
    }

    /// <summary>1, 2 or 3 spare springs, labelled with the hours each gives (12h a spring from the doll's owner).</summary>
    private static void DrawWindSpringButtons(Action<int> onClick, bool fromOwner, bool smallButtons)
    {
        foreach (var springs in WindSpringPresets)
        {
            var label = $"{springs} ({SpringRules.WindHours(springs, fromOwner):0}h)###wind{springs}";
            var clicked = smallButtons ? ImGui.SmallButton(label) : ImGui.Button(label);
            if (clicked)
                onClick(springs);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Spend {springs} spare spring{(springs == 1 ? string.Empty : "s")}.");
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
            // 06-A4: each setting is saved as it is committed: the checkbox at once, the text and the slider when the
            // edit ends.
            var safewordOn = _config.SafewordEnabled;
            if (ImGui.Checkbox("Enable safeword", ref safewordOn))
            {
                _config.SafewordEnabled = safewordOn;
                _config.Save();
            }

            // Edited in a draft: stored trimmed when the edit ends, and an empty entry keeps the old safeword.
            var safeword = _safewordDraft ?? _config.Safeword;
            if (ImGui.InputText("Safeword (/windup safeword …)", ref safeword, 64))
                _safewordDraft = safeword;
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                _config.Safeword = SafewordText.Store(_safewordDraft, _config.Safeword);
                _config.Save();
                _safewordDraft = null;
            }
            else if (!ImGui.IsItemActive())
            {
                _safewordDraft = null;
            }

            var safewordHours = (int)Math.Round(_config.SafewordHours);
            if (ImGui.SliderInt("Safeword hours", ref safewordHours, 1, 24))
                _config.SafewordHours = Math.Clamp(safewordHours, 1, 24);
            if (ImGui.IsItemDeactivatedAfterEdit())
                _config.Save();

            DrawSafewordPause();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Hardcore");
        ImGui.Separator();

        if (_config.HardcoreMode)
        {
            ImGui.TextWrapped("Hardcore is on. You are locked as a Doll and cannot switch to Winder.");
            ImGui.TextDisabled("Use /windup unlock to begin clearing Hardcore (also clears all owners).");
        }
        else if (_hardcoreConfirm is null)
        {
            if (ImGui.Button("Enable Hardcore"))
                _hardcoreConfirm = CharacterBoundConfirm.Open(_config.ActiveContentId);
        }
        else if (!_hardcoreConfirm.StillValid(_config.ActiveContentId, _config.IsDoll, _ => true))
        {
            // 06-A7: opened for another character (or she is no longer a doll): dropped, nothing changes.
            _hardcoreConfirm = null;
        }
        else
        {
            ImGui.TextWrapped("This cannot be turned off in the plugin. Safeword will also be disabled. Continue?");
            if (ImGui.Button("Confirm Hardcore"))
            {
                _config.HardcoreMode = true;
                _config.SafewordEnabled = false;
                // 06-B1: Hardcore has no safeword, so it ends a pause too.
                _config.SafewordPausedUntilUtc = null;
                _config.Role = PlayerRole.Doll;
                _config.Save();
                _hardcoreConfirm = null;
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                _hardcoreConfirm = null;
        }

        ImGui.Spacing();
        if (ImGui.Button("Save"))
            _config.Save();
    }

    /// <summary>
    /// 06-B1: the pause of remote control that using the safeword started, when it ends, and the button that ends it
    /// early (as <c>/windup safeword resume</c> does).
    /// </summary>
    private void DrawSafewordPause()
    {
        ImGui.Spacing();
        if (_timer.SafewordPauseEndsAt is not { } pauseEnd)
        {
            ImGui.TextWrapped(SafewordPauseHelp);
            return;
        }

        ImGui.TextColored(
            new Vector4(0.45f, 0.85f, 0.45f, 1f),
            $"Remote control is paused until {pauseEnd.ToLocalTime():g}.");
        ImGui.TextWrapped(PausedRemoteControlText + " Winding still works.");
        if (ImGui.Button("End pause now"))
            _timer.ResumeRemoteControl();
    }

    private const string PausedRemoteControlText = "Partners can't unwind you, and owners can't change your settings or call you.";
    private const string SafewordPauseHelp =
        "Using the safeword also pauses remote control for the safeword hours. " + PausedRemoteControlText + " Winding still works.";

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
        ImGui.TextUnformatted("Call travel");
        // 07-A9: Draw only raises the request; the store and recall run on the next framework tick.
        if (ImGui.Button("Store"))
            _relay.RequestStoreDebugCallPoint();

        ImGui.SameLine();
        if (!_relay.HasDebugCallPoint)
            ImGui.BeginDisabled();
        if (ImGui.Button("Recall"))
            _relay.RequestRecallDebugCallPoint();
        if (!_relay.HasDebugCallPoint)
            ImGui.EndDisabled();

        ImGui.TextDisabled(_relay.HasDebugCallPoint
            ? "Recall travels to the last stored point (local; no relay)."
            : "Store a point first, then Recall acts like an owner call to that spot.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Self wind");
        // Wound locally as a player's wind (no springs are spent on yourself), so it can test overwind and breaking.
        DrawWindSpringButtons(
            springs =>
            {
                if (EnsureDebugSelfPair(canWind: true, canUnwind: false))
                    _relay.Forget(_relay.SendWindByKeyAsync(_config.PairingKey, springs), "debug self-wind");
            },
            fromOwner: false,
            smallButtons: false);

        if (ImGui.Button("Unwind") && EnsureDebugSelfPair(canWind: false, canUnwind: true))
            _relay.Forget(_relay.SendUnwindByKeyAsync(_config.PairingKey), "debug self-unwind");
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
                ConsentUpdatedUtc = DateTimeOffset.UtcNow,
            });
            _config.Save();
            _relay.QueueDollStatePush();
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
            _relay.NotifyConsentChanged(pair);
        return true;
    }
}
