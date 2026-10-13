#if WINDUP_TESTING || WINDUP_SELFTEST_UNITS
using System;
using System.Collections.Generic;
using System.Linq;

namespace WindUpKey.SelfTest;

/// <summary>How much of a check the self-test does (04-D4).</summary>
public enum CheckCoverage
{
    /// <summary>Fully automated.</summary>
    Full,

    /// <summary>The automated part runs; the rest is reported as MANUAL steps.</summary>
    Partial,

    /// <summary>Runs only through an opt-in disruptive verb with <c>confirm</c> (04-D7).</summary>
    OptIn,

    /// <summary>Only a person can do it: reported as MANUAL with exact steps.</summary>
    Manual,

    /// <summary>Not on the checklist: an extra state check (planning §5).</summary>
    Extra,

    /// <summary>Post-patch health (04-D8).</summary>
    PostPatch,
}

/// <summary>One catalog entry: what is checked, how, and the exact manual steps for what isn't automated.</summary>
public sealed record CatalogEntry(
    string Id,
    string Title,
    CheckCoverage Coverage,
    string Automated,
    IReadOnlyList<string> ManualSteps,
    string? OptInVerb = null,
    string? Restore = null);

/// <summary>
/// Every check the self-test reports, as data (Architecture): the 19 items of <c>plans/IN-GAME-CHECKS.md</c> in
/// checklist order with the planning report's classification (04-D4), then the extra checks (planning §5), the opt-in
/// verbs (04-D7) and the post-patch checks (04-D8). Every entry with a manual part has exact steps; every opt-in has a
/// restore.
/// </summary>
public static class SelfTestCatalog
{
    private const string Rerun = "Then run /windup selftest again.";

