# Wind-Up Key

Dalamud plugin for wind-up doll key RP: others wind your key from the context menu; when time runs out, movement and teleport lock. Connection settings are built into the plugin.

## Install (testers)

1. In game, open **Dalamud Settings** (`/xlsettings`).
2. Open **Experimental**.
3. Enable **Get plugin testing builds** (required — this plugin is testing-only).
4. Under **Custom Plugin Repositories**, add:

   ```
   https://raw.githubusercontent.com/alicewonderland-dev/WindUpKey/master/repo.json
   ```

5. Click the **+** / save, then open the plugin installer (`/xlplugins`).
6. Find **Wind-Up Key** and install / enable it.

## Host (required for multiplayer winds)

Someone must run the relay host with Tailscale Funnel up (see [`deploy/HOSTING.md`](deploy/HOSTING.md) and `Start-WindUpHost.bat`). Testers only install the plugin; the baked-in address points at that host.

## Security note

A shared relay token is compiled into the plugin. Anyone who installs can connect while the host is running. Rotate the token if the tester group grows beyond people you trust.

## Develop / pack a release

```powershell
.\deploy\Pack-Plugin.ps1
# produces deploy\dist\WindUpKey.zip
```
