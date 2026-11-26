# Releasing

Releases are built by GitHub Actions. Everyone running the app is offered the update automatically. The app checks at most once a day, and users can also click **Check for updates**.

## Cut a release

1. Make sure `main` is green in CI.
2. Pick the new version (`MAJOR.MINOR.PATCH`): fixes bump PATCH, new features bump MINOR.
3. Update the files:
   - `src/LolConfigSync.App/LolConfigSync.App.csproj`: set `<Version>`.
   - `CHANGELOG.md`: rename `## [Unreleased]` to `## [x.y.z] - YYYY-MM-DD`. This section becomes the release notes the app shows under "What's new".
4. Commit, tag and push:

   ```powershell
   git commit -am "Release x.y.z"
   git tag vx.y.z
   git push origin main --tags
   ```

5. The **Release** workflow runs the tests, builds `LolConfigSync-Setup-x.y.z.exe` and `LolConfigSync-x.y.z-portable.zip`, and publishes them as the GitHub release for the tag.

The version the app reports comes from the tag (the workflow passes it to `build/package.ps1`), so a tag always produces matching binaries. Keep the csproj in step anyway for local builds.

## Building locally

```powershell
powershell -ExecutionPolicy Bypass -File build\package.ps1             # version from the csproj
powershell -ExecutionPolicy Bypass -File build\package.ps1 -Version 1.2.3
```

This needs the .NET 10 SDK and Inno Setup 6 (`winget install JRSoftware.InnoSetup`). Output goes to `dist\`.

## Manual check before a release

The unit tests cover the sync logic with a fake client. These steps check it end to end with the real client:

- [ ] Fresh install, log into account A, answer **Use as main**: `main-setup.json` exists in the data folder, and `PersistedSettings.json` is no longer read-only.
- [ ] Log into account B: a notification appears, B's hotkeys, HUD and camera match A, and **Backups** lists B (including "Original settings").
- [ ] On B, change a hotkey in a game: after the game, `main-setup.json` has it, and logging into A gives A the new hotkey.
- [ ] Start a game and press **Sync now**: the status says "Waiting for your game to end" and nothing changes until the game ends.
- [ ] Turn **Sound** off, change volume on B, log into A: A's volume is unchanged.
- [ ] Give B its own value in the editor: B gets it, A doesn't.
- [ ] Porofessor: an account always has one free rune page slot, and Porofessor's page never shows up on other accounts.
- [ ] Restore B's backup while on B: B's original settings come back.
- [ ] Close and reopen the client: the app reconnects by itself.
- [ ] Update: with an older version installed, **Check for updates** offers the new release, installs it and restarts on the new version with settings intact. Repeat with the portable zip.