    public static readonly IReadOnlyList<CatalogEntry> Checklist =
    [
        new("01-1", "First connect", CheckCoverage.Partial,
            "The identity file is next to the WindUpKey folder with an entry for this character that records its pairing key, and the register was accepted.",
            [
                "On the relay host, read /stats (with the token) and note \"accounts\".",
                "Log into a character that has never connected to this relay (or after reset-account for it).",
                "Read /stats again: \"accounts\" went up by exactly 1.",
            ]),
        new("01-2", "Existing pairs survive", CheckCoverage.Partial,
            "Every local partner is in the relay's pairSync, and presence queries to partners are answered.",
            [
                "With a partner online on the new build: wind them, then have them wind you; each side sees the remaining time.",
                "Unwind each other the same way.",
                "As owner, open the doll's settings in the Owner tab, change one value, and check the doll gets it.",
            ]),
        new("01-3", "Old client still works", CheckCoverage.Manual,
            "Never automated: it would need a secretless register, which the self-test never sends (04-D5).",
            [
                "A partner on the current Release build (an unclaimed key) pairs with you.",
                "Wind them and have them wind you; both work.",
            ]),
        new("01-4", "Relay identity section (Pairing tab)", CheckCoverage.Partial,
            "Copy has a valid identity to give, and importing the current value is recognised as already this character's.",
            [
                "Pairing tab, Relay identity: read the text.",
                "Press Copy; paste into a text editor: one line of 43 characters (don't share it).",
                "Paste it into Import and press Import: it says it's already this character's identity.",
                "Import a different valid identity (from another character's Copy): a confirmation appears, then it reconnects.",
                "Revert appears; press it: it switches back and reconnects.",
            ]),
        new("01-5", "Second PC (or a second Dalamud profile)", CheckCoverage.Manual,
            "Needs a second client.",
            [
                "On a second PC or Dalamud profile, log into the same character before importing.",
                "It shows the \"identity rejected\" message once in chat and doesn't keep reconnecting.",
                "Copy the identity on the first PC and import it on the second: it connects.",
            ]),
        new("01-6", "Reconnect button", CheckCoverage.Manual,
            "The UI can't be read by the self-test. /windup selftest reconnect confirm runs the button's code path.",
            [
                "Get a block message (for example log into the same character on a second client).",
                "In the Pairing tab the Reconnect button sits next to the block message; press it: it reconnects.",
            ]),
        new("01-7", "Character switch", CheckCoverage.Manual,
            "Needs a logout and a second character.",
            [
                "Open the import confirmation, then log out and into another character.",
                "That character connects with its own identity, and the import dialog is gone.",
            ]),
        new("01-8", "Zone load or teleport while the import dialog is open", CheckCoverage.Manual,
            "Needs the dialog open during a zone load.",
            [
                "Open the import confirmation, then teleport or change zone.",
                "The dialog is still open afterwards.",
            ]),
        new("01-9", "Unreachable relay", CheckCoverage.OptIn,
            "/windup selftest outage confirm holds the connection in memory, checks locks are suspended after 60 s, then reconnects.",
            [
                "Run /windup selftest outage confirm while unwound as a doll (about 110 s).",
                "Or stop the relay by hand: after about 60 s, movement locks on an unwound doll are suspended.",
            ],
            OptInVerb: "outage",
            Restore: "The hold is lifted and the client reconnects (Start), in finally; if it doesn't reconnect, press Reconnect."),
        new("01-10", "Dalamud \"reset plugin data\"", CheckCoverage.Partial,
            "The identity file is outside pluginConfigs/WindUpKey/, which reset deletes.",
            [
                "Dalamud plugin installer: Wind-Up Key, reset plugin data.",
                "The file pluginConfigs/WindUpKey.relay-identity.json is still there.",
                "The character reconnects with the same identity (no block message).",
            ]),
        new("01-11", "Release <-> Testing switch", CheckCoverage.Partial,
            "The identity is in its own file, outside the plugin config, at a version this build reads.",
            [
                "Switch Wind-Up Key to the Release channel, log in, then back to Testing.",
                "Both connect without a block message (the identity is kept).",
            ]),
        new("01-12", "Funnel (Authorization header)", CheckCoverage.Full,
            "Through the compiled Funnel host: an upgrade without the token gets 401, with the Bearer header 101; /stats without and with it 401 and 200.",
            []),
        new("02-1", "Sync notices", CheckCoverage.Manual,
            "Needs another PC making the change.",
            [
                "On another PC, add a partner, or turn on owner or call rights for one.",
                "This PC prints one chat line per change.",
            ]),
        new("02-2", "Partner presence", CheckCoverage.Partial,
            "Presence queries to up to 10 partners are answered, and a random unpaired key looks offline.",
            [
                "Pairing tab: each partner shows online when they're logged in and offline when not.",
            ]),
        new("02-3", "Offline unpair", CheckCoverage.Partial,
            "No queued unpair is left pending once the register was accepted.",
            [
                "Stop the relay (or disconnect), unpair a partner, then start the relay (reconnect).",
                "The partner disappears on both sides and doesn't come back after a relog.",
            ]),
        new("02-4", "Owner unpairs a doll", CheckCoverage.Partial,
            "No owned-doll entry is left for a key that isn't a partner.",
            [
                "As owner, unpair a doll.",
                "The doll vanishes from the Owner tab, and no stale \"Owner\" button is left.",
            ]),
        new("02-5", "Unpaired winds", CheckCoverage.Full,
            "A wind and an unwind to a random unpaired key are refused with not_allowed (relay 03 or newer only).",
            []),
        new("03-1", "Funnel forwarded-IP header", CheckCoverage.Manual,
            "Only the relay host can see the header.",
            [
                "Connect through the real Funnel URL.",
                "In the relay log, the line \"Client address headers: …\" says X-Forwarded-For is present and the per-IP cap applies.",
                "Check by hand, without writing it down, that the value is your public IP (not a Tailscale or loopback address).",
                "Send an upgrade with your own X-Forwarded-For: the relay sees it replaced, not appended to.",
            ]),
        new("03-2", "Funnel 60 KiB / 1009", CheckCoverage.Partial,
            "A 60 KiB ping on the live session gets a pong, and before register 4 KiB + 1 gets close 1009 message_too_big with the description intact.",
            [
                "Run /windup selftest oversize confirm: 64 KiB + 1 after register gets close 1009 message_too_big, then the client reconnects.",
            ]),
    ];

