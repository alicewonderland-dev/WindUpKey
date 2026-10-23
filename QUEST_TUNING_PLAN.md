# 0.2.9.0: quest board tuning and vacation mode — plan

The quest board's duty picks and rewards change. It builds on the spare springs work (branch `springs`, 0.2.8.0).

**Shipping:**
- Code goes on a new branch, `quest-tuning`, off `springs`.
- Version 0.2.9.0, as a public testing build (Release compile).
- It changes the relay's daily line limit, so the relay is deployed first.

## 1. Medium duties (3 a week)

- **Pool:** unchanged. The Extremes of the highest level bracket the character reaches, with Unreal trials excluded.
- **New rule:** one slot is always an Extreme from that pool that the character has already cleared, if they have one.
- **Other two slots:** random from the rest of the pool, as now.

## 2. Hard duties (2 a week)

**Pool.** The Savage fights of the **newest tier only**, plus every Ultimate the character has cleared.
- At the level cap, the newest tier comes from the game's current-content flag (`HighEndDuty` on ContentFinderCondition). Today that is M9S–M12S.
- Below the cap, it's the newest Savage tier in their level bracket.
- I'll check both against the game data before relying on them. If the flag turns out not to mark exactly the four fights, tiers are grouped by item level instead.

**Order.** "Earliest" means the tier's fight order (M9S, M10S, M11S, M12S), by the sheet's sort order.

**Picking.**
1. **Slot 1:** a Savage of the tier that the character has cleared, if any. It's picked at random when several are cleared.
2. **Every other slot** is one weighted draw:
   - "a Savage" has weight 1 for each tier fight not already on the board;
   - each cleared Ultimate has weight 0.25.
3. **When "a Savage" wins:**
   - it's the **earliest** uncleared fight not already on the board;
   - once every fight is cleared, it's a random cleared one.

**Examples.**
- Nothing cleared and no Ultimates: M9S and M10S.
- M9S cleared: M9S and M10S.
- With one cleared Ultimate and no fight on the board yet, the draw gives about 6% for the Ultimate (0.25 out of 4.25).

**Rerolls.** A Hard reroll follows the same rules but skips the fights it replaces where it can. So a reroll of M9S and M10S gives M11S and M12S. A cleared Savage already on the board stays, and completed slots never change (as now).

## 3. Rewards

| What | Change |
|---|---|
| First clear | A board duty the character had never cleared gives **3× its wind**. Stickers stay the same. This covers every duty slot (Easy, Unlock, Medium, Hard), but not MSQ turn-ins or Leveling Roulette. |
| Ultimate | **3× wind** (72h), still 3 stickers. |
| Party bonus | **+50% wind** if a paired partner (doll or winder) is in the party when the duty or turn-in completes. **+100%** instead if one of the doll's owners is. Party members are matched to pairs by pairing key, from their ContentId, the way Call already does. |

**When "first time" is decided.** The clear state is read when the character enters the duty, so the game marking it completed during the run doesn't hide the bonus. The game sets the completed flag only at the clear, so it is also read correctly when the plugin loads, or the doll joins, partway through. A clear whose start was never seen reads it after the fact and gives no bonus.

**Stacking.**
- The multipliers combine by multiplying, on the duty's own wind only. Example: an Ultimate's first clear with the owner in the party gives 24h × 3 × 3 × 2 = 432h.
- An unsynced clear gets them on its quarter share.
- Line bonuses don't get the multipliers.
- Quest wind is still capped at 2× max wind hours.

**Winders** get no wind from the board, so none of these change anything for them. Their springs come from lines.

**Chat.** Chat lines still never name hours. They can say "first clear" and "with your owner" or "with a partner", so the doll knows why the wind was bigger.

## 4. The sticker board

- **A line wipes only itself** (clarified by the owner). When a sticker finishes lines, only those lines' stickers are wiped; the rest of the board stays.
  - The board can never fill, because the last empty cell always finishes its row.
  - One sticker can finish a row, a column and a diagonal at once.
  - Bonuses stay at 1 line +24h, 2 lines +72h, and 3 or more fill to the overwind cap.
- **The board UI** flashes the finished lines and fades their stickers out; the other stickers stay put.

## 5. Easy duties

- **A full clear rerolls them.** When all of the day's Easy slots are completed, a new set is rolled at once, without waiting for the daily reset. This can happen any number of times a day.
- **MSQ:** a turn-in still gives a sticker only for the first one each day.
- **Daily reset:** it still rolls a fresh set, as now.

## 6. Springs

