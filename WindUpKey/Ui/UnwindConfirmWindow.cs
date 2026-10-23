using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using WindUpKey.Services;

namespace WindUpKey.Ui;

/// <summary>
/// Step 2 of an unwind: shows what the doll quoted (spare springs, or free for their owner) and sends the paid unwind
/// on Confirm. It opens itself when a quote arrives, whether the unwind came from the Pairing tab or the context menu.
/// </summary>
public sealed class UnwindConfirmWindow : Window
{
    private readonly RelayClient _relay;

    public UnwindConfirmWindow(RelayClient relay)
        : base("Unwind###WindUpKeyUnwindConfirm", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
        _relay = relay;
        IsOpen = false;
    }

    /// <summary>The quote this window was last opened for, so closing it (Cancel or the X) never reopens it.</summary>
    private object? _shownQuote;

    /// <summary>
    /// Framework thread, each tick: opens once for each new quote. Dalamud calls <see cref="OnClose"/> only on the next
    /// draw, after this tick, so reopening on any quote still there would bring back one the player just closed.
    /// </summary>
    public void Sync()
    {
        if (_relay.LastUnwindQuote is { } quote && !ReferenceEquals(quote, _shownQuote))
        {
            _shownQuote = quote;
            IsOpen = true;
        }
    }

    public override void OnClose() => _relay.ClearUnwindQuote();

    public override void Draw()
    {
        if (_relay.LastUnwindQuote is not { } quote)
        {
            IsOpen = false;
            return;
        }

        if (quote.Free)
            ImGui.TextWrapped($"Unwind {quote.Label}? As their owner, it's free.");
        else if (quote.Springs == 0)
            ImGui.TextWrapped($"{quote.Label} has nothing left to unwind.");
        else
        {
            var plural = quote.Springs == 1 ? "spring" : "springs";
            ImGui.TextWrapped($"Unwind {quote.Label}? It costs {quote.Springs} spare {plural}.");
            if (_relay.SpringBalance is { } balance)
                ImGui.TextDisabled($"You have {balance}.");
        }

        // A cost past what the unwinder holds (more than any cap allows, for a long max with overwind) can't be paid.
        var affordable = quote.Free || _relay.SpringBalance is not { } held || held >= quote.Springs;
        if (!affordable)
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.2f, 1f), "You don't have enough spare springs to unwind them.");

        ImGui.Spacing();
        var canConfirm = (quote.Free || quote.Springs > 0) && affordable;
        if (!canConfirm)
            ImGui.BeginDisabled();
        if (ImGui.Button("Unwind", new Vector2(120, 0)))
        {
            _relay.Forget(_relay.SendPaidUnwindAsync(quote.DollKey, quote.Free ? 0 : quote.Springs), "paid unwind");
            IsOpen = false;
        }

        if (!canConfirm)
            ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120, 0)))
        {
            _relay.ClearUnwindQuote();
            IsOpen = false;
        }
    }
}
