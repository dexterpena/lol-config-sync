using LolConfigSync.Core.Lcu;

namespace LolConfigSync.Core.Tests.Lcu;

public class LeagueInstallLocatorTests
{
    [Fact]
    public void Prefers_riot_installs_json()
    {
        var json = """{"associated_client":{"D:/Riot Games/League of Legends/":"x","D:/Riot Games/VALORANT/live/":"y"}}""";
        Assert.Equal(@"D:\Riot Games\League of Legends", LeagueInstallLocator.Locate(json, null, _ => true));
    }

    [Fact]
    public void Falls_back_to_running_process_then_default()
    {
        Assert.Equal(@"E:\LoL", LeagueInstallLocator.Locate(null, @"E:\LoL\LeagueClientUx.exe", _ => true));
        Assert.Equal(@"C:\Riot Games\League of Legends", LeagueInstallLocator.Locate("{}", null, d => d.StartsWith("C:")));
        Assert.Null(LeagueInstallLocator.Locate(null, null, _ => false));
    }

    [Fact]
    public void Ignores_installs_json_entry_that_does_not_exist()
    {
        var json = """{"associated_client":{"D:/Riot Games/League of Legends/":"x"}}""";
        Assert.Equal(@"E:\LoL", LeagueInstallLocator.Locate(json, @"E:\LoL\LeagueClientUx.exe", d => d.StartsWith("E:")));
    }

    [Fact]
    public void Malformed_installs_json_is_ignored()
    {
        Assert.Equal(@"E:\LoL", LeagueInstallLocator.Locate("{not json", @"E:\LoL\LeagueClientUx.exe", _ => true));
    }

    [Fact]
    public void Running_client_wins_when_multiple_installs_exist()
    {
        var json = """{"associated_client":{"D:/League of Legends/":"riot"}}""";
        Assert.Equal(@"F:\Games\Live", LeagueInstallLocator.Locate(json, @"F:\Games\Live\LeagueClientUx.exe", _ => true));
    }

    [Fact]
    public void Recognizes_custom_folder_and_rejects_leftover_directories()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var stale = root.CreateSubdirectory("League of Legends");
            var custom = root.CreateSubdirectory("My Games");
            File.WriteAllText(Path.Combine(custom.FullName, "LeagueClient.exe"), "");
            var json = new System.Text.Json.Nodes.JsonObject
            {
                ["associated_client"] = new System.Text.Json.Nodes.JsonObject { [stale.FullName] = "riot", [custom.FullName] = "riot" }
            };
            Assert.Equal(custom.FullName, LeagueInstallLocator.Locate(json.ToJsonString(), null, LeagueInstallLocator.IsLeagueInstall));
        }
        finally { root.Delete(recursive: true); }
    }
}
