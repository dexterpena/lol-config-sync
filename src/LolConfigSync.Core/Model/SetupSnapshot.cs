using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LolConfigSync.Core.Model;

public enum SnapshotPart { Game, Input, Video, Runes, ItemSets, Client }

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

    // Older backups have no record of which reads failed.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HashSet<SnapshotPart>? UnreadableParts { get; set; }

    internal void CopyPartsFrom(SetupSnapshot source, IEnumerable<SnapshotPart> parts)
    {
        foreach (var part in parts)
        {
            switch (part)
            {
                case SnapshotPart.Game: GameSettings = source.GameSettings; break;
                case SnapshotPart.Input: InputSettings = source.InputSettings; break;
                case SnapshotPart.Video: VideoSettings = source.VideoSettings; break;
                case SnapshotPart.Runes: RunePages = source.RunePages; break;
                case SnapshotPart.ItemSets: ItemSets = source.ItemSets; break;
                case SnapshotPart.Client: ClientSettings = source.ClientSettings; break;
            }
        }
    }

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
