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
            "0.2.2.3 — Owner call (Testing)",
            [
                "Owners can Call a doll to travel near them when the doll allows it (always allowed in Hardcore).",
                "Requires Lifestream and vnavmesh on the doll. Same-data-center world travel is supported; cross-DC is not.",
                "Calls wait for crafting and duties, and show a presence prompt for combat, blocked movement, or travel failure.",
            ]),
        new(
            "0.2.2.1 — Pairing keys",
            [
                "Pairing keys are now based on your character ContentId, so they stay the same after rename, world transfer, or a config wipe.",
                "Existing pairs need to be re-added once with the new keys.",
            ]),
        new(
            "0.2.2.0 — Relay hosts",
            [
                "The plugin can reach either the Linux or Windows relay host and prefers the last one that worked.",
            ]),
        new(
            "0.2.1.0 — Pair labels",
            [
                "Pairs can have local nicknames, and dolls can assign local titles to owners.",
                "Pair-related messages use the chosen title and name.",
                "Wind charge Moodles are restored more reliably after login and area changes.",
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

            ImGui.SetNextItemOpen(i == 0, ImGuiCond.Once);
            if (!ImGui.CollapsingHeader($"{entry.Title}###changelog_{i}"))
                continue;

            foreach (var detail in entry.Details)
                ImGui.BulletText(detail);
        }
    }

    private sealed record ChangelogEntry(string Title, string[] Details);
}
