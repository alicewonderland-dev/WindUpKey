using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace WindUpKey.Ui;

/// <summary>
/// Pairing and permissions, apart from winding: the pairing key, adding a partner, pending requests, and each partner's
/// labels, permissions and owner settings. The drawing (and its confirms) lives in <see cref="ConfigWindow"/>.
/// </summary>
public sealed class PairsWindow : Window
{
    private readonly Configuration _config;
    private readonly ConfigWindow _host;

    public PairsWindow(Configuration config, ConfigWindow host)
        : base("Pairs###WindUpKeyPairs")
    {
        _config = config;
        _host = host;
        Size = new Vector2(420, 460);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>With tabs, this is drawn as the main window's Pairs tab, never as its own window too.</summary>
    public override bool DrawConditions() => !_config.TabbedMainWindow;

    public override void Draw()
    {
        _host.DropStaleConfirms();
        if (RoleGate.Blocks(_config))
            return;

        _host.SyncDebugSelfOwnership();
        // A role preview can open headers and the Labels/Permissions/Owner windows, but changes nothing.
        RoleGate.DrawPreviewBanner(_config);
        _host.DrawPairsContent(viewOnly: _config.IsPreviewingOtherRole);
    }

    /// <summary>06-A12: closing the window dismisses the ownership confirm's refusal.</summary>
    public override void OnClose() => _host.OnPairsClosed();
}
