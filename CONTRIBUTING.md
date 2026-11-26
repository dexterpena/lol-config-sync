# Contributing

Thanks for helping. Bug reports, ideas and pull requests are all welcome.

## Reporting a bug

Open an issue with:

- what you did, what you expected, and what happened
- your app version (shown at the bottom of the main window)
- the relevant lines from `log.txt` in the data folder (**Open data folder** in the app). Remove anything you don't want to share. The log contains Riot IDs but no passwords or tokens.

## Development setup

You need Windows 10 or 11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download) (`winget install Microsoft.DotNet.SDK.10`).

```powershell
git clone https://github.com/dexterpena/lol-config-sync
cd lol-config-sync
dotnet test                                  # run the unit tests
dotnet run --project src/LolConfigSync.App   # run the app
```

### Try the UI without touching your League settings

Running the app normally connects to your League client and changes settings. To work on the UI safely, use **sandbox mode**:

```powershell
$env:LOLCONFIGSYNC_DATA_DIR = "$env:TEMP\lcs-sandbox"
dotnet run --project src/LolConfigSync.App
```

In sandbox mode the app keeps all data in that folder and never connects to the client. It also never edits League's files, never touches the startup registry entry, and skips the automatic update check.

## Making changes

- **Logic goes in `LolConfigSync.Core`** and comes with tests in `tests/LolConfigSync.Core.Tests`. The app project (`LolConfigSync.App`) is a thin WPF layer that binds to Core. Write the test first, watch it fail, then make it pass.
- **The sync engine is safety-critical.** It must never write a user's real settings unexpectedly. Every change to `SyncEngine` needs a test, including what happens on disconnects, account switches and failed requests. `SyncEngineSafetyTests` shows the pattern, using the in-memory fake client.
- **Never touch the game process.** Only the League client's local API and the files in `League of Legends\Config` are fair game.
- **UI:** use the styles in `src/LolConfigSync.App/Themes/Theme.xaml` (League client palette). Keep wording plain and in sentence case.
- Build with no warnings. Warnings are treated as errors.

See [docs/architecture.md](docs/architecture.md) for how the pieces fit together.

## Pull requests

1. Fork, create a branch, make your change with tests.
2. `dotnet test` must pass. CI runs it on every pull request.
3. Describe what changed and why. Add a line under a new `## [Unreleased]` heading in `CHANGELOG.md`.

By contributing, you agree your contribution is licensed under the [MIT License](LICENSE).
