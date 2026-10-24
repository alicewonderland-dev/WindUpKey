# 0.2.10.0: quest board rework — plan

This builds on Release 0.2.9.0 (master).

**Shipping:**
- Code goes on a new branch, `quest-rework`, off master, for both repos.
- The relay changes the spring limit, so it is deployed first.

**Turns:** the board talks in turns. 8 hours of wind is 1 turn, and 4 hours is half a turn, so 12 hours reads "1½ turns".

## 1. Categories and rewards

| Category | Slots | Resets | Reroll | Each completion gives |
|---|---|---|---|---|
| **Daily** (new) | 1: *complete 2 roulettes* | daily (15:00 UTC) | none | 1½ turns, 1 sticker, 1 spare spring |
| **Easy** | 5 | **weekly** (Tuesday 08:00 UTC) | **once a day** | 1½ turns, 1 sticker, 1 spare spring |
| **Medium** | 3 | weekly | once a week | 2 turns, 2 stickers, 1 spare spring |
| **Hard** | 2 | weekly | once a week | 3 turns, 3 stickers, 1 spare spring |

**Winders** get no wind from the board, as now. Their spare springs are doubled by the relay (2 per completion and per line), as now.

**Daily**
- Any two roulette completions count, PvE or PvP, except the Normal Raid and Trial roulettes. The same roulette may count twice.
- The slot shows its progress (0/2, 1/2), and the second completion finishes it.
- PvP completion (decided by the owner during implementation): a roulette, PvP included, counts only when the game reports the duty completed. A PvP match the game never reports completed doesn't count. To check in game.
- Unsynced (decided during implementation): a Medium or Hard slot completed unsynced pays no spring, as it gives no stickers.

**Easy**
- **Pool:** mentor-roulette dungeons and guildhests only. Alliance raids, normal raids and trials are no longer offered.
- **Slots:** the Unlock slot (a duty the character can unlock), the Leveling Roulette slot (a job below the cap) and random duties stay as they are.
- **Full clear:** finishing every Easy duty no longer rolls a new set. The set stays until the weekly reset.
- **Reroll:** once a day. It replaces the open Easy slots and keeps the completed ones, as Medium and Hard do.
- **Main scenario:** there is no MSQ slot on the board any more. Each MSQ turn-in still gives 1 turn of wind, once per quest ever (New Game+ replays still earn nothing), with no sticker or spring.

**Medium and Hard** are unchanged apart from the spare spring per completion: the same picks, cleared guarantees, Ultimates and weekly rerolls as 0.2.9.0.

**Hidden multipliers:** first clear, Ultimate and the party bonus stay, and so does the unsynced rule. They are no longer explained anywhere (section 4).

## 2. The board

- **A finished line wipes the whole board again**, as in 0.2.7.0 and 0.2.8.0. Any stickers still to be placed go on the fresh board.
- **Line bonuses** stay the same: 1 line gives 3 turns (24h), 2 lines give 9 turns (72h), and 3 or more fill to the overwind cap.
- **Spring per line:** each line still pays 1 spring (2 for winders).

## 3. Springs (relay)

- **One limit:** quest springs from lines and from completions share one daily limit of **20** (each counted once, before the winder doubling).
- **One message:** completions are sent in the same idempotent grant batches as lines, so the relay needs no new message. A grant's `lines` field becomes "spring units". `MaxLinesPerGrant` rises to 20.
- `MaxQuestLinesPerDay` becomes 20.
- **Older clients:** a 0.2.9.0 client keeps working; its line grants simply count toward the same limit.

## 4. What players see

**Slots** show their sticker count and their wind in turns, for example "1 sticker, 1½ turns" or "Complete 2 roulettes (1/2)". No hours and no multipliers appear anywhere.

**The help paragraph and per-slot hover texts are removed.** An **Information** button opens a pop-out window that covers:
- the categories and when they reset;
- the rerolls;
- stickers per category, and wind in turns;
- lines wiping the board, with their bonus;
- spare springs per completion and per line;
- that quest wind can overwind.

It never mentions:
- the first-clear, Ultimate and party bonuses;
- the unsynced rules;
- the newest-tier and cleared-duty pick rules.

**Chat lines become vague hints:**
- A reward made bigger by a hidden bonus adds "The key turns more than usual." It no longer names the reason.
- An unsynced Easy clear says "Quest board: that clear didn't count this time."

**The in-game changelog entries** for 0.2.7.0 to 0.2.9.0 lose the bonus, penalty and pick-rule specifics, so the plugin itself doesn't explain them. The Discord posts and GitHub notes already published stay as they are.

## 5. Updating from 0.2.9.0

- **Board:** the saved board gains a Daily slot and Easy's weekly period and daily reroll stamp. On the first load, the Easy set is rolled again under the new rules (old daily Easy slots and the MSQ slot are dropped).
- **Kept as they are:** stickers already on the board, Medium and Hard this week, and pending spring grants.
- **Config:** version bump, with a migration that only adds fields.

## 6. Tests

- **Daily:** progress and completion; which roulettes count, including the same one twice and PvP; the daily reset.
- **Easy:** the pool without trials, normal raids or alliance raids; the weekly reset; one reroll a day with completed slots kept; no full-clear reroll.
- **MSQ:** a turn-in gives 1 turn of wind only, once per quest.
- **Rewards and springs:** completion springs for Easy, Medium, Hard and Daily, in the grant batches; the relay's limit of 20 a day; wind in turns.
- **Board:** a line wipes the whole board, and the stickers left go on the fresh board.
- **UI:** turns labels, no hours, and the Information window (source checks).
- **Migration:** from 0.2.9.0.

As before, the review and fix loop with independent agents runs until clean.
