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
    public void Backups_are_saved_under_the_accounts_nickname()
    {
        var path = Store.SaveBackup(Snapshot("puuid-123", DateTimeOffset.UnixEpoch)); // RiotId "puuid-123#NA1"

        Assert.Equal(Path.Combine(_root, "backups", "puuid-123#NA1"), Path.GetDirectoryName(path));
    }

    [Theory]
    [InlineData("Bad:Name?#EUW", "Bad_Name_#EUW")]
    [InlineData("", "puuid-x")]
    [InlineData("  ", "puuid-x")]
    public void Nickname_folder_is_made_safe_and_falls_back_to_account_id(string riotId, string expectedFolder)
    {
        var snapshot = new SetupSnapshot { CapturedFromPuuid = "puuid-x", CapturedFromRiotId = riotId, CapturedAt = DateTimeOffset.UnixEpoch };

        var path = Store.SaveBackup(snapshot);

        Assert.Equal(expectedFolder, Path.GetFileName(Path.GetDirectoryName(path)));
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

    private const string AppDir = @"E:\Games\LolConfigSync";

    [Fact]
    public void Data_lives_next_to_the_program_when_writable() =>
        Assert.Equal(Path.Combine(AppDir, "data"), AppDataStore.ResolveRoot(null, AppDir, _ => true));

    [Fact]
    public void Falls_back_to_appdata_when_program_folder_is_read_only()
    {
        var root = AppDataStore.ResolveRoot(null, @"C:\Program Files\LolConfigSync", _ => false);

        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LolConfigSync"), root);
    }

    [Fact]
    public void Data_dir_override_wins_over_program_folder()
    {
        Assert.Equal(_root, AppDataStore.ResolveRoot(_root, AppDir, _ => true));
        Assert.Equal(Path.Combine(AppDir, "data"), AppDataStore.ResolveRoot("  ", AppDir, _ => true));
    }

    [Fact]
    public void Writable_check_is_true_for_temp_and_creates_nothing_extra()
    {
        var dir = Path.Combine(_root, "probe");

        Assert.True(AppDataStore.CanWriteTo(dir));
        Assert.Empty(Directory.GetFiles(dir));
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
