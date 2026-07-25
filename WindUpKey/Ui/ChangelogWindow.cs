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
            "0.2.2.8 — Call travel pathing (Testing)",
            [
                "Call no longer walks toward the owner while still in another zone or housing division (that caused wall-running before Lifestream finished).",
                "Local vnavmesh pathing only starts after arriving in the correct ward/division and within a sane distance of the owner.",
                "Housing Calls retry via city aetheryte if Lifestream address travel stalls, and surface a chat error instead of failing silently.",
                "Aetheryte teleport waits for the cast and zone change (Teleport does not set Lifestream busy) instead of aborting after two seconds.",
                "Call input mute no longer blocks Action 5 (Teleport); that was misclassified as Jump and prevented Lifestream teleports from casting.",
                "After aetheryte arrival, open-world pathing is no longer blocked by the housing 200-yalm guard; Mount Roulette runs before vnav (flight enabled when mounted).",
                "Landing after teleport no longer cancels the Call (LocalPlayer is briefly null mid-zone-load; that is not a logout).",
                "During Call pathing, input mute no longer zeroes RMI walk/fly axes (that froze the doll after mounting while vnav tried to move).",
                "Call input mute no longer zeroes fly RMI (that cancelled aetheryte teleport while already mounted) and no longer clears vnav UserInput every frame during pathing (that caused standing jitter).",
                "After arriving in the owner's zone, Call retries vnavmesh path start for several seconds (mesh settle) and falls back to ground pathing if a flight path fails.",
                "Call pathing allows Jump during pathing so vnavmesh can take off on a flight path, waits for groundsit get-up, and prefers flight when mounted (ground fallback if fly pathfind fails).",
                "Call input mute no longer zeroes walk RMI during Lifestream housing travel (that stranded ward/aethernet navigation); vnav IgnoreUserInput tweaks apply only while pathing.",
                "Call pathing prefers flight by default (Mount Roulette, then fly path); falls back to foot pathing if mounting or flight is unavailable.",
                "Call arrival range tightened to within 2 yalms of the owner.",
                "Indoor housing Calls wait for Lifestream to enter the owner's house, then path inside (no longer treating the outdoor ward as arrived, and ward checks work in house territories).",
                "Call travel writes throttled debug lines to CallTravel.debug.log in the plugin config folder when debug mode is enabled (phase, housing snapshot, path gates, failures).",
                "Indoor Call snapshots use HouseId when HousingManager ward is -1, so house interiors attach ward/plot/outdoor territory instead of sending a bare indoor territory id.",
            ]),
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
