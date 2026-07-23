# Wind-Up Key

Current version: **0.2.2.1**

A (hopefully) simple plugin. Become the wind-up doll of your dreams, or assist dolls by helping to wind them.

## Installation

1. Open Dalamud Settings in game with `/xlsettings`.
2. Select **Experimental** and add this URL under **Custom Plugin Repositories**:

   ```text
   https://raw.githubusercontent.com/alicewonderland-dev/WindUpKey/master/repo.json
   ```

3. Save the settings and open the plugin installer with `/xlplugins`.
4. Find **Wind-Up Key**, then install and enable it.

## Features

- Per-character Doll and Winder roles with secure partner pairing (pairing keys are derived from ContentId, so they stay the same across rename, world transfer, and config wipe; an updated relay can restore partner keys without restoring consent).
- Local pair nicknames and doll-assigned owner titles, including personalized pair-related messages.
- Context-menu and remote winding with configurable consent; offline winds queue on the relay until the doll is back.
- Automatic failover between alternate relay hosts, preferring the last host that connected successfully.
- Optional partner unwinding and owner-controlled doll settings.
- Movement and teleport restrictions when a doll runs out of winding.
- Automatic winding bonuses when a doll receives a duty commendation.
- Customizable notifications.
- Optional sound effects, Moodles integration, safeword, and Hardcore mode.
- In-plugin change log on the About tab.
