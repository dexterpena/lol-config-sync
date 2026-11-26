using System.Text.Json.Nodes;
using LolConfigSync.Core.Sync;

namespace LolConfigSync.Core.Files;

/// <summary>Reads/writes the <see cref="SectionMap.VideoKeys"/> in a game.cfg file.</summary>
public sealed class GameCfgVideoFile(string gameCfgPath) : IVideoSettingsFile
{
    public JsonObject Read() =>
        File.Exists(gameCfgPath) ? GameCfgFile.ReadKeys(gameCfgPath, SectionMap.VideoKeys) : [];

    public void Write(JsonObject values) => GameCfgFile.WriteKeys(gameCfgPath, values);
}
