namespace LolConfigSync.Core.Model;

/// <summary>A user-toggleable group of things that sync.</summary>
public enum SyncSection
{
    Hotkeys,
    Sound,
    Interface,
    Game,
    Video,
    RunePages,
    ItemSets,
    ClientSettings,
}

/// <summary>Where a key/value setting comes from.</summary>
public enum SettingsKind
{
    /// <summary>Game.cfg sections exposed by <c>/lol-game-settings/v1/game-settings</c> (per account).</summary>
    Game,

    /// <summary>Input.ini sections exposed by <c>/lol-game-settings/v1/input-settings</c> (per account).</summary>
    Input,

    /// <summary>Local-only graphics keys in game.cfg.</summary>
    Video,
}
