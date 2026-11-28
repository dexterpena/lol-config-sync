# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).
The release workflow uses each version's section below as that release's notes on GitHub.

## [1.0.3] - 2026-11-28

- A brand-new account now receives your saved client preferences (champ select, general, notifications, runes). 1.0.2 wrongly skipped them as a "different format". Only preferences that would downgrade an account are skipped.
- If League rejects one client preference category, edits to the other categories still sync back to the shared setup. A successful retry also resumes syncing edits to the affected category.

## [1.0.2] - 2026-11-27

- League detection prefers the running client and recognizes custom installation folders. Leftover folders from old installs are ignored.
- Sync skips client preferences the account already has. If a preference differs and the account uses a different format, the app explains why it skipped that preference.
- The profile picture retries if League wasn't ready the first time it was requested.

## [1.0.1] - 2026-11-26

- Switching League accounts cancels the previous account's sync and discards its queued settings changes.
- When a backup read fails, the app fills in that part of the original backup on the next successful read, before changing those settings.
- Restoring a backup uses all available rune page slots and restores saved tool pages and empty item sets. If an older backup's empty list is ambiguous, the app keeps the current list and explains why.
- Quickly changing a hotkey and changing it back now saves the final value for both shared and per-account settings.

## [1.0.0] - 2026-11-26

First release.

- Keeps one main setup and applies it to whichever account logs into the League client, after backing that account up.
- Syncs hotkeys, interface, game and camera settings, sound, rune pages, item sets, client preferences and graphics, with a toggle per group.
- Saves changes made in-game or in the client back into the main setup, so the next account gets them.
- Editor for every setting, with per-setting "don't sync" and per-account values.
- Backups filed under each account's Riot ID, including the account's original settings, which are never deleted.
- Always leaves one rune page slot free for Porofessor and never syncs Porofessor's pages.
- Shows who is logged in (Riot ID, level, profile icon).
- Installer or portable zip. Data lives next to the app.
- Checks GitHub for updates and updates itself.
