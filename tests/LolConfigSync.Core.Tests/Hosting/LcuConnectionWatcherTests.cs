using LolConfigSync.Core.Hosting;
using LolConfigSync.Core.Lcu;
using Microsoft.Extensions.Time.Testing;

namespace LolConfigSync.Core.Tests.Hosting;

public sealed class LcuConnectionWatcherTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lcs-watch-").FullName;
    private readonly FakeTimeProvider _time = new();
    private readonly List<LockfileInfo> _attempts = [];
    private readonly Queue<bool> _results = new();
    private int _disconnects;
    private readonly LcuConnectionWatcher _watcher;

    public LcuConnectionWatcherTests()
    {
        _watcher = new LcuConnectionWatcher(_dir, _time, info =>
        {
            _attempts.Add(info);
            return Task.FromResult(_results.Count == 0 || _results.Dequeue());
        });
        _watcher.Disconnected += () => _disconnects++;
    }

    public void Dispose()
    {
        _watcher.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private void WriteLockfile(int port) =>
        File.WriteAllText(Path.Combine(_dir, "lockfile"), $"LeagueClient:100:{port}:pw:https");

    private async Task TickAsync()
    {
        _time.Advance(TimeSpan.FromSeconds(2));
        await _watcher.LastPoll;
    }

    [Fact]
    public async Task Connects_when_lockfile_appears()
    {
        _watcher.Start();
        await TickAsync();
        Assert.Empty(_attempts);

        WriteLockfile(5000);
        await TickAsync();
        await TickAsync();

        Assert.Equal(5000, Assert.Single(_attempts).Port);
        Assert.True(_watcher.IsConnected);
    }

    [Fact]
    public async Task Stale_lockfile_retries()
    {
        WriteLockfile(5000);
        _results.Enqueue(false);
        _results.Enqueue(true);

        _watcher.Start();
        await _watcher.LastPoll;
        Assert.False(_watcher.IsConnected);

        await TickAsync();
        await TickAsync();

        Assert.Equal(2, _attempts.Count);
        Assert.True(_watcher.IsConnected);
    }

    [Fact]
    public async Task Disconnects_when_lockfile_removed()
    {
        WriteLockfile(5000);
        _watcher.Start();
        await _watcher.LastPoll;

        File.Delete(Path.Combine(_dir, "lockfile"));
        await TickAsync();

        Assert.False(_watcher.IsConnected);
        Assert.Equal(1, _disconnects);
    }

    [Fact]
    public async Task Reconnects_when_lockfile_changes()
    {
        WriteLockfile(5000);
        _watcher.Start();
        await _watcher.LastPoll;

        WriteLockfile(6000);
        await TickAsync();

        Assert.Equal([5000, 6000], _attempts.Select(a => a.Port));
        Assert.Equal(1, _disconnects);
        Assert.True(_watcher.IsConnected);
    }

    [Fact]
    public async Task External_disconnect_allows_reconnect_to_same_lockfile()
    {
        WriteLockfile(5000);
        _watcher.Start();
        await _watcher.LastPoll;

        _watcher.MarkDisconnected();
        await TickAsync();

        Assert.Equal(2, _attempts.Count);
        Assert.True(_watcher.IsConnected);
    }
}
