using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace WindUpKey.Ui;

/// <summary>The Discord's invite (shared with LookingGlass) and its icon button.</summary>
public static class DiscordLink
{
    public const string Invite = "https://discord.gg/WY6hPnrNJ3";

    /// <summary>
    /// A square button with no background until hovered, showing the embedded Discord icon (as in LookingGlass). Opens the
    /// invite when clicked; returns whether it was.
    /// </summary>
    public static bool IconButton(ITextureProvider textures, string id = "##discord")
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        var clicked = ImGui.Button(id, new Vector2(ImGui.GetFrameHeight()));
        ImGui.PopStyleColor();

        var texture = textures.GetFromManifestResource(typeof(DiscordLink).Assembly, "WindUpKey.discord-symbol.png");
        if (texture.TryGetWrap(out var wrap, out _) && wrap.Width > 0 && wrap.Height > 0)
        {
            var height = ImGui.GetFontSize();
            var fitted = new Vector2(height * wrap.Width / wrap.Height, height);
            var min = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax() - fitted) / 2;
            ImGui.GetWindowDrawList().AddImage(wrap.Handle, min, min + fitted);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Join the Discord");

        if (clicked)
            Dalamud.Utility.Util.OpenLink(Invite);
        return clicked;
    }
}