    public static readonly IReadOnlyList<CatalogEntry> Extras =
    [
        new("X-identity-file", "Identity file", CheckCoverage.Extra, "Location, version and store status.", []),
        new("X-identity-entry", "Identity entry", CheckCoverage.Extra, "Entry, valid secret and previous, matching pairingKey.", []),
        new("X-identity-backups", "Identity backups", CheckCoverage.Extra, "Count of .corrupt-* copies (WARN if any).", []),
        new("X-pairing-key", "Pairing key", CheckCoverage.Extra, "PairingKey matches the ContentId-derived key.", []),
        new("X-relay-token", "Relay token", CheckCoverage.Extra, "RelayToken equals the compiled token (compared, never shown).", []),
        new("X-host", "Relay host", CheckCoverage.Extra, "The connected host is a compiled host, and LastSuccessfulRelayUrl matches it.", []),
        new("X-register", "Register", CheckCoverage.Extra, "The register was accepted.", []),
        new("X-stamps", "Sync stamps", CheckCoverage.Extra, "No future-dated consent or wind stamps.", []),
        new("X-suspend", "Lock safety", CheckCoverage.Extra, "ShouldSuspendMovementLocks is false while connected.", []),
        new("X-safeword-pause", "Safeword pause", CheckCoverage.Extra, "A stored safeword pause ends within 24 h and isn't kept in Hardcore (06-B1).", []),
        new("X-lock-emote", "Lock emote", CheckCoverage.Extra, "The stored unwound emote is unlocked on this character (WARN if not: Ground Sit is used; 06-C2).", []),
        new("X-pending", "Pending lists", CheckCoverage.Extra, "Pending pair requests and queued unpairs (counts).", []),
        new("X-standby", "Standby host", CheckCoverage.Extra, "The other compiled host is reachable without registering (INFO).", []),
        new("X-clock", "Clock skew", CheckCoverage.Extra, "Clock skew against the relay's /stats Date header (WARN over 2 minutes).", []),
        new("X-stats", "Relay /stats", CheckCoverage.Extra, "The relay's accounts count (INFO), for the manual part of 01-1.", []),
        new("X-limit-oversize", "4 KiB + 1 before register", CheckCoverage.Extra, "Close 1009 message_too_big with its description (part of 03-2; proves a 03 relay).", []),
        new("X-ping-60k", "60 KiB ping", CheckCoverage.Extra, "A 60 KiB ping on the live session gets a pong (part of 03-2).", []),
        new("X-limit-binary", "Binary before register", CheckCoverage.Extra, "A binary message before register gets close 1003.", []),
        new("X-auth-canary", "Auth canary", CheckCoverage.Extra, "A random unused key with a malformed secret tells whether relay auth is active.", []),
        new("X-auth-own-malformed", "Own key, malformed secret", CheckCoverage.Extra, "This key with a malformed secret gets auth_failed (auth active only).", []),
        new("X-auth-own-random", "Own key, wrong secret", CheckCoverage.Extra, "This key with a fresh random secret gets auth_failed (auth active only).", []),
        new("X-session", "Session invariants", CheckCoverage.Extra, "The live session was the same before and after the run.", []),
        new("X-restore", "Verb restore", CheckCoverage.Extra, "After an unload or logout, a verb's restore only lifts the hold and doesn't reconnect (04-D10).", []),
    ];

    public static readonly IReadOnlyList<CatalogEntry> Verbs =
    [
        new("V-outage", "Outage (01-9)", CheckCoverage.OptIn,
            "Holds the connect loop in memory and closes the socket; after 60 s or more locks are suspended; then reconnects.",
            ["Run /windup selftest outage confirm. The doll shows offline and messages queue for about 110 s."],
            OptInVerb: "outage",
            Restore: "In finally: the hold is cleared (never persisted) and Start() reconnects; a failure is a loud FAIL."),
        new("V-oversize", "Oversize (03-2)", CheckCoverage.OptIn,
            "Sends 64 KiB + 1 on the live session: close 1009 message_too_big, then an accepted automatic reconnect.",
            ["Run /windup selftest oversize confirm."],
            OptInVerb: "oversize",
            Restore: "In finally: if the client hasn't reconnected, Start() reconnects; a failure is a loud FAIL."),
        new("V-reconnect", "Reconnect", CheckCoverage.OptIn,
            "The Reconnect button's code path: the client reconnects and the register is accepted.",
            ["Run /windup selftest reconnect confirm."],
            OptInVerb: "reconnect",
            Restore: "In finally: if the client hasn't reconnected, Start() again; a failure is a loud FAIL."),
    ];

