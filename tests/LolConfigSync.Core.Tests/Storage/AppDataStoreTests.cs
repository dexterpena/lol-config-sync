using System.Text.Json.Nodes;
using LolConfigSync.Core.Model;
using LolConfigSync.Core.Storage;

namespace LolConfigSync.Core.Tests.Storage;

public sealed class AppDataStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lcs-tests-" + Guid.NewGuid().ToString("N"));
    private AppDataStore Store => new(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static SetupSnapshot Snapshot(string puuid, DateTimeOffset at) => new()
    {
        GameSettings = JsonNode.Parse("""{"HUD":{"GlobalScale":0.42}}""")!.AsObject(),
        CapturedAt = at,
        CapturedFromPuuid = puuid,
        CapturedFromRiotId = puuid + "#NA1",
    };

    [Fact]
    public void Main_setup_round_trips()
    {
        Assert.Null(Store.LoadMainSetup());
        Store.SaveMainSetup(Snapshot("p1", DateTimeOffset.UnixEpoch));

        var loaded = Store.LoadMainSetup()!;

        Assert.Equal(0.42, loaded.GameSettings["HUD"]!["GlobalScale"]!.GetValue<double>());
        Assert.Equal("p1#NA1", loaded.CapturedFromRiotId);
    }

    [Fact]
    public void Settings_default_when_missing_or_corrupt()
    {
        Assert.Equal(8, Store.LoadSettings().EnabledSections.Count);

        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "settings.json"), "{not json");

        Assert.Equal(8, Store.LoadSettings().EnabledSections.Count);
    }

    [Fact]
    public void Settings_round_trip()
    {
        var s = new AppSettings { StartWithWindows = false };
        s.EnabledSections.Remove(SyncSection.Video);
        s.ExcludedKeys.Add("game:HUD.GlobalScale");
        Store.SaveSettings(s);

        var loaded = Store.LoadSettings();

        Assert.False(loaded.StartWithWindows);
        Assert.DoesNotContain(SyncSection.Video, loaded.EnabledSections);
        Assert.Contains("game:HUD.GlobalScale", loaded.ExcludedKeys);
    }

    [Fact]
    public void Backups_pruned_to_ten_per_account()
    {
        var t0 = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 12; i++) Store.SaveBackup(Snapshot("A", t0.AddMinutes(i)));
        Store.SaveBackup(Snapshot("B", t0));

        var backups = Store.ListBackups();

        Assert.Equal(10, backups.Count(b => b.Puuid == "A" && !b.IsOriginal));
        Assert.Single(backups, b => b.Puuid == "B" && !b.IsOriginal);
        Assert.DoesNotContain(backups, b => b.Puuid == "A" && !b.IsOriginal && b.CreatedAt < t0.AddMinutes(2));
    }

    [Fact]
    public void First_backup_per_account_is_kept_forever()
    {
        var t0 = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        var first = Snapshot("A", t0);
        first.GameSettings["HUD"]!["GlobalScale"] = 0.11;
        Store.SaveBackup(first);
        for (var i = 1; i <= 12; i++) Store.SaveBackup(Snapshot("A", t0.AddMinutes(i)));

        var backups = Store.ListBackups();

        var original = Assert.Single(backups, b => b.IsOriginal);
        Assert.Equal(t0, original.CreatedAt);
        Assert.Equal(0.11, Store.LoadBackup(original.Path).GameSettings["HUD"]!["GlobalScale"]!.GetValue<double>());
        Assert.Equal(10, backups.Count(b => b.Puuid == "A" && !b.IsOriginal));
    }

    [Fact]
    public void Saving_main_setup_keeps_previous_copy()
    {
        Store.SaveMainSetup(Snapshot("first", DateTimeOffset.UnixEpoch));
        Store.SaveMainSetup(Snapshot("second", DateTimeOffset.UnixEpoch));

        Assert.Equal("first#NA1", Store.LoadPreviousMainSetup()!.CapturedFromRiotId);
        Assert.Equal("second#NA1", Store.LoadMainSetup()!.CapturedFromRiotId);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("  ", false)]
    [InlineData(@"C:\sandbox", true)]
    public void Data_dir_override_means_sandbox(string? overrideDir, bool expected) =>
        Assert.Equal(expected, AppDataStore.IsSandbox(overrideDir));

    [Fact]
    public void Data_dir_override_wins_over_appdata()
    {
        Assert.Equal(_root, AppDataStore.ResolveRoot(_root));
        Assert.EndsWith("LolConfigSync", AppDataStore.ResolveRoot(null));
        Assert.EndsWith("LolConfigSync", AppDataStore.ResolveRoot("  "));
    }

    [Fact]
    public void Backups_listed_newest_first()
    {
        var t0 = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        Store.SaveBackup(Snapshot("A", t0));
        Store.SaveBackup(Snapshot("A", t0.AddHours(1)));

        var backups = Store.ListBackups();

        Assert.Equal(t0.AddHours(1), backups[0].CreatedAt);
        Assert.Equal("A#NA1", backups[0].RiotId);
        Assert.Equal(0.42, Store.LoadBackup(backups[0].Path).GameSettings["HUD"]!["GlobalScale"]!.GetValue<double>());
    }
}
