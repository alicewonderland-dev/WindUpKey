using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using WindUpKey.Services;

namespace WindUpKey.Ui;

/// <summary>The quest board, as its own window (with its Information pop-out).</summary>
public sealed class QuestsWindow : Window
{
    private readonly Configuration _config;
    private readonly QuestBoardTab _board;

    public QuestsWindow(Configuration config, QuestBoardService quests, ITextureProvider textures)
        : base("Quests###WindUpKeyQuests")
    {
        _config = config;
        _board = new QuestBoardTab(quests, textures, () => config.ShowsAsDoll, () => config.IsPreviewingOtherRole);
        Size = new Vector2(620, 440);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>With tabs, this is drawn as the main window's Quests tab, never as its own window too.</summary>
    public override bool DrawConditions() => !_config.TabbedMainWindow;

    public override void Draw()
    {
        if (RoleGate.Blocks(_config))
            return;

        // A role preview shows the board and its Information button; the board turns its rerolls off itself.
        RoleGate.DrawPreviewBanner(_config);
        _board.Draw();
        _board.DrawInfoWindow();
    }
}
