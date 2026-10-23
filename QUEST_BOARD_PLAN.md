# Quest board overhaul: plan

Planning notes only. No code has been written. Items marked **(default)** are my assumption where the brief didn't say. Correct any of them before implementation starts.

## 1. What changes

The difficulty picker, the 24h lock and the single-reward quest (`QuestWindService`, `QuestDifficulty`, the `Quest*` fields in `CharacterProfile`/`Configuration`) all go. In their place:

- **A 4×4 sticker board** for each doll character that persists until a line forms.
- **10 duty slots**: 5 Easy that reset daily, plus 3 Medium and 2 Hard that reset weekly.
- **Wind from every sticker and every line**, allowed to overwind up to 2× the doll's max wind hours.

## 2. Rules

### Rewards per completed slot

| Slot | Stickers | Wind |
|---|---|---|
| Easy, normal duty | 1 | 8h |
| Easy, trial or normal raid | 1 | 4h (half). Alliance raids get the full 8h |
| Easy, MSQ | 1 (first MSQ of the day only) | 8h per MSQ completion |
| Medium (Extreme), synced | 2 | 16h |
| Hard (Savage/Ultimate), synced | 3 | 24h |
| Medium, unsynced | 0 | 4h (¼) |
| Hard, unsynced | 0 | 6h (¼) |
| Easy, unsynced | 0 | 0, and the slot stays open |

- **(default)** An unsynced Medium/Hard clear *uses up* the slot. If it didn't, someone could farm ¼ wind from repeated unsynced clears.
- **(default)** One clear credits every open slot it matches. For example, a Leveling Roulette that lands in a dungeon which is also an Easy slot credits both. Duplicate duties are avoided when the slots are rolled.

### Lines

- **Board:** 16 cells and 10 lines (4 rows, 4 columns, 2 diagonals). Stickers go into random empty cells.
- **Line bonus:** 1 line = +24h, 2 lines at once = +72h, 3 or more at once = wind set to the overwind cap (2× max). Any line clears the whole board.
- **Triple-line flag:** a triple clear sets a persistent achievement flag (`quest.tripleLine`, with a timestamp) for the future achievement system.
- **Possible?** A triple can happen. One sticker on a corner or diagonal cell can finish a row, a column and a diagonal together.
- **Decided:** lines are checked after each sticker. A line clears the board immediately, and any leftover stickers from the same clear go on the fresh board. "At once" therefore means lines finished by one sticker, so a triple needs one sticker that finishes three lines.

### Overwind

- All quest wind uses a cap of `2 × MaxWindHours` from now. It never shortens a longer timer (06-A8 still holds).
- Today quest wind has no cap at all, and you can't accept a quest while above max. The block goes away because the board is always active.
- Owner winds and commendations keep the normal 1× cap. The safeword stays uncapped.
- Guardrail: the doll still never sees remaining time. Chat stays vague ("the key turns…"). The UI shows reward hours as it does today, but never says "you're at the cap".

### Resets

- **Easy:** daily at 15:00 UTC.
- **Medium/Hard:** weekly, Tuesday 08:00 UTC.
- At a reset all slots in that tier are re-rolled, open or done. The board itself is not cleared.
- Slots are rolled lazily, on login, character switch, or opening the tab, whenever the stored period start is older than the latest reset.

### Rerolls

- **Decided:** one Medium reroll and one Hard reroll per week. They reset at the weekly reset, and unused rerolls don't carry over. Easy has no rerolls.
- **Decided:** a reroll replaces every *open* slot in that difficulty at once. Completed slots stay as they are.
  - For example, with 1 of 3 Medium done, the other 2 are rerolled.
  - The weekly limit stays at 3 Medium and 2 Hard completions.
  - When every slot in a difficulty is done there's nothing left to reroll, so the button is disabled.
- **(default)** The new roll avoids both the duties it replaces and the completed slots' duties, when the pool has enough others. In a small bracket a replaced duty may come back, but a completed one never does.
- **(default)** The UI shows a "Reroll" button next to the Medium and Hard headings, with "1 left this week" or "used", and asks for confirmation before rerolling.

### Sync rule

- **Decided:** a clear counts as synced when the character's level was synced to the duty, or was already at or below the duty's sync level.
  - Normal Duty Finder and Party Finder runs count, including Minimum IL and Silence Echo.
  - Unrestricted Party counts only with its Level Sync option on.
  - Unrestricted Party without Level Sync is unsynced.
- **How it's detected:** snapshot at duty start from `ContentsFinder.QueueInfo` (`IsUnrestrictedParty`, `IsLevelSync`) and `PlayerState` level-sync state, checked against the duty's `ClassJobLevelSync`.

