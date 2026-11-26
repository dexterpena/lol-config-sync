using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LolConfigSync.Core.Model;

namespace LolConfigSync.Core.Storage;

public sealed record BackupEntry(string Puuid, string RiotId, DateTimeOffset CreatedAt, string Path);

/// <summary>Persists the main setup, app settings and per-account backups under one root directory.</summary>
public sealed class AppDataStore(string rootDir)
{
    public const int BackupsPerAccount = 10;
    private const string BackupTimeFormat = "yyyyMMdd-HHmmss-fff";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultRoot { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LolConfigSync");

    public string RootDir { get; } = rootDir;
    private string MainSetupPath => System.IO.Path.Combine(RootDir, "main-setup.json");
    private string SettingsPath => System.IO.Path.Combine(RootDir, "settings.json");
    private string BackupsDir => System.IO.Path.Combine(RootDir, "backups");

    public SetupSnapshot? LoadMainSetup() => TryRead<SetupSnapshot>(MainSetupPath);

    public void SaveMainSetup(SetupSnapshot snapshot) => WriteAtomic(MainSetupPath, snapshot);

    public AppSettings LoadSettings() => TryRead<AppSettings>(SettingsPath) ?? new AppSettings();

    public void SaveSettings(AppSettings settings) => WriteAtomic(SettingsPath, settings);

    /// <summary>Saves a backup named after <see cref="SetupSnapshot.CapturedAt"/> and prunes old ones for that account.</summary>
    public string SaveBackup(SetupSnapshot snapshot)
    {
        var dir = System.IO.Path.Combine(BackupsDir, SafeName(snapshot.CapturedFromPuuid));
        var name = snapshot.CapturedAt.UtcDateTime.ToString(BackupTimeFormat, CultureInfo.InvariantCulture);
        var path = System.IO.Path.Combine(dir, name + ".json");
        WriteAtomic(path, snapshot);

        foreach (var old in Directory.GetFiles(dir, "*.json").OrderDescending(StringComparer.Ordinal).Skip(BackupsPerAccount))
        {
            File.Delete(old);
        }
        return path;
    }

    public IReadOnlyList<BackupEntry> ListBackups()
    {
        if (!Directory.Exists(BackupsDir)) return [];
        var entries = new List<BackupEntry>();
        foreach (var file in Directory.EnumerateFiles(BackupsDir, "*.json", SearchOption.AllDirectories))
        {
            if (!DateTime.TryParseExact(System.IO.Path.GetFileNameWithoutExtension(file), BackupTimeFormat,
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var created))
            {
                continue;
            }
            var snapshot = TryRead<SetupSnapshot>(file);
            if (snapshot is null) continue;
            entries.Add(new BackupEntry(snapshot.CapturedFromPuuid, snapshot.CapturedFromRiotId,
                new DateTimeOffset(created, TimeSpan.Zero), file));
        }
        return entries.OrderByDescending(e => e.CreatedAt).ToList();
    }

    public SetupSnapshot LoadBackup(string path) =>
        TryRead<SetupSnapshot>(path) ?? throw new InvalidDataException($"Backup is missing or unreadable: {path}");

    private static T? TryRead<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }

    private static string SafeName(string puuid)
    {
        var name = string.Concat(puuid.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return name.Length == 0 ? "unknown" : name;
    }
}
