using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace WindUpKey.Ui;

/// <summary>
/// The safeword, as its own window: use it, and the pause of remote control it starts (with End pause). Its setup (the
/// word, the hours, Hardcore) stays in Settings, under Safeword. Dolls only.
/// </summary>
public sealed class SafewordWindow : Window
{
    private readonly Configuration _config;
    private readonly ConfigWindow _host;
    private readonly Action<string> _useSafeword;

    /// <summary>The word as typed, cleared once used. Shown masked.</summary>
    private string _typed = string.Empty;

    /// <param name="useSafeword">The same use as <c>/windup safeword &lt;word&gt;</c> (it prints the result).</param>
    public SafewordWindow(Configuration config, ConfigWindow host, Action<string> useSafeword)
        : base("Safeword###WindUpKeySafeword", ImGuiWindowFlags.AlwaysAutoResize)
    {
        _config = config;
        _host = host;
        _useSafeword = useSafeword;
    }

    public override void OnClose() => _typed = string.Empty;

    public override void Draw()
    {
        if (RoleGate.Blocks(_config))
            return;

        if (!_config.IsDoll)
        {
            ImGui.TextWrapped("The safeword is for dolls.");
            return;
        }

        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 22);
        if (_config.HardcoreMode)
        {
            // Hardcore has no safeword and no pause, so nothing of either is shown.
            ImGui.TextWrapped("Safeword is disabled while Hardcore is on.");
        }
        else
        {
            if (!_config.SafewordEnabled)
                ImGui.TextWrapped("The safeword is off. Turn it on in Settings, under Safeword.");
            else
                DrawUse();

            _host.DrawSafewordPause();
        }

        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        if (ImGui.Button("Safeword settings"))
            _host.Open(SettingsSection.Safeword);
    }

    private void DrawUse()
    {
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12);
        var entered = ImGui.InputTextWithHint(
            "##safeword_use", "Type your safeword", ref _typed, 64, ImGuiInputTextFlags.Password | ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        var clicked = ImGui.Button("Use safeword");
        if ((entered || clicked) && _typed.Trim().Length > 0)
        {
            _useSafeword(_typed);
            _typed = string.Empty;
        }
    }
}
