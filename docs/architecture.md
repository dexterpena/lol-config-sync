# Architecture

LoL Config Sync is a WPF tray app on .NET 10. Everything that can be tested lives in a UI-free core library.

```
src/
  LolConfigSync.Core/      all logic, no UI (unit tested)
    Lcu/                   lockfile parsing, HTTPS client and WebSocket events for the League client API
    League/                typed endpoints (ILeagueGateway) on top of the raw client
    Sync/                  SyncEngine plus filtering, diffing, section map and rune page planning
    Model/                 SetupSnapshot, AppSettings, AccountOverrides, SettingKey
    Storage/               AppDataStore: main setup, settings, backups, per-account values
    Files/                 game.cfg reader and writer (local graphics keys)
    Editing/               editor rows and friendly labels
    Presentation/          status wording for the main window
    Hosting/               AppHost (wires everything up), connection watcher, log
    Updates/               GitHub release check and self-update
  LolConfigSync.App/       WPF shell: tray icon, windows, view models, theme
tests/
  LolConfigSync.Core.Tests/  xUnit tests with an in-memory fake League client and a fake clock
installer/                 Inno Setup script
build/package.ps1          builds the installer and portable zip
```

## Talking to the League client

The League client runs a local HTTPS API, the "LCU API", on a random port. When it starts, it writes `lockfile` into its install folder (`name:pid:port:password:protocol`). `LcuConnectionWatcher` polls for that file every 2 seconds and opens it with full sharing, because the client keeps it open. When the file appears, `AppHost` connects:

- `LcuHttpClient` makes REST calls to `https://127.0.0.1:<port>` with basic auth `riot:<password>`. The client's self-signed certificate is accepted only for that address.
- `LcuEventStream` subscribes to every client event over the WebSocket (`[5, "OnJsonApiEvent"]`). `AppHost` keeps only the events the engine cares about and feeds them to it in order.

Endpoints used (see `LeagueGateway`):

| What | Endpoint |
|------|----------|
| Who's logged in | `GET /lol-summoner/v1/current-summoner` |
| In a game or not | `GET /lol-gameflow/v1/gameflow-phase` |
| Settings loaded yet | `GET /lol-game-settings/v1/ready` |
| Game settings (Game.cfg sections) | `GET`/`PATCH /lol-game-settings/v1/game-settings` |
| Hotkeys (Input.ini) | `GET`/`PATCH /lol-game-settings/v1/input-settings` |
| Save the above | `POST /lol-game-settings/v1/save` |
| Rune pages | `GET /lol-perks/v1/pages`, `GET /lol-perks/v1/inventory`, `POST`/`DELETE /lol-perks/v1/pages[/{id}]` |
| Item sets | `GET`/`PUT /lol-item-sets/v1/item-sets/{summonerId}/sets` |
| Client preferences | `GET`/`PATCH /lol-settings/v2/account/LCUPreferences/{category}` |
| Profile icon | `GET /lol-game-data/assets/v1/profile-icons/{id}.jpg` |

Graphics settings (resolution, window mode, quality) are **not** stored per account by Riot. They live only in the PC's `game.cfg`, so `GameCfgVideoFile` reads and writes those keys directly, and only while no game is running.

## The sync engine

`SyncEngine` is the heart of the app. All of its state changes happen one at a time behind a single lock. Every operation gets a cancellation token that fires when the client disconnects or a different account logs in.

**Push (an account logs in):**

1. Wait until the gameflow phase is `None` or `Lobby` and `/lol-game-settings/v1/ready` is true.
2. Read everything the account has and save it as a backup under its Riot ID. If any part can't be read, that part is skipped. If the backup can't be saved, nothing is changed.
3. Apply the main setup, filtered by the section toggles and per-setting exclusions, with the account's own values on top. Each category is isolated: one failing endpoint becomes a warning, and the rest still sync.
4. Read the settings back. Sections League didn't keep are not marked as pushed.
5. Rune pages: leave `ReservedRunePageSlots` free (for Porofessor), skip the rewrite when the pages already match, and never touch pages named with a tool prefix.

**Capture (the user changes something):**

- Capture is only allowed for the account just pushed to, only after a 5 second settle window, and only for sections that push actually wrote and League kept. This is what stops an account's stale settings from leaking into the main setup.
- Changes are debounced for 3 seconds, then merged into the main setup. Changes to settings the account has its own value for update that value instead.
- Graphics are captured when a game ends, because that's when the game writes `game.cfg`.

**First run:** the "use this account as main?" question is asked only when the user is out of a game. The engine lock isn't held while the question is open, and the question is cancelled if the account changes or the client closes.

The rules above are pinned by `SyncEnginePushTests`, `SyncEngineCaptureTests`, `SyncEngineSafetyTests` and `AccountOverridesTests`.

## Data

Data lives in a `data` folder next to `LolConfigSync.exe` when that folder is writable (installer default and portable), otherwise in `%APPDATA%\LolConfigSync`. `LOLCONFIGSYNC_DATA_DIR` overrides it and turns on sandbox mode (see [CONTRIBUTING.md](../CONTRIBUTING.md)).

| File | Contents |
|------|----------|
| `main-setup.json` / `main-setup.prev.json` | The main setup and the version before the last save |
| `settings.json` | Section toggles, per-setting exclusions, rune slot reservation, tool page prefixes, update preferences |
| `account-settings.json` | Each account's own values |
| `backups\<Riot ID>\original.json` | The account's settings from before its first sync (never pruned) |
| `backups\<Riot ID>\<timestamp>.json` | The 10 most recent backups |
| `log.txt` | Rolling log (1 MB) |

All writes go to a temporary file first and are then renamed into place, so a crash can't leave a half-written file.

## Updates

`UpdateChecker` reads `https://api.github.com/repos/dexterpena/lol-config-sync/releases/latest`. `Updater` downloads the right file for the copy that's running and checks it against the SHA-256 digest GitHub publishes. Installed copies (with `unins000.exe` next to them) get the installer. Portable copies get the zip.

- **Installed:** the installer runs silently with `/RELAUNCH=1` once the app has exited, and the installer's `[Run]` section starts the new version.
- **Portable:** the running `.exe` is renamed to `.old`, the new one is put in its place, and the app restarts. The `.old` file is deleted on the next start.

See [releasing.md](releasing.md) for how releases are made.