## 3. Duty pools

> **Checked against the game data (2026-10-23):**
> - `HighEndDuty` is only set on the *current* tier (2 extremes and 4 savage at level 100). Pools therefore classify by content type and English name instead:
>   - Extremes: "(Extreme)" or "Minstrel's Ballad".
>   - Savage: "(Savage)".
>   - Ultimates: content type 28.
> - The Mentor Roulette includes the older Extremes, so the Easy pool excludes Extremes explicitly.
> - Extreme brackets are 50/60/70/80/90/100; Savage brackets are 50 (Second Coil)/60/70/80/90/100.

All classification reads the **English** sheets (the existing 11-D4 rule), so the plugin works on JP/DE/FR clients. This drops the hand-maintained `CurrentSavageNameNeedle`: pools come from sheet data, so new tiers need no code change.

"Highest combat job" means the highest-level Disciple of War/Magic job. Limited jobs (Blue Mage, and Beastmaster if that's treated the same) and crafters/gatherers don't count.

### Easy (5 slots, rolled daily, filled in this order)

1. **Next MSQ**, if there is one. Read from `AgentScenarioTree` (the MSQ journal's current quest).
   - The slot stays open all day, and every MSQ quest turned in awards 8h. A sticker comes only from the first one that day. The slot's label moves on to the new next MSQ.
   - Completion is detected by polling `QuestManager.IsQuestComplete` for the tracked quest id (cheap, a few times a minute).
   - MSQ isn't instanced, so the sync rule doesn't apply.
2. **Content the character can unlock but hasn't.**
   - Mentor-pool duties (below) that are not yet unlocked, whose `UnlockQuest` is accepted or acceptable now (prerequisite quests done, level met).
   - **(default)** Completing the slot means *clearing that duty synced*, not just finishing the unlock quest.
3. **Leveling Roulette on a job below the level cap**, if any non-limited combat job is below cap.
   - Credited when the queued roulette is Leveling and the current job is below cap at duty start.
4. **Random unlocked duties** for the remaining slots, from the mentor pool:
   - The duty has `MentorRoulette` set in `ContentFinderCondition`.
   - It isn't an extreme (`HighEndDuty`).
   - It's unlocked (`UIState.IsInstanceContentUnlocked`).
   - Its required level is at or below the highest combat job.

Low-level characters may get fewer than 5 Easy slots. The board shows what exists.

### Medium (3 slots, rolled weekly)

- **Bracket:** the highest extreme bracket at or below the highest combat job level. For example, a character at 86 gets the level 80 extremes and one at 100 gets the level 100 extremes.
- **Unlock not needed:** extremes are offered whether or not the character has unlocked them.
- **(default)** Unreal trials are excluded.
- **Below 50** there are no extremes, so no Medium slots (question in §7).

### Hard (2 slots, rolled weekly)

- **Pool:** Savage raids from the same kind of bracket (60 Alexander, 70 Omega, 80 Eden, 90 Pandæmonium, 100 Arcadion, 50 Second Coil), plus any Ultimate the character has cleared (`UIState.IsInstanceContentCompleted`).
- **(default)** The Chaotic alliance raid is excluded.

## 4. Code layout

The repo's convention is that rules have no Dalamud dependency and get their own tests, while game reads go through thin adapters.

New pure rule files:

- **`Quest/StickerBoard.cs`:** 16-bit mask, the 10 line masks, `Place(rng, count)` and `CompletedLines`.
- **`Quest/QuestRewardRules.cs`:** stickers and hours per slot kind, the half/¼ rules, the line bonus table.
- **`Quest/QuestResetClock.cs`:** daily 15:00 UTC and weekly Tuesday 08:00 UTC period starts.
- **`Quest/QuestSlot.cs`:** tier, kind (Msq / Unlock / LevelingRoulette / Duty / Extreme / Savage / Ultimate), target id (CFC row or quest id), completed, unsynced.
- **`Quest/QuestPoolRules.cs`:** bracket selection and the slot-filling order. It works on plain row records, so it can be tested without game data.

Rewritten or replaced:

- **`Quest/QuestContentCatalog.cs`:** rewritten to build the duty-row records from Lumina (English), with cached caps and brackets.
- **`Quest/IQuestPlayerFacts.cs` + the adapter in `Services`:** job levels, unlocked/completed instances, next MSQ id, quest-complete checks, the sync snapshot. This keeps `QuestBoardService` testable.
- **`Services/QuestBoardService.cs`:** replaces `QuestWindService`. It hooks `DutyStarted`/`DutyCompleted`/`Logout`/framework (for MSQ polling), rolls slots, matches clears to slots, places stickers, awards wind and saves.
- **`WindTimerService.AddQuestWind`:** takes the overwind cap (2× max) instead of no cap. `IsAboveMaxWind` stays for other callers or is removed if nothing else uses it.

Saved state, in the per-character profile with the usual `Configuration` mirror:

- **New fields:** board mask, the daily and weekly roll (period start and slots), the Medium and Hard reroll-used flags for the week, the date of the last MSQ sticker, and an achievement flag list.
- **`ProfileMigration`:** drops the old `Quest*` fields. **(default)** Any in-progress old quest is discarded without reward, and the changelog says so.
- **Relay:** no protocol change. The board stays local. Owner visibility would be a later feature.

## 5. UI (Quests tab, dolls only)

- **Left: the board.** A 4×4 grid of about 56px cells drawn on the ImGui draw list. Filled cells show the sticker texture.
- **Line clear:** the completed line glows for about 1.5s, then the stickers fade out.
- **Right: three groups,** Easy (with time until the daily reset) and Medium/Hard (with time until the weekly reset).
  - Each row shows the duty name, a type icon (game duty icon from the CFC/ContentType row), the reward ("1 sticker · 8h") and a check when done.
  - It shows "unsynced: ¼ time" when that happened.
  - Hovering a row shows the sync rule.
- **Removed:** the difficulty buttons, confirm popup and lock text.
- **Textures (decided):** the game's own Wondrous Tails UI textures (`ui/uld/WeeklyBingo*.tex`, using the `_hr1` variant when available), loaded through the existing `ITextureProvider` from game files. Nothing is bundled.
  - The board frame, empty cell and sticker are drawn as sub-rectangles (UVs) of that texture.
  - The exact paths and UV rectangles must be confirmed in-game first (step 1 of the UI work), using Dalamud's data/texture viewer.
  - Fail open: if the texture is missing or a patch moves it, the board falls back to a plain drawn grid with a check mark, so the tab never breaks.

## 6. Tests and release

- **New suites in `WindUpKey.Tests`:**
  - board lines and the triple case (corner + row + column + diagonal)
  - reward math, including half time, ¼ time and zero stickers
  - the overwind cap through `WindMath`
  - reset clock edges (14:59 vs 15:00 UTC, Monday vs Tuesday)
  - pool brackets (49 → none, 86 → 80, 99 → 90, 100 → 100)
  - slot fill order and the MSQ once-a-day sticker
  - rerolls: one per difficulty per week, only open slots replaced, completed slots kept, never more than 3 Medium / 2 Hard completions a week, disabled when all are done, restored at the weekly reset
- **Release (decided): a public testing build first, then full release.**
  - **(default) Branch:** the work lives on a `quest-board` branch. `master` keeps the current quest system, so a Release hotfix (0.2.6.x) can still go out during testing. The branch merges into `master` when it's promoted.
  - **Build (corrected in review):** the public testing build is the **Release compile** packed from the `quest-board` branch, at **0.2.7.0**, while Release stays at 0.2.6.1. The Testing compile carries the self-test and turns on the input diagnostics log, which must stay private.
    - Dalamud only offers a testing build whose version is higher than Release. That breaks rule 11-D2 (both channels share a version) for the length of the test.
    - `deploy/Pack-Plugin.ps1` needs a way to pack Testing alone at its own version.
    - The pack script's DLL name check needs updating, because it looks for the old quest type names.
  - **Catalog:** add `TestingAssemblyVersion` and `DownloadLinkTesting` back to the WindUpKey entry in Wonderland's repo.json, which has had no testing link since 0.2.6.0.
  - **Promotion:**
    - Merge `quest-board` into `master`.
    - Ship Release 0.2.7.0 (or the next number), with Release and Testing back on one version (11-D2 again).
    - Either remove the catalog's testing link, or set it equal to Release.
  - The changelog entry and README section are rewritten. The Discord changelog post goes out for the testing build, labelled as testing.
- **In-game checks before promotion:**
  - a dungeon clear
  - an Unrestricted Party clear, both with and without Level Sync
  - a Leveling Roulette on a job below the level cap
  - an MSQ turn-in
  - a Medium reroll
  - a daily and a weekly reset rollover

## 7. Smaller open points (defaults above unless you say otherwise)

- **Below level 50:** no Medium/Hard slots. The alternative is to fill them with extra Easy duties.
- **Overwind at the cap:** stickers and lines still happen, but the extra time is lost.
- **Owners:** they don't see the board for now.
