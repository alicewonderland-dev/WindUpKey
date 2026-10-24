# UI overhaul: plan

GagSpeak-style: a small main window with the most important information, and buttons that open a pop-out window for each feature.

- **Chosen layout:** main window A1 and Wind window option 1.
- **Shipping:** next update, on the `next-update` branch (with the guildhest change). The plugin only; the relay doesn't change.

## 1. Main window (A1)

Replaces today's tabbed config window as the window `/windup` opens.

**Header row:**
- Spare springs pill ("7 springs").
- Connection pill (Connected, Connecting, Offline). Clicking it shows the last status and has Reconnect.

**Profile block:**
- Character name, and role line: "Doll · owned by Alice", "Doll", or "Winder".
- **Moodle icon row (dolls only):** the game icons of the wind Moodles the doll has now, taken from the same level rules the Moodles sync uses.
  - The band icon: Fully wound, Wound, Winding down, Nearly spent or Unwound.
  - Plus the Clockwork icon when overwound.
  - The Shackles of Time icon alone when broken.
  - Each is labelled with its Moodle title. Hovering shows the title and description, as Moodles does.
  - The row shows even when the Moodles plugin isn't installed or the Moodle setting is off.
  - It never shows a time or a percentage: the doll still never learns their exact wind.
- **Notices, when they apply:** safeword pause, on vacation, Hardcore, update required.
- Partners online count.

**Module grid** (2 columns of buttons), each opening its window:
- Wind
- Pairs
- Quests
- Safeword (dolls)
- Vacation (dolls)
- Settings
- Changelog
- Discord (opens the invite)

Winders see the same grid without Safeword and Vacation. The first-time role picker stays as it is, shown in the main window until a role is chosen.

## 2. Wind window (option 1)

**Left: the partner list.**
- A search box, and the partners (doll first, then winders).
- Each row shows: name, owner crown, online or offline, and for a doll their percentage and a low or broken marker.

**Right: actions for the selected partner.**
- Name, role, online status, and a percentage bar for a doll.
- **Wind with springs:** 1, 2, 3 (springs only, as now).
- **Give springs:** an amount box with Give.
- **Repair** (shown when the doll is broken).
- **Unwind:** set apart in danger colours. It still opens the existing cost confirmation.
- Buttons a partner's permissions don't allow are hidden, as today.

**Other behaviour:**
- **Header:** the player's spring balance.
- **Context menu:** "Wind Up" and its items stay as they are, and keep working without this window.
- **Debug:** with debug on, the player's own self-pair appears in the list for self-wind and self-unwind (replacing the Debug tab's buttons).

## 3. Pairs window

Pairing and permissions only, with no winding.

- **Header:** Add partner (key entry), and the player's own pairing key with Copy.
- **One card per partner:** name with nickname and title editing, online status, and the "Can wind me", "Can unwind me", "Can call me" and Owner toggles (doll side). Unpair sits behind a confirmation.
- **Owner settings** for an owned doll: under the doll's card, collapsed by default.
- **Pending requests** (sent and received): listed at the top.

## 4. Settings window

Everything from today's General tab, in sidebar sections like GagSpeak's settings.

| Section | Contents | Shown to |
|---|---|---|
| Timer | Max wind hours, break recovery, owner lock notice | Dolls |
| Unwound emote | Play emote when unwound, emote picker | Dolls |
| Vacation | The same toggle and confirmation as the main window's Vacation button | Dolls |
| Safeword | Enable, word, hours, Hardcore (today's Safeword tab) | Dolls |
| Moodles | Wind charge Moodle, automation re-apply | Everyone |
| Audio and messages | Sound effects, open messages file | Everyone |
| Connection | Status, Reconnect, pairing key | Everyone |
| Role | Change role (hidden in Hardcore), with the current notices | Everyone |
| Debug | Today's Debug tab | Debug mode only |

**The header** shows the optional plugins as status pills: Moodles, Lifestream, vnavmesh. It also has links to the Discord, the changelog and GitHub.

## 5. Other windows

- **Quests:** today's Quests tab moved as is into its own window, with its Information pop-out.
- **Safeword:** a small window: use the safeword, resume remote control, and the pause status. Its setup stays in Settings.
- **Changelog:** the existing changelog window.
- **About:** folded into the main window title (version) and the Settings header (links).

## 6. Code shape

- **One window class per window,** registered in the plugin's WindowSystem:
  - `MainWindow`
  - `WindWindow`
  - `PairsWindow`
  - `SettingsWindow`
  - `QuestsWindow` (hosting today's QuestBoardTab)
  - `SafewordWindow`
- **Existing drawing code moves rather than being rewritten:** the General, Pairing, Safeword and Debug tabs' sections become methods those windows call.
  - The source-shape tests that read `ConfigWindow.cs` follow the code to its new files.
- **The Moodle icon row:** reuses `MoodlesWindStatusService.ResolveLevel` and the level's icon IDs and titles, moved to a shared helper so the main window and the Moodles sync can't disagree.
- **Commands:** `/windup` opens the main window. The Dalamud "Open config" button opens Settings, and "Open main UI" opens the main window.
- **Window state:** windows remember their open state and position (ImGui ini), as Dalamud windows do.

## 7. Tests

- **Source checks:**
  - every former General-tab setting still saves as it did (the 06-A4 save-on-commit checks move with the code);
  - the main window shows no hours or percentage to a doll;
  - Wind keeps the existing send paths;
  - Unwind still goes through the cost quote.
- **Moodle helper:** the icon row and the Moodles sync pick the same icons for every level (overwound, broken, vacation).
- **Build:** Release and Testing.

The review and fix loop with independent agents runs until clean, as before.
