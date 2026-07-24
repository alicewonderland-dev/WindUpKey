# Future Feature Reminders

Ideas to consider for a future WindUpKey release. These are planning notes, not commitments or current behavior.

## High priority

### Partner nicknames and titles

- Let users assign private nicknames or thematic titles to paired partners, such as "Keyholder," "Clockmaker," or "Caretaker."
- Use them in local notifications and other appropriate plugin UI.
- Keep the labels local to the user unless a future design explicitly adds consensual sharing.

### Wind requests

- Let a doll send a deliberately vague request for winding to an eligible paired partner.
- Never reveal the doll's exact remaining time or timer bracket in the request.
- Add sensible cooldowns and per-partner permission controls to prevent spam or unwanted requests.
- Preserve the normal pairing, consent, relay, and successful `windResult` flow for the actual wind.

## Medium priority

### Thank-you system

- Let a doll send a preset or customized appreciation response after a successful wind.
- Make responses optional and configurable, with safeguards against spam.
- Do not reveal the wind bonus, exact remaining time, or other private timer information.

## Low priority

### Duty wind rules

- Allow optional pair-specific rules for winding after agreed duty events, such as completing a roulette or raid together.
- Keep all normal consent checks and timer caps in effect.
- Decide during design whether a matching event should trigger a wind automatically or offer a confirmation prompt; default toward explicit, understandable behavior.
- Keep commendation winding separate and local; do not route it through these remote duty rules.

## Unwound and low-wind restriction possibilities

Consider these either as configurable penalties that begin during low-wind states or as alternatives to the current full unwound movement lock. The eventual design may support restriction profiles rather than treating every option as mandatory.

### No Teleport or Return

- Block Teleport and Return while the selected restriction is active.
- Continue to allow ordinary movement and safe local travel unless combined with another restriction.

### Restricted actions

- Optionally block Sprint, mounts, and jumping as a bundle or as individually selectable restrictions.
- Consider a "grounded" profile that combines these actions with the Teleport and Return restriction while retaining ordinary ground movement.

### Idle wind-down

- After the doll remains idle for a configurable period, play or enforce the selected unwound looping emote.
- Allow movement to cancel the pose when this is a low-wind penalty; the full unwound profile may continue enforcing it according to the existing setting.
- Avoid repeatedly issuing emote commands or interrupting active gameplay.

### Leash-style area rules

- Consider limiting voluntary travel outside the current area or an agreed area while the restriction is active.
- Never forcibly move the doll, remotely expose their location, or interfere with duties and required loading transitions.
- Define area boundaries and escape behavior clearly before implementation so a stale state or game update cannot strand the player.

### Restriction model questions

- Decide whether low-wind penalties activate in vague stages, through one configured threshold, or only after an explicit state change; never display exact time or thresholds to the doll.
- Decide which restrictions the doll can configure, which may be owner-controlled with prior permission, and whether profiles can be selected per character.
- Preserve the existing full immobilization behavior as an available profile rather than silently changing it for existing users.
- Suspend all movement and travel penalties during duties, zone transitions, login settling, and the relay safety bypass.
- Fail open when a restriction cannot be applied safely after a game update.

## Hosting and infrastructure possibilities

### Oracle Cloud Always Free VM

- Consider moving the relay from the current Windows tray host to an Oracle Cloud Always Free virtual machine for always-on hosting without a regular hosting charge.
- Deploy the ASP.NET Core relay as a managed Linux service, likely on an Ampere ARM instance using an ARM64-compatible .NET runtime or self-contained build.
- Provide TLS and a stable `wss://` endpoint through a carefully chosen reverse proxy, Tailscale Funnel on the VM, or another documented ingress design.
- Keep the relay token in protected environment or service configuration rather than source control, command output, or public deployment files.
- Configure automatic service restart, security updates, firewall rules, minimal logging, and a simple health check.
- Confirm current Always Free eligibility, regional capacity, account requirements, and resource limits before migration because provider terms can change.
- Plan a tested fallback to the existing host and account for the relay safety bypass during deployment or outages.
- Prefer a stable hostname or indirection strategy so a future hosting migration does not require changing and redistributing the plugin solely for a new relay URL.

## Design guardrails

- The doll must never see exact remaining time through UI, chat, configuration, requests, or responses.
- Winders learn remaining time only through the existing notifier after a successful wind result.
- Pairing consent and owner permissions remain authoritative.
- New social features should be opt-in, configurable, and safe from notification spam.
- Restriction profiles and low-wind penalties should be opt-in, understandable in advance, and must not block chat, logout, recovery controls, or closing the game.
