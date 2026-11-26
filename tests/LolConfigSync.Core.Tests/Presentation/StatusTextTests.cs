using LolConfigSync.Core.League;
using LolConfigSync.Core.Presentation;
using LolConfigSync.Core.Sync;

namespace LolConfigSync.Core.Tests.Presentation;

public class StatusTextTests
{
    private static readonly Account Alt = new("p", "Alt#EUW", 2);
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Waiting_for_client()
    {
        var v = StatusText.Describe(SyncStatus.Initial, hasMainSetup: true, Now);
        Assert.Equal(StatusTone.Offline, v.Tone);
        Assert.Equal("Waiting for League", v.Headline);
    }

    [Fact]
    public void Connected_without_account_asks_to_log_in() =>
        Assert.Equal("Log into an account", StatusText.Describe(new SyncStatus(SyncState.Connected, null, null, []), true, Now).Headline);

    [Fact]
    public void Connected_without_main_setup_asks_to_pick_one()
    {
        var v = StatusText.Describe(new SyncStatus(SyncState.Connected, Alt, null, []), hasMainSetup: false, Now);
        Assert.Equal("Pick your main setup", v.Headline);
        Assert.Equal(StatusTone.Waiting, v.Tone);
    }

    [Fact]
    public void Waiting_for_game_to_end_names_account()
    {
        var v = StatusText.Describe(new SyncStatus(SyncState.WaitingForIdle, Alt, null, []), true, Now);
        Assert.Equal("Waiting for your game to end", v.Headline);
        Assert.Contains("Alt#EUW", v.Detail);
    }

    [Fact]
    public void Synced_shows_relative_time()
    {
        var v = StatusText.Describe(new SyncStatus(SyncState.Synced, Alt, Now.AddMinutes(-2), []), true, Now);
        Assert.Equal(StatusTone.Synced, v.Tone);
        Assert.Equal("Synced to Alt#EUW", v.Headline);
        Assert.Equal("Last synced 2 minutes ago", v.Detail);
    }

    [Fact]
    public void Synced_with_warnings_uses_warning_tone() =>
        Assert.Equal(StatusTone.Warning, StatusText.Describe(new SyncStatus(SyncState.Synced, Alt, Now, ["x"]), true, Now).Tone);

    [Fact]
    public void Syncing_tone() =>
        Assert.Equal(StatusTone.Syncing, StatusText.Describe(new SyncStatus(SyncState.Syncing, Alt, null, []), true, Now).Tone);

    [Theory]
    [InlineData(10, "just now")]
    [InlineData(60, "1 minute ago")]
    [InlineData(150, "2 minutes ago")]
    [InlineData(3600, "1 hour ago")]
    [InlineData(7300, "2 hours ago")]
    [InlineData(90000, "1 day ago")]
    [InlineData(200000, "2 days ago")]
    public void Relative_time(int seconds, string expected) =>
        Assert.Equal(expected, StatusText.Ago(TimeSpan.FromSeconds(seconds)));
}
