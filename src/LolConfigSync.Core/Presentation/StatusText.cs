using LolConfigSync.Core.Sync;

namespace LolConfigSync.Core.Presentation;

public enum StatusTone
{
    Offline,
    Waiting,
    Syncing,
    Synced,
    Warning,
}

public sealed record StatusView(StatusTone Tone, string Headline, string Detail);

/// <summary>The words the main window shows for each sync state.</summary>
public static class StatusText
{
    public static StatusView Describe(SyncStatus status, bool hasMainSetup, DateTimeOffset now)
    {
        var riotId = status.Account?.RiotId;
        return status.State switch
        {
            SyncState.WaitingForClient => new(StatusTone.Offline, "Waiting for League",
                "Open the League client and log in. Your setup syncs automatically."),

            SyncState.Connected when riotId is null => new(StatusTone.Waiting, "Log into an account",
                "Your setup syncs as soon as you log in."),

            SyncState.Connected when !hasMainSetup => new(StatusTone.Waiting, "Pick your main setup",
                $"Choose \"Use this account as main\" to save {riotId}'s settings as the setup every account gets."),

            SyncState.Connected => new(StatusTone.Waiting, $"Connected to {riotId}", "Getting ready to sync."),

            SyncState.WaitingForIdle => new(StatusTone.Waiting, "Waiting for your game to end",
                $"Your setup syncs to {riotId} when you're back in the client."),

            SyncState.Syncing => new(StatusTone.Syncing, $"Syncing to {riotId}",
                "Backing up this account, then applying your setup."),

            SyncState.Synced => new(status.Warnings.Count > 0 ? StatusTone.Warning : StatusTone.Synced, $"Synced to {riotId}",
                status.LastSync is { } last ? $"Last synced {Ago(now - last)}" : "Up to date"),

            _ => new(StatusTone.Warning, "Sync stopped", "See the details below."),
        };
    }

    public static string Ago(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.FromMinutes(1)) return "just now";
        if (elapsed < TimeSpan.FromHours(1)) return Plural((int)elapsed.TotalMinutes, "minute");
        if (elapsed < TimeSpan.FromDays(1)) return Plural((int)elapsed.TotalHours, "hour");
        return Plural((int)elapsed.TotalDays, "day");
    }

    private static string Plural(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")} ago";
}
