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
                    var isBoolean = value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False;
                    rows.Add(new SettingRow(key, SectionMap.SectionFor(kind, section), SettingLabels.For(key),
                        Display(value), !settings.IsExcluded(key), isBoolean));
                }
            }
        }
        return Sorted(rows);
    }

    /// <summary>Rows for editing one account's own values: each shows the account's value (own or main) and the main value.</summary>
    public static IReadOnlyList<SettingRow> BuildForAccount(SetupSnapshot setup, AppSettings settings, AccountOverrides overrides, string puuid)
    {
        var rows = new List<SettingRow>();
        foreach (var kind in Kinds)
        {
            foreach (var (section, node) in setup.Settings(kind))
            {
                if (node is not JsonObject values) continue;
                foreach (var (name, mainValue) in values)
                {
                    var key = new SettingKey(kind, section, name);
                    var own = overrides.Has(puuid, key);
                    var shown = own ? overrides.Get(puuid, key) : mainValue;
                    var isBoolean = mainValue?.GetValueKind() is JsonValueKind.True or JsonValueKind.False;
                    rows.Add(new SettingRow(key, SectionMap.SectionFor(kind, section), SettingLabels.For(key), Display(shown),
                        !settings.IsExcluded(key), isBoolean, accountMode: true, mainValue: Display(mainValue), isOwnValue: own));
                }
            }
        }
        return Sorted(rows);
    }

    /// <summary>
    /// Saves the account's own values from account-mode rows (typed like the main setup's values) and removes
    /// the ones turned off. Returns keys whose text couldn't be converted; those keep their old own value.
    /// </summary>
    public static IReadOnlyList<SettingKey> ApplyToAccount(AccountOverrides overrides, string puuid, string riotId, SetupSnapshot setup,
        IEnumerable<SettingRow> rows)
    {
        var rejected = new List<SettingKey>();
        foreach (var row in rows)
        {
            if (!row.IsOwnValue)
            {
                overrides.Remove(puuid, row.Key);
                continue;
            }
            var template = (setup.Settings(row.Key.Kind)[row.Key.Section] as JsonObject)?[row.Key.Name] ?? overrides.Get(puuid, row.Key);
            if (TryConvert(template, row.Value, out var converted)) overrides.Set(puuid, riotId, row.Key, converted);
            else rejected.Add(row.Key);
        }
        return rejected;
    }

    private static IReadOnlyList<SettingRow> Sorted(IEnumerable<SettingRow> rows) => rows
        .OrderBy(r => r.Section)
        .ThenBy(r => SettingLabels.Rank(r.Key))
        .ThenBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

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
