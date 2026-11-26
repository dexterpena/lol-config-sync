using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LolConfigSync.Core.Model;

namespace LolConfigSync.Core.Storage;

/// <summary>One saved backup. <see cref="IsOriginal"/> marks the account's settings from before its first sync, which is never pruned.</summary>
public sealed record BackupEntry(string Puuid, string RiotId, DateTimeOffset CreatedAt, string Path, bool IsOriginal = false);

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

    private const string OriginalBackupName = "original.json";

    /// <summary>
    /// Environment variable that redirects all app data to a sandbox folder. A sandboxed run never
    /// connects to the League client and never touches the registry or League's files.
    /// </summary>
    public const string DataDirVariable = "LOLCONFIGSYNC_DATA_DIR";

    /// <summary>Folder next to the program that holds its data, so a portable copy carries its setup with it.</summary>
    public const string PortableFolderName = "data";

    /// <summary>
    /// Where data lives: the sandbox override if set, else a <c>data</c> folder next to the program,
    /// else (when the program folder isn't writable, e.g. Program Files) <c>%APPDATA%\LolConfigSync</c>.
    /// </summary>
    public static string DefaultRoot =>
        ResolveRoot(Environment.GetEnvironmentVariable(DataDirVariable), AppContext.BaseDirectory, CanWriteTo);

    public static bool IsSandboxed => IsSandbox(Environment.GetEnvironmentVariable(DataDirVariable));

    public static bool IsSandbox(string? overrideDir) => !string.IsNullOrWhiteSpace(overrideDir);

    public static string ResolveRoot(string? overrideDir, string appDir, Func<string, bool> canWriteTo)
    {
        if (!string.IsNullOrWhiteSpace(overrideDir)) return overrideDir;
        var portable = System.IO.Path.Combine(appDir, PortableFolderName);
        return canWriteTo(portable)
            ? portable
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LolConfigSync");
    }

    /// <summary>True when <paramref name="dir"/> exists (or can be created) and a file can be written there.</summary>
    public static bool CanWriteTo(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = System.IO.Path.Combine(dir, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public string RootDir { get; } = rootDir;
    private string MainSetupPath => System.IO.Path.Combine(RootDir, "main-setup.json");
    private string PreviousMainSetupPath => System.IO.Path.Combine(RootDir, "main-setup.prev.json");
    private string SettingsPath => System.IO.Path.Combine(RootDir, "settings.json");
    private string BackupsDir => System.IO.Path.Combine(RootDir, "backups");

    public SetupSnapshot? LoadMainSetup() => TryRead<SetupSnapshot>(MainSetupPath);

    /// <summary>The main setup as it was before the most recent save (a one-step undo).</summary>
    public SetupSnapshot? LoadPreviousMainSetup() => TryRead<SetupSnapshot>(PreviousMainSetupPath);

    public void SaveMainSetup(SetupSnapshot snapshot)
    {
        if (File.Exists(MainSetupPath)) File.Copy(MainSetupPath, PreviousMainSetupPath, overwrite: true);
        WriteAtomic(MainSetupPath, snapshot);
    }

    public AppSettings LoadSettings() => TryRead<AppSettings>(SettingsPath) ?? new AppSettings();

    public void SaveSettings(AppSettings settings) => WriteAtomic(SettingsPath, settings);

    /// <summary>
    /// Saves a backup named after <see cref="SetupSnapshot.CapturedAt"/> and prunes old ones for that account.
    /// The account's very first backup is also pinned as its original and is never pruned.
    /// </summary>
    public string SaveBackup(SetupSnapshot snapshot)
    {
        var dir = System.IO.Path.Combine(BackupsDir, SafeName(snapshot.CapturedFromPuuid));
        var original = System.IO.Path.Combine(dir, OriginalBackupName);
        if (!File.Exists(original)) WriteAtomic(original, snapshot);

        var name = snapshot.CapturedAt.UtcDateTime.ToString(BackupTimeFormat, CultureInfo.InvariantCulture);
        var path = System.IO.Path.Combine(dir, name + ".json");
        WriteAtomic(path, snapshot);

        var rotating = Directory.GetFiles(dir, "*.json")
            .Where(f => !string.Equals(System.IO.Path.GetFileName(f), OriginalBackupName, StringComparison.OrdinalIgnoreCase));
        foreach (var old in rotating.OrderDescending(StringComparer.Ordinal).Skip(BackupsPerAccount))
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
            if (string.Equals(System.IO.Path.GetFileName(file), OriginalBackupName, StringComparison.OrdinalIgnoreCase))
            {
                if (TryRead<SetupSnapshot>(file) is { } first)
                {
                    entries.Add(new BackupEntry(first.CapturedFromPuuid, first.CapturedFromRiotId, first.CapturedAt, file, IsOriginal: true));
                }
                continue;
            }
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
