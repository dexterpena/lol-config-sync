using System.Text.Json;
using System.Text.Json.Nodes;
using LolConfigSync.Core.Files;
using LolConfigSync.Core.Lcu;
using LolConfigSync.Core.League;
using LolConfigSync.Core.Model;
using LolConfigSync.Core.Storage;

namespace LolConfigSync.Core.Sync;

/// <summary>
/// Pushes the main setup to whichever account logs in and captures the user's changes back.
/// All work is serialized; call it from any thread.
/// </summary>
public sealed class SyncEngine(IVideoSettingsFile video, AppDataStore store, TimeProvider time) : IDisposable
{
    public static readonly IReadOnlySet<string> IdlePhases = new HashSet<string> { "None", "Lobby" };
    public static readonly TimeSpan CaptureDebounce = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan PostPushSettle = TimeSpan.FromSeconds(5);
    private static readonly IReadOnlySet<string> InGamePhases = new HashSet<string> { "InProgress", "Reconnect" };

    private readonly SemaphoreSlim _lock = new(1, 1);
    private ILeagueGateway? _gateway;
    private Account? _account;
    private bool _pushPending;
    private string _phase = "None";
    private SyncStatus _status = SyncStatus.Initial;

    // Capture is allowed only for the account we last pushed to, after the settle window.
    private string? _pushedPuuid;
    private DateTimeOffset _pushCompletedAt;
    private readonly Dictionary<SettingsKind, JsonObject> _pendingValues = [];
    private readonly HashSet<string> _pendingClient = [];
    private bool _pendingRunes;
    private bool _pendingItemSets;
    private ITimer? _flushTimer;

    /// <summary>The most recent debounced capture flush (for tests).</summary>
    internal Task PendingFlush { get; private set; } = Task.CompletedTask;

    public SyncStatus Status => _status;
    public event Action<SyncStatus>? StatusChanged;

    /// <summary>A short message worth showing the user (e.g. a tray toast).</summary>
    public event Action<string>? Notify;

    /// <summary>Asked on first run: should this account's settings become the main setup?</summary>
    public Func<Account, Task<bool>>? FirstRunPrompt { get; set; }

