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

    private readonly SemaphoreSlim _lock = new(1, 1);
    private ILeagueGateway? _gateway;
    private Account? _account;
    private bool _pushPending;
    private string _phase = "None";
    private SyncStatus _status = SyncStatus.Initial;

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

    public void Dispose() => _lock.Dispose();

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
        _phase = e.Data.GetValue<string>();
        if (_pushPending && IdlePhases.Contains(_phase)) await TryPushAsync(ct).ConfigureAwait(false);
    }

    private async Task HandleLoginAsync(Account account, CancellationToken ct)
    {
        _account = account;
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

    private async Task PushAsync(ILeagueGateway gateway, Account account, SetupSnapshot main, AppSettings settings, CancellationToken ct)
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

        SetStatus(SyncState.Synced, account, lastSync: time.GetUtcNow(), warnings: warnings);
        Notify?.Invoke(warnings.Count == 0
            ? $"Synced your setup to {account.RiotId}"
            : $"Synced your setup to {account.RiotId} ({warnings.Count} warning{(warnings.Count == 1 ? "" : "s")})");
    }

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
