using Dalamud.Bindings.ImGui;

namespace WindUpKey.Ui;

/// <summary>
/// The pop-outs do nothing until this character has a role, as the old window and the context menu: a window left open
/// across Change role, or a switch to an alt with no role yet, shows this instead.
/// </summary>
internal static class RoleGate
{
    /// <summary>True (and draws why) when no role is chosen yet: the caller draws nothing else.</summary>
    public static bool Blocks(Configuration config)
    {
        if (config.HasChosenRole)
            return false;

        ImGui.TextWrapped("Choose a role in the main window first.");
        return true;
    }
}