    public async Task OnConnectedAsync(ILeagueGateway gateway, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _gateway = gateway;
            _account = null;
            SetStatus(SyncState.Connected, null);
            var account = await gateway.GetCurrentAccountAsync(ct).ConfigureAwait(false);
            if (account is not null) await HandleLoginAsync(account, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void OnDisconnected()
    {
        _gateway = null;
        _account = null;
        _pushPending = false;
        _pushedPuuid = null;
        ClearPending();
        SetStatus(SyncState.WaitingForClient, null, warnings: []);
    }

    public async Task HandleLcuEventAsync(LcuEvent e, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_gateway is null) return;
            switch (e.Uri)
            {
                case LeagueGateway.CurrentSummoner:
                    await OnSummonerEventAsync(e, ct).ConfigureAwait(false);
                    break;
                case LeagueGateway.GameflowPhase:
                    await OnPhaseEventAsync(e, ct).ConfigureAwait(false);
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
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>"Sync now": push the main setup to the current account (waits for idle if in a game).</summary>
    public async Task PushMainSetupAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_gateway is null || _account is null) return;
            _pushPending = true;
            await TryPushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>"Use this account as main": replace the main setup with the current account's settings.</summary>
    public async Task AdoptCurrentAccountAsMainAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_gateway is null || _account is null) throw new InvalidOperationException("Log into an account first.");
            ClearPending();
            await AdoptAsync(_account, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Pushes a backup back to its own account, unfiltered. Capturing is then paused for that
    /// account until the next login or "Sync now", so the restored values don't leak into the main setup.
    /// </summary>
    public async Task RestoreBackupAsync(string backupPath, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_gateway is null || _account is null) throw new InvalidOperationException("Log into the account first.");
            var backup = store.LoadBackup(backupPath);
            if (backup.CapturedFromPuuid != _account.Puuid)
            {
                throw new InvalidOperationException($"This backup belongs to {backup.CapturedFromRiotId}. Log into that account to restore it.");
            }
            _phase = await _gateway.GetGameflowPhaseAsync(ct).ConfigureAwait(false);
            if (!IdlePhases.Contains(_phase)) throw new InvalidOperationException("Finish your game first, then restore.");

            ClearPending();
            _pushPending = false;
            await PushAsync(_gateway, _account, backup, new AppSettings(), ct, $"Restored backup to {_account.RiotId}").ConfigureAwait(false);
            _pushedPuuid = null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Saves the user's edits and pushes them right away when an account is logged in and idle.</summary>
    public async Task SaveEditedSetupAsync(SetupSnapshot edited, AppSettings settings, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            store.SaveSettings(settings);
            edited.CapturedAt = time.GetUtcNow();
            store.SaveMainSetup(edited);
            if (_gateway is null || _account is null) return;
            ClearPending();
            _pushPending = true;
            await TryPushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _flushTimer?.Dispose();
        _lock.Dispose();
    }

    // ---- login / phase ----

    private async Task OnSummonerEventAsync(LcuEvent e, CancellationToken ct)
    {
        var puuid = e.Data?["puuid"]?.GetValueKind() == JsonValueKind.String ? e.Data["puuid"]!.GetValue<string>() : null;
        if (e.EventType == "Delete" || string.IsNullOrEmpty(puuid))
        {
            _account = null;
            _pushPending = false;
            SetStatus(SyncState.Connected, null, warnings: []);
            return;
        }
        if (puuid == _account?.Puuid) return;

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

        if (_pushPending && IdlePhases.Contains(_phase)) await TryPushAsync(ct).ConfigureAwait(false);
    }

    private async Task HandleLoginAsync(Account account, CancellationToken ct)
    {
        _account = account;
        _pushedPuuid = null;
        ClearPending();
        SetStatus(SyncState.Connected, account, warnings: []);

        if (store.LoadMainSetup() is null)
        {
            if (FirstRunPrompt is not null && await FirstRunPrompt(account).ConfigureAwait(false))
            {
                await AdoptAsync(account, ct).ConfigureAwait(false);
            }
            return;
        }

        _pushPending = true;
        await TryPushAsync(ct).ConfigureAwait(false);
    }

    private async Task AdoptAsync(Account account, CancellationToken ct)
    {
        var (snapshot, warnings) = await CaptureAsync(account, ct).ConfigureAwait(false);
        store.SaveMainSetup(snapshot);
        MarkPushed(account);
        SetStatus(SyncState.Synced, account, lastSync: time.GetUtcNow(), warnings: warnings);
        Notify?.Invoke($"Main setup saved from {account.RiotId}");
    }

    // ---- push ----

    private async Task TryPushAsync(CancellationToken ct)
    {
        var gateway = _gateway!;
        _phase = await gateway.GetGameflowPhaseAsync(ct).ConfigureAwait(false);
        if (!IdlePhases.Contains(_phase))
        {
            SetStatus(SyncState.WaitingForIdle, _account);
            return;
        }

        var main = store.LoadMainSetup();
        if (main is null || _account is null) return;
        _pushPending = false;
        await PushAsync(gateway, _account, main, store.LoadSettings(), ct).ConfigureAwait(false);
    }

    private async Task PushAsync(ILeagueGateway gateway, Account account, SetupSnapshot main, AppSettings settings, CancellationToken ct,
        string? doneMessage = null)
    {
        SetStatus(SyncState.Syncing, account);

        var (backup, warnings) = await CaptureAsync(account, ct).ConfigureAwait(false);
        store.SaveBackup(backup);

        var game = SettingsFilter.Apply(main.GameSettings, SettingsKind.Game, settings);
        var input = SettingsFilter.Apply(main.InputSettings, SettingsKind.Input, settings);
        var patched = false;
        if (game.Count > 0)
        {
            patched |= await TryAsync("Game settings", warnings, () => gateway.PatchGameSettingsAsync(game, ct)).ConfigureAwait(false);
        }
        if (input.Count > 0)
        {
            patched |= await TryAsync("Hotkeys", warnings, () => gateway.PatchInputSettingsAsync(input, ct)).ConfigureAwait(false);
        }
        if (patched)
        {
            await TryAsync("Saving settings", warnings, () => gateway.SaveGameSettingsAsync(ct)).ConfigureAwait(false);
        }

        await TryAsync("Video", warnings, () =>
        {
            var wanted = SettingsFilter.Apply(main.VideoSettings, SettingsKind.Video, settings);
            var changes = SettingsDiff.Changes(wanted, video.Read());
            if (changes.Count > 0) video.Write(changes);
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        if (settings.IsEnabled(SyncSection.RunePages) && main.RunePages.Count > 0)
        {
            await TryAsync("Rune pages", warnings, () => PushRunePagesAsync(gateway, main.RunePages, warnings, ct)).ConfigureAwait(false);
        }
        if (settings.IsEnabled(SyncSection.ItemSets) && main.ItemSets.Count > 0)
        {
            await TryAsync("Item sets", warnings, () => gateway.PutItemSetsAsync(account, main.ItemSets, ct)).ConfigureAwait(false);
        }
        if (settings.IsEnabled(SyncSection.ClientSettings))
        {
            foreach (var category in SectionMap.ClientSettingsCategories)
            {
                if (main.ClientSettings[category] is not JsonObject value) continue;
                await TryAsync($"Client settings ({category})", warnings,
                    () => gateway.PatchClientSettingAsync(category, (JsonObject)value.DeepClone(), ct)).ConfigureAwait(false);
            }
        }

        MarkPushed(account);
        SetStatus(SyncState.Synced, account, lastSync: time.GetUtcNow(), warnings: warnings);
        var message = doneMessage ?? $"Synced your setup to {account.RiotId}";
        Notify?.Invoke(warnings.Count == 0
            ? message
            : $"{message} ({warnings.Count} warning{(warnings.Count == 1 ? "" : "s")})");
    }

    private void MarkPushed(Account account)
    {
        _pushedPuuid = account.Puuid;
        _pushCompletedAt = time.GetUtcNow();
    }

    // ---- capture (debounced) ----

    private bool CanCapture =>
        _account is not null && _pushedPuuid == _account.Puuid && time.GetUtcNow() >= _pushCompletedAt + PostPushSettle;

    private void QueueValues(SettingsKind kind, JsonNode? data)
    {
        if (!CanCapture || data is not JsonObject values) return;
        var main = store.LoadMainSetup();
        if (main is null) return;

        var settings = store.LoadSettings();
        var changes = SettingsDiff.Changes(
            SettingsFilter.Apply(values, kind, settings),
            SettingsFilter.Apply(main.Settings(kind), kind, settings));
        if (changes.Count == 0) return;

        if (!_pendingValues.TryGetValue(kind, out var pending))
        {
            pending = [];
            _pendingValues[kind] = pending;
        }
        SettingsDiff.MergeInto(pending, changes);
        ScheduleFlush();
    }

    private void QueueFlag(SyncSection section, Action mark)
    {
        if (!CanCapture || !store.LoadSettings().IsEnabled(section)) return;
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

    private async Task FlushAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var gateway = _gateway;
            var account = _account;
            var main = store.LoadMainSetup();
            if (account is null || main is null)
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
                        var pages = await gateway.GetEditableRunePagesAsync().ConfigureAwait(false);
                        if (!SamePages(pages, main.RunePages)) { main.RunePages = pages; changed = true; }
                    }).ConfigureAwait(false);
                }
                if (_pendingItemSets)
                {
                    await TryAsync("Saving item sets", warnings, async () =>
                    {
                        var sets = await gateway.GetItemSetsAsync(account).ConfigureAwait(false);
                        if (!JsonNode.DeepEquals(sets, main.ItemSets)) { main.ItemSets = sets; changed = true; }
                    }).ConfigureAwait(false);
                }
                foreach (var category in _pendingClient)
                {
                    await TryAsync($"Saving client settings ({category})", warnings, async () =>
                    {
                        var value = await gateway.GetClientSettingAsync(category).ConfigureAwait(false);
                        if (value is not null && !JsonNode.DeepEquals(value, main.ClientSettings[category]))
                        {
                            main.ClientSettings[category] = value;
                            changed = true;
                        }
                    }).ConfigureAwait(false);
                }
            }
            ClearPending();

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
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Compares the parts of rune pages we sync, ignoring server ids and timestamps.</summary>
    private static bool SamePages(JsonArray a, JsonArray b) =>
        JsonNode.DeepEquals(
            new JsonArray([.. RunePagePlanner.Plan(a, [], int.MaxValue).Create]),
            new JsonArray([.. RunePagePlanner.Plan(b, [], int.MaxValue).Create]));

    private static async Task PushRunePagesAsync(ILeagueGateway gateway, JsonArray mainPages, List<string> warnings, CancellationToken ct)
    {
        var target = await gateway.GetEditableRunePagesAsync(ct).ConfigureAwait(false);
        var capacity = await gateway.GetRunePageCapacityAsync(ct).ConfigureAwait(false);
        var plan = RunePagePlanner.Plan(mainPages, target, capacity);
        foreach (var id in plan.DeleteIds) await gateway.DeleteRunePageAsync(id, ct).ConfigureAwait(false);
        foreach (var page in plan.Create) await gateway.CreateRunePageAsync(page, ct).ConfigureAwait(false);
        if (plan.Skipped.Count > 0)
        {
            warnings.Add($"Rune pages: no room on this account for {string.Join(", ", plan.Skipped)}");
        }
    }

    // ---- capture ----

    /// <summary>Reads everything the account has (unfiltered). Categories that fail are left empty and reported.</summary>
    private async Task<(SetupSnapshot Snapshot, List<string> Warnings)> CaptureAsync(Account account, CancellationToken ct)
    {
        var gateway = _gateway!;
        var warnings = new List<string>();
        var snapshot = new SetupSnapshot
        {
            CapturedAt = time.GetUtcNow(),
            CapturedFromPuuid = account.Puuid,
            CapturedFromRiotId = account.RiotId,
        };

        await TryAsync("Reading game settings", warnings, async () => snapshot.GameSettings = await gateway.GetGameSettingsAsync(ct).ConfigureAwait(false)).ConfigureAwait(false);
        await TryAsync("Reading hotkeys", warnings, async () => snapshot.InputSettings = await gateway.GetInputSettingsAsync(ct).ConfigureAwait(false)).ConfigureAwait(false);
        await TryAsync("Reading video", warnings, () => { snapshot.VideoSettings = video.Read(); return Task.CompletedTask; }).ConfigureAwait(false);
        await TryAsync("Reading rune pages", warnings, async () => snapshot.RunePages = await gateway.GetEditableRunePagesAsync(ct).ConfigureAwait(false)).ConfigureAwait(false);
        await TryAsync("Reading item sets", warnings, async () => snapshot.ItemSets = await gateway.GetItemSetsAsync(account, ct).ConfigureAwait(false)).ConfigureAwait(false);
        await TryAsync("Reading client settings", warnings, async () =>
        {
            foreach (var category in SectionMap.ClientSettingsCategories)
            {
                if (await gateway.GetClientSettingAsync(category, ct).ConfigureAwait(false) is { } value) snapshot.ClientSettings[category] = value;
            }
        }).ConfigureAwait(false);

        return (snapshot, warnings);
    }

    // ---- helpers ----

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
