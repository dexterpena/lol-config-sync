using LolConfigSync.Core.League;

namespace LolConfigSync.Core.Sync;

public enum SyncState
{
    WaitingForClient,
    Connected,
    WaitingForIdle,
    Syncing,
    Synced,
    Error,
}

public sealed record SyncStatus(SyncState State, Account? Account, DateTimeOffset? LastSync, IReadOnlyList<string> Warnings)
{
    public static SyncStatus Initial { get; } = new(SyncState.WaitingForClient, null, null, []);
}
