# LoL Config Sync — Design

Date: 2026-11-26
Status: Approved 2026-11-26

## Intent

The user plays League of Legends on several accounts and switches between them constantly. Riot stores each account's settings server-side and pushes them to the local files on login, so every account feels different. The goal: **"feel like I'm in the same account no matter what."** One setup — keybinds, graphics, HUD, camera, everything possible — follows the user to every account automatically, and they can view and modify it from a Windows GUI.

## Goals

- One **main setup** that is applied to whatever account logs in, automatically, with no clicks.
- Changes the user makes in-game or in the client (on any account) are captured back into the main setup, so the next account gets them too.
- Covers everything the League client lets us read and write:
  - **Game settings** — all of Settings: Hotkeys, Sound, Interface, Game, Video (incl. camera, HUD scale, minimap, etc.).
  - **Rune pages**
  - **Item sets**
  - **Client settings** (League client account preferences)
  - **The user picks what syncs**, at two levels:
    - **Section toggles** (all on by default): Hotkeys, Sound, Interface, Game, Video, Rune pages, Item sets, Client settings. Game settings are split into the five in-game Settings tabs so e.g. Video can be left per-account.
    - **Per-setting exclusions**: any individual setting can be marked "don't sync" in the editor. Excluded settings are never pushed or captured; each account keeps its own value.
- A GUI to see sync status, view and edit the main setup, and restore backups.
- Never permanently lose an account's own original settings.

## Non-goals

- Multiple profiles / per-account variants. Every account gets the same setup.
- Logging in or switching accounts (no password storage).
- Syncing between different PCs.
- Anything Riot keeps server-only (mastery, ranked, collection).

## Anti-goals (failure even if it "works")

- Touching the game process or memory in any way (ban risk). Only the League client's local web interface (LCU API) and its config files.
- Ping-pong loops where the app's own writes are re-captured and re-pushed endlessly.
- Silently clobbering an account's settings with no way back.
- Changing settings while a game is in progress.
- Relying on read-only file locks (that's the current broken workaround).

## Constraints / environment facts

- League installed at `D:\Riot Games\League of Legends` (non-default). Install path must be discovered, not hardcoded: read `%ProgramData%\Riot Games\RiotClientInstalls.json` (`associated_client` keys), fall back to the running `LeagueClientUx.exe` process path, then `C:\Riot Games\League of Legends`.
- `Config\PersistedSettings.json` is currently **read-only** (since 2026-10-30) — the user's earlier workaround. The app must back it up and clear the read-only flag on first run, or the client can't save settings.
- LCU API: the client writes `lockfile` in the install dir (`name:pid:port:password:protocol`). HTTPS on `127.0.0.1:<port>`, basic auth `riot:<password>`, self-signed cert (accept it for loopback only). WebSocket on the same port for events (WAMP-style `[5, "OnJsonApiEvent"]` subscribe).
- **Verified against the live client (2026-11-26):**
  - Per-account (server-synced) settings are exactly what's in `PersistedSettings.json`: `Game.cfg` sections Accessibility, Chat, ColorPalette, FloatingText, General, HUD, ItemShop, LossOfControl, Performance (only `EnableHUDAnimations`), TFTChat, TFTHUD, Voice, Volume; and `Input.ini` sections GameEvents, HUDEvents, Quickbinds, ShopEvents, WASD. Exposed via `/lol-game-settings/v1/game-settings` and `/input-settings`.
  - **Graphics are NOT per-account.** `game.cfg` `[Performance]` quality keys and `[General]` `WindowMode`/`Width`/`Height`/`WaitForVerticalSync` are local-only. So the Video toggle means "keep these local `game.cfg` keys pinned to the main setup" (written directly to the file when no game is running), not an API push.
  - Rune pages: `/lol-perks/v1/pages`, `/lol-perks/v1/inventory` (`customPageCount`, `canAddCustomPage`). Item sets: `/lol-item-sets/v1/item-sets/{summonerId}/sets` (`{accountId, itemSets, timestamp}`). Client settings: `/lol-settings/v2/account/LCUPreferences/{category}` (`{data, schemaVersion}`); categories mirror the top-level keys of `Config\LCUAccountPreferences.yaml`.
  - Client settings are synced from an **allowlist** of meaningful categories (champ select, spell selections, party/position prefs, quick play, perks prefs, notifications, general, TFT). The rest are tooltip-seen flags, cosmetics and per-account history and are left alone.
- Toolchain: .NET runtime is present but **no .NET SDK** — plan must include installing it (`winget install Microsoft.DotNet.SDK.10`). Git is available.

## Approaches considered

1. **Sync through the League client's LCU API** — *chosen.* App connects to the running client, writes the main setup into the logged-in account so Riot saves it to that account, and listens for changes to capture back. Works with Riot instead of against it; covers runes/item sets/client settings. Trade-off: the LCU API is unofficial and a patch can occasionally change an endpoint (same interface Blitz/Porofessor use; doesn't touch the game).
2. **Lock config files read-only** — rejected. Simple, but in-game changes never save, the server copy drifts, and runes/item sets aren't covered. It's what the user has now and it isn't working.
3. **Hybrid (API + locked files as backup)** — rejected by the user: the lock breaks saving changes. Replaced by one-click backup restore.

