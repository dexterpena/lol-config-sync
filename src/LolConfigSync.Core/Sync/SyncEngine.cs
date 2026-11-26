using System.Text.Json;
using System.Text.Json.Nodes;
using LolConfigSync.Core.Files;
using LolConfigSync.Core.Hosting;
using LolConfigSync.Core.Lcu;
using LolConfigSync.Core.League;
using LolConfigSync.Core.Model;
using LolConfigSync.Core.Storage;

namespace LolConfigSync.Core.Sync;

/// <summary>
/// Pushes the main setup to whichever account logs in and captures the user's changes back.
/// All state changes are serialized behind one lock; call it from any thread.
/// </summary>
/// <remarks>
/// Safety rules: nothing is captured from an account until our push to it has landed and settled,
/// and only for the sections that push actually wrote and League kept. Account changes and
/// disconnects cancel in-flight work so stale results never apply to the wrong account.
/// </remarks>
public sealed class SyncEngine(IVideoSettingsFile video, AppDataStore store, TimeProvider time) : IDisposable
{
    public static readonly IReadOnlySet<string> IdlePhases = new HashSet<string> { "None", "Lobby" };
    public static readonly TimeSpan CaptureDebounce = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan PostPushSettle = TimeSpan.FromSeconds(5);
    private static readonly IReadOnlySet<string> InGamePhases = new HashSet<string> { "InProgress", "Reconnect" };
    private static readonly SyncSection[] AllSections = Enum.GetValues<SyncSection>();

    /// <summary>The independently read parts of an account, used to track partial read failures.</summary>
    private enum Part { Game, Input, Video, Runes, ItemSets, Client }

    private readonly SemaphoreSlim _lock = new(1, 1);
    private ILeagueGateway? _gateway;
    private CancellationTokenSource? _connectionCts;
    private CancellationTokenSource? _loginCts;
    private Account? _account;
    private bool _pushPending;
    private bool _promptPending;
    private string _phase = "None";
    private SyncStatus _status = SyncStatus.Initial;

    // Capture is allowed only for the account we last pushed to, after the settle window,
    // and only for sections that push wrote successfully.
    private string? _pushedPuuid;
    private DateTimeOffset _pushCompletedAt;
    private readonly HashSet<SyncSection> _pushedSections = [];
    private int? _runeCapacityShortfall;

    private readonly Dictionary<SettingsKind, JsonObject> _pendingValues = [];
    private readonly HashSet<string> _pendingClient = [];
    private bool _pendingRunes;
    private bool _pendingItemSets;
    private ITimer? _flushTimer;

    public SyncStatus Status => _status;
    public event Action<SyncStatus>? StatusChanged;

    /// <summary>A short message worth showing the user (e.g. a tray toast).</summary>
    public event Action<string>? Notify;

    /// <summary>
    /// Asked on first run (only while out of a game): should this account's settings become the main setup?
    /// The token is cancelled if the account changes or the client disconnects while the question is open.
    /// </summary>
    public Func<Account, CancellationToken, Task<bool>>? FirstRunPrompt { get; set; }

    /// <summary>The most recent debounced capture flush (for tests).</summary>
    internal Task PendingFlush { get; private set; } = Task.CompletedTask;

    /// <summary>The most recent first-run prompt (for tests).</summary>
    internal Task PendingPrompt { get; private set; } = Task.CompletedTask;

    // ================= connection lifecycle =================

