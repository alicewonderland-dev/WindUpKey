# Wind-Up Key

Dalamud plugin. Connection settings are compiled in (no relay URL/token in the UI).

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

### Testing build

1. Enable **Get plugin testing builds** in Dalamud Experimental settings.
2. In the plugin installer, open **Wind-Up Key** and opt in to its testing builds.
3. Update / reinstall so the name shows **Wind-Up Key (Testing)**.

Testing adds Unwind / Add 1h in settings and self-wind from your own context menu.

## Develop / pack

Prefer Testing / Debug while iterating. Pack Release only when publishing.

```powershell
# Both Release and Testing zips
.\deploy\Pack-Plugin.ps1

# One channel:
.\deploy\Pack-Plugin.ps1 -Channel Release
.\deploy\Pack-Plugin.ps1 -Channel Testing
```

Outputs:

- `deploy\dist\WindUpKey.zip` — public build
- `deploy\dist\WindUpKey-Testing.zip` — testing helpers

Local Debug builds include testing helpers (`WINDUP_TESTING`).

