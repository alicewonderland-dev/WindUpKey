#if WINDUP_TESTING
using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace WindUpKey.Ui;

/// <summary>What the Call prompt is showing.</summary>
public enum CallPromptReason
{
    /// <summary>07-B1: asking her, with nothing else in the way.</summary>
    Asking,

    Crafting,

    /// <summary>A duty or a zone load.</summary>
    Instance,

    Combat,

    /// <summary>Another plugin (or she herself) is driving her travel.</summary>
    Busy,

    /// <summary>Lifestream or vnavmesh isn't ready.</summary>
    NotReady,

    Failed,

    /// <summary>07-B1: the small "Answering…" window during travel.</summary>
    Travelling,
}

/// <summary>
/// Doll-side Call window: 07-B1's ask-first prompt (Accept and Decline), the failed prompt, the Hardcore information
/// prompt, and the small non-modal "Answering…" window with Stop during travel. <c>CallTravelService</c> sets
/// everything it shows each tick; its buttons only raise <see cref="OnAccept"/> and <see cref="OnDecline"/>, whose
/// handlers only set flags for the next framework tick. Esc and a close button can't dismiss it (it would only reopen
/// on the next tick).
/// </summary>
public sealed class CallPromptWindow : Window
{
    public CallPromptReason Reason { get; set; } = CallPromptReason.Asking;

    /// <summary>07-B4: the owner's local label (never payload text).</summary>
    public string OwnerLabel { get; set; } = "your owner";

    /// <summary>07-B4 (D13): the destination from Lumina names and the Call's numbers (never payload text).</summary>
    public string Destination { get; set; } = string.Empty;

    /// <summary>She has accepted; the Call waits for a gate to clear.</summary>
    public bool Accepted { get; set; }

    /// <summary>07-B1 (D5): Hardcore shows information only (no Decline or Stop).</summary>
    public bool Hardcore { get; set; }

    public bool ShowAccept { get; set; }

    /// <summary>Decline in the prompt, Stop in the travel window.</summary>
    public bool ShowDecline { get; set; }

    public Action? OnAccept { get; set; }

    /// <summary>07-B1: Decline, or Stop during travel (Stop counts as a Decline).</summary>
    public Action? OnDecline { get; set; }

    public CallPromptWindow()
        : base("###WindUpKeyCallPrompt")
    {
        Flags = ImGuiWindowFlags.AlwaysAutoResize
                | ImGuiWindowFlags.NoCollapse
                | ImGuiWindowFlags.NoScrollbar
                | ImGuiWindowFlags.NoScrollWithMouse;
        SizeCondition = ImGuiCond.Appearing;
        // 07-B1: Esc and the close button no longer dismiss the prompt only for the next tick to reopen it.
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        IsOpen = false;
    }

    public override void Draw()
    {
        if (Reason == CallPromptReason.Travelling)
        {
            // ASCII dots: the game font may lack U+2026.
            ImGui.TextUnformatted($"Answering {OwnerLabel}'s Call...");
            if (ShowDecline)
            {
                ImGui.SameLine();
                if (ImGui.Button("Stop"))
                    OnDecline?.Invoke();
            }

            return;
        }

        ImGui.TextUnformatted($"Your presence is requested: a Call from {OwnerLabel}.");
        if (!string.IsNullOrEmpty(Destination))
            ImGui.TextUnformatted($"To: {Destination}");
        ImGui.Spacing();
        ImGui.TextWrapped(BodyFor(Reason, Accepted, Hardcore));
        ImGui.Spacing();

        if (ShowAccept)
        {
            if (ImGui.Button("Accept", new Vector2(120, 0)))
                OnAccept?.Invoke();
            if (ShowDecline)
                ImGui.SameLine();
        }

        if (ShowDecline && ImGui.Button("Decline", new Vector2(120, 0)))
            OnDecline?.Invoke();
    }

    private static string BodyFor(CallPromptReason reason, bool accepted, bool hardcore)
    {
        // Accepted, or Hardcore (which goes on its own): the Call goes once the gate clears.
        var going = accepted || hardcore;
        return reason switch
        {
            CallPromptReason.Failed =>
                "The way was blocked. You may answer again when you are ready.",
            CallPromptReason.Crafting => going
                ? "You will go once you finish crafting."
                : "You must finish crafting before you can go.",
            CallPromptReason.Instance => going
                ? "You will go once you are free to leave."
                : "You must be free to leave before you can go.",
            CallPromptReason.Combat => going
                ? "You will go once the fighting ends."
                : "The fighting must end before you can go.",
            CallPromptReason.Busy =>
                "You are already being led elsewhere. Accepting ends that journey.",
            CallPromptReason.NotReady => going
                ? "You will go once Lifestream and vnavmesh are ready."
                : "Lifestream and vnavmesh aren't ready yet.",
            _ => going ? "You will go now." : "Will you answer?",
        };
    }
}
#endif
