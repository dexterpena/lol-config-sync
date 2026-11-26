using System.Text.Json.Nodes;
using LolConfigSync.Core.Files;
using LolConfigSync.Core.Sync;

namespace LolConfigSync.Core.Tests.Files;

public sealed class GameCfgFileTests : IDisposable
{
    private const string Cfg = "[General]\r\nWindowMode=2\r\nCursorScale=0.5\r\n[Performance]\r\nShadowQuality=4\r\n[HUD]\r\nGlobalScale=0.3\r\n";
    private readonly string _path = Path.Combine(Path.GetTempPath(), "lcs-game-" + Guid.NewGuid().ToString("N") + ".cfg");

    public GameCfgFileTests() => File.WriteAllText(_path, Cfg);

    public void Dispose()
    {
        if (!File.Exists(_path)) return;
        File.SetAttributes(_path, FileAttributes.Normal);
        File.Delete(_path);
    }

    [Fact]
    public void Reads_only_requested_keys()
    {
        var values = GameCfgFile.ReadKeys(_path, SectionMap.VideoKeys);

        Assert.Equal("""{"Performance":{"ShadowQuality":"4"},"General":{"WindowMode":"2"}}""", values.ToJsonString());
    }

    [Fact]
    public void Write_replaces_and_appends_preserving_other_lines()
    {
        GameCfgFile.WriteKeys(_path, JsonNode.Parse("""
            {"General":{"WindowMode":"0","Width":"1920"},"Performance":{"ShadowQuality":"2"}}
            """)!.AsObject());

        Assert.Equal(
            "[General]\r\nWindowMode=0\r\nCursorScale=0.5\r\nWidth=1920\r\n[Performance]\r\nShadowQuality=2\r\n[HUD]\r\nGlobalScale=0.3\r\n",
            File.ReadAllText(_path));
    }

    [Fact]
    public void Write_appends_missing_section_at_end()
    {
        GameCfgFile.WriteKeys(_path, JsonNode.Parse("""{"Replay":{"Foo":"1"}}""")!.AsObject());

        Assert.EndsWith("GlobalScale=0.3\r\n[Replay]\r\nFoo=1\r\n", File.ReadAllText(_path));
    }

    [Fact]
    public void ClearReadOnly_reports_and_clears()
    {
        File.SetAttributes(_path, FileAttributes.ReadOnly);

        Assert.True(GameCfgFile.ClearReadOnly(_path));
        Assert.False(new FileInfo(_path).IsReadOnly);
        Assert.False(GameCfgFile.ClearReadOnly(_path));
    }

    [Fact]
    public void Video_file_reads_and_writes_video_keys()
    {
        var video = new GameCfgVideoFile(_path);

        video.Write(JsonNode.Parse("""{"Performance":{"ShadowQuality":"1"}}""")!.AsObject());

        Assert.Equal("1", video.Read()["Performance"]!["ShadowQuality"]!.GetValue<string>());
    }
}
