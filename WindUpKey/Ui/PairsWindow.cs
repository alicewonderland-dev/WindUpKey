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

    public override void Draw()
    {
        _host.DropStaleConfirms();
        if (RoleGate.Blocks(_config))
            return;

        _host.SyncDebugSelfOwnership();
        _host.DrawPairsContent();
    }

    /// <summary>06-A12: closing the window dismisses the ownership confirm's refusal.</summary>
    public override void OnClose() => _host.OnPairsClosed();
}
