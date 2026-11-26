namespace LolConfigSync.Core.Model;

/// <summary>User preferences for what syncs and how the app behaves.</summary>
public sealed class AppSettings
{
    public HashSet<SyncSection> EnabledSections { get; set; } = [.. Enum.GetValues<SyncSection>()];

    /// <summary>Per-setting exclusions, stored as <see cref="SettingKey.ToString"/>.</summary>
    public HashSet<string> ExcludedKeys { get; set; } = [];

    public bool StartWithWindows { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public string? InstallDirOverride { get; set; }

    /// <summary>Check GitHub for a new version once a day.</summary>
    public bool CheckForUpdates { get; set; } = true;
    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>Rune page slots always left empty so a rune tool (Porofessor) can create its page in champ select.</summary>
    public int ReservedRunePageSlots { get; set; } = 1;

    /// <summary>Rune pages whose names start with these belong to a rune tool: never synced, cleared to free their slot.</summary>
    public List<string> ToolRunePagePrefixes { get; set; } = ["Porofessor", "Poro"];

    public bool IsEnabled(SyncSection section) => EnabledSections.Contains(section);
    public bool IsExcluded(SettingKey key) => ExcludedKeys.Contains(key.ToString());
}
