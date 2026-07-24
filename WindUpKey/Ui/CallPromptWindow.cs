#if WINDUP_TESTING
using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace WindUpKey.Ui;

public enum CallPromptReason
{
    Combat,
    Busy,
    Failed,
}

/// <summary>Doll-side prompt when an owner call cannot auto-start or travel failed.</summary>
public sealed class CallPromptWindow : Window
{
    public CallPromptReason Reason { get; set; } = CallPromptReason.Combat;
    public bool CanAccept { get; set; }
    public Action? OnAccept { get; set; }

    public CallPromptWindow()
        : base("###WindUpKeyCallPrompt")
    {
        Flags = ImGuiWindowFlags.AlwaysAutoResize
                | ImGuiWindowFlags.NoCollapse
                | ImGuiWindowFlags.NoScrollbar
                | ImGuiWindowFlags.NoScrollWithMouse;
        SizeCondition = ImGuiCond.Appearing;
        IsOpen = false;
    }

    public override void Draw()
    {
        ImGui.TextUnformatted("Your presence is requested.");
        ImGui.Spacing();
        ImGui.TextWrapped(BodyFor(Reason));
        ImGui.Spacing();

        if (!CanAccept)
            ImGui.BeginDisabled();

        if (ImGui.Button("Accept", new Vector2(120, 0)))
            OnAccept?.Invoke();

        if (!CanAccept)
            ImGui.EndDisabled();
    }

    private static string BodyFor(CallPromptReason reason) => reason switch
    {
        CallPromptReason.Busy =>
            "You are already being led elsewhere. Answer when you are free to move.",
        CallPromptReason.Failed =>
            "The way was blocked. You may answer again when you are ready.",
        _ => "The fighting must end before you can answer.",
    };
}
#endif
