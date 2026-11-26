using System.Text.Json.Nodes;

namespace LolConfigSync.Core.Sync;

/// <summary>Two-level diff/merge for <c>{section: {key: value}}</c> settings.</summary>
public static class SettingsDiff
{
    /// <summary>Values in <paramref name="current"/> that differ from, or are missing in, <paramref name="baseline"/>.</summary>
    public static JsonObject Changes(JsonObject current, JsonObject baseline)
    {
        var diff = new JsonObject();
        foreach (var (section, node) in current)
        {
            if (node is not JsonObject values) continue;
            var baseValues = baseline[section] as JsonObject;

            var changed = new JsonObject();
            foreach (var (name, value) in values)
            {
                var hasBase = baseValues is not null && baseValues.ContainsKey(name);
                if (!hasBase || !JsonNode.DeepEquals(value, baseValues![name]))
                {
                    changed[name] = value?.DeepClone();
                }
            }
            if (changed.Count > 0) diff[section] = changed;
        }
        return diff;
    }

    public static void MergeInto(JsonObject target, JsonObject changes)
    {
        foreach (var (section, node) in changes)
        {
            if (node is not JsonObject values) continue;
            if (target[section] is not JsonObject targetValues)
            {
                targetValues = [];
                target[section] = targetValues;
            }
            foreach (var (name, value) in values)
            {
                targetValues[name] = value?.DeepClone();
            }
        }
    }
}
