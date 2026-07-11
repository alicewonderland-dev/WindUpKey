# Wind-Up Key

Dalamud plugin. Connection settings are built into the plugin.

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

## Develop / pack a release

```powershell
.\deploy\Pack-Plugin.ps1
# produces deploy\dist\WindUpKey.zip
```
