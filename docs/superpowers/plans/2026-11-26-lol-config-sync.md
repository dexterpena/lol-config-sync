# LoL Config Sync Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Windows tray app that pushes one "main setup" of League of Legends settings to whichever account logs in, captures changes back, and lets the user pick what syncs and edit the setup.

**Architecture:** A UI-free `LolConfigSync.Core` library holds everything testable: lockfile/LCU transport, a `LeagueGateway` over the LCU REST endpoints, filtering/diffing, storage, and the `SyncEngine` state machine. A thin WPF app (`LolConfigSync.App`) hosts the tray icon and windows and only binds to Core. The engine is driven by LCU WebSocket events. It never touches the game process.

**Tech Stack:** C# / .NET 10, WPF, System.Text.Json.Nodes, H.NotifyIcon.Wpf 2.3.0, xUnit, Microsoft.Extensions.TimeProvider.Testing 10.0.0.

**Spec:** `docs/superpowers/specs/2026-11-26-lol-config-sync-design.md`

## Global Constraints

- Only the LCU API (`https://127.0.0.1:<port>`, basic auth `riot:<password>` from `lockfile`) and files under `<install>\Config\`. Never read or write the game process.
- Self-signed cert accepted **only** for host `127.0.0.1`.
- Never push or write `game.cfg` while gameflow phase is anything but `None` or `Lobby`.
- Install dir discovered: `%ProgramData%\Riot Games\RiotClientInstalls.json` → running `LeagueClientUx.exe` dir → `C:\Riot Games\League of Legends`. This PC: `D:\Riot Games\League of Legends`.
- App data root: `%APPDATA%\LolConfigSync\` with `main-setup.json`, `settings.json`, `backups\<puuid>\<timestamp>.json` (keep 10 newest per account), `log.txt`.
- Sections (toggles, all on by default): Hotkeys, Sound, Interface, Game, Video, RunePages, ItemSets, ClientSettings.
- Section mapping: Input.ini (all) → Hotkeys; Game.cfg `Volume`, `Voice` → Sound; `General` → Game; every other Game.cfg section → Interface; local video keys → Video.
- Video keys (local `game.cfg` only): `Performance`: ShadowQuality, FrameCapType, EnvironmentQuality, EffectsQuality, CharacterQuality, EnableFXAA; `General`: WindowMode, Width, Height, WaitForVerticalSync.
- Client settings allowlist: `champ-select`, `lol-champ-select`, `custom-spell-selections`, `lol-general`, `lol-notifications`, `lol-parties-game-select`, `parties-settings`, `partiesPositionPreferences`, `lol-premade-voice`, `lol-perks`, `lol-quick-play`, `lol-tft`.
- Capture debounce 3 s. Ignore captures until 5 s after a push completes.
- Run at Windows login via `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `LolConfigSync` = `"<exe>" --tray`, default on.

## Review Focus

1. **Stale settings right after login.** League fires change events carrying account B's *old* settings before our push lands. These must never be captured into the main setup. (Task 9 test `Events_before_push_completes_are_not_captured`.)
2. **Switching accounts without restarting the client** (logout → login as someone else). The new account must get a push, and captures from the old one must not apply to it. (Task 8 test `Account_switch_triggers_new_push`.)
3. **One endpoint broken by a Riot patch.** A 404/500 on one category must not stop the other categories, and it shows as a warning. (Task 8 test `Failing_category_is_isolated`.)
4. **Stale lockfile after a client crash.** The connection fails, so the watcher must retry quietly instead of crashing or spamming errors. (Task 11 test `Stale_lockfile_retries`.)
5. **App launched twice** (startup entry plus a manual launch). The second instance must show the first instance's window and exit. (Task 12 manual check plus `SingleInstance` unit.)

---

### Task 1: Toolchain and solution skeleton

**Files:**
- Create: `LolConfigSync.sln`, `src/LolConfigSync.Core/LolConfigSync.Core.csproj` (net10.0), `src/LolConfigSync.App/LolConfigSync.App.csproj` (net10.0-windows, `UseWPF`, `OutputType WinExe`), `tests/LolConfigSync.Core.Tests/LolConfigSync.Core.Tests.csproj` (xunit, refs Core, adds `Microsoft.Extensions.TimeProvider.Testing` 10.0.0), `.gitignore` (`dotnet new gitignore`), `Directory.Build.props` (`Nullable enable`, `ImplicitUsings enable`, `TreatWarningsAsErrors true`)

- [ ] **Step 1:** `winget install --id Microsoft.DotNet.SDK.10 -e --accept-source-agreements --accept-package-agreements`, then open a new shell. Expected: `dotnet --list-sdks` shows `10.0.x`.
- [ ] **Step 2:** Create the projects with `dotnet new classlib|wpf|xunit` and `dotnet sln add`. Add the `H.NotifyIcon.Wpf` 2.3.0 package and a Core project reference to App. Delete the template `Class1.cs`/`UnitTest1.cs`.
- [ ] **Step 3:** Add a smoke test `SmokeTests.Core_assembly_loads` (`Assert.NotNull(typeof(LolConfigSync.Core.AssemblyMarker))`) and an empty `public sealed class AssemblyMarker` in Core.
- [ ] **Step 4:** `dotnet build` then `dotnet test`. Expected: build succeeds, 1 test passes.
- [ ] **Step 5:** Commit `chore: scaffold solution`.

