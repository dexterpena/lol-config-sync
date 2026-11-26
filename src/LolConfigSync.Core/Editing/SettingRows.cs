using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LolConfigSync.Core.Model;
using LolConfigSync.Core.Sync;

namespace LolConfigSync.Core.Editing;

/// <summary>Converts between a <see cref="SetupSnapshot"/> and editor rows.</summary>
public static class SettingRows
{
    private static readonly SettingsKind[] Kinds = [SettingsKind.Game, SettingsKind.Input, SettingsKind.Video];

    public static IReadOnlyList<SettingRow> Build(SetupSnapshot setup, AppSettings settings)
    {
        var rows = new List<SettingRow>();
        foreach (var kind in Kinds)
        {
            foreach (var (section, node) in setup.Settings(kind))
            {
                if (node is not JsonObject values) continue;
                foreach (var (name, value) in values)
                {
                    var key = new SettingKey(kind, section, name);
                    rows.Add(new SettingRow(key, SectionMap.SectionFor(kind, section), SettingLabels.For(key),
                        Display(value), !settings.IsExcluded(key)));
                }
            }
        }
        return rows
            .OrderBy(r => r.Section)
            .ThenBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Writes edited values back (keeping each value's original JSON type) and updates exclusions.
    /// Returns the keys whose edited text couldn't be converted; those keep their old value.
    /// </summary>
    public static IReadOnlyList<SettingKey> ApplyTo(SetupSnapshot setup, AppSettings settings, IEnumerable<SettingRow> rows)
    {
        var rejected = new List<SettingKey>();
        foreach (var row in rows)
        {
            if (row.Sync) settings.ExcludedKeys.Remove(row.Key.ToString());
            else settings.ExcludedKeys.Add(row.Key.ToString());

            if (!row.IsModified) continue;
            if (setup.Settings(row.Key.Kind)[row.Key.Section] is not JsonObject values) continue;

            if (TryConvert(values[row.Key.Name], row.Value, out var converted)) values[row.Key.Name] = converted;
            else rejected.Add(row.Key);
        }
        return rejected;
    }

    private static string Display(JsonNode? value) => value?.GetValueKind() switch
    {
        null or JsonValueKind.Null => "",
        JsonValueKind.String => value.GetValue<string>(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.ToJsonString(),
    };

    private static bool TryConvert(JsonNode? original, string text, out JsonNode? converted)
    {
        text = text.Trim();
        switch (original?.GetValueKind())
        {
            case JsonValueKind.True or JsonValueKind.False:
                converted = bool.TryParse(text, out var b) ? JsonValue.Create(b) : null;
                return converted is not null;

            case JsonValueKind.Number:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d))
                {
                    converted = null;
                    return false;
                }
                var wasIntegral = !original.ToJsonString().Contains('.');
                converted = wasIntegral && d == Math.Floor(d) && Math.Abs(d) < long.MaxValue
                    ? JsonValue.Create((long)d)
                    : JsonValue.Create(d);
                return true;

            default:
                converted = JsonValue.Create(text);
                return true;
        }
    }
}
