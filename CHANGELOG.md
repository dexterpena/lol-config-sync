# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).
The release workflow uses each version's section below as that release's notes on GitHub.

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
