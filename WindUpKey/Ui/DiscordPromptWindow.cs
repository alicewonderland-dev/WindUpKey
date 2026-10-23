using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace WindUpKey.Ui;

/// <summary>
/// A one-time note inviting players to the Discord, shown once after a login (any character), then never again: the
/// Discord button stays on the General tab.
/// </summary>
public sealed class DiscordPromptWindow : Window
{
    private readonly Configuration _config;
    private readonly ITextureProvider _textures;

    public DiscordPromptWindow(Configuration config, ITextureProvider textures)
        : base("Wind-Up Key###WindUpKeyDiscordPrompt", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
        _config = config;
        _textures = textures;
        IsOpen = false;
    }

    private bool _opened;

    /// <summary>
    /// Framework thread, each tick: opens once, while logged in, until it has been seen. Only once per session too:
    /// Dalamud calls <see cref="OnClose"/> on the next draw, after this tick, so a closed window must not reopen first.
    /// </summary>
    public void Sync(bool loggedIn)
    {
        if (!loggedIn || _opened || _config.DiscordPromptShown)
            return;

        _opened = true;
        IsOpen = true;
    }

    /// <summary>Closed any way (a button or the window's X): it never shows again.</summary>
    public override void OnClose() => MarkShown();

    private void MarkShown()
    {
        if (_config.DiscordPromptShown)
            return;

        _config.DiscordPromptShown = true;
        _config.Save();
    }

    public override void Draw()
    {
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 24);
        ImGui.TextWrapped(
            "Wind-Up Key has a Discord! Come by for news, testing builds, help, and other dolls and winders to pair with.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        DiscordLink.IconButton(_textures, "##discordPrompt");
        ImGui.SameLine();
        if (ImGui.Button("Join the Discord", new Vector2(150, 0)))
        {
            Dalamud.Utility.Util.OpenLink(DiscordLink.Invite);
            MarkShown();
            IsOpen = false;
        }

        ImGui.SameLine();
        if (ImGui.Button("Not now", new Vector2(100, 0)))
        {
            MarkShown();
            IsOpen = false;
        }

        ImGui.TextDisabled("The Discord button is also on the General tab.");
    }
}
