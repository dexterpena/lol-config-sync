using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LolConfigSync.Core.Lcu;

/// <summary>Finds the League of Legends install directory (where <c>lockfile</c> and <c>Config\</c> live).</summary>
public static class LeagueInstallLocator
{
    public const string DefaultInstallDir = @"C:\Riot Games\League of Legends";

    public static string? Locate(string? riotClientInstallsJson, string? runningClientExePath, Func<string, bool> isLeagueInstall)
    {
        foreach (var candidate in Candidates(riotClientInstallsJson, runningClientExePath))
        {
            if (isLeagueInstall(candidate)) return candidate;
        }
        return null;
    }

    public static string? LocateOnThisMachine()
    {
        string? installsJson = null;
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games", "RiotClientInstalls.json");
            if (File.Exists(path)) installsJson = File.ReadAllText(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return Locate(installsJson, RunningClientExePath(), IsLeagueInstall);
    }

    public static bool IsLeagueInstall(string dir) =>
        Path.IsPathFullyQualified(dir) && File.Exists(Path.Combine(dir, "LeagueClient.exe"));

    private static IEnumerable<string> Candidates(string? installsJson, string? runningExe)
    {
        if (!string.IsNullOrEmpty(runningExe))
        {
            var dir = Path.GetDirectoryName(runningExe);
            if (!string.IsNullOrEmpty(dir)) yield return dir;
        }
        foreach (var dir in FromInstallsJson(installsJson)) yield return dir;
        yield return DefaultInstallDir;
    }

    private static IEnumerable<string> FromInstallsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["associated_client"] is not JsonObject clients) return [];
            return clients
                .Select(kv => kv.Key)
                .OrderByDescending(k => k.Contains("League of Legends", StringComparison.OrdinalIgnoreCase))
                .Select(Normalize)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Normalize(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);

    private static string? RunningClientExePath()
    {
        try
        {
            var proc = Process.GetProcessesByName("LeagueClientUx").FirstOrDefault();
            return proc?.MainModule?.FileName;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