    public async Task OnConnectedAsync(ILeagueGateway gateway, CancellationToken ct)
    {
        var connection = new CancellationTokenSource();
        await LockedAsync(async token =>
        {
            ResetConnection();
            _connectionCts = connection;
            _gateway = gateway;
            SetStatus(SyncState.Connected, null, warnings: []);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, connection.Token);
            var account = await gateway.GetCurrentAccountAsync(linked.Token).ConfigureAwait(false);
            if (account is not null) await HandleLoginAsync(account, linked.Token).ConfigureAwait(false);
        }, ct, useConnectionToken: false).ConfigureAwait(false);
    }

    /// <summary>Cancels in-flight work for the old connection, then resets state.</summary>
    public async Task OnDisconnectedAsync()
    {
        var connection = _connectionCts;
        if (connection is not null) await connection.CancelAsync().ConfigureAwait(false);
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            ResetConnection();
            SetStatus(SyncState.WaitingForClient, null, warnings: []);
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task HandleLcuEventAsync(LcuEvent e, CancellationToken ct) => LockedAsync(async token =>
    {
        if (_gateway is null) return;
        switch (e.Uri)
        {
            case LeagueGateway.CurrentSummoner:
                await OnSummonerEventAsync(e, token).ConfigureAwait(false);
                break;
            case LeagueGateway.GameflowPhase:
                await OnPhaseEventAsync(e, token).ConfigureAwait(false);
                break;
            case LeagueGateway.GameSettingsReady:
                if (_pushPending && e.Data?.GetValueKind() == JsonValueKind.True) await TryPushAsync(token).ConfigureAwait(false);
                break;
            case LeagueGateway.GameSettings:
                QueueValues(SettingsKind.Game, e.Data);
                break;
            case LeagueGateway.InputSettings:
                QueueValues(SettingsKind.Input, e.Data);
                break;
            case LeagueGateway.RunePages:
                QueueFlag(SyncSection.RunePages, () => _pendingRunes = true);
                break;
            case var uri when uri.StartsWith(LeagueGateway.ItemSetsPrefix, StringComparison.Ordinal):
                QueueFlag(SyncSection.ItemSets, () => _pendingItemSets = true);
                break;
            case var uri when uri.StartsWith(LeagueGateway.ClientSettingsPrefix, StringComparison.Ordinal):
                var category = uri[LeagueGateway.ClientSettingsPrefix.Length..];
                if (SectionMap.ClientSettingsCategories.Contains(category))
                {
                    QueueFlag(SyncSection.ClientSettings, () => _pendingClient.Add(category));
                }
                break;
        }
    }, ct);

    // ================= user actions =================

    /// <summary>"Sync now": push the main setup to the current account (waits until out of a game).</summary>
    public Task PushMainSetupAsync(CancellationToken ct) => LockedAsync(async token =>
    {
        if (_gateway is null || _account is null) return;
        _pushPending = true;
        await TryPushAsync(token).ConfigureAwait(false);
    }, ct);

    /// <summary>"Use this account as main": replace the main setup with the current account's settings.</summary>
    public Task AdoptCurrentAccountAsMainAsync(CancellationToken ct) => LockedAsync(async token =>
    {
        if (_gateway is null || _account is null) throw new InvalidOperationException("Log into an account first.");
        ClearPending();
        await AdoptAsync(_gateway, _account, token).ConfigureAwait(false);
    }, ct);

    /// <summary>
    /// Pushes a backup back to its own account, unfiltered. Capturing is then paused for that
    /// account until the next login or "Sync now", so the restored values don't leak into the main setup.
    /// </summary>
    public Task RestoreBackupAsync(string backupPath, CancellationToken ct) => LockedAsync(async token =>
    {
        if (_gateway is null || _account is null) throw new InvalidOperationException("Log into the account first.");
        var backup = store.LoadBackup(backupPath);
        if (backup.CapturedFromPuuid != _account.Puuid)
        {
            throw new InvalidOperationException($"This backup belongs to {backup.CapturedFromRiotId}. Log into that account to restore it.");
        }
        _phase = await _gateway.GetGameflowPhaseAsync(token).ConfigureAwait(false);
        if (!IdlePhases.Contains(_phase)) throw new InvalidOperationException("Finish your game first, then restore.");

        ClearPending();
        _pushPending = false;
        await PushAsync(_gateway, _account, backup, new AppSettings(), token, $"Restored backup to {_account.RiotId}").ConfigureAwait(false);
        _pushedPuuid = null;
    }, ct);

    /// <summary>Saves the user's edits and pushes them right away when an account is logged in and idle.</summary>
    public Task SaveEditedSetupAsync(SetupSnapshot edited, AppSettings settings, CancellationToken ct) => LockedAsync(async token =>
    {
        store.SaveSettings(settings);
        edited.CapturedAt = time.GetUtcNow();
        store.SaveMainSetup(edited);
        if (_gateway is null || _account is null) return;
        ClearPending();
        _pushPending = true;
        await TryPushAsync(token).ConfigureAwait(false);
    }, ct);

    public void Dispose()
    {
        _flushTimer?.Dispose();
        _loginCts?.Dispose();
        _connectionCts?.Dispose();
        _lock.Dispose();
    }

    // ================= login / phase =================

    private async Task OnSummonerEventAsync(LcuEvent e, CancellationToken ct)
    {
        var puuid = e.Data?["puuid"]?.GetValueKind() == JsonValueKind.String ? e.Data["puuid"]!.GetValue<string>() : null;
        if (e.EventType != "Delete" && !string.IsNullOrEmpty(puuid) && puuid == _account?.Puuid) return;

        // Forget the old account first, so nothing can be captured into it while we look up the new one.
        ResetAccount();
        SetStatus(SyncState.Connected, null, warnings: []);
        if (e.EventType == "Delete" || string.IsNullOrEmpty(puuid)) return;

        var account = await _gateway!.GetCurrentAccountAsync(ct).ConfigureAwait(false);
        if (account is not null) await HandleLoginAsync(account, ct).ConfigureAwait(false);
    }

    private async Task OnPhaseEventAsync(LcuEvent e, CancellationToken ct)
    {
        if (e.Data?.GetValueKind() != JsonValueKind.String) return;
        var previous = _phase;
        _phase = e.Data.GetValue<string>();

        // Graphics are only written to game.cfg by the game itself, so check them when a game ends.
        if (InGamePhases.Contains(previous) && !InGamePhases.Contains(_phase))
        {
            try
            {
                QueueValues(SettingsKind.Video, video.Read());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        if (!IdlePhases.Contains(_phase)) return;
        if (_promptPending) await TryPromptAsync(ct).ConfigureAwait(false);
        else if (_pushPending) await TryPushAsync(ct).ConfigureAwait(false);
    }

    private async Task HandleLoginAsync(Account account, CancellationToken ct)
    {
        ResetAccount();
        _account = account;
        _loginCts = CancellationTokenSource.CreateLinkedTokenSource(_connectionCts?.Token ?? CancellationToken.None);
        SetStatus(SyncState.Connected, account, warnings: []);

        if (store.LoadMainSetup() is null)
        {
            _promptPending = true;
            await TryPromptAsync(ct).ConfigureAwait(false);
            return;
        }

        _pushPending = true;
        await TryPushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Asks the first-run question once out of a game. The question runs outside the lock.</summary>
    private async Task TryPromptAsync(CancellationToken ct)
    {
        if (_gateway is null || _account is null || FirstRunPrompt is null) return;
        _phase = await _gateway.GetGameflowPhaseAsync(ct).ConfigureAwait(false);
        if (!IdlePhases.Contains(_phase))
        {
            SetStatus(SyncState.WaitingForIdle, _account);
            return;
        }
        _promptPending = false;
        PendingPrompt = RunPromptAsync(_account, FirstRunPrompt, _loginCts!.Token);
    }

    private async Task RunPromptAsync(Account account, Func<Account, CancellationToken, Task<bool>> prompt, CancellationToken loginToken)
    {
        bool yes;
        try
        {
            yes = await prompt(account, loginToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (!yes || loginToken.IsCancellationRequested) return;

        await LockedAsync(async token =>
        {
            // The world may have moved on while the question was open.
            if (loginToken.IsCancellationRequested || _gateway is null || _account?.Puuid != account.Puuid) return;
            if (store.LoadMainSetup() is not null) return;
            await AdoptAsync(_gateway, account, token).ConfigureAwait(false);
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task AdoptAsync(ILeagueGateway gateway, Account account, CancellationToken ct)
    {
        var (snapshot, failed, warnings) = await CaptureAsync(gateway, account, ct).ConfigureAwait(false);
        var previous = store.LoadMainSetup();
        if (previous is null && failed.Contains(Part.Game) && failed.Contains(Part.Input))
        {
            SetStatus(SyncState.Synced, account, warnings: [.. warnings, "Couldn't read this account's settings. Try again in a moment."]);
            return;
        }
        if (previous is not null) KeepPreviousForFailedParts(snapshot, previous, failed);
        snapshot.RunePages = RunePagePlanner.WithoutToolPages(snapshot.RunePages, store.LoadSettings().ToolRunePagePrefixes);

        ct.ThrowIfCancellationRequested();
        store.SaveMainSetup(snapshot);
        MarkPushed(account, AllSections, runeShortfallCapacity: null);
        SetStatus(SyncState.Synced, account, lastSync: time.GetUtcNow(), warnings: warnings);
        Notify?.Invoke($"Main setup saved from {account.RiotId}");
    }

    private static void KeepPreviousForFailedParts(SetupSnapshot snapshot, SetupSnapshot previous, IReadOnlySet<Part> failed)
    {
        if (failed.Contains(Part.Game)) snapshot.GameSettings = previous.GameSettings;
        if (failed.Contains(Part.Input)) snapshot.InputSettings = previous.InputSettings;
        if (failed.Contains(Part.Video)) snapshot.VideoSettings = previous.VideoSettings;
        if (failed.Contains(Part.Runes)) snapshot.RunePages = previous.RunePages;
        if (failed.Contains(Part.ItemSets)) snapshot.ItemSets = previous.ItemSets;
        if (failed.Contains(Part.Client)) snapshot.ClientSettings = previous.ClientSettings;
    }

    // ================= push =================

    private async Task TryPushAsync(CancellationToken ct)
    {
        var gateway = _gateway;
        if (gateway is null || _account is null) return;

        _phase = await gateway.GetGameflowPhaseAsync(ct).ConfigureAwait(false);
        if (!IdlePhases.Contains(_phase) || !await gateway.IsGameSettingsReadyAsync(ct).ConfigureAwait(false))
        {
            SetStatus(SyncState.WaitingForIdle, _account);
            return;
        }

        var main = store.LoadMainSetup();
        if (main is null) return;
        _pushPending = false;
        await PushAsync(gateway, _account, main, store.LoadSettings(), ct).ConfigureAwait(false);
    }

    private async Task PushAsync(ILeagueGateway gateway, Account account, SetupSnapshot source, AppSettings settings, CancellationToken ct,
        string? doneMessage = null)
    {
        SetStatus(SyncState.Syncing, account);

        var (backup, unreadable, warnings) = await CaptureAsync(gateway, account, ct).ConfigureAwait(false);
        try
        {
            store.SaveBackup(backup);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Never change an account we couldn't back up first.
            FileLog.Error($"Backup for {account.RiotId} failed; nothing was changed", e);
            SetStatus(SyncState.Error, account,
                warnings: [$"Couldn't save a backup of {account.RiotId}'s settings, so nothing was changed. {e.Message}"]);
            Notify?.Invoke($"Didn't sync {account.RiotId}: couldn't save a backup first");
            return;
        }
        var pushed = new HashSet<SyncSection>();

        bool Skipped(Part part, string label)
        {
            if (!unreadable.Contains(part)) return false;
            warnings.Add($"{label} skipped: couldn't back them up first.");
            return true;
        }

        // Game settings (Sound, Interface, Game) and hotkeys.
        var game = SettingsFilter.Apply(source.GameSettings, SettingsKind.Game, settings);
        var input = SettingsFilter.Apply(source.InputSettings, SettingsKind.Input, settings);
        var gameOk = game.Count > 0 && !Skipped(Part.Game, "Game settings")
            && await TryAsync("Game settings", warnings, () => gateway.PatchGameSettingsAsync(game, ct)).ConfigureAwait(false);
        var inputOk = input.Count > 0 && !Skipped(Part.Input, "Hotkeys")
            && await TryAsync("Hotkeys", warnings, () => gateway.PatchInputSettingsAsync(input, ct)).ConfigureAwait(false);
        if (gameOk || inputOk)
        {
            await TryAsync("Saving settings", warnings, () => gateway.SaveGameSettingsAsync(ct)).ConfigureAwait(false);
        }
        if (gameOk) await VerifyKeptAsync(SettingsKind.Game, game, () => gateway.GetGameSettingsAsync(ct), pushed, warnings).ConfigureAwait(false);
        if (inputOk) await VerifyKeptAsync(SettingsKind.Input, input, () => gateway.GetInputSettingsAsync(ct), pushed, warnings).ConfigureAwait(false);

        // Local graphics keys.
        if (settings.IsEnabled(SyncSection.Video) && !Skipped(Part.Video, "Graphics")
            && await TryAsync("Graphics", warnings, () =>
            {
                var changes = SettingsDiff.Changes(SettingsFilter.Apply(source.VideoSettings, SettingsKind.Video, settings), video.Read());
                if (changes.Count > 0) video.Write(changes);
                return Task.CompletedTask;
            }).ConfigureAwait(false))
        {
            pushed.Add(SyncSection.Video);
        }

        // Rune pages.
        int? shortfallCapacity = null;
        if (settings.IsEnabled(SyncSection.RunePages) && !Skipped(Part.Runes, "Rune pages"))
        {
            // Tool pages (Porofessor) are never synced; a matching account with one in its free slot is left alone.
            var sourcePages = RunePagePlanner.WithoutToolPages(source.RunePages, settings.ToolRunePagePrefixes);
            var accountPages = RunePagePlanner.WithoutToolPages(backup.RunePages, settings.ToolRunePagePrefixes);
            if (sourcePages.Count == 0 || SamePages(accountPages, sourcePages))
            {
                pushed.Add(SyncSection.RunePages);
            }
            else if (await TryAsync("Rune pages", warnings, async () =>
                     {
                         shortfallCapacity = await PushRunePagesAsync(gateway, sourcePages, settings.ReservedRunePageSlots, warnings, ct)
                             .ConfigureAwait(false);
                     }).ConfigureAwait(false))
            {
                pushed.Add(SyncSection.RunePages);
            }
        }

        // Item sets.
        if (settings.IsEnabled(SyncSection.ItemSets) && !Skipped(Part.ItemSets, "Item sets"))
        {
            if (source.ItemSets.Count == 0
                || await TryAsync("Item sets", warnings, () => gateway.PutItemSetsAsync(account, source.ItemSets, ct)).ConfigureAwait(false))
            {
                pushed.Add(SyncSection.ItemSets);
            }
        }

        // Client preferences.
        if (settings.IsEnabled(SyncSection.ClientSettings) && !Skipped(Part.Client, "Client settings"))
        {
            var allOk = true;
            foreach (var category in SectionMap.ClientSettingsCategories)
            {
                if (source.ClientSettings[category] is not JsonObject value) continue;
                allOk &= await TryAsync($"Client settings ({category})", warnings,
                    () => gateway.PatchClientSettingAsync(category, (JsonObject)value.DeepClone(), ct)).ConfigureAwait(false);
            }
            if (allOk) pushed.Add(SyncSection.ClientSettings);
        }

        ct.ThrowIfCancellationRequested(); // the client went away mid-push: don't report success
        MarkPushed(account, pushed, shortfallCapacity);
        SetStatus(SyncState.Synced, account, lastSync: time.GetUtcNow(), warnings: warnings);
        foreach (var w in warnings) FileLog.Info($"Sync to {account.RiotId}: {w}");

        var message = doneMessage ?? $"Synced your setup to {account.RiotId}";
        Notify?.Invoke(warnings.Count == 0
            ? message
            : $"{message} ({warnings.Count} warning{(warnings.Count == 1 ? "" : "s")})");
    }

    /// <summary>
    /// Reads settings back after a push. Sections League didn't keep are excluded from capture,
    /// so the account's own values can't leak into the main setup.
    /// </summary>
    private static async Task VerifyKeptAsync(SettingsKind kind, JsonObject pushedValues, Func<Task<JsonObject>> read,
        HashSet<SyncSection> pushed, List<string> warnings)
    {
        var sections = pushedValues.Select(kv => SectionMap.SectionFor(kind, kv.Key)).ToHashSet();
        JsonObject actual;
        try
        {
            actual = await read().ConfigureAwait(false);
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            return; // can't verify: leave capture off for these sections
        }

        var notKept = SettingsDiff.Changes(pushedValues, actual);
        var failedSections = notKept.Select(kv => SectionMap.SectionFor(kind, kv.Key)).ToHashSet();
        pushed.UnionWith(sections.Except(failedSections));
        if (failedSections.Count > 0)
        {
            warnings.Add($"League didn't keep some {string.Join(", ", failedSections)} settings on this account. Press Sync now to try again.");
        }
    }

    /// <summary>
    /// Replaces the account's pages, leaving <paramref name="reservedSlots"/> empty (any tool page is cleared to free
    /// its slot; the tool recreates it in champ select). Returns the usable slot count when not all pages fit.
    /// </summary>
    private static async Task<int?> PushRunePagesAsync(ILeagueGateway gateway, JsonArray mainPages, int reservedSlots,
        List<string> warnings, CancellationToken ct)
    {
        var target = await gateway.GetEditableRunePagesAsync(ct).ConfigureAwait(false);
        var capacity = await gateway.GetRunePageCapacityAsync(ct).ConfigureAwait(false);
        var plan = RunePagePlanner.Plan(mainPages, target, capacity, reservedSlots);
        foreach (var id in plan.DeleteIds) await gateway.DeleteRunePageAsync(id, ct).ConfigureAwait(false);
        foreach (var page in plan.Create) await gateway.CreateRunePageAsync(page, ct).ConfigureAwait(false);
        if (plan.Skipped.Count == 0) return null;
        var reason = reservedSlots > 0 ? $" ({reservedSlots} slot{(reservedSlots == 1 ? " is" : "s are")} kept free for Porofessor)" : "";
        warnings.Add($"Rune pages: no room on this account for {string.Join(", ", plan.Skipped)}{reason}");
        return plan.UsableSlots;
    }

    private void MarkPushed(Account account, IEnumerable<SyncSection> sections, int? runeShortfallCapacity)
    {
        _pushedPuuid = account.Puuid;
        _pushCompletedAt = time.GetUtcNow();
        _pushedSections.Clear();
        _pushedSections.UnionWith(sections);
        _runeCapacityShortfall = runeShortfallCapacity;
    }

    // ================= capture (debounced) =================

    private bool CaptureOpen =>
        _account is not null && _pushedPuuid == _account.Puuid && time.GetUtcNow() >= _pushCompletedAt + PostPushSettle;

    private void QueueValues(SettingsKind kind, JsonNode? data)
    {
        if (!CaptureOpen || data is not JsonObject values) return;
        var main = store.LoadMainSetup();
        if (main is null) return;

        var settings = store.LoadSettings();
        var incoming = OnlyPushedSections(SettingsFilter.Apply(values, kind, settings), kind);
        var changes = SettingsDiff.Changes(incoming, SettingsFilter.Apply(main.Settings(kind), kind, settings));
        if (changes.Count == 0) return;

        if (!_pendingValues.TryGetValue(kind, out var pending))
        {
            pending = [];
            _pendingValues[kind] = pending;
        }
        SettingsDiff.MergeInto(pending, changes);
        ScheduleFlush();
    }

    private JsonObject OnlyPushedSections(JsonObject values, SettingsKind kind)
    {
        foreach (var section in values.Select(kv => kv.Key).ToList())
        {
            if (!_pushedSections.Contains(SectionMap.SectionFor(kind, section))) values.Remove(section);
        }
        return values;
    }

    private void QueueFlag(SyncSection section, Action mark)
    {
        if (!CaptureOpen || !_pushedSections.Contains(section) || !store.LoadSettings().IsEnabled(section)) return;
        mark();
        ScheduleFlush();
    }

    private void ScheduleFlush()
    {
        if (_flushTimer is null)
        {
            _flushTimer = time.CreateTimer(_ => PendingFlush = FlushAsync(), null, CaptureDebounce, Timeout.InfiniteTimeSpan);
        }
        else
        {
            _flushTimer.Change(CaptureDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void ClearPending()
    {
        _pendingValues.Clear();
        _pendingClient.Clear();
        _pendingRunes = false;
        _pendingItemSets = false;
        _flushTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private Task FlushAsync() => LockedAsync(async ct =>
    {
        var gateway = _gateway;
        var account = _account;
        var main = store.LoadMainSetup();
        if (account is null || main is null || !CaptureOpen)
        {
            ClearPending();
            return;
        }

        var changed = false;
        foreach (var (kind, values) in _pendingValues)
        {
            SettingsDiff.MergeInto(main.Settings(kind), values);
            changed = true;
        }

        var warnings = new List<string>();
        if (gateway is not null)
        {
            if (_pendingRunes)
            {
                await TryAsync("Saving rune pages", warnings, async () =>
                {
                    var pages = RunePagePlanner.WithoutToolPages(
                        await gateway.GetEditableRunePagesAsync(ct).ConfigureAwait(false), store.LoadSettings().ToolRunePagePrefixes);
                    var merged = MergeCapturedPages(pages, main.RunePages);
                    if (!SamePages(merged, main.RunePages)) { main.RunePages = merged; changed = true; }
                }).ConfigureAwait(false);
            }
            if (_pendingItemSets)
            {
                await TryAsync("Saving item sets", warnings, async () =>
                {
                    var sets = await gateway.GetItemSetsAsync(account, ct).ConfigureAwait(false);
                    if (!JsonNode.DeepEquals(sets, main.ItemSets)) { main.ItemSets = sets; changed = true; }
                }).ConfigureAwait(false);
            }
            foreach (var category in _pendingClient)
            {
                await TryAsync($"Saving client settings ({category})", warnings, async () =>
                {
                    var value = await gateway.GetClientSettingAsync(category, ct).ConfigureAwait(false);
                    if (value is not null && !JsonNode.DeepEquals(value, main.ClientSettings[category]))
                    {
                        main.ClientSettings[category] = value;
                        changed = true;
                    }
                }).ConfigureAwait(false);
            }
        }
        ClearPending();
        ct.ThrowIfCancellationRequested();

        if (changed)
        {
            main.CapturedAt = time.GetUtcNow();
            main.CapturedFromPuuid = account.Puuid;
            main.CapturedFromRiotId = account.RiotId;
            store.SaveMainSetup(main);
        }
        if (changed || warnings.Count > 0)
        {
            SetStatus(_status.State, account, lastSync: changed ? time.GetUtcNow() : null, warnings: warnings.Count > 0 ? warnings : null);
        }
    }, CancellationToken.None);

    /// <summary>
    /// On an account that couldn't fit every main page, the account only holds the first <c>capacity</c>
    /// pages; keep the main setup's remaining pages instead of dropping them.
    /// </summary>
    private JsonArray MergeCapturedPages(JsonArray captured, JsonArray mainPages)
    {
        if (_runeCapacityShortfall is not { } capacity) return captured;
        return [.. captured.Select(p => p?.DeepClone()), .. mainPages.Skip(capacity).Select(p => p?.DeepClone())];
    }

    /// <summary>Compares the parts of rune pages we sync, ignoring server ids and timestamps.</summary>
    private static bool SamePages(JsonArray a, JsonArray b) =>
        JsonNode.DeepEquals(
            new JsonArray([.. RunePagePlanner.Plan(a, [], int.MaxValue).Create]),
            new JsonArray([.. RunePagePlanner.Plan(b, [], int.MaxValue).Create]));

    // ================= reading an account =================

    /// <summary>Reads everything the account has (unfiltered). Parts that fail are left empty and reported.</summary>
    private async Task<(SetupSnapshot Snapshot, HashSet<Part> Failed, List<string> Warnings)> CaptureAsync(
        ILeagueGateway gateway, Account account, CancellationToken ct)
    {
        var warnings = new List<string>();
        var failed = new HashSet<Part>();
        var snapshot = new SetupSnapshot
        {
            CapturedAt = time.GetUtcNow(),
            CapturedFromPuuid = account.Puuid,
            CapturedFromRiotId = account.RiotId,
        };

        async Task Read(Part part, string label, Func<Task> action)
        {
            if (!await TryAsync(label, warnings, action).ConfigureAwait(false)) failed.Add(part);
        }

        await Read(Part.Game, "Reading game settings", async () => snapshot.GameSettings = await gateway.GetGameSettingsAsync(ct).ConfigureAwait(false)).ConfigureAwait(false);
        await Read(Part.Input, "Reading hotkeys", async () => snapshot.InputSettings = await gateway.GetInputSettingsAsync(ct).ConfigureAwait(false)).ConfigureAwait(false);
        await Read(Part.Video, "Reading graphics", () => { snapshot.VideoSettings = video.Read(); return Task.CompletedTask; }).ConfigureAwait(false);
        await Read(Part.Runes, "Reading rune pages", async () => snapshot.RunePages = await gateway.GetEditableRunePagesAsync(ct).ConfigureAwait(false)).ConfigureAwait(false);
        await Read(Part.ItemSets, "Reading item sets", async () => snapshot.ItemSets = await gateway.GetItemSetsAsync(account, ct).ConfigureAwait(false)).ConfigureAwait(false);
        await Read(Part.Client, "Reading client settings", async () =>
        {
            foreach (var category in SectionMap.ClientSettingsCategories)
            {
                if (await gateway.GetClientSettingAsync(category, ct).ConfigureAwait(false) is { } value) snapshot.ClientSettings[category] = value;
            }
        }).ConfigureAwait(false);

        return (snapshot, failed, warnings);
    }

    // ================= helpers =================

    /// <summary>
    /// Runs <paramref name="body"/> under the engine lock with a token that is cancelled when the
    /// connection drops. Cancellation caused by a disconnect ends the work quietly.
    /// </summary>
    private async Task LockedAsync(Func<CancellationToken, Task> body, CancellationToken ct, bool useConnectionToken = true)
    {
        var connectionToken = useConnectionToken ? _connectionCts?.Token ?? CancellationToken.None : CancellationToken.None;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, connectionToken);
        try
        {
            await _lock.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return;
        }
        try
        {
            await body(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The connection (or this login) went away mid-operation; the disconnect handler resets state.
        }
        finally
        {
            _lock.Release();
        }
    }

    private void ResetAccount()
    {
        _loginCts?.Cancel();
        _loginCts?.Dispose();
        _loginCts = null;
        _account = null;
        _pushedPuuid = null;
        _pushedSections.Clear();
        _runeCapacityShortfall = null;
        _pushPending = false;
        _promptPending = false;
        ClearPending();
    }

    private void ResetConnection()
    {
        ResetAccount();
        _connectionCts?.Dispose();
        _connectionCts = null;
        _gateway = null;
    }

    /// <summary>Runs one category; a recoverable failure becomes a warning instead of stopping the rest.</summary>
    private static async Task<bool> TryAsync(string label, List<string> warnings, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            warnings.Add($"{label} failed: {Describe(e)}");
            return false;
        }
    }

    private static bool IsRecoverable(Exception e) =>
        e is LcuException or HttpRequestException or IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException
            || (e is TaskCanceledException tce && !tce.CancellationToken.IsCancellationRequested);

    private static string Describe(Exception e) => e switch
    {
        LcuException l => $"League client returned {l.StatusCode}",
        TaskCanceledException => "timed out",
        _ => e.Message,
    };

    private void SetStatus(SyncState state, Account? account, DateTimeOffset? lastSync = null, IReadOnlyList<string>? warnings = null)
    {
        _status = new SyncStatus(state, account, lastSync ?? _status.LastSync, warnings ?? _status.Warnings);
        StatusChanged?.Invoke(_status);
    }
}
