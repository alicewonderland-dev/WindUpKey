using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using WindUpKey.Services;

namespace WindUpKey.Ui;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Configuration _config;
    private readonly RelayClient _relay;
    private readonly WindTimerService _timer;
    private string _whitelistDraft = string.Empty;
    private bool _hardcoreConfirm;

    public ConfigWindow(Configuration config, RelayClient relay, WindTimerService timer)
        : base("Wind-Up Key###WindUpKeyConfig")
    {
        _config = config;
        _relay = relay;
        _timer = timer;
        Size = new Vector2(480, 420);
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

        DrawConnection();

#if WINDUP_TESTING
        ImGui.Spacing();
        ImGui.TextDisabled("TEST BUILD: Unwind / Add wind in doll settings; right-click yourself for Wind Up (Self Test).");
#endif

        if (_config.IsWinder)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Winder mode — use the player context menu to wind someone up.");
            if (ImGui.Button("Change role…"))
            {
                _config.Role = PlayerRole.Unset;
                _config.Save();
            }

            return;
        }

        DrawDollSettings();
    }


    private void DrawRoleSetup()
    {
        // Hardcore dolls must stay dolls — never offer Winder.
        if (_config.HardcoreMode)
        {
            _config.Role = PlayerRole.Doll;
            _config.Save();
            _timer.Tick();
            return;
        }

        ImGui.TextUnformatted("Welcome");
        ImGui.Separator();
        ImGui.TextWrapped(
            "Choose your role. You can change this later from settings.");
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

        ImGui.TextDisabled("You can be wound by others, and can wind other dolls. Timer, whitelist, and safeword apply.");

        ImGui.Spacing();
        if (ImGui.Button("I am a Winder", new Vector2(200, 0)))
        {
            _config.Role = PlayerRole.Winder;
            _config.HasCompletedInitialSetup = true;
            _timer.SuspendDollRestrictions();
            _config.Save();
        }

        ImGui.TextDisabled("You wind others from the context menu. No timer or movement lock.");
    }

    private void DrawConnection()
    {
        ImGui.TextUnformatted("Connection");
        ImGui.Separator();

        ImGui.TextUnformatted(_relay.IsConnected ? "Status: Online" : "Status: Offline");
        if (!_relay.IsConnected)
        {
            var hint = string.IsNullOrEmpty(_relay.LastStatus)
                ? "Waiting for the host. Use Reconnect after the host is running."
                : _relay.LastStatus;
            ImGui.TextDisabled(hint);
        }

        if (ImGui.Button("Reconnect"))
        {
            _config.ApplyRelayDefaults();
            _relay.Start();
        }
    }

    private void DrawDollSettings()
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Timer");
        ImGui.Separator();

        var maxHours = (float)_config.MaxWindHours;
        if (ImGui.SliderFloat("Max wind hours", ref maxHours, 1f, 168f, "%.0f"))
            _config.MaxWindHours = Math.Clamp(maxHours, 1, 168);

        ImGui.TextDisabled("Remaining time is never shown to you (the doll).");

#if WINDUP_TESTING
        ImGui.Spacing();
        ImGui.TextUnformatted("Testing");
        ImGui.Separator();
        ImGui.TextDisabled("Testing build only: adjust timer without a remote wind.");
        if (ImGui.Button("Unwind"))
            _timer.UnwindForTesting();
        ImGui.SameLine();
        if (ImGui.Button("Add 1h wind"))
            _timer.AddHours(1);
#endif

        ImGui.Spacing();
        ImGui.TextUnformatted("Consent");
        ImGui.Separator();

        var whitelist = _config.WhitelistEnabled;
        if (ImGui.Checkbox("Whitelist only", ref whitelist))
            _config.WhitelistEnabled = whitelist;

        ImGui.TextDisabled("Default is global permission (anyone with the plugin can wind you).");

        if (_config.WhitelistEnabled)
        {
            ImGui.InputText("Add Name@World", ref _whitelistDraft, 128);
            ImGui.SameLine();
            if (ImGui.Button("Add") && !string.IsNullOrWhiteSpace(_whitelistDraft))
            {
                var entry = _whitelistDraft.Trim();
                if (!_config.Whitelist.Exists(x => string.Equals(x, entry, StringComparison.OrdinalIgnoreCase)))
                    _config.Whitelist.Add(entry);
                _whitelistDraft = string.Empty;
            }

            for (var i = 0; i < _config.Whitelist.Count; i++)
            {
                ImGui.PushID(i);
                ImGui.TextUnformatted(_config.Whitelist[i]);
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove"))
                {
                    _config.Whitelist.RemoveAt(i);
                    i--;
                }

                ImGui.PopID();
            }
        }

        ImGui.Spacing();
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

        ImGui.TextDisabled("Use /windup <safeword> — confirmation only; no remaining-time readout.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Hardcore");
        ImGui.Separator();

        if (_config.HardcoreMode)
        {
            ImGui.TextWrapped("Hardcore is on. You are locked as a Doll and cannot switch to Winder.");
            ImGui.TextDisabled("Use /windup unlock to clear Hardcore.");
        }
        else
        {
            ImGui.TextDisabled("Permanently lock yourself as a Doll (cannot become a Winder).");
            if (!_hardcoreConfirm)
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
        }

        ImGui.Spacing();
        if (ImGui.Button("Save"))
            _config.Save();

        if (!_config.HardcoreMode)
        {
            ImGui.SameLine();
            if (ImGui.Button("Change role…"))
            {
                _timer.SuspendDollRestrictions();
                _config.Role = PlayerRole.Unset;
                _config.Save();
            }
        }
    }
}
