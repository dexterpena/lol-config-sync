using System.Text.Json.Nodes;

namespace LolConfigSync.Core.Files;

/// <summary>The local-only graphics settings, shaped <c>{section: {key: "value"}}</c>.</summary>
public interface IVideoSettingsFile
{
    JsonObject Read();
    void Write(JsonObject values);
}
