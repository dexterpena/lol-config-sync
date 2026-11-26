using System.Text.Json.Nodes;

namespace LolConfigSync.Core.Files;

/// <summary>
/// Minimal line-based access to League's INI-style config files. Rewrites only the lines it
/// changes, so comments, ordering and unknown sections survive untouched.
/// </summary>
public static class GameCfgFile
{
    private const string NewLine = "\r\n";

    /// <summary>Reads the requested keys as strings, shaped <c>{section: {key: value}}</c>, in the order requested.</summary>
    public static JsonObject ReadKeys(string path, IReadOnlyDictionary<string, string[]> keys)
    {
        var all = Parse(File.ReadAllLines(path));
        var result = new JsonObject();
        foreach (var (section, names) in keys)
        {
            if (!all.TryGetValue(section, out var values)) continue;
            var found = new JsonObject();
            foreach (var name in names)
            {
                if (values.TryGetValue(name, out var value)) found[name] = value;
            }
            if (found.Count > 0) result[section] = found;
        }
        return result;
    }

    /// <summary>Sets each <c>{section: {key: value}}</c>: replaces existing lines, appends missing keys/sections.</summary>
    public static void WriteKeys(string path, JsonObject values)
    {
        var lines = File.ReadAllLines(path).ToList();
        foreach (var (section, node) in values)
        {
            if (node is not JsonObject sectionValues) continue;
            foreach (var (name, value) in sectionValues)
            {
                SetValue(lines, section, name, value is null ? "" : ValueText(value));
            }
        }
        File.WriteAllText(path, string.Join(NewLine, lines) + NewLine);
    }

    /// <summary>Clears the read-only attribute. Returns whether it was set.</summary>
    public static bool ClearReadOnly(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || !info.IsReadOnly) return false;
        info.IsReadOnly = false;
        return true;
    }

    private static string ValueText(JsonNode value) =>
        value.GetValueKind() == System.Text.Json.JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();

    private static void SetValue(List<string> lines, string section, string name, string value)
    {
        var sectionStart = lines.FindIndex(l => IsHeader(l, out var s) && s == section);
        if (sectionStart < 0)
        {
            lines.Add($"[{section}]");
            lines.Add($"{name}={value}");
            return;
        }

        var sectionEnd = lines.FindIndex(sectionStart + 1, l => IsHeader(l, out _));
        if (sectionEnd < 0) sectionEnd = lines.Count;

        for (var i = sectionStart + 1; i < sectionEnd; i++)
        {
            if (TrySplit(lines[i], out var key, out _) && key == name)
            {
                lines[i] = $"{name}={value}";
                return;
            }
        }

        // Insert after the section's last non-blank line.
        var insertAt = sectionEnd;
        while (insertAt > sectionStart + 1 && string.IsNullOrWhiteSpace(lines[insertAt - 1])) insertAt--;
        lines.Insert(insertAt, $"{name}={value}");
    }

    private static Dictionary<string, Dictionary<string, string>> Parse(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        Dictionary<string, string>? current = null;
        foreach (var line in lines)
        {
            if (IsHeader(line, out var section))
            {
                if (!result.TryGetValue(section, out current))
                {
                    current = [];
                    result[section] = current;
                }
            }
            else if (current is not null && TrySplit(line, out var key, out var value))
            {
                current[key] = value;
            }
        }
        return result;
    }

    private static bool IsHeader(string line, out string section)
    {
        var t = line.Trim();
        if (t.Length > 2 && t[0] == '[' && t[^1] == ']')
        {
            section = t[1..^1];
            return true;
        }
        section = "";
        return false;
    }

    private static bool TrySplit(string line, out string key, out string value)
    {
        var eq = line.IndexOf('=');
        if (eq <= 0 || line.TrimStart().StartsWith(';'))
        {
            key = value = "";
            return false;
        }
        key = line[..eq].Trim();
        value = line[(eq + 1)..].Trim();
        return true;
    }
}
