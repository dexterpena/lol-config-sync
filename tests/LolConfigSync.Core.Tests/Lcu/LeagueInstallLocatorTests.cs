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
}
