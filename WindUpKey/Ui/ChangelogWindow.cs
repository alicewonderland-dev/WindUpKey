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
            "0.2.10.0 — Quest board rework",
            [
                "New Daily quest: complete 2 roulettes (the same one may count twice) for 1½ turns, a sticker and a spare spring.",
                "Easy quests now reset weekly, give 1½ turns, a sticker and a spare spring, and are dungeons and guildhests only. Easy can be rerolled once a day.",
                "Medium and Hard quests also give a spare spring.",
                "A finished line clears the whole board again.",
                "Wind is shown in turns of the key. The board's details are in its Information window; some things are yours to find out.",
                "Main scenario quests no longer have a board slot; each turn-in still winds the key.",
            ]),
        new(
            "0.2.9.0 — Quest board tuning and vacation mode",
            [
                "Hard duties were retuned.",
                "Vacation mode (General tab): for a long break, it freezes a doll's wind and lets them move freely. Winding, springs, Calls and the quest board pause until they're back.",
                "The Discord button moved to the General tab.",
            ]),
        new(
            "0.2.8.0 — Spare springs (Testing)",
            [
                "Winding now costs spare springs: each spring winds a partner for 8 hours, or 12 from a doll's owner. Dolls hold up to 10 springs and winders up to 20.",
                "You get free springs on your first login each day (winders get two), and more from the quest board. Winders can now use the quest board too.",
                "Dolls can wind other dolls with springs, and anyone can give springs to a paired player.",
                "Other players can overwind a doll past 100%. The first overwind is always safe; wind an overwound doll too far and they break until someone repairs them or they recover by themselves.",
                "Unwinding costs a spring per 8 hours of the doll's wind (free for their owner), and never takes quest overwind.",
                "The wind Moodle now follows a percentage of your wind, and winders see a percentage instead of the exact time.",
                "Everyone needs this version to wind, unwind or repair.",
            ]),
        new(
            "0.2.7.0 — Quest board (Testing)",
            [
                "The quest board replaces daily quests.",
                "Quests give stickers, and every sticker winds your key. Finish a line on the 4×4 board for bonus wind.",
                "Quest wind can take you past your max wind, up to twice it.",
                "Any daily quest in progress is dropped when you update.",
            ]),
        new(
            "0.2.6.1 — Moodle fix and Discord link",
            [
                "The wind charge Moodle is now off by default for new installs. Your current setting is kept.",
                "Fixed the wind charge Moodle staying visible to others after you turned it off or became a Winder.",
                "The About tab has a Discord button.",
            ]),
        new(
            "0.2.6.0 — Calls, daily quests and safety",
            [
                "Each character now has its own relay secret. To use another PC, copy it from the Pairing tab.",
                "Using your safeword now also pauses remote control for a while.",
                "Being wound stands you up with the game's own emote interrupt. Doze can no longer be the unwound emote, and the unwound emote is now saved per character.",
                "New Moodles option: re-apply your Moodles automation after every zone change.",
                "Calls: an owner can call their doll to them. The doll needs Lifestream and vnavmesh.",
                "A doll is asked first and can decline the Call. A Hardcore doll is not asked.",
                "Daily quests: dolls can take an Easy, Medium or Hard quest and earn winding time from duties.",
                "Many smaller fixes.",
            ]),
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
