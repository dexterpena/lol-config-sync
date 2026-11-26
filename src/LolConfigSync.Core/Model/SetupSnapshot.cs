using System.Text.Json;
using System.Text.Json.Nodes;

namespace LolConfigSync.Core.Model;

/// <summary>
/// A full set of settings: the user's main setup, or an account backup.
/// Key/value settings are shaped <c>{section: {key: value}}</c>.
/// </summary>
public sealed class SetupSnapshot
{
    public JsonObject GameSettings { get; set; } = [];
    public JsonObject InputSettings { get; set; } = [];
    public JsonObject VideoSettings { get; set; } = [];
    public JsonArray RunePages { get; set; } = [];
    public JsonArray ItemSets { get; set; } = [];

    /// <summary><c>{category: {data, schemaVersion}}</c> for allowlisted client preference categories.</summary>
    public JsonObject ClientSettings { get; set; } = [];

    public DateTimeOffset CapturedAt { get; set; }
    public string CapturedFromPuuid { get; set; } = "";
    public string CapturedFromRiotId { get; set; } = "";

    public JsonObject Settings(SettingsKind kind) => kind switch
    {
        SettingsKind.Game => GameSettings,
        SettingsKind.Input => InputSettings,
        SettingsKind.Video => VideoSettings,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public SetupSnapshot DeepClone() =>
        JsonSerializer.Deserialize<SetupSnapshot>(JsonSerializer.Serialize(this))!;
}
