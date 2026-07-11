# Wind-Up Key

Dalamud plugin. Connection settings are built into the plugin.

## Install

1. In game, open **Dalamud Settings** (`/xlsettings`).
2. Open **Experimental**.
3. Under **Custom Plugin Repositories**, add:

   ```
   https://raw.githubusercontent.com/alicewonderland-dev/WindUpKey/master/repo.json
   ```

4. Click the **+** / save, then open the plugin installer (`/xlplugins`).
5. Find **Wind-Up Key** and install / enable it.

### Testing build

1. Enable **Get plugin testing builds** in Dalamud Experimental settings.
2. In the plugin installer, open **Wind-Up Key** and opt in to its testing builds (required for non-exclusive plugins).
3. Update / reinstall so the plugin name shows **Wind-Up Key (Testing)** (version 0.1.2.0).

That build includes Unwind / Add 1h wind in settings and self-wind from your own context menu.

## Develop / pack

```powershell
# Both Release (normal) and Testing zips
.\deploy\Pack-Plugin.ps1

# Or one channel:
.\deploy\Pack-Plugin.ps1 -Channel Release
.\deploy\Pack-Plugin.ps1 -Channel Testing
```

Outputs:

- `deploy\dist\WindUpKey.zip` — public build
- `deploy\dist\WindUpKey-Testing.zip` — testing helpers

Local Debug builds also include testing helpers (`WINDUP_TESTING`).
