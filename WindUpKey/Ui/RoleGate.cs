using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace WindUpKey.Ui;

/// <summary>
/// The pop-outs do nothing until this character has a role, as the old window and the context menu: a window left open
/// across Change role, or a switch to an alt with no role yet, shows this instead.
/// </summary>
internal static class RoleGate
{
    private static readonly Vector4 PreviewColor = new(1f, 0.75f, 0.3f, 1f);

    /// <summary>True (and draws why) when no role is chosen yet: the caller draws nothing else.</summary>
    public static bool Blocks(Configuration config)
    {
        if (config.HasChosenRole)
            return false;

        ImGui.TextWrapped("Choose a role in the main window first.");
        return true;
    }

    /// <summary>
    /// Debug preview of the other role (<see cref="Configuration.IsPreviewingOtherRole"/>): draws the banner and disables
    /// what follows, so the preview only shows. Returns whether it began; pass that to <see cref="EndPreview"/>.
    /// </summary>
    public static bool BeginPreview(Configuration config)
    {
        if (!config.IsPreviewingOtherRole)
            return false;

        DrawPreviewBanner(config);
        ImGui.BeginDisabled();
        return true;
    }

    public static void EndPreview(bool began)
    {
        if (began)
            ImGui.EndDisabled();
    }

    /// <summary>The preview's banner alone, for a window that disables only its actions (main, Wind, Pairs, Quests).</summary>
    public static void DrawPreviewBanner(Configuration config)
    {
        if (!config.IsPreviewingOtherRole)
            return;

        var role = config.ShowsAsDoll ? "doll" : "winder";
        ImGui.TextColored(PreviewColor, $"Previewing the {role} view (debug): view only.");
    }
}
