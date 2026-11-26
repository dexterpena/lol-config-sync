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

    public bool IsEnabled(SyncSection section) => EnabledSections.Contains(section);
    public bool IsExcluded(SettingKey key) => ExcludedKeys.Contains(key.ToString());
}
