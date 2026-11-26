using System.Text.Json.Nodes;
using LolConfigSync.Core.Model;

namespace LolConfigSync.Core.Sync;

/// <summary>Keeps only the settings the user has chosen to sync.</summary>
public static class SettingsFilter
{
    /// <summary>Returns a deep copy without disabled sections, excluded keys, or sections left empty.</summary>
    public static JsonObject Apply(JsonObject settings, SettingsKind kind, AppSettings app)
    {
        var result = new JsonObject();
        foreach (var (section, node) in settings)
        {
            if (node is not JsonObject values) continue;
            if (!app.IsEnabled(SectionMap.SectionFor(kind, section))) continue;

            var kept = new JsonObject();
            foreach (var (name, value) in values)
            {
                if (app.IsExcluded(new SettingKey(kind, section, name))) continue;
                kept[name] = value?.DeepClone();
            }
            if (kept.Count > 0) result[section] = kept;
        }
        return result;
    }
}
