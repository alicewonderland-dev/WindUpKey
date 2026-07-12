# Wind-Up Key

Dalamud plugin (current release **0.1.4.1**).

Dolls get wound via paired partners; winders use the context menu. Remaining time is shown only to the winder after a successful wind — never to the doll.

## Install

1. In game, open **Dalamud Settings** (`/xlsettings`).
2. Open **Experimental**.
3. Under **Custom Plugin Repositories**, add:

   ```
   https://raw.githubusercontent.com/alicewonderland-dev/WindUpKey/master/repo.json
   ```

4. Save, then open the plugin installer (`/xlplugins`).
5. Find **Wind-Up Key** and install / enable it.

## Usage

- `/windup` opens the config window.
- Log in so your pairing key appears, then exchange keys and pair. Each character has its own pairing and timer state.
- Winders wind a paired doll from the target context menu. Dolls never see an exact countdown; they may get vague low-wind chat echoes.
- When a doll’s wind runs out, movement and teleport stay locked until someone winds them again.
- If the relay host stays unreachable for about a minute while you are logged in, those locks suspend until it reconnects (the timer itself is not cleared).
- Optional safeword: set it in config, then `/windup safeword <word>` (disabled while Hardcore is on).
- Hardcore locks you as a Doll. Clear it with `/windup unlock` and the confirmation phrase the plugin prints; clearing starts a 3-day re-unlock cooldown.