    public static readonly IReadOnlyList<CatalogEntry> PostPatch =
    [
        new("P-hook-rmi-walk", "RMI walk signature", CheckCoverage.PostPatch, "Resolved with the signature scanner, without hooking.", []),
        new("P-hook-rmi-fly", "RMI fly signature", CheckCoverage.PostPatch, "Resolved with the signature scanner, without hooking.", []),
        new("P-addr-use-action", "ActionManager.UseAction", CheckCoverage.PostPatch, "Address is non-zero.", []),
        new("P-addr-set-rotation", "GameObject.SetRotation", CheckCoverage.PostPatch, "Address is non-zero.", []),
        new("P-addr-input-pressed", "InputData.IsInputIdPressed", CheckCoverage.PostPatch, "Address is non-zero.", []),
        new("P-addr-input-down", "InputData.IsInputIdDown", CheckCoverage.PostPatch, "Address is non-zero.", []),
        new("P-addr-input-held", "InputData.IsInputIdHeld", CheckCoverage.PostPatch, "Address is non-zero.", []),
        new("P-addr-execute-command", "GameMain.ExecuteCommand", CheckCoverage.PostPatch, "Address is non-zero; the emote cancel's interrupt command (06-C2). Read only: never called.", []),
        new("P-addr-emote-loop", "EmoteController.IsInEmoteLoop", CheckCoverage.PostPatch, "Address is non-zero; the emote cancel's loop check (06-C2). Read only: never called.", []),
        new("P-hook-last", "Last hook install", CheckCoverage.PostPatch, "The last hook-install results this session (INFO).", []),
        new("P-data-emote", "Emote sheet", CheckCoverage.PostPatch, "Sit and Sit on Ground rows, and the looping-emote query.", []),
        new("P-data-territory", "TerritoryType sheet", CheckCoverage.PostPatch, "The residential district rows Call uses, with their regions.", []),
        new("P-data-world", "World sheet", CheckCoverage.PostPatch, "This world's row with its data center.", []),
        new("P-data-aetheryte", "Aetheryte sheet", CheckCoverage.PostPatch, "The residential aetheryte rows Call uses.", []),
        new("P-data-general-action", "GeneralAction sheet", CheckCoverage.PostPatch, "Jump, Return, Teleport and Mount Roulette rows.", []),
        new("P-data-action", "Action sheet", CheckCoverage.PostPatch, "The Teleport row Call checks.", []),
        new("P-ipc-lifestream", "Lifestream IPC", CheckCoverage.PostPatch, "Installed and loaded (INFO), or SKIP.", []),
        new("P-ipc-vnavmesh", "vnavmesh IPC", CheckCoverage.PostPatch, "Installed, loaded and travel-ready (INFO), or SKIP.", []),
        new("P-ui-selectyesno", "SelectYesno prompt node", CheckCoverage.PostPatch,
            "Needs a game window with the prompt open.",
            [
                "Walk to a house entrance and interact so the \"Enter the estate hall?\" prompt opens.",
                "With /xldata (Addon Inspector) open SelectYesno: NodeList[15] is the text node holding the prompt.",
            ]),
        new("P-emote-cancel", "Emote cancel", CheckCoverage.PostPatch,
            "Needs the game world and a wind; the self-test never sends the interrupt command itself (06-C2).",
            [
                "With debug mode on and connected to the relay, stand next to a chair and press Unwind in the Debug tab.",
                "Take a pose: groundsit (/groundsit), chair sit (/sit at the chair), /doze, or another looping emote (for example /pray).",
                "Press Self wind \"1h\" in the Debug tab: the doll stands up (or the emote ends) by herself, without moving from her spot.",
                "Repeat from Unwind for each of the four poses.",
            ]),
        new("P-ui-doors", "Door names", CheckCoverage.PostPatch,
            "Needs the game world.",
            [
                "Target a house's entrance: its name is still \"Entrance\" (or this client language's word for it).",
                "Target an apartment building's entrance: the name matches what Call expects.",
            ]),
    ];

    /// <summary>Every entry, in report order.</summary>
    public static IEnumerable<CatalogEntry> All => Checklist.Concat(Extras).Concat(Verbs).Concat(PostPatch);

    public static CatalogEntry? Find(string id) =>
        All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The MANUAL result for a checklist entry that has no automated part (04-D4).</summary>
    public static CheckResult ManualResult(CatalogEntry entry) =>
        CheckResult.Manual(entry.Id, entry.Coverage == CheckCoverage.OptIn
            ? $"opt-in only: run /windup selftest {entry.OptInVerb} confirm (see /windup selftest manual {entry.Id})"
            : $"see /windup selftest manual {entry.Id}");

    /// <summary>The steps printed by <c>/windup selftest manual [id]</c>: one entry, or every entry with steps.</summary>
    public static IReadOnlyList<string> ManualText(string? id)
    {
        var entries = string.IsNullOrWhiteSpace(id)
            ? All.Where(e => e.ManualSteps.Count > 0).ToList()
            : Find(id) is { } one ? [one] : [];
        var lines = new List<string>();
        foreach (var entry in entries)
        {
            lines.Add($"{entry.Id} {entry.Title} ({entry.Coverage}): {entry.Automated}");
            for (var i = 0; i < entry.ManualSteps.Count; i++)
                lines.Add($"  {i + 1}. {entry.ManualSteps[i]}");
            if (entry.Coverage is CheckCoverage.Partial or CheckCoverage.Manual && entry.ManualSteps.Count > 0)
                lines.Add($"  {Rerun}");
        }

        return lines;
    }
}
#endif
