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

    private readonly SemaphoreSlim _lock = new(1, 1);
    private ILeagueGateway? _gateway;
    private volatile LeagueLoginSession? _session;
    private CancellationToken _accountToken;
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
    private IReadOnlyCollection<string> _capturableClientCategories = [];
    private int? _runeCapacityShortfall;

    private readonly Dictionary<SettingKey, JsonNode?> _pendingValues = [];
    private readonly Dictionary<SettingKey, JsonNode?> _pendingOwn = [];
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

    public Task OnConnectedAsync(ILeagueGateway gateway, CancellationToken ct) =>
        OnConnectedAsync(gateway, new LeagueLoginSession(), ct);

    internal async Task OnConnectedAsync(ILeagueGateway gateway, LeagueLoginSession session, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ResetConnection();
            _session = session;
            _gateway = gateway;
            SetStatus(SyncState.Connected, null, warnings: []);
            var login = session.Token;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, login);
            var account = await gateway.GetCurrentAccountAsync(linked.Token).ConfigureAwait(false);
            if (account is not null) await HandleLoginAsync(account, login, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        finally
        {
            _lock.Release();
        }
    }

    public async Task OnDisconnectedAsync()
    {
        var session = _session;
        session?.Close();
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(session, _session)) return;
            ResetConnection();
            SetStatus(SyncState.WaitingForClient, null, warnings: []);
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task HandleLcuEventAsync(LcuEvent e, CancellationToken ct)
    {
        var session = _session;
        return session is null ? Task.CompletedTask : HandleObservedEventAsync(e, session, session.Observe(e), ct);
    }

    internal Task HandleObservedEventAsync(LcuEvent e, LeagueLoginSession session, CancellationToken login,
        CancellationToken ct) => LockedAsync(async token =>
    {
        if (!ReferenceEquals(session, _session) || _gateway is null) return;
        switch (e.Uri)
        {
            case LeagueGateway.CurrentSummoner:
                await OnSummonerEventAsync(e, login, token).ConfigureAwait(false);
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
                if (_capturableClientCategories.Contains(category))
                {
                    QueueFlag(SyncSection.ClientSettings, () => _pendingClient.Add(category));
                }
                break;
        }
    }, ct, login, accountBound: e.Uri != LeagueGateway.CurrentSummoner);

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
    public async Task<SyncStatus> RestoreBackupAsync(string backupPath, CancellationToken ct)
    {
        SyncStatus? result = null;
        await LockedAsync(async token =>
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
            await PushAsync(_gateway, _account, backup, new AppSettings(), token, restoring: true).ConfigureAwait(false);
            _pushedPuuid = null;
            result = _status;
        }, ct).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("The account changed during restore.");
    }

    /// <summary>
    /// Saves the user's edits (and per-account values, when given) and pushes them right away when an account
    /// is logged in and idle.
    /// </summary>
    public Task SaveEditedSetupAsync(SetupSnapshot edited, AppSettings settings, CancellationToken ct,
        AccountOverrides? overrides = null) => LockedAsync(async token =>
    {
        if (overrides is not null) store.SaveOverrides(overrides);
        store.SaveSettings(settings);
        edited.CapturedAt = time.GetUtcNow();
        store.SaveMainSetup(edited);
        if (_gateway is null || _account is null) return;
        ClearPending();
        _pushPending = true;
        await TryPushAsync(token).ConfigureAwait(false);
    }, ct);

    /// <summary>Saves per-account settings and applies them right away if their account is logged in and idle.</summary>
    public Task SaveAccountOverridesAsync(AccountOverrides overrides, CancellationToken ct) => LockedAsync(async token =>
    {
        store.SaveOverrides(overrides);
        if (_gateway is null || _account is null || store.LoadMainSetup() is null) return;
        ClearPending();
        _pushPending = true;
        await TryPushAsync(token).ConfigureAwait(false);
    }, ct);

    public void Dispose()
    {
        _flushTimer?.Dispose();
        _session?.Close();
        _lock.Dispose();
    }

    // ================= login / phase =================

    private async Task OnSummonerEventAsync(LcuEvent e, CancellationToken login, CancellationToken ct)
    {
        var puuid = LeagueLoginSession.SummonerPuuid(e);
        if (!string.IsNullOrEmpty(puuid) && puuid == _account?.Puuid && login == _accountToken)
        {
            // Same account: just refresh what we show (name, icon, level) without re-syncing.
            if (LeagueGateway.ToAccount(e.Data) is { } updated && updated != _account)
            {
                _account = updated;
                SetStatus(_status.State, updated);
            }
            return;
        }

        // Forget the old account first, so nothing can be captured into it while we look up the new one.
        ResetAccount();
        SetStatus(SyncState.Connected, null, warnings: []);
        if (string.IsNullOrEmpty(puuid)) return;

        var account = await _gateway!.GetCurrentAccountAsync(ct).ConfigureAwait(false);
        if (account is not null) await HandleLoginAsync(account, login, ct).ConfigureAwait(false);
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

    private async Task HandleLoginAsync(Account account, CancellationToken login, CancellationToken ct)
    {
        if (!_session!.Confirm(account.Puuid, login)) return;
        ResetAccount();
        _account = account;
        _accountToken = login;
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
        PendingPrompt = RunPromptAsync(_account, FirstRunPrompt, _accountToken);
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
        if (previous is null && failed.Contains(SnapshotPart.Game) && failed.Contains(SnapshotPart.Input))
        {
            SetStatus(SyncState.Synced, account, warnings: [.. warnings, "Couldn't read this account's settings. Try again in a moment."]);
            return;
        }
        if (previous is not null) snapshot.CopyPartsFrom(previous, failed);
        snapshot.RunePages = RunePagePlanner.WithoutToolPages(snapshot.RunePages, store.LoadSettings().ToolRunePagePrefixes);

        ct.ThrowIfCancellationRequested();
        store.SaveMainSetup(snapshot);
        MarkPushed(account, AllSections, failed.Contains(SnapshotPart.Client) ? [] : SectionMap.ClientSettingsCategories,
            runeShortfallCapacity: null);
        SetStatus(SyncState.Synced, account, lastSync: time.GetUtcNow(), warnings: warnings);
        Notify?.Invoke($"Main setup saved from {account.RiotId}");
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
        await PushAsync(gateway, _account, main, store.LoadSettings(), ct, overrides: store.LoadOverrides()).ConfigureAwait(false);
    }

    /// <param name="overrides">Each account's own values, applied on top for this account (null for a restore, which is exact).</param>
    private async Task PushAsync(ILeagueGateway gateway, Account account, SetupSnapshot source, AppSettings settings, CancellationToken ct,
        bool restoring = false, AccountOverrides? overrides = null)
    {
        SetStatus(SyncState.Syncing, account);

        var (backup, unreadable, warnings) = await CaptureAsync(gateway, account, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if ((await gateway.GetCurrentAccountAsync(ct).ConfigureAwait(false))?.Puuid != account.Puuid)
        {
            SetStatus(SyncState.Error, account, warnings: ["The logged-in account changed during backup."]);
            throw new InvalidOperationException("The logged-in account changed during backup.");
        }
        ct.ThrowIfCancellationRequested();
        try
        {
            store.SaveBackup(backup, unreadable);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Never change an account we couldn't back up first.
            FileLog.Error($"Backup for {account.RiotId} failed; nothing was changed", e);
            SetStatus(SyncState.Error, account,
                warnings: [$"Couldn't save a backup of {account.RiotId}'s settings, so nothing was changed. {e.Message}"]);
            Notify?.Invoke($"Didn't sync {account.RiotId}: couldn't save a backup first");
            return;
        }
        var pushed = new HashSet<SyncSection>();

        bool Skipped(SnapshotPart part, string label)
        {
            ct.ThrowIfCancellationRequested();
            if (restoring && source.UnreadableParts is null &&
                ((part == SnapshotPart.Runes && source.RunePages.Count == 0) ||
                 (part == SnapshotPart.ItemSets && source.ItemSets.Count == 0)))
            {
                warnings.Add($"{label} skipped: this older backup did not record whether the empty list was read successfully.");
                return true;
            }
            if (restoring && source.UnreadableParts?.Contains(part) == true)
            {
                warnings.Add($"{label} skipped: the selected backup could not read them.");
                return true;
            }
            if (!unreadable.Contains(part)) return false;
            warnings.Add($"{label} skipped: couldn't back them up first.");
            return true;
        }

        // Game settings (Sound, Interface, Game) and hotkeys.
        JsonObject Effective(SettingsKind kind)
        {
            var filtered = SettingsFilter.Apply(source.Settings(kind), kind, settings);
            return overrides?.ApplyTo(filtered, kind, account.Puuid) ?? filtered;
        }

        var game = Effective(SettingsKind.Game);
        var input = Effective(SettingsKind.Input);
        var gameOk = !Skipped(SnapshotPart.Game, "Game settings") && game.Count > 0
            && await TryAsync("Game settings", warnings, () => gateway.PatchGameSettingsAsync(game, ct)).ConfigureAwait(false);
        var inputOk = !Skipped(SnapshotPart.Input, "Hotkeys") && input.Count > 0
            && await TryAsync("Hotkeys", warnings, () => gateway.PatchInputSettingsAsync(input, ct)).ConfigureAwait(false);
        if (gameOk || inputOk)
        {
            await TryAsync("Saving settings", warnings, () => gateway.SaveGameSettingsAsync(ct)).ConfigureAwait(false);
        }
        if (gameOk) await VerifyKeptAsync(SettingsKind.Game, game, () => gateway.GetGameSettingsAsync(ct), pushed, warnings).ConfigureAwait(false);
        if (inputOk) await VerifyKeptAsync(SettingsKind.Input, input, () => gateway.GetInputSettingsAsync(ct), pushed, warnings).ConfigureAwait(false);

        // Local graphics keys.
        if (settings.IsEnabled(SyncSection.Video) && !Skipped(SnapshotPart.Video, "Graphics")
            && await TryAsync("Graphics", warnings, () =>
            {
                var changes = SettingsDiff.Changes(Effective(SettingsKind.Video), video.Read());
                if (changes.Count > 0) video.Write(changes);
                return Task.CompletedTask;
            }).ConfigureAwait(false))
        {
            pushed.Add(SyncSection.Video);
        }

        // Rune pages.
        int? shortfallCapacity = null;
        if (settings.IsEnabled(SyncSection.RunePages) && !Skipped(SnapshotPart.Runes, "Rune pages"))
        {
            // Normal sync leaves room for tools. A restore uses the complete backed-up list.
            var sourcePages = restoring ? source.RunePages : RunePagePlanner.WithoutToolPages(source.RunePages, settings.ToolRunePagePrefixes);
            var accountPages = restoring ? backup.RunePages : RunePagePlanner.WithoutToolPages(backup.RunePages, settings.ToolRunePagePrefixes);
            if ((sourcePages.Count == 0 && !restoring) || SamePages(accountPages, sourcePages, keepCurrentPage: restoring))
            {
                pushed.Add(SyncSection.RunePages);
            }
            else if (await TryAsync("Rune pages", warnings, async () =>
                     {
                         shortfallCapacity = await PushRunePagesAsync(gateway, sourcePages, restoring ? 0 : settings.ReservedRunePageSlots, restoring, warnings, ct)
                             .ConfigureAwait(false);
                     }).ConfigureAwait(false))
            {
                pushed.Add(SyncSection.RunePages);
            }
        }

        // Item sets.
        if (settings.IsEnabled(SyncSection.ItemSets) && !Skipped(SnapshotPart.ItemSets, "Item sets"))
        {
            if ((!restoring && source.ItemSets.Count == 0)
                || await TryAsync("Item sets", warnings, () => gateway.PutItemSetsAsync(account, source.ItemSets, ct)).ConfigureAwait(false))
            {
                pushed.Add(SyncSection.ItemSets);
            }
        }

        // Client preferences.
        var capturableClientCategories = new HashSet<string>();
        if (settings.IsEnabled(SyncSection.ClientSettings) && !Skipped(SnapshotPart.Client, "Client settings"))
        {
            // A category with no saved value can still be populated by a later client edit.
            capturableClientCategories.UnionWith(SectionMap.ClientSettingsCategories);
            foreach (var category in SectionMap.ClientSettingsCategories)
            {
                if (source.ClientSettings[category] is not JsonObject value) continue;
                if (backup.ClientSettings[category] is JsonObject current)
                {
                    if (JsonNode.DeepEquals(value, current)) continue;
                    // A normal PATCH only needs to apply the saved keys. Newer clients can
                    // already have all of them plus extra per-account fields.
                    if (!restoring && value["data"] is JsonObject desired && current["data"] is JsonObject existing &&
                        desired.All(pair => existing.TryGetPropertyValue(pair.Key, out var v) && JsonNode.DeepEquals(pair.Value, v)))
                        continue;
                    // League rejects a write that would downgrade an account's preference format. A newer saved
                    // format (e.g. onto a brand-new account still at version 0) is fine and gets upgraded.
                    if (SchemaVersion(current) > SchemaVersion(value))
                    {
                        warnings.Add($"Skipped {category}: this account uses a different preference format.");
                        capturableClientCategories.Remove(category);
                        continue;
                    }
                }
                var applied = await TryAsync($"Client settings ({category})", warnings,
                    () => gateway.PatchClientSettingAsync(category, (JsonObject)value.DeepClone(), ct)).ConfigureAwait(false);
                if (!applied) capturableClientCategories.Remove(category);
            }
            pushed.Add(SyncSection.ClientSettings);
        }

        ct.ThrowIfCancellationRequested(); // the client went away mid-push: don't report success
        MarkPushed(account, pushed, capturableClientCategories, shortfallCapacity);
        SetStatus(SyncState.Synced, account, lastSync: time.GetUtcNow(), warnings: warnings);
        foreach (var w in warnings) FileLog.Info($"Sync to {account.RiotId}: {w}");

        var message = restoring ? $"Restored backup to {account.RiotId}" : $"Synced your setup to {account.RiotId}";
        Notify?.Invoke(warnings.Count == 0
            ? message
            : $"{message} ({warnings.Count} warning{(warnings.Count == 1 ? "" : "s")})");
    }

    private static int SchemaVersion(JsonObject setting) =>
        setting["schemaVersion"]?.GetValueKind() == System.Text.Json.JsonValueKind.Number ? setting["schemaVersion"]!.GetValue<int>() : 0;

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
    private static async Task<int?> PushRunePagesAsync(ILeagueGateway gateway, JsonArray mainPages, int reservedSlots, bool restoring,
        List<string> warnings, CancellationToken ct)
    {
        var target = await gateway.GetEditableRunePagesAsync(ct).ConfigureAwait(false);
        var capacity = await gateway.GetRunePageCapacityAsync(ct).ConfigureAwait(false);
        var plan = RunePagePlanner.Plan(mainPages, target, capacity, reservedSlots, keepCurrentPage: restoring);
        if (restoring && plan.Skipped.Count > 0)
            throw new InvalidOperationException("The backup has more rune pages than this account can hold. Its current pages were kept.");
        foreach (var id in plan.DeleteIds) await gateway.DeleteRunePageAsync(id, ct).ConfigureAwait(false);
        foreach (var page in plan.Create) await gateway.CreateRunePageAsync(page, ct).ConfigureAwait(false);
        if (plan.Skipped.Count == 0) return null;
        var reason = reservedSlots > 0 ? $" ({reservedSlots} slot{(reservedSlots == 1 ? " is" : "s are")} kept free for Porofessor)" : "";
        warnings.Add($"Rune pages: no room on this account for {string.Join(", ", plan.Skipped)}{reason}");
        return plan.UsableSlots;
    }

    private void MarkPushed(Account account, IEnumerable<SyncSection> sections,
        IReadOnlyCollection<string> capturableClientCategories, int? runeShortfallCapacity)
    {
        _pushedPuuid = account.Puuid;
        _pushCompletedAt = time.GetUtcNow();
        _pushedSections.Clear();
        _pushedSections.UnionWith(sections);
        _capturableClientCategories = capturableClientCategories;
        _pendingClient.IntersectWith(capturableClientCategories);
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
        var puuid = _account!.Puuid;

        var overrides = store.LoadOverrides();
        var settings = store.LoadSettings();
        var queued = false;
        foreach (var (section, node) in values)
        {
            var group = SectionMap.SectionFor(kind, section);
            if (node is not JsonObject incoming || !_pushedSections.Contains(group)) continue;
            var saved = main.Settings(kind)[section] as JsonObject;
            foreach (var (name, value) in incoming)
            {
                var key = new SettingKey(kind, section, name);
                var own = overrides.Has(puuid, key);
                if (!own && (!settings.IsEnabled(group) || settings.IsExcluded(key))) continue;
                var pending = own ? _pendingOwn : _pendingValues;
                var baseline = own ? overrides.Get(puuid, key) : saved?[name];
                if ((own || saved?.ContainsKey(name) == true) && JsonNode.DeepEquals(value, baseline))
                {
                    pending.Remove(key); // Reverted before the flush; keep changes to other keys.
                    continue;
                }
                pending[key] = value?.DeepClone();
                queued = true;
            }
        }
        if (queued) ScheduleFlush();
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
        _pendingOwn.Clear();
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
        foreach (var (key, value) in _pendingValues)
        {
            var target = main.Settings(key.Kind);
            if (target[key.Section] is not JsonObject section)
            {
                section = [];
                target[key.Section] = section;
            }
            section[key.Name] = value;
            changed = true;
        }

        var ownChanged = _pendingOwn.Count > 0;
        if (ownChanged)
        {
            var overrides = store.LoadOverrides();
            foreach (var (key, value) in _pendingOwn) overrides.Set(account.Puuid, account.RiotId, key, value);
            store.SaveOverrides(overrides);
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
        if (changed || ownChanged || warnings.Count > 0)
        {
            SetStatus(_status.State, account, lastSync: changed || ownChanged ? time.GetUtcNow() : null,
                warnings: warnings.Count > 0 ? warnings : null);
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
    private static bool SamePages(JsonArray a, JsonArray b, bool keepCurrentPage = false) =>
        JsonNode.DeepEquals(
            new JsonArray([.. RunePagePlanner.Plan(a, [], int.MaxValue, keepCurrentPage: keepCurrentPage).Create]),
            new JsonArray([.. RunePagePlanner.Plan(b, [], int.MaxValue, keepCurrentPage: keepCurrentPage).Create]));

    // ================= reading an account =================

    /// <summary>Reads everything the account has (unfiltered). Parts that fail are left empty and reported.</summary>
    private async Task<(SetupSnapshot Snapshot, HashSet<SnapshotPart> Failed, List<string> Warnings)> CaptureAsync(
        ILeagueGateway gateway, Account account, CancellationToken ct)
    {
        var warnings = new List<string>();
        var failed = new HashSet<SnapshotPart>();
        var snapshot = new SetupSnapshot
        {
            CapturedAt = time.GetUtcNow(),
            CapturedFromPuuid = account.Puuid,
            CapturedFromRiotId = account.RiotId,
        };

        async Task Read(SnapshotPart part, string label, Func<Task> action)
        {
            if (!await TryAsync(label, warnings, action).ConfigureAwait(false)) failed.Add(part);
        }

        await Read(SnapshotPart.Game, "Reading game settings", async () => snapshot.GameSettings = await gateway.GetGameSettingsAsync(ct).ConfigureAwait(false)).ConfigureAwait(false);
        await Read(SnapshotPart.Input, "Reading hotkeys", async () => snapshot.InputSettings = await gateway.GetInputSettingsAsync(ct).ConfigureAwait(false)).ConfigureAwait(false);
        await Read(SnapshotPart.Video, "Reading graphics", () => { snapshot.VideoSettings = video.Read(); return Task.CompletedTask; }).ConfigureAwait(false);
        await Read(SnapshotPart.Runes, "Reading rune pages", async () => snapshot.RunePages = await gateway.GetEditableRunePagesAsync(ct).ConfigureAwait(false)).ConfigureAwait(false);
        await Read(SnapshotPart.ItemSets, "Reading item sets", async () => snapshot.ItemSets = await gateway.GetItemSetsAsync(account, ct).ConfigureAwait(false)).ConfigureAwait(false);
        await Read(SnapshotPart.Client, "Reading client settings", async () =>
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
    /// login changes or the connection drops. Stale work ends quietly.
    /// </summary>
    private async Task LockedAsync(Func<CancellationToken, Task> body, CancellationToken ct, CancellationToken? observedLogin = null, bool accountBound = true)
    {
        var login = observedLogin ?? _session?.Token ?? CancellationToken.None;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, login);
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
            linked.Token.ThrowIfCancellationRequested();
            if (accountBound && _account is not null && _accountToken != login) return;
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
        _accountToken = default;
        _account = null;
        _pushedPuuid = null;
        _pushedSections.Clear();
        _capturableClientCategories = [];
        _runeCapacityShortfall = null;
        _pushPending = false;
        _promptPending = false;
        ClearPending();
    }

    private void ResetConnection()
    {
        ResetAccount();
        _session?.Close();
        _session = null;
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
