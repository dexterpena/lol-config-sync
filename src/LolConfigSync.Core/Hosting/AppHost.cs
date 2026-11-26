using System.Threading.Channels;
using LolConfigSync.Core.Files;
using LolConfigSync.Core.Lcu;
using LolConfigSync.Core.League;
using LolConfigSync.Core.Storage;
using LolConfigSync.Core.Sync;

namespace LolConfigSync.Core.Hosting;

/// <summary>Wires the lockfile watcher, LCU connection and event stream to the <see cref="SyncEngine"/>.</summary>
public sealed class AppHost : IAsyncDisposable
{
    private static readonly string[] RelevantPrefixes =
    [
        LeagueGateway.CurrentSummoner,
        LeagueGateway.GameflowPhase,
        "/lol-game-settings/",
        LeagueGateway.RunePages,
        LeagueGateway.ItemSetsPrefix,
        LeagueGateway.ClientSettingsPrefix,
    ];

    private readonly AppDataStore _store;
    private readonly LcuConnectionWatcher _watcher;
    private readonly Channel<LcuEvent> _events = Channel.CreateUnbounded<LcuEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private LcuHttpClient? _http;
    private LeagueGateway? _gateway;
    private LcuEventStream? _stream;
    private Task? _eventLoop;
    private bool _readOnlyChecked;

    private readonly bool _connect;

    /// <param name="connect">False for a sandboxed run: never connect to the League client or touch its files.</param>
    public AppHost(AppDataStore store, string installDir, TimeProvider time, bool connect = true)
    {
        _store = store;
        _connect = connect;
        InstallDir = installDir;
        var configDir = Path.Combine(installDir, "Config");
        Engine = new SyncEngine(new GameCfgVideoFile(Path.Combine(configDir, "game.cfg")), store, time);
        _watcher = new LcuConnectionWatcher(installDir, time, TryConnectAsync);
        _watcher.Disconnected += () => _ = HandleDisconnectAsync();
    }

    public SyncEngine Engine { get; }
    public string InstallDir { get; }

    public Task StartAsync()
    {
        FileLog.Info($"Starting; League install at {InstallDir}{(_connect ? "" : " (sandbox: not connecting)")}");
        _eventLoop = Task.Run(() => PumpEventsAsync(_cts.Token));
        if (_connect) _watcher.Start();
        return Task.CompletedTask;
    }

    private async Task<bool> TryConnectAsync(LockfileInfo lockfile)
    {
        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseConnectionAsync().ConfigureAwait(false);

            var http = new LcuHttpClient(lockfile);
            var gateway = new LeagueGateway(http);
            try
            {
                await gateway.GetGameflowPhaseAsync(_cts.Token).ConfigureAwait(false); // probe: stale lockfiles fail here
            }
            catch (Exception e) when (e is HttpRequestException or LcuException or TaskCanceledException)
            {
                http.Dispose();
                return false;
            }

            var stream = new LcuEventStream(lockfile);
            stream.Received += e =>
            {
                if (IsRelevant(e.Uri)) _events.Writer.TryWrite(e);
            };
            stream.Closed += () => OnStreamClosed(stream);
            try
            {
                await stream.ConnectAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is System.Net.WebSockets.WebSocketException or HttpRequestException or TaskCanceledException)
            {
                FileLog.Error("WebSocket connect failed", e);
                await stream.DisposeAsync().ConfigureAwait(false);
                http.Dispose();
                return false;
            }

            _http = http;
            _gateway = gateway;
            _stream = stream;
            FileLog.Info($"Connected to League client on port {lockfile.Port}");
            FixReadOnlySettingsOnce();

            try
            {
                await Engine.OnConnectedAsync(gateway, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                FileLog.Error("Initial sync failed", e);
            }
            return true;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private void OnStreamClosed(LcuEventStream stream)
    {
        if (!ReferenceEquals(stream, _stream) || _cts.IsCancellationRequested) return;
        FileLog.Info("League client connection closed");
        _watcher.MarkDisconnected();
        _ = HandleDisconnectAsync();
    }

    private async Task HandleDisconnectAsync()
    {
        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await Engine.OnDisconnectedAsync().ConfigureAwait(false);
            await CloseConnectionAsync().ConfigureAwait(false);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task CloseConnectionAsync()
    {
        var stream = _stream;
        _stream = null;
        if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
        _gateway = null;
        _http?.Dispose();
        _http = null;
    }

    /// <summary>The logged-in account's profile icon from the client, or null when not connected or unavailable.</summary>
    public async Task<byte[]?> GetProfileIconAsync(int iconId)
    {
        var gateway = _gateway;
        if (gateway is null) return null;
        try
        {
            return await gateway.GetProfileIconAsync(iconId, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or ObjectDisposedException)
        {
            return null;
        }
    }

    private async Task PumpEventsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var e in _events.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await Engine.HandleLcuEventAsync(e, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    FileLog.Error($"Handling {e.Uri} failed", ex);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Undo the common "make PersistedSettings.json read-only" workaround, keeping a copy.</summary>
    private void FixReadOnlySettingsOnce()
    {
        if (_readOnlyChecked) return;
        _readOnlyChecked = true;
        var path = Path.Combine(InstallDir, "Config", "PersistedSettings.json");
        try
        {
            if (!File.Exists(path) || !new FileInfo(path).IsReadOnly) return;
            Directory.CreateDirectory(_store.RootDir);
            File.Copy(path, Path.Combine(_store.RootDir, "PersistedSettings.readonly-backup.json"), overwrite: true);
            GameCfgFile.ClearReadOnly(path);
            FileLog.Info("Cleared read-only flag on PersistedSettings.json (copy saved)");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            FileLog.Error("Could not clear read-only flag on PersistedSettings.json", e);
        }
    }

    private static bool IsRelevant(string uri) =>
        RelevantPrefixes.Any(p => uri.StartsWith(p, StringComparison.Ordinal));

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _watcher.Dispose();
        _events.Writer.TryComplete();
        if (_eventLoop is not null) await _eventLoop.ConfigureAwait(false);
        await CloseConnectionAsync().ConfigureAwait(false);
        Engine.Dispose();
        _cts.Dispose();
        _connectionLock.Dispose();
    }
}