## How it works

### Tech

- C# / .NET 10, WPF desktop app, single `.exe` publish. Tray icon via `H.NotifyIcon.Wpf` (builder may substitute an equivalent).
- Data in `%APPDATA%\LolConfigSync\`:
  - `main-setup.json` — the main setup (all categories, plus a timestamp and which account it was last captured from).
  - `settings.json` — app preferences (section toggles, per-setting exclusion list, start with Windows, notifications).
  - `backups\<puuid>\<timestamp>.json` — an account's own settings, snapshotted before the app first overwrites them, and before every push after that. Keep the last 10 per account.
  - `log.txt` — rolling log for troubleshooting.

### Lifecycle

1. **Start** — runs at Windows login (toggle, default on), lives in the tray. Watches for the `lockfile` to appear or disappear to know when the client is up.
2. **Connect** — read the lockfile, open the HTTPS client and WebSocket.
3. **Detect account** — `GET /lol-summoner/v1/current-summoner` (puuid, Riot ID). Also react to login/logout events so account switches without a client restart are caught.
4. **First run ever** (no `main-setup.json`): ask "Use **<RiotID>**'s settings as your main setup?" Yes → capture everything from this account. No → wait for the next account. Also do the PersistedSettings read-only fix here.
5. **On login to any account** — wait until the gameflow phase is idle (`/lol-gameflow/v1/gameflow-phase` is `None`/`Lobby`, not `InProgress`), back up this account's current settings, then push the main setup:
   - Game + input settings: `PATCH /lol-game-settings/v1/game-settings` and `/lol-game-settings/v1/input-settings`, then `POST /lol-game-settings/v1/save`.
   - Rune pages: replace the account's editable pages with the main setup's pages, up to the account's available slots (`/lol-perks/v1/inventory`); if it has fewer slots, push in main-setup order and say so in the status.
   - Item sets: `PUT /lol-item-sets/v1/item-sets/{summonerId}/sets`.
   - Client settings: `/lol-settings/v1/account/{category}` for the categories that are account preferences.
   - Tray toast: "Synced your setup to <RiotID>".
6. **Capture changes** — subscribe to change events for the synced endpoints. When the user changes something (in-game settings land when the game exits; client settings land right away), debounce for ~3s, then update `main-setup.json` with only the changed values.
7. **Loop guard** — after the app pushes, ignore change events for that account until those events settle (or compare incoming data to what was just pushed and drop matches). Captures happen only when the data differs from the main setup.
8. **Client closes** — go back to watching for the lockfile.

### GUI (main window, opened from the tray)

```
┌ LoL Config Sync ───────────────────────────────────┐
│ ● Connected — Dexter#NA1      Last sync: 2 min ago │
│ Main setup captured from: Dexter#NA1, Nov 26       │
│                                                    │
│ Sync:  [x] Hotkeys   [x] Sound      [x] Interface  │
│        [x] Game      [x] Video                     │
│        [x] Rune pages [x] Item sets [x] Client     │
│                                                    │
│ [Edit setup]  [Use this account as main]  [Sync now]│
│ [Backups / restore]          [x] Start with Windows │
└────────────────────────────────────────────────────┘
```

- **Edit setup** — tabbed editor (Hotkeys, Video, Interface, Game, Sound, plus a raw "All settings" searchable table for anything without a friendly label). Every setting row has a "sync" checkbox (unchecked = per-setting exclusion). Saving updates the main setup and pushes to the logged-in account immediately if one is connected and idle.
- **Use this account as main** — re-capture the main setup from the current account.
- **Sync now** — force a push to the current account.
- **Backups** — list per account and timestamp; Restore pushes that snapshot back to its own account (only when that account is logged in).

### Error handling

- Client not running → status "Waiting for League client", no errors shown.
- Endpoint missing or changed (patch) → skip that category, log it, show a warning badge on the category; other categories keep working.
- Push fails mid-way → leave the backup in place, show a "Restore" shortcut in the toast.

### Testing

- Sync logic (diffing, loop guard, rune slot handling, debounce) lives behind an `ILcuClient` interface with unit tests against a fake.
- A manual checklist for live testing against the real client: first run, account switch, in-game change captured, mid-game no-push, restore.

## Left to the builder

- Exact LCU endpoint shapes and which `/lol-settings` categories count as account preferences — verify against the live client (it's running on this PC) and community docs.
- Tray library and MVVM structure.
- Friendly labels for the editor's settings keys (start with common ones; the raw table covers the rest).
- Debounce and settle timings.

## Calls made by Claude (not explicitly decided by the user)

- Rune pages, item sets and client settings are included and on by default ("everything possible").
- First run asks which account's settings become the main setup.
- Runes on accounts with fewer page slots: fill in main-setup order and report what didn't fit.
- An in-app settings editor covers "modify"; in-game changes also flow back automatically.
- Backups before every push, 10 kept per account.
- Starts with Windows by default.
- C#/.NET 10 WPF.
