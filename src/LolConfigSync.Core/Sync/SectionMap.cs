using LolConfigSync.Core.Model;

namespace LolConfigSync.Core.Sync;

/// <summary>Maps raw settings sections to the user-facing <see cref="SyncSection"/> toggles.</summary>
public static class SectionMap
{
    /// <summary>Local-only graphics keys in game.cfg, by section.</summary>
    public static IReadOnlyDictionary<string, string[]> VideoKeys { get; } = new Dictionary<string, string[]>
    {
        ["Performance"] = ["ShadowQuality", "FrameCapType", "EnvironmentQuality", "EffectsQuality", "CharacterQuality", "EnableFXAA"],
        ["General"] = ["WindowMode", "Width", "Height", "WaitForVerticalSync"],
    };

    /// <summary>Client preference categories worth syncing; everything else is per-account history or tooltip flags.</summary>
    public static IReadOnlyList<string> ClientSettingsCategories { get; } =
    [
        "champ-select", "lol-champ-select", "custom-spell-selections", "lol-general", "lol-notifications",
        "lol-parties-game-select", "parties-settings", "partiesPositionPreferences", "lol-premade-voice",
        "lol-perks", "lol-quick-play", "lol-tft",
    ];

    public static SyncSection SectionFor(SettingsKind kind, string section) => kind switch
    {
        SettingsKind.Input => SyncSection.Hotkeys,
        SettingsKind.Video => SyncSection.Video,
        SettingsKind.Game => section switch
        {
            "Volume" or "Voice" => SyncSection.Sound,
            "General" => SyncSection.Game,
            _ => SyncSection.Interface,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
