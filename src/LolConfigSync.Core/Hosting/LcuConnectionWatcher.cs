using LolConfigSync.Core.Lcu;

namespace LolConfigSync.Core.Hosting;

/// <summary>
/// Polls the League install dir for <c>lockfile</c>. Calls <c>tryConnect</c> when a new lockfile
/// appears (retrying on the next poll if that fails, e.g. a stale file after a crash) and raises
/// <see cref="Disconnected"/> when it goes away or changes.
/// </summary>
public sealed class LcuConnectionWatcher(string installDir, TimeProvider time, Func<LockfileInfo, Task<bool>> tryConnect) : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly string _lockfilePath = Path.Combine(installDir, "lockfile");
    private readonly object _gate = new();
    private ITimer? _timer;
    private string? _connectedContent;
    private bool _polling;

    public bool IsConnected { get; private set; }
    public event Action? Disconnected;

    /// <summary>The most recent poll (for tests).</summary>
    internal Task LastPoll { get; private set; } = Task.CompletedTask;

    public void Start()
    {
        _timer = time.CreateTimer(_ => Poll(), null, PollInterval, PollInterval);
        Poll();
    }

    /// <summary>The connection dropped while the lockfile stayed; reconnect on the next poll.</summary>
    public void MarkDisconnected()
    {
        lock (_gate)
        {
            IsConnected = false;
            _connectedContent = null;
        }
    }

    private void Poll()
    {
        lock (_gate)
        {
            if (_polling) return;
            _polling = true;
        }
        LastPoll = PollAsync();
    }

    private async Task PollAsync()
    {
        try
        {
            var content = ReadLockfile();
            if (content == _connectedContent && IsConnected) return;

            if (IsConnected)
            {
                MarkDisconnected();
                Disconnected?.Invoke();
            }

            if (content is null || !LockfileInfo.TryParse(content, out var info)) return;
            if (await tryConnect(info).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    IsConnected = true;
                    _connectedContent = content;
                }
            }
        }
        finally
        {
            lock (_gate) _polling = false;
        }
    }

    private string? ReadLockfile()
    {
        try
        {
            // The client keeps the lockfile open; share everything so we never block it.
            using var stream = new FileStream(_lockfilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose() => _timer?.Dispose();
}
