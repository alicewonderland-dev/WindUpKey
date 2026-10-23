# Spare springs, overwind and breaking: plan

Planning notes only. No code has been written. **(default)** marks my assumption where the brief didn't say. §8 lists the open questions.

## 1. How it works today, and what that means

- **Winding** happens peer to peer. Any paired player picks a free preset (1/6/12/24h). The relay only routes the message, queueing it while the doll is offline. The doll's own client applies the wind, clamps it to max wind, and replies with the **exact** remaining time.
- **Storage:** the relay stores each doll's wind expiry (`doll_wind`, last write wins) so it follows them across PCs. Permissions come from per-pair consent, which the relay also stores: Can wind me, Can unwind me, Is owner, Can call me.
- **Unwinding** clears the timer outright. It's gated only by Can unwind me and the safeword pause.
- **Overwind** today is just "the expiry is more than max from now": quest wind and the safeword can push it there.

Springs are a currency that players spend and hand to each other. **Decided: the relay holds the balances.** Kept in client configs, a balance could be edited by hand, would differ between PCs, and a give or spend could not be checked.

## 2. Springs

| | Doll | Winder |
|---|---|---|
| Cap | 10 | 20 |
| Free each day (on the first login after the 15:00 UTC reset; missed days are lost) | 1 | 2 |
| Per quest board line | 1 | 2 |
| Can wind other dolls with them | yes | yes |
| Can wind themselves | no | n/a |
| Can give them to other paired players (up to the receiver's cap) | yes | yes (decided) |

- **Value:** 1 spring = an 8h wind. An **owner** winding an owned doll gets +50%, so 1 spring = 12h. Doll-to-doll winds and other winders get the plain 8h.
- **Over the cap:** a free spring, quest spring or gift that would go over the cap is lost, not banked. **(default)** A gift is refused if the receiver is full, and the giver keeps it.
- **Winders and the quest board:** winders get the quest board too. It gives them springs only, because they have no timer: stickers still go on the board, and lines pay springs. **(default)** The same pools and rules apply, judged on the winder's own character.
- **Role switch:** a switch keeps the balance but clamps it to the new cap, so a winder with 20 who becomes a doll keeps 10.

## 3. The doll's wind, in three layers

The doll's wind becomes a stack. Time drains from the **top** down:

```
  player overwind   (top: drains first; only other players add to it)
  quest overwind    (quest board wind past 100%)
  normal wind       (0–100% of max wind hours)
```

### Winds from other players

1. Fill normal wind up to 100%.
2. Put the rest into player overwind.
3. If player overwind reaches max wind hours, the doll **breaks** (§4): all player overwind is lost.

### Quest wind (board stickers, lines, MSQ)

1. Fill normal wind up to 100%.
2. Put the rest into quest overwind, up to the overwind cap.

**Decided: separate stacks.** Quest overwind can reach 1× max on top of normal wind (normal + quest ≤ 2× max, as now). Player overwind sits on top of both and breaks the doll at 1× max, so the total can approach 3× max.

### Other sources

- **Commendation wind** fills normal only (no overwind), as now.
- **Safeword:** **(default)** it fills normal and then quest overwind with no cap, so it can never break a doll.

### Movement

- The doll can move whenever **normal wind > 0** and they aren't broken.
- With quest overwind but no normal wind, they stay frozen.
- **Decided:** while normal wind is 0, quest overwind **doesn't drain**. It's held until someone winds them again.

### Unwinding

An unwind clears normal wind and player overwind. Quest overwind is never touched.

**Cost:** ceiling of (normal + player overwind) / 8h springs, so 0h–8h costs 1, 8h–16h costs 2, and so on. **Decided:** player overwind counts, because the unwind removes it.

- An **owner** unwinding an owned doll pays nothing.
- With too few springs, the unwind is refused.
- **(default)** An unwind of a doll already at 0 normal and 0 player overwind costs nothing and does nothing.

### Storage and sync

- **Doll's client:** stores the total expiry plus the sizes of the two lower layers. Because the stack drains from the top, those sizes only shrink when the time left falls into them.
- **Relay:** `doll_wind` gains the layer sizes and the broken state, so they follow the doll between PCs.

## 4. Broken

- **Trigger:** a player wind that takes player overwind to max wind hours.
  - All player overwind is lost.
  - Normal wind and quest overwind are kept.
  - The doll is locked like when unwound: the same lock and the unwound emote.
- **Ends** when either of these happens first (a repair costs no springs, so the relay's repair message is not charged):
  - **Repaired** by another player. **Decided:** any paired player with Can wind me (owners included), free.
  - **Recovers by itself** after 30 minutes. This is a new setting with the same rules as max wind hours: the doll sets it, and an owner can change it, or lock it, from the owner settings. **(default)** It can be set from 5 to 240 minutes.
- **Wind while broken:**
  - **Decided:** the wind timer is **paused** while broken: nothing drains, and when the break ends the doll carries on where they were.
  - **Decided:** a wind sent to a broken doll is refused, and the sender keeps the springs (the relay refunds them). Only a repair is accepted.
- **Message:** the broken doll gets a vague RP chat line, and the winder is told they broke the doll.

## 5. Moodles

- **Wind level:** the wind Moodle becomes a **percentage of normal wind** (of max wind hours) instead of hours. That also fixes the old fixed-hour thresholds, which gave away exact times.

  | Normal wind | Moodle |
  |---|---|
  | 90–100% | Fully Wound (existing icon) |
  | 50–90% | Wound |
  | 10–50% | Winding Low |
  | under 10% | Nearly Spent |
  | 0 | Unwound |

  **(default)** Each band includes its lower edge, so exactly 50% is "Wound".
- **New overwind Moodle:** shown while there is any player overwind. It uses the Clockwork icon (215825), with an RP warning that more winding may break them. It sits alongside the wind Moodle.
- **New broken Moodle:** shown while broken. It uses the Shackles of Time icon (215372), with an RP description of being broken by careless winding. It replaces the wind Moodle for that time.
- **Text:** all Moodle text is editable in the existing low-wind messages file, as the current Moodles are.

## 6. What other players see

- **Wind result:** a winder gets the doll's **normal wind as a percentage**, capped at 100%, instead of the exact remaining time. `windResult` gains a `percent` field, and the exact `remainingSeconds` is no longer sent.
- **Wind requests** already send a rounded percentage, which stays.
- **Hidden from the winder:** the doll's quest or player overwind. They're only told that the doll is overwound (the Clockwork Moodle shows it anyway) or broken.

## 7. Implementation outline

### Relay (deployed first, as always)

- **New tables:**
  - `springs`: key, balance, `last_daily_utc`.
  - `spring_ledger`: an append-only record of every change, for support and abuse checks.
- **Daily grant:** at register, if `last_daily_utc` is before the latest daily reset, the relay grants 1 or 2 springs by role. Role is a new register field; **(default)** a role change is rate-limited so the daily grant can't be farmed by switching.
- **Quest grants:** a new `springGrant` message from the client: lines completed and the board's line counter.
  - The relay can't see the game, so it trusts the client but caps how much a day can earn. **(default)** Up to 6 lines a day; the board's real maximum is lower.
  - The counter makes a replayed grant earn nothing.
- **Winding:**
  - `wind` now carries `springs` (1–N) instead of `hours`.
  - The relay checks the sender's balance, takes the springs, and works out the hours: 8 each, or 12 from an owner.
  - It forwards the wind, or queues it if the doll is offline, as now.
  - If the doll refuses it (no permission, broken, safeword pause, queue expired), the relay refunds the springs.
- **Unwinding:** the doll's client works out the cost from the layers, and the relay charges it.
  - **(default)** The unwind is a two-step exchange: the doll quotes a cost, the relay charges the sender, then confirms. That way an unwind never costs springs unless it happens.
  - Owners are free; the relay already knows who owns whom.
- **Gifts:** a new `springGift` message. The relay checks that both are paired (dolls and winders can both give), and the receiver's cap, then moves the springs and tells both sides.
- **Repair:** a new `repair` message, free, allowed for Can wind me partners.
- **Spring balance:** sent with `pairSync` and after every change.
- **Version gate:** the protocol version goes from 5 to 6, and older clients get a clear "please update" error on any wind or unwind. **Decided.**

### Plugin

- **Wind layers:** the three-layer model, the broken state and its timer, and the lock rule (normal > 0 and not broken). It's pure code with tests, as the quest board was.
- **Spring balance:** read from the relay; never edited locally.
- **Pairing tab:**
  - The wind buttons become **1 / 2 / 3 springs** (with the hours they give, 12h each from an owner).
  - Unwind shows its cost before you confirm it.
  - New buttons for Give springs (dolls) and Repair (when the doll is broken).
  - Your balance shows at the top of the tab.
- **Context-menu wind:** spends springs the same way.
- **Quest board:** opens to winders (springs only). Line awards also send `springGrant`.
- **Moodles:** the percentage bands, plus the Clockwork and Shackles Moodles.
- **Owner settings:** gain the break-recovery minutes.
- **Release:** a public testing build first, as with the quest board. It needs the relay deployed on the VM before it goes out.

### Tests

- **Plugin:**
  - layer arithmetic: drain order, the fill order for player and quest wind, and the break trigger
  - unwind cost rounding
  - percentage bands
  - spring caps and doubling
- **Relay:**
  - charging and refunding
  - the daily grant across resets
  - gift caps
  - owner discounts
  - replayed and over-limit quest grants
  - old-client refusal

## 8. Decisions

All eight questions are answered:

1. **Where springs live:** on the relay.
2. **Repair:** any paired player with Can wind me, free.
3. **Overwind caps:** separate stacks, so the total can approach 3× max.
4. **Frozen doll:** quest overwind is held at 0 normal wind.
5. **Unwind cost:** normal plus player overwind, rounded up.
6. **Gifts:** dolls and winders can both give springs.
7. **Old clients:** refused for winding and unwinding.
8. **Broken doll:** the timer is paused and winds are refused.

The remaining **(default)** items above stand unless you change them.
