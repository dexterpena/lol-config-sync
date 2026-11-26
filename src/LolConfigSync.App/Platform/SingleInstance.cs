namespace LolConfigSync.App.Platform;

/// <summary>Keeps one copy of the app running; a second launch asks the first to show its window.</summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\LolConfigSync";
    private const string ShowEventName = @"Local\LolConfigSync.Show";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly RegisteredWaitHandle _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle showEvent, Action onShowRequested)
    {
        _mutex = mutex;
        _showEvent = showEvent;
        _registration = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => onShowRequested(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Returns null when another instance already owns the app.</summary>
    public static SingleInstance? TryAcquire(Action onShowRequested)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        return new SingleInstance(mutex, showEvent, onShowRequested);
    }

    public static void SignalExisting()
    {
        if (EventWaitHandle.TryOpenExisting(ShowEventName, out var handle))
        {
            using (handle) handle.Set();
        }
    }

    public void Dispose()
    {
        _registration.Unregister(null);
        _showEvent.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
