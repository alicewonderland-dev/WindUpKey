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
            "0.2.2.7 — Call owner plugins (Testing)",
            [
                "Calling a doll no longer requires Lifestream or vnavmesh on the owner. Only the doll answering the Call needs those plugins.",
            ]),
        new(
            "0.2.2.6 — Call cancel retry (Testing)",
            [
                "Cancelling or failing a Call now aborts leftover Lifestream/vnavmesh work so the next Call and Accept retry can start cleanly.",
            ]),
        new(
            "0.2.2.5 — Call housing travel (Testing)",
            [
                "Call travel now distinguishes housing wards and subdivisions (they share a zone id).",
                "Dolls use Lifestream housing travel to the owner's ward before pathing, so vnavmesh is not fed coordinates from the wrong ward.",
            ]),
        new(
            "0.2.2.4 — Owner grant spam (Testing)",
            [
                "Designating an owner no longer floods them with repeated ownership messages while the config window is open.",
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
