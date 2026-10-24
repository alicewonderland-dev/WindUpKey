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
        _board = new QuestBoardTab(quests, textures);
        Size = new Vector2(620, 440);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        if (RoleGate.Blocks(_config))
            return;

        _board.Draw();
        _board.DrawInfoWindow();
    }
}