### Task 2: Lockfile parsing and install locator

**Files:**
- Create: `src/LolConfigSync.Core/Lcu/LockfileInfo.cs`, `src/LolConfigSync.Core/Lcu/LeagueInstallLocator.cs`
- Test: `tests/LolConfigSync.Core.Tests/Lcu/LockfileInfoTests.cs`, `.../Lcu/LeagueInstallLocatorTests.cs`

**Interfaces:**
- Produces: `sealed record LockfileInfo(int Pid, int Port, string Password, string Protocol)` with `static bool TryParse(string content, out LockfileInfo? info)`; `static class LeagueInstallLocator` with `string? Locate(string? riotClientInstallsJson, string? runningClientExePath, Func<string,bool> dirExists)` and `string? LocateOnThisMachine()`.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void Parses_lockfile() {
    Assert.True(LockfileInfo.TryParse("LeagueClient:1234:54321:abcPW:https", out var i));
    Assert.Equal(new LockfileInfo(1234, 54321, "abcPW", "https"), i);
}
[Theory, InlineData(""), InlineData("a:b:c"), InlineData("LeagueClient:x:54321:pw:https")]
public void Rejects_bad_lockfile(string s) => Assert.False(LockfileInfo.TryParse(s, out _));

[Fact] public void Prefers_riot_installs_json() {
    var json = """{"associated_client":{"D:/Riot Games/League of Legends/":"x","D:/Riot Games/VALORANT/live/":"y"}}""";
    Assert.Equal(@"D:\Riot Games\League of Legends", LeagueInstallLocator.Locate(json, null, _ => true));
}
[Fact] public void Falls_back_to_running_process_then_default() {
    Assert.Equal(@"E:\LoL", LeagueInstallLocator.Locate(null, @"E:\LoL\LeagueClientUx.exe", _ => true));
    Assert.Equal(@"C:\Riot Games\League of Legends", LeagueInstallLocator.Locate("{}", null, d => d.StartsWith("C:")));
    Assert.Null(LeagueInstallLocator.Locate(null, null, _ => false));
}
```

- [ ] **Step 2:** `dotnet test --filter "FullyQualifiedName~Lcu"`. Expected: FAIL (types don't exist).
- [ ] **Step 3:** Implement. `Locate` picks the `associated_client` key that contains `League of Legends`, normalizes it with `Path.GetFullPath` and trims the trailing separator, and returns it only if `dirExists`. `LocateOnThisMachine` reads the ProgramData file (if present) and `Process.GetProcessesByName("LeagueClientUx")[0].MainModule?.FileName`, swallowing access errors.
- [ ] **Step 4:** Run the tests. Expected: PASS.
- [ ] **Step 5:** Commit `feat(core): lockfile parsing and install locator`.

### Task 3: Model, section map, filter, diff

**Files:**
- Create: `src/LolConfigSync.Core/Model/SyncSection.cs`, `Model/SettingKey.cs`, `Model/SetupSnapshot.cs`, `Model/AppSettings.cs`, `Sync/SectionMap.cs`, `Sync/SettingsFilter.cs`, `Sync/SettingsDiff.cs`
- Test: `tests/LolConfigSync.Core.Tests/Sync/SettingsFilterTests.cs`, `.../Sync/SettingsDiffTests.cs`

**Interfaces:**
- Produces:
  - `enum SyncSection { Hotkeys, Sound, Interface, Game, Video, RunePages, ItemSets, ClientSettings }`
  - `enum SettingsKind { Game, Input, Video }`
  - `readonly record struct SettingKey(SettingsKind Kind, string Section, string Name)`. `ToString()` gives `"game:HUD.GlobalScale"` (kind lower-case). Also `static SettingKey Parse(string)`.
  - `sealed class SetupSnapshot`, mutable with `{ get; set; }`: `JsonObject GameSettings`, `JsonObject InputSettings`, `JsonObject VideoSettings` (all shaped `{section:{key:value}}`), `JsonArray RunePages`, `JsonArray ItemSets`, `JsonObject ClientSettings` (`{category:{data,schemaVersion}}`), `DateTimeOffset CapturedAt`, `string CapturedFromPuuid`, `string CapturedFromRiotId`. Plus `SetupSnapshot DeepClone()`.
  - `sealed class AppSettings`: `HashSet<SyncSection> EnabledSections` (default all), `HashSet<string> ExcludedKeys` (stores `SettingKey.ToString()`), `bool StartWithWindows = true`, `bool ShowNotifications = true`, `string? InstallDirOverride`, `bool IsEnabled(SyncSection)`, `bool IsExcluded(SettingKey)`.
  - `static class SectionMap`: `SyncSection SectionFor(SettingsKind kind, string section)`, `IReadOnlyDictionary<string,string[]> VideoKeys`, `IReadOnlyList<string> ClientSettingsCategories` (values from Global Constraints).
  - `static class SettingsFilter`: `JsonObject Apply(JsonObject settings, SettingsKind kind, AppSettings app)` returns a deep clone without disabled sections, excluded keys, or sections left empty.
  - `static class SettingsDiff`: `JsonObject Changes(JsonObject current, JsonObject baseline)` (two-level; keys whose value differs by `JsonNode.DeepEquals` or that are missing from baseline) and `void MergeInto(JsonObject target, JsonObject changes)`.

- [ ] **Step 1: Write failing tests**

```csharp
static JsonObject Gs() => JsonNode.Parse("""{"HUD":{"GlobalScale":0.5,"FlipMiniMap":true},"Volume":{"MasterVolume":0.3},"General":{"CursorScale":1}}""")!.AsObject();