- **Daily limit:** the relay pays for up to **10** board lines a day, up from 6.
- **Per grant:** `MaxLinesPerGrant` becomes 10, so a batch of several clears is never refused as too big.
- The relay ships these limits in `SpringRules`, so it needs a redeploy.
- **Other spring rules:** unchanged. A line pays 1 spring to a doll and 2 to a winder, the relay enforces the caps, and a grant past the cap is refused.

## 7. Vacation mode (dolls)

For a doll taking a long break, so they don't come back unwound.

**Who controls it.** Only the doll, at any time:
- even with owner-locked settings or in Hardcore mode;
- even while unwound or broken;
- with no minimum length.

Owners can't turn it on or off. Winders don't have it.

**While it's on:**
- **Timer:** frozen. All three wind layers stay as they were, and a break's recovery countdown stops too.
- **Movement:** the doll moves freely. The unwound lock and sit are off, even if they were unwound when they turned it on.
- **Refused for the doll:**
  - being wound, unwound (quotes too) or repaired;
  - winding other dolls;
  - giving or receiving springs;
  - being Called;
  - quest board completions (the board doesn't roll or award);
  - daily springs and quest line springs.
- **Still works:** owner settings changes, which take effect when they're back. The spring balance is kept as it was.
- **Moodles:** the wind and overwind Moodles are removed. There's no vacation Moodle.

**Turning it off.**
- **Timer:** it carries on from where it froze, and a break resumes with the time it had left.
- **Board:** any daily or weekly reset that came due is applied at once.
- **Springs:** daily springs start again from the next reset. Days away aren't paid back.

**The relay knows.**
- **Storage:** the doll's plugin pushes the state with its doll state, and the relay stores it per key.
- **Refusals:** winds, unwinds, quotes, repairs and gifts to or from the doll are refused *before* any spring is held, with a new error, `on_vacation` ("That doll is on vacation."). The doll's own winds of other dolls, gifts and quest grants are refused the same way.
- **Daily spring:** not granted while away.
- **Already-queued paid messages:** if the doll turns it on while some are waiting, the plugin answers them with `on_vacation`, so the relay refunds them in full.
- **Partners:** they see "On vacation" next to the doll, from pair sync and the partner list.
- **Protocol:** stays 6, because the field and the error are additive. A 0.2.8.0 partner sees its generic failure for `on_vacation`.
- **Known limit:** a doll who goes back to 0.2.8.0 while on vacation stays "on vacation" on the relay, because both builds speak protocol 6 and 0.2.8.0 has no toggle. Updating again and ending vacation clears it.
- **Becoming a winder ends vacation.** The plugin ends it on the switch, and the relay ends any still on when the key registers as a winder; that day's spring stays taken, so a role flip can't collect springs while out of reach.
  The relay then also moves its stored wind on to that moment undrained (a break keeps its time left) and stamps it newer than the frozen row, since a winder never pushes wind: every PC carries on from where it froze. A wind row with no layers (pushed by a build before 0.2.8.0) isn't moved; vacation needs 0.2.9.0, which always pushes layers.

**Config and UI.**
- **Setting:** the setting and the frozen state are kept in the character profile. The UI shows an "On vacation" banner, and the toggle is in the doll's settings.
- **Turning it on:** it asks to confirm, explaining what stops.
- **Turning it off:** takes effect at once.
- **Debug and Testing:** the self-test and debug tools still work.

## 7b. Discord (added by the owner during implementation)

- The Discord icon button moves from the About tab to the General tab, on the Connection heading's line.
- **One-time pop-up:** shown once after a login, after a role is chosen, inviting players to the Discord.
  - It has "Join the Discord" and "Not now" buttons.
  - Closing it any way stores `DiscordPromptShown`, which is global (every character on the PC), so it never shows again.

## 8. Tests

- **Picks:**
  - the Medium cleared-Extreme rule;
  - the newest-tier pool;
  - earliest-first;
  - the cleared-Savage slot;
  - the Ultimate weight, checked over many seeded rolls;
  - rerolls skipping replaced fights.
- **Rewards:**
  - the multipliers and how they stack;
  - the unsynced share;
  - the MSQ and Leveling Roulette exclusions.
- **Board:**
  - a finished line wipes only its own stickers;
  - the rest of the board stays;
  - several lines from one sticker.
- **Easy:** a full clear rerolls.
- **Relay:** the 10-line limit.
- **Vacation:**
  - the frozen timer and break, and resuming after;
  - free movement while unwound;
  - every refusal, on the plugin and on the relay, with no spring held;
  - no daily spring;
  - queued paid messages refunded;
  - partners shown "On vacation";
  - board resets applied on return.

The review and fix loop with independent agents runs until clean, as before.
