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
#if WINDUP_TESTING
        new(
            "0.2.5.1 — Calls in housing areas (Testing)",
            [
                "Calls to an owner outside in a housing ward now switch from Lifestream to walking once you're close, so you no longer go past your owner and come back.",
            ]),
        new(
            "0.2.5.0 — Security and safety update (Testing)",
            [
                "Each character now has its own relay secret, so nobody else can pose as you on the relay. To use another PC, copy it over from the Pairing tab.",
                "The safeword now also pauses remote control (unwind, Calls and owner settings) for its duration.",
                "Being wound again stands you up with the game's own emote interrupt instead of a step forward. Doze can no longer be the unwound emote, and the unwound emote is now saved per character.",
                "Calls now ask first (Hardcore excepted), can be declined, expire, and stop if the path gets stuck.",
                "Many smaller fixes: owner grant confirmation, clearer messages when a wind doesn't go out, and safer logging.",
            ]),
        new(
            "0.2.4.4 — Moodles automation after zone changes (Testing)",
            [
                "New option under General > Moodles: re-apply your Moodles automation after every zone change.",
                "Automation moodles you removed come back on the next zone change instead of waiting for a job swap or relog.",
            ]),
        new(
            "0.2.4.3 — Second relay host (Testing)",
            [
                "Added an always-on relay host as primary, with the desktop relay as standby.",
                "Should mean fewer dropped connections when the desktop host is offline.",
            ]),
        new(
            "0.2.4.2 — Relay token rotated (Testing)",
            [
                "Rotated the shared relay secret. Update to keep pairing and winding working.",
            ]),
        new(
            "0.2.4.1 — Daily quests (Testing)",
            [
                "Dolls can accept Easy, Medium, or Hard daily quests for winding time from duty completions.",
                "Quest rewards can exceed max wind hours; a new quest cannot be accepted while already above the max.",
            ]),
#endif
        new(
            "0.2.4.0 — Connection reliability",
            [
                "Added an always-on relay host as primary, with the desktop relay as standby, for fewer dropped connections.",
                "Pair consent and remaining wind now sync through the relay, so logging in on another PC keeps your pairs and winding time.",
                "Rotated the shared relay secret; older versions can no longer connect.",
            ]),
        new(
            "0.2.3.6 — Multi-PC sync (Testing)",
            [
                "Pair consent and remaining wind are stored on the relay so logging in on another PC keeps the same pairs and winding time.",
            ]),
        new(
            "0.2.3.5 — Rewind movement fix (Testing)",
            [
                "Fixed dolls continuing to run forward after being rewound.",
            ]),
        new(
            "0.2.3.4 — Pending pair controls (Testing)",
            [
                "Pending pairing requests can now be cancelled.",
            ]),
        new(
            "0.2.3.3 — Input diagnostics (Testing)",
            [
                "Fixed corrupted input while unwound and retained diagnostic logging for confirmation.",
            ]),
        new(
            "0.2.3.0 — Wind requests",
            [
                "Dolls can request winding from a paired partner once per hour.",
                "Requests show the partner the doll's rounded wind percentage and remain queued while they are offline.",
            ]),
        new(
            "0.2.2.10 — Call improvements (Testing)",
            [
                "Numerous bug fixes and improvements regarding Owner Call handling.",
            ]),
        new(
            "0.2.2.9 — Indoor Call fixes (Testing)",
            [
                "Fixed a bug involving Calls to indoor housing locations.",
            ]),
        new(
            "0.2.2.8 — Call travel fixes (Testing)",
            [
                "Improved Owner Call travel handling.",
            ]),
        new(
            "0.2.2.7 — Call requirements (Testing)",
            [
                "Owners no longer need travel helpers to Call a doll.",
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