[Fact] public void Disabled_section_is_dropped() {
    var app = new AppSettings(); app.EnabledSections.Remove(SyncSection.Sound);
    var r = SettingsFilter.Apply(Gs(), SettingsKind.Game, app);
    Assert.False(r.ContainsKey("Volume")); Assert.True(r.ContainsKey("HUD"));
}
[Fact] public void Excluded_key_is_dropped_and_empty_section_removed() {
    var app = new AppSettings();
    app.ExcludedKeys.Add("game:General.CursorScale");
    var r = SettingsFilter.Apply(Gs(), SettingsKind.Game, app);
    Assert.False(r.ContainsKey("General"));
    Assert.Equal(0.5, r["HUD"]!["GlobalScale"]!.GetValue<double>());
}
[Fact] public void Input_sections_map_to_hotkeys() =>
    Assert.Equal(SyncSection.Hotkeys, SectionMap.SectionFor(SettingsKind.Input, "GameEvents"));

[Fact] public void Diff_returns_only_changed_values() {
    var cur = Gs(); var base_ = Gs(); cur["HUD"]!["GlobalScale"] = 0.7;
    var d = SettingsDiff.Changes(cur, base_);
    Assert.Equal("""{"HUD":{"GlobalScale":0.7}}""", d.ToJsonString());
}
[Fact] public void Merge_applies_changes() {
    var t = Gs(); SettingsDiff.MergeInto(t, JsonNode.Parse("""{"HUD":{"GlobalScale":0.9},"Chat":{"ChatScale":50}}""")!.AsObject());
    Assert.Equal(0.9, t["HUD"]!["GlobalScale"]!.GetValue<double>()); Assert.Equal(50, t["Chat"]!["ChatScale"]!.GetValue<int>());
}
```

- [ ] **Step 2:** Run `--filter "FullyQualifiedName~Sync"`. Expected: FAIL.
- [ ] **Step 3:** Implement the types above.
- [ ] **Step 4:** Run the tests. Expected: PASS.
- [ ] **Step 5:** Commit `feat(core): sync model, section map, filter and diff`.

### Task 4: AppDataStore

**Files:**
- Create: `src/LolConfigSync.Core/Storage/AppDataStore.cs`, `Storage/BackupEntry.cs`
- Test: `tests/LolConfigSync.Core.Tests/Storage/AppDataStoreTests.cs` (each test uses a fresh temp dir)

**Interfaces:**
- Consumes: `SetupSnapshot`, `AppSettings` (Task 3).
- Produces: `sealed class AppDataStore(string rootDir)` with `static string DefaultRoot` (`%APPDATA%\LolConfigSync`), `SetupSnapshot? LoadMainSetup()`, `void SaveMainSetup(SetupSnapshot)`, `AppSettings LoadSettings()` (returns defaults when the file is missing or corrupt), `void SaveSettings(AppSettings)`, `string SaveBackup(SetupSnapshot)`, `IReadOnlyList<BackupEntry> ListBackups()` (newest first), `SetupSnapshot LoadBackup(string path)`; `sealed record BackupEntry(string Puuid, string RiotId, DateTimeOffset CreatedAt, string Path)`.

- [ ] **Step 1: Write failing tests**: `Main_setup_round_trips` (values and `CapturedFromRiotId` survive), `Settings_default_when_missing_or_corrupt` (write `"{not json"` and expect all 8 sections enabled), `Backups_pruned_to_ten_per_account` (save 12 for puuid A and 1 for B; expect `ListBackups()` to have 10 for A and 1 for B, with A's oldest two gone), `Backups_listed_newest_first`.
- [ ] **Step 2:** Run them. Expected: FAIL.
- [ ] **Step 3:** Implement. Writes go to `*.tmp` and then `File.Move(..., overwrite: true)` so a crash can't leave a half-written `main-setup.json`. Backup file name is `yyyyMMdd-HHmmss-fff.json` from `snapshot.CapturedAt` (UTC).
- [ ] **Step 4:** Run them. Expected: PASS.
- [ ] **Step 5:** Commit `feat(core): app data store with atomic writes and backup pruning`.

### Task 5: game.cfg video keys and read-only fix

**Files:**
- Create: `src/LolConfigSync.Core/Files/GameCfgFile.cs`, `Files/IVideoSettingsFile.cs`, `Files/GameCfgVideoFile.cs`
- Test: `tests/LolConfigSync.Core.Tests/Files/GameCfgFileTests.cs`

**Interfaces:**
- Produces: `static class GameCfgFile` with `JsonObject ReadKeys(string path, IReadOnlyDictionary<string,string[]> keys)` (string values, shaped `{section:{key:value}}`), `void WriteKeys(string path, JsonObject values)`, `bool ClearReadOnly(string path)` (returns whether it was read-only); `interface IVideoSettingsFile { JsonObject Read(); void Write(JsonObject values); }`; `sealed class GameCfgVideoFile(string gameCfgPath) : IVideoSettingsFile` (uses `SectionMap.VideoKeys`).

- [ ] **Step 1: Write failing tests**

```csharp
const string Cfg = "[General]\r\nWindowMode=2\r\nCursorScale=0.5\r\n[Performance]\r\nShadowQuality=4\r\n[HUD]\r\nGlobalScale=0.3\r\n";
[Fact] public void Reads_only_requested_keys() { /* write Cfg to temp; ReadKeys(VideoKeys) == {"General":{"WindowMode":"2"},"Performance":{"ShadowQuality":"4"}} */ }
[Fact] public void Write_replaces_and_appends_preserving_other_lines() {
    // WriteKeys({"General":{"WindowMode":"0","Width":"1920"},"Performance":{"ShadowQuality":"2"}})
    // expected file text exactly:
    // "[General]\r\nWindowMode=0\r\nCursorScale=0.5\r\nWidth=1920\r\n[Performance]\r\nShadowQuality=2\r\n[HUD]\r\nGlobalScale=0.3\r\n"
}
[Fact] public void ClearReadOnly_reports_and_clears() { /* set IsReadOnly=true; Assert.True(ClearReadOnly); Assert.False(new FileInfo(p).IsReadOnly); Assert.False(ClearReadOnly) */ }
```

- [ ] **Step 2:** Run them. Expected: FAIL.
- [ ] **Step 3:** Implement a line-based INI rewrite (not a parser library) so comments, ordering and unknown sections survive byte for byte. A section that's missing gets appended at the end of the file.
- [ ] **Step 4:** Run them. Expected: PASS.
- [ ] **Step 5:** Commit `feat(core): game.cfg video key read/write and read-only fix`.

### Task 6: LCU transport (HTTP and WebSocket)

**Files:**
- Create: `src/LolConfigSync.Core/Lcu/ILcuClient.cs`, `Lcu/LcuException.cs`, `Lcu/LcuHttpClient.cs`, `Lcu/LcuEvent.cs`, `Lcu/WampMessage.cs`, `Lcu/LcuEventStream.cs`
- Test: `tests/LolConfigSync.Core.Tests/Lcu/LcuHttpClientTests.cs`, `.../Lcu/WampMessageTests.cs`

**Interfaces:**
- Consumes: `LockfileInfo` (Task 2).
- Produces:
  - `interface ILcuClient { Task<JsonNode?> GetAsync(string path, CancellationToken ct = default); Task SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct = default); }`, which throws `LcuException(int StatusCode, string Path)` on a non-2xx response.
  - `sealed class LcuHttpClient(LockfileInfo lockfile, HttpMessageHandler? handler = null) : ILcuClient, IDisposable`
  - `sealed record LcuEvent(string Uri, string EventType, JsonNode? Data)`
  - `static class WampMessage { const string SubscribeAll = "[5,\"OnJsonApiEvent\"]"; static LcuEvent? Parse(string text); }`
  - `sealed class LcuEventStream(LockfileInfo lockfile) : IAsyncDisposable`, with `event Action<LcuEvent>? Received`, `event Action? Closed`, `Task ConnectAsync(CancellationToken ct)`

- [ ] **Step 1: Write failing tests**: `Sends_basic_auth_to_loopback_port` (a stub handler captures the request; expect `https://127.0.0.1:54321/lol-x`, header `Basic cmlvdDphYmNQVw==`), `Non_success_throws_LcuException_with_status` (404 → `StatusCode == 404`), `Patch_sends_json_body`, `Parse_event_frame` (`[8,"OnJsonApiEvent",{"data":{"a":1},"eventType":"Update","uri":"/lol-gameflow/v1/gameflow-phase"}]` → matching record), `Parse_ignores_other_frames` (`[0,"x",1,"y"]` → null, invalid JSON → null).
- [ ] **Step 2:** Run them. Expected: FAIL.
- [ ] **Step 3:** Implement. Without a handler, use a `SocketsHttpHandler` whose `SslOptions.RemoteCertificateValidationCallback` returns `true` only when the request host is `127.0.0.1`. Do the same for `ClientWebSocket.Options.RemoteCertificateValidationCallback` (wss://127.0.0.1:port/, basic auth header). The receive loop runs until close, then raises `Closed`.
- [ ] **Step 4:** Run them. Expected: PASS.
- [ ] **Step 5:** Commit `feat(core): LCU HTTP client and event stream`.

### Task 7: LeagueGateway and rune page planner

**Files:**
- Create: `src/LolConfigSync.Core/League/Account.cs`, `League/ILeagueGateway.cs`, `League/LeagueGateway.cs`, `Sync/RunePagePlanner.cs`
- Test: `tests/LolConfigSync.Core.Tests/League/LeagueGatewayTests.cs` (uses a recording fake `ILcuClient`), `.../Sync/RunePagePlannerTests.cs`

**Interfaces:**
- Consumes: `ILcuClient`, `LcuException` (Task 6).
- Produces:
  - `sealed record Account(string Puuid, string RiotId, long SummonerId)`. `RiotId` is `gameName#tagLine`.
  - `interface ILeagueGateway` with: `Task<Account?> GetCurrentAccountAsync(ct)` (null on 404 = not logged in), `Task<string> GetGameflowPhaseAsync(ct)`, `Task<JsonObject> GetGameSettingsAsync(ct)`, `Task PatchGameSettingsAsync(JsonObject, ct)`, `Task<JsonObject> GetInputSettingsAsync(ct)`, `Task PatchInputSettingsAsync(JsonObject, ct)`, `Task SaveGameSettingsAsync(ct)`, `Task<JsonArray> GetEditableRunePagesAsync(ct)` (`isEditable && isDeletable`), `Task<int> GetRunePageCapacityAsync(ct)` (inventory `ownedPageCount`), `Task DeleteRunePageAsync(long id, ct)`, `Task CreateRunePageAsync(JsonObject page, ct)`, `Task<JsonArray> GetItemSetsAsync(Account, ct)`, `Task PutItemSetsAsync(Account, JsonArray sets, ct)`, `Task<JsonObject?> GetClientSettingAsync(string category, ct)`, `Task PatchClientSettingAsync(string category, JsonObject value, ct)`. All `ct` parameters are `CancellationToken ct = default`.
  - Endpoints (pinned): `GET /lol-summoner/v1/current-summoner`; `GET /lol-gameflow/v1/gameflow-phase`; `GET|PATCH /lol-game-settings/v1/game-settings`; `GET|PATCH /lol-game-settings/v1/input-settings`; `POST /lol-game-settings/v1/save`; `GET /lol-perks/v1/pages`; `GET /lol-perks/v1/inventory`; `DELETE /lol-perks/v1/pages/{id}`; `POST /lol-perks/v1/pages`; `GET|PUT /lol-item-sets/v1/item-sets/{summonerId}/sets` (PUT body `{accountId: summonerId, itemSets, timestamp: unix ms}`); `GET|PATCH /lol-settings/v2/account/LCUPreferences/{category}`.
  - `static class RunePagePlanner { RunePagePlan Plan(JsonArray mainPages, JsonArray targetEditablePages, int capacity); }` and `sealed record RunePagePlan(IReadOnlyList<long> DeleteIds, IReadOnlyList<JsonObject> Create, IReadOnlyList<string> Skipped)`. It deletes all of the target's editable pages and creates the main pages in order up to `capacity`. Each created page is stripped to `name, primaryStyleId, subStyleId, selectedPerkIds` with `current:false`. Names past capacity go in `Skipped`.

- [ ] **Step 1: Write failing tests**: `Current_account_maps_riot_id` (`{"puuid":"p","gameName":"Dex","tagLine":"NA1","summonerId":7}` → `Account("p","Dex#NA1",7)`), `Not_logged_in_returns_null` (fake throws 404), `Item_sets_put_uses_target_account_id`, `Planner_fills_up_to_capacity_and_reports_skipped` (main has 4 pages and capacity is 3 → 3 creates, `Skipped == ["Page4"]`, every target id deleted), `Planner_strips_server_fields` (created page has no `id`/`lastModified`/`isActive`).
- [ ] **Step 2:** Run them. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run them. Expected: PASS.
- [ ] **Step 5:** Commit `feat(core): league gateway and rune page planner`.

### Task 8: SyncEngine push path

**Files:**
- Create: `src/LolConfigSync.Core/Sync/SyncEngine.cs`, `Sync/SyncStatus.cs`
- Create (test support): `tests/LolConfigSync.Core.Tests/Fakes/FakeLeagueGateway.cs`, `Fakes/FakeVideoFile.cs`
- Test: `tests/LolConfigSync.Core.Tests/Sync/SyncEnginePushTests.cs`

**Interfaces:**
- Consumes: Tasks 3–7.
- Produces:
  - `enum SyncState { WaitingForClient, Connected, WaitingForIdle, Syncing, Synced, Error }`
  - `sealed record SyncStatus(SyncState State, Account? Account, DateTimeOffset? LastSync, IReadOnlyList<string> Warnings)`
  - `sealed class SyncEngine(IVideoSettingsFile video, AppDataStore store, TimeProvider time)` with:
    - `SyncStatus Status`, `event Action<SyncStatus>? StatusChanged`, `event Action<string>? Notify`
    - `Func<Account, Task<bool>>? FirstRunPrompt { get; set; }` (set by the UI)
    - `Task OnConnectedAsync(ILeagueGateway gateway, CancellationToken ct)`, `void OnDisconnected()`
    - `Task HandleLcuEventAsync(LcuEvent e, CancellationToken ct)`
    - `Task PushMainSetupAsync(CancellationToken ct)`
    - `public static readonly IReadOnlySet<string> IdlePhases = { "None", "Lobby" }`
  - `FakeLeagueGateway`: in-memory state for the current account (settings, pages, item sets, client settings, phase); records every write as `List<string> Calls` (e.g. `"PATCH game"`, `"DELETE rune 5"`); `HashSet<string> FailingCategories` (values `"game"`, `"input"`, `"save"`, `"runes"`, `"itemsets"`, `"client"`) makes the matching writes throw `LcuException(500, ...)`; `SwitchAccount(Account, state)`.

- [ ] **Step 1: Write failing tests**
  - `First_run_prompt_yes_adopts_account`: no main setup and prompt returns true → `store.LoadMainSetup()` equals the fake's settings and `CapturedFromRiotId == "Dex#NA1"`; no writes in `Calls`.
  - `First_run_prompt_no_does_nothing`.
  - `Login_pushes_filtered_setup_and_saves`: main has HUD and Volume, Sound is disabled → the `PATCH game` body has `HUD` and no `Volume`; `Calls` contains `"POST save"`; `Notify` receives `"Synced your setup to Alt#EUW"`.
  - `Excluded_key_never_pushed`.
  - `Backup_written_before_push`: `store.ListBackups()` has 1 entry for the account, holding its *pre-push* values.
  - `Waits_for_idle_phase`: phase is `InProgress` → no writes, state is `WaitingForIdle`. After `HandleLcuEventAsync(new("/lol-gameflow/v1/gameflow-phase","Update","Lobby"))` the push happens.
  - `Failing_category_is_isolated`: `FailingCategories={"runes"}` → game, input, item sets and client pushes still happen; `Status.Warnings` contains a message naming "Rune pages"; state is `Synced`.
  - `Rune_capacity_shortfall_reported`: the warning lists the skipped page names.
  - `Video_written_when_enabled_and_differs`: `FakeVideoFile.Writes == 1`; zero when equal or Video is disabled.
  - `Account_switch_triggers_new_push`: a `/lol-summoner/v1/current-summoner` Update event with a different puuid → a second backup plus push for the new account.
- [ ] **Step 2:** Run `--filter "FullyQualifiedName~SyncEnginePush"`. Expected: FAIL.
- [ ] **Step 3:** Implement. On login: check the main setup (or prompt), wait for idle, capture the current account's full unfiltered snapshot and `store.SaveBackup` it, then push each enabled category inside its own `try/catch (LcuException)` that adds a warning. Push order: game PATCH, input PATCH, `save`, video, runes (plan → deletes → creates), item sets, client categories from the allowlist that exist in the main setup. Record `_pushCompletedAt = time.GetUtcNow()` and the pushed account's puuid. Serialize all engine work with a `SemaphoreSlim(1)`.
- [ ] **Step 4:** Run them. Expected: PASS.
- [ ] **Step 5:** Commit `feat(core): sync engine push path`.

### Task 9: SyncEngine capture path and user actions

**Files:**
- Modify: `src/LolConfigSync.Core/Sync/SyncEngine.cs`
- Test: `tests/LolConfigSync.Core.Tests/Sync/SyncEngineCaptureTests.cs`

**Interfaces:**
- Produces (added to `SyncEngine`): `Task AdoptCurrentAccountAsMainAsync(CancellationToken ct)`, `Task RestoreBackupAsync(string backupPath, CancellationToken ct)` (pushes the snapshot unfiltered, only if the current puuid matches the backup, otherwise throws `InvalidOperationException`), `Task SaveEditedSetupAsync(SetupSnapshot edited, AppSettings settings, CancellationToken ct)` (saves both and pushes if connected and idle), `public static readonly TimeSpan CaptureDebounce = TimeSpan.FromSeconds(3)`, `PostPushSettle = TimeSpan.FromSeconds(5)`.

- [ ] **Step 1: Write failing tests** (all use `FakeTimeProvider`)
  - `Game_setting_change_captured_after_debounce`: after the push, `Advance(6s)`, then send a game-settings Update with `HUD.GlobalScale=0.9`. At `Advance(2s)` the main setup is unchanged; at `Advance(2s)` more, the main setup has 0.9.
  - `Events_before_push_completes_are_not_captured`: an Update with the account's old values arrives while the push is pending (phase `InProgress`), then the push runs → main setup unchanged.
  - `Events_within_settle_window_ignored`: an Update at +2 s after the push → not captured.
  - `Excluded_and_disabled_keys_not_captured`.
  - `Unchanged_values_produce_no_save` (store file timestamp unchanged).
  - `Rune_pages_change_replaces_main_pages`: a `/lol-perks/v1/pages` event → after debounce, the main setup's `RunePages` equals the fake's editable pages.
  - `Item_set_and_allowlisted_client_category_captured`; `Non_allowlisted_client_category_ignored` (`lol-loot`).
  - `Video_captured_when_leaving_game`: phase events `InProgress` → `EndOfGame` while `FakeVideoFile` holds new values → main setup's Video is updated.
  - `Restore_rejects_other_account`.
  - `Adopt_replaces_main_setup`.
- [ ] **Step 2:** Run `--filter "FullyQualifiedName~SyncEngineCapture"`. Expected: FAIL.
- [ ] **Step 3:** Implement. Capture is allowed only when a push for the *current* puuid completed and `now >= _pushCompletedAt + PostPushSettle`. Incoming data is filtered with `SettingsFilter`, diffed against the filtered main setup, and dropped if empty. Pending changes are coalesced per category, and a timer is restarted with `time.CreateTimer(..., CaptureDebounce, Timeout.InfiniteTimeSpan)`. On flush, merge or replace into the main setup, set `CapturedAt`, and save.
- [ ] **Step 4:** Run all tests. Expected: PASS.
- [ ] **Step 5:** Commit `feat(core): sync engine capture path and actions`.

### Task 10: Editor rows and labels

**Files:**
- Create: `src/LolConfigSync.Core/Editing/SettingRow.cs`, `Editing/SettingRows.cs`, `Editing/SettingLabels.cs`
- Test: `tests/LolConfigSync.Core.Tests/Editing/SettingRowsTests.cs`

**Interfaces:**
- Produces: `sealed class SettingRow` (`SettingKey Key`, `SyncSection Section`, `string Label`, `string Value` settable, `bool Sync` settable; implements `INotifyPropertyChanged`); `static class SettingRows` with `IReadOnlyList<SettingRow> Build(SetupSnapshot, AppSettings)` (one row per game/input/video key, sorted by section then label) and `IReadOnlyList<SettingKey> ApplyTo(SetupSnapshot, AppSettings, IEnumerable<SettingRow>)` (writes values back *keeping the original JSON type*; returns keys whose edited value couldn't be parsed to that type, which are left unchanged; `Sync == false` adds the key to `ExcludedKeys`, `true` removes it); `static class SettingLabels { string For(SettingKey) }` (friendly names for about 40 common keys, falling back to splitting `Name` on camel case: `"GlobalScale"` → `"Global Scale"`).

- [ ] **Step 1: Write failing tests**: `Build_makes_row_per_key_with_section`, `Apply_keeps_bool_and_number_types` (edit `"FlipMiniMap"` to `"false"` → JSON `false`, not `"false"`; `"0.75"` → number), `Invalid_number_edit_is_rejected` (`"abc"` for a number → value unchanged and the key is in the returned list), `Unchecking_sync_adds_exclusion`, `Label_fallback_splits_camel_case`.
- [ ] **Step 2:** Run them. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run them. Expected: PASS.
- [ ] **Step 5:** Commit `feat(core): editor rows and labels`.

### Task 11: Connection watcher and AppHost

**Files:**
- Create: `src/LolConfigSync.Core/Hosting/LcuConnectionWatcher.cs`, `Hosting/AppHost.cs`, `Hosting/FileLog.cs`
- Test: `tests/LolConfigSync.Core.Tests/Hosting/LcuConnectionWatcherTests.cs`

**Interfaces:**
- Consumes: everything in Core.
- Produces:
  - `sealed class LcuConnectionWatcher(string installDir, TimeProvider time, Func<LockfileInfo, Task<bool>> tryConnect) : IDisposable` with `void Start()`, `event Action? Disconnected`, and `bool IsConnected`. It polls `<installDir>\lockfile` every 2 s, opened with `FileShare.ReadWrite | FileShare.Delete`. It calls `tryConnect` when a new or changed lockfile appears. If that returns false (stale lockfile), it retries on the next poll. When the lockfile disappears, it raises `Disconnected`.
  - `sealed class AppHost : IAsyncDisposable` with `AppHost(AppDataStore store, string installDir, TimeProvider time)`, `SyncEngine Engine`, `Task StartAsync()`. On connect it builds `LcuHttpClient`, `LcuEventStream` and `LeagueGateway`, then calls `Engine.OnConnectedAsync`, and forwards `Received` to `Engine.HandleLcuEventAsync`. On the first connect it backs up `Config\PersistedSettings.json` to `<store root>\PersistedSettings.readonly-backup.json` if it's read-only, then calls `GameCfgFile.ClearReadOnly`.
  - `static class FileLog { void Init(string path); void Info(string); void Error(string, Exception?); }`, a rolling 1 MB log with one `.old` file.

- [ ] **Step 1: Write failing tests**: `Connects_when_lockfile_appears`, `Stale_lockfile_retries` (`tryConnect` returns false, then true → called twice across two 2 s advances, connected only once), `Disconnects_when_lockfile_removed`, `Reconnects_when_lockfile_changes` (new port).
- [ ] **Step 2:** Run them. Expected: FAIL.
- [ ] **Step 3:** Implement. `AppHost` isn't unit tested. It gets covered by the live checklist in Task 14.
- [ ] **Step 4:** Run them. Expected: PASS.
- [ ] **Step 5:** Commit `feat(core): connection watcher and app host`.

### Task 12: WPF shell (tray, main window, single instance, startup)

**Files:**
- Create: `src/LolConfigSync.App/App.xaml(.cs)`, `MainWindow.xaml(.cs)`, `ViewModels/MainViewModel.cs`, `ViewModels/RelayCommand.cs`, `Platform/SingleInstance.cs`, `Platform/StartupRegistration.cs`, `Assets/app.ico`
- Test: `tests/LolConfigSync.Core.Tests` doesn't cover WPF. `SingleInstance` lives in App, so it's verified manually (below).

**Interfaces:**
- Consumes: `AppHost`, `SyncEngine`, `SyncStatus`, `AppSettings`, `AppDataStore`.
- Produces: `static class StartupRegistration { void Apply(bool enabled, string exePath); }`; `sealed class SingleInstance : IDisposable { static SingleInstance? TryAcquire(Action onShowRequested); static void SignalExisting(); }` (Mutex `Local\LolConfigSync`, EventWaitHandle `Local\LolConfigSync.Show`); `MainViewModel` exposes `StatusText`, `AccountText`, `CapturedFromText`, `Warnings`, eight `bool` toggle properties (each writes `AppSettings` and saves), `StartWithWindows`, and the commands `EditCommand`, `UseThisAccountCommand`, `SyncNowCommand`, `BackupsCommand`.

- [ ] **Step 1:** Build the App startup. Acquire `SingleInstance`, or signal and exit. Start `FileLog`, `AppDataStore(AppDataStore.DefaultRoot)`, `LeagueInstallLocator.LocateOnThisMachine()` (use `AppSettings.InstallDirOverride` first; if both are null, show a folder picker once and save the choice there), and `AppHost.StartAsync()`. With `--tray`, don't show the window. Wire `Engine.FirstRunPrompt` to a `MessageBox` "Use {RiotId}'s settings as your main setup?" via `Dispatcher.InvokeAsync`, and `Engine.Notify` to a tray balloon when `ShowNotifications` is on. Apply `StartupRegistration` from settings on every launch.
- [ ] **Step 2:** Build the tray icon (`TaskbarIcon`). Tooltip is the status text; double-click opens the window; context menu has Open, Sync now, Quit. Closing the window hides it (`e.Cancel = true`); Quit disposes `AppHost` and shuts down.
- [ ] **Step 3:** Lay out `MainWindow` as in the spec mockup: a status line, a captured-from line, the 8 toggles in a wrap panel, the buttons, a Start with Windows checkbox, and a warnings list. Marshal engine events to the UI thread.
- [ ] **Step 4:** `dotnet build`, then run the app with the League client open. Expected: tray icon appears, status reads `Connected — <RiotId>`, a second launch brings the existing window forward and exits, and toggling Start with Windows adds or removes the Run value (`reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v LolConfigSync`).
- [ ] **Step 5:** Commit `feat(app): tray shell, main window, single instance, startup`.

### Task 13: Editor and Backups windows

**Files:**
- Create: `src/LolConfigSync.App/EditorWindow.xaml(.cs)`, `ViewModels/EditorViewModel.cs`, `BackupsWindow.xaml(.cs)`, `ViewModels/BackupsViewModel.cs`

**Interfaces:**
- Consumes: `SettingRows`, `SettingRow`, `SettingLabels` (Task 10); `SyncEngine.SaveEditedSetupAsync`, `RestoreBackupAsync` (Task 9); `AppDataStore.ListBackups` (Task 4).

- [ ] **Step 1:** Build the Editor. It works on a `DeepClone()` of the main setup. Tabs are Hotkeys, Sound, Interface, Game, Video (each a `DataGrid` filtered by `Section` with columns Sync, Setting, Value) plus an All tab with a search box filtering on label or key. Save calls `ApplyTo`, shows rejected keys if there are any, otherwise calls `SaveEditedSetupAsync` and closes. Cancel discards. If no main setup exists yet, the button is disabled with the tooltip "Log into an account first".
- [ ] **Step 2:** Build Backups. A list grouped by RiotId shows timestamps. Restore is enabled only for the logged-in account's entries, asks for confirmation, then calls `RestoreBackupAsync` and reports success or failure.
- [ ] **Step 3:** `dotnet build`, then run. Expected: editing `Global Scale` and saving changes it in the client's in-game settings on the next game (or right away in `/lol-game-settings/v1/game-settings`); unchecking Sync on a row persists across restarts; Restore puts back the values from that backup.
- [ ] **Step 4:** Commit `feat(app): settings editor and backups window`.

### Task 14: Publish and live checklist

**Files:**
- Modify: `src/LolConfigSync.App/LolConfigSync.App.csproj` (`PublishSingleFile true`, `SelfContained false`, `RuntimeIdentifier win-x64`, `ApplicationIcon Assets\app.ico`)
- Create: `README.md` (what it does, how to build and publish, where data lives, how to undo: delete the Run value and `%APPDATA%\LolConfigSync`, restore backups)

- [ ] **Step 1:** `dotnet publish src/LolConfigSync.App -c Release`. Expected: a single `LolConfigSync.App.exe` under `bin\Release\net10.0-windows\win-x64\publish\`. Check that the .NET 10 Desktop Runtime is installed (`dotnet --list-runtimes`); if it's missing, switch to `SelfContained true`.
- [ ] **Step 2:** Run the live checklist with the real client and record results in the README's "Verified" section:
  1. First run on account A, answer Yes → `main-setup.json` exists and `PersistedSettings.json` is no longer read-only.
  2. Log out, log into account B → toast appears, B's hotkeys/HUD/camera match A in the in-game settings, and a backup exists for B.
  3. On B, change a hotkey in-game and finish a game → `main-setup.json` updates. Log into A → A gets the new hotkey.
  4. Start a game, then press Sync now → nothing is written until the game ends.
  5. Disable Sound, change volume on B, log into A → A's volume unchanged.
  6. Restore B's backup while on B → B's original settings come back.
  7. Close and reopen the League client → reconnects without restarting the app.
- [ ] **Step 3:** Commit `chore: publish config, README and verified checklist`.
