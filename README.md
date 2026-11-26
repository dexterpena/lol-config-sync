# LoL Config Sync

A small Windows tray app that makes every League of Legends account feel like the same account. It keeps one **main setup** (hotkeys, interface, camera, sound, rune pages, item sets, client preferences and graphics), applies it to whichever account you log into, and saves any changes you make in-game back into the main setup.

It only talks to the League client's local API (`127.0.0.1`) and the files in `League of Legends\Config`. It never touches the game process.

## How it works

1. **First run:** when you log in, the app asks whether that account's settings should become your main setup.
2. **Every login after that:** the app waits until you're out of a game, backs up the account's current settings, then applies your main setup. A tray notification confirms it.
3. **When you change something** in the client or in a game (hotkeys, HUD, volume, runes…), the app copies the change into the main setup a few seconds later, so the next account gets it too.
4. **Pick what syncs:** the main window has a toggle for each group. In **Edit setup** you can change any value or uncheck **Sync** on a single setting so it stays different per account.
5. **Backups:** before the app changes anything in the client, it backs up that account's current settings into a folder named after its Riot ID (for example `data\backups\Fiebre#L4N\`). If the backup can't be saved, nothing is changed. Its original settings (from before the first sync) are kept forever, plus the 10 most recent backups. Restore one from **Backups** while logged into that account. The previous main setup is also kept as `main-setup.prev.json`.

**Rune pages and Porofessor:** one rune page slot on every account is always left empty so Porofessor can create its page in champ select. Pages whose names start with `Porofessor` or `Poro` are never synced. If one is sitting in the free slot when a sync needs to rewrite your pages, it's cleared, and Porofessor makes a new one next champ select. Both are adjustable in `settings.json` (`ReservedRunePageSlots`, `ToolRunePagePrefixes`), for example if Porofessor names its pages differently.

Graphics (resolution, window mode, quality) aren't stored per account by Riot. They live in this PC's `game.cfg`, so the Graphics toggle just keeps them pinned to your main setup.

If you'd previously made `PersistedSettings.json` read-only to stop League overwriting it, the app turns that off (keeping a copy) so League can save your settings again.

## Install

There are two downloads. Neither needs .NET installed.

- **`LolConfigSync-Setup-<version>.exe` (recommended):** installs for your Windows user to `%LOCALAPPDATA%\Programs\LoL Config Sync` (no admin prompt) and adds a Start menu shortcut. It shows up in Windows' Installed apps for uninstalling. Uninstalling removes the startup entry and asks before deleting your saved setup and backups. You can choose an all-users install in the setup window instead. Data then goes to `%APPDATA%\LolConfigSync`, because Program Files isn't writable.
- **`LolConfigSync-<version>-portable.zip`:** extract it wherever you like (a USB stick, a games folder). You get a `LoL Config Sync` folder. Run `LolConfigSync.exe` inside it, and your data stays in that folder.

## Build

Requires the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`). Building the installer also needs Inno Setup 6 (`winget install JRSoftware.InnoSetup`).

```powershell
dotnet test                                   # 152 unit tests (Core)
dotnet run --project src/LolConfigSync.App    # run from source
powershell -ExecutionPolicy Bypass -File build\package.ps1   # tests, then both downloads into .\dist
```

`build\package.ps1` publishes a self-contained single-file `LolConfigSync.exe`, zips the portable folder and compiles `installer\LolConfigSync.iss`. The version comes from `<Version>` in `src/LolConfigSync.App/LolConfigSync.App.csproj`.

## Where things live

The app is portable. Everything it saves goes in a `data` folder next to `LolConfigSync.exe`, so moving or copying the program folder carries your setup with it. If the program sits somewhere Windows won't let it write (like `Program Files`), it uses `%APPDATA%\LolConfigSync` instead. The main window shows the folder in use and has an **Open data folder** link.

| What | Where (inside the data folder) |
|------|-------|
| Main setup | `main-setup.json` (previous version in `main-setup.prev.json`) |
| App settings (toggles, per-setting exclusions) | `settings.json` |
| Account backups | `backups\<Riot ID>\` (`original.json` is never pruned) |
| Log | `log.txt` |
| Start with Windows | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` → `LolConfigSync` (updated to the exe's current location on every launch) |

Setting `LOLCONFIGSYNC_DATA_DIR` runs the app in **sandbox mode**: all data goes to that folder, and the app never connects to the League client, never edits League's files and never touches the Run key. Use it to try the UI safely.

## Uninstall / undo

1. Quit from the tray icon.
2. Untick **Start with Windows** first, or delete the `LolConfigSync` value under the Run key above.
3. Optionally restore an account from **Backups** before quitting.
4. Delete the program folder (which includes its `data` folder), and `%APPDATA%\LolConfigSync` if that was used.

## Project layout

- `src/LolConfigSync.Core`: everything testable. League client connection (`Lcu/`), typed endpoints (`League/`), filtering, diffing and the sync engine (`Sync/`), storage, `game.cfg` access, editor model and status wording.
- `src/LolConfigSync.App`: the WPF tray app and windows, themed in the League client's colors (`Themes/Theme.xaml`).
- `tests/LolConfigSync.Core.Tests`: xUnit tests with an in-memory fake League client.

## Live checklist

Run these against the real client after building. The unit tests cover the logic, but these steps are the end-to-end proof.

- [ ] First run on account A, answer **Use as main**: `main-setup.json` exists and `PersistedSettings.json` is no longer read-only.
- [ ] Log out and into account B: a toast appears, B's hotkeys, HUD and camera match A in the in-game settings, and **Backups** lists B.
- [ ] On B, change a hotkey in a game and finish it: `main-setup.json` updates. Log into A and A has the new hotkey.
- [ ] Start a game and press **Sync now**: nothing is written until the game ends (status says "Waiting for your game to end").
- [ ] Turn off **Sound**, change volume on B, log into A: A's volume is unchanged.
- [ ] Restore B's backup while on B: B's original settings come back.
- [ ] Close and reopen the League client: the app reconnects without restarting.
