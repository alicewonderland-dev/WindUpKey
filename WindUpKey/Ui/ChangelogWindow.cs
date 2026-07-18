using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace WindUpKey.Ui;

public sealed class ChangelogWindow : Window
{
    private const int MaximumVisibleEntries = 4;

    private static readonly ChangelogEntry[] Entries =
    [
        new(
            "0.2.1.0 — Pair labels",
            [
                "Pairs can have local nicknames, and dolls can assign local titles to owners.",
                "Pair-related messages use the chosen title and name.",
                "Wind charge Moodles are restored more reliably after login and area changes.",
            ]),
        new(
            "0.2.0.2 — Pairing",
            [
                "Pairing keys no longer change when you rename or transfer worlds, as long as your plugin config is kept.",
                "Winds and other partner messages can wait on the relay while someone is offline, then deliver when they return.",
                "After a config wipe with the same character name and world, the relay can restore paired partner keys. Consent still needs to be turned back on.",
            ]),
        new(
            "0.2.0.1 — Stability",
            [
                "Fixed a bug that could cause intermittent crashes or unexpected behavior.",
            ]),
        new(
            "0.2.0.0 — Commendations",
            [
                "Dolls can gain winding time after receiving commendations from a completed duty.",
                "The bonus is based on duty length. Multiple commendations award time once and change the winding sound.",
                "Added a Change Log button on the About tab.",
            ]),
    ];

    public ChangelogWindow()
        : base("Wind-Up Key Change Log###WindUpKeyChangelog")
    {
        Size = new Vector2(480, 300);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var count = Math.Min(MaximumVisibleEntries, Entries.Length);
        for (var i = 0; i < count; i++)
        {
            var entry = Entries[i];
            if (i > 0)
                ImGui.Spacing();

            ImGui.TextUnformatted(entry.Title);
            ImGui.Separator();
            foreach (var detail in entry.Details)
                ImGui.BulletText(detail);
        }
    }

    private sealed record ChangelogEntry(string Title, string[] Details);
}
