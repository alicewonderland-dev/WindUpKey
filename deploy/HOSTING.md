# Wind-Up Key hosting (Tailscale Funnel)

Relay listens on `127.0.0.1:8787`. **Tailscale Funnel** publishes it at:

`https://dollhome.ancon-universe.ts.net`

Players never see the URL or token — both are compiled into [`WindUpKey/RelayDefaults.cs`](../WindUpKey/RelayDefaults.cs).

## Day-to-day

1. Sign into Tailscale on this PC (machine nickname: **dollhome**).
2. Run `Start-WindUpHost.bat` → **Start**.
3. Minimize to tray. Tray → **Exit** when done (turns Funnel off).

Plugin address: `wss://dollhome.ancon-universe.ts.net/ws`

## Token rotation

1. Change `Relay:Token` in `WindUpRelay/appsettings.Production.json`.
2. Change `RelayDefaults.RelayToken` to match.
3. Rebuild and redistribute the plugin.

## Troubleshooting

- `tailscale.exe not found` — install Tailscale and sign in.
- Funnel errors — enable Funnel + HTTPS certificates in the Tailscale admin console; run `tailscale funnel status`.
- Plugin cannot connect — host must be Start’ed; tokens must match.
- Port 8787 in use — Stop in the host UI, wait a few seconds, Start again.
