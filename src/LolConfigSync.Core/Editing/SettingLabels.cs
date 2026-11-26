using System.Text;
using LolConfigSync.Core.Model;

namespace LolConfigSync.Core.Editing;

/// <summary>Human-friendly names for settings keys, with a camel-case fallback for everything else.</summary>
public static class SettingLabels
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        // Hotkeys
        ["evtCastSpell1"] = "Ability Q",
        ["evtCastSpell2"] = "Ability W",
        ["evtCastSpell3"] = "Ability E",
        ["evtCastSpell4"] = "Ability R",
        ["evtCastAvatarSpell1"] = "Summoner Spell D",
        ["evtCastAvatarSpell2"] = "Summoner Spell F",
        ["evtLevelSpell1"] = "Level Up Q",
        ["evtLevelSpell2"] = "Level Up W",
        ["evtLevelSpell3"] = "Level Up E",
        ["evtLevelSpell4"] = "Level Up R",
        ["evtSelfCastSpell1"] = "Self-Cast Q",
        ["evtSelfCastSpell2"] = "Self-Cast W",
        ["evtSelfCastSpell3"] = "Self-Cast E",
        ["evtSelfCastSpell4"] = "Self-Cast R",
        ["evtUseItem1"] = "Item Slot 1",
        ["evtUseItem2"] = "Item Slot 2",
        ["evtUseItem3"] = "Item Slot 3",
        ["evtUseItem4"] = "Item Slot 4",
        ["evtUseItem5"] = "Item Slot 5",
        ["evtUseItem6"] = "Item Slot 6",
        ["evtUseVisionItem"] = "Trinket",
        ["evtPlayerAttackMove"] = "Attack Move",
        ["evtPlayerAttackMoveClick"] = "Attack Move on Cursor",
        ["evtPlayerAttackOnlyClick"] = "Attack Only on Cursor",
        ["evtPlayerStopPosition"] = "Stop",
        ["evtPlayerHoldPosition"] = "Hold Position",
        ["evtCameraLockToggle"] = "Toggle Camera Lock",
        ["evtCameraSnap"] = "Center Camera on Champion",
        ["evtSelectSelf"] = "Select Self",
        ["evtOpenShop"] = "Open Shop",
        ["evtShowScoreBoard"] = "Show Scoreboard",
        ["evtChatHistory"] = "Chat History",
        ["evtToggleMinionHealthBars"] = "Toggle Minion Health Bars",
        ["evtRecall"] = "Recall",
        ["evtPetMoveClick"] = "Pet Move",
        ["evtSmartPing"] = "Smart Ping",

        // Interface
        ["GlobalScale"] = "HUD Scale",
        ["MinimapScale"] = "Minimap Scale",
        ["ChatScale"] = "Chat Scale",
        ["ObjectiveVoteScale"] = "Objective Vote Scale",
        ["FlipMiniMap"] = "Minimap on Left",
        ["CameraLockMode"] = "Camera Lock Mode",
        ["DrawHealthBars"] = "Show Health Bars",
        ["ShowTimestamps"] = "Chat Timestamps",
        ["ShowAllChannelChat"] = "Show All Chat",
        ["HidePlayerNames"] = "Hide Player Names",
        ["ShowSummonerNames"] = "Show Summoner Names",
        ["ShowAttackRadius"] = "Show Attack Range",
        ["FlashScreenWhenDamaged"] = "Flash Screen When Damaged",
        ["FlashScreenWhenStunned"] = "Flash Screen When Stunned",
        ["EmoteSize"] = "Emote Size",

        // Game
        ["CursorScale"] = "Cursor Scale",
        ["GameMouseSpeed"] = "Mouse Speed",
        ["CameraOffsetScale"] = "Camera Offset",
        ["OffsetEdgepanSpeed"] = "Edge Pan Speed",
        ["SnapCameraOnRespawn"] = "Snap Camera on Respawn",
        ["AutoAcquireTarget"] = "Auto Attack",
        ["EnableTargetedAttackMove"] = "Attack Move Targets Champions",
        ["PredictMovement"] = "Predict Movement",
        ["ShowTurretRangeIndicators"] = "Show Turret Ranges",
        ["RelativeTeamColors"] = "Relative Team Colors",

        // Sound
        ["MasterVolume"] = "Master Volume",
        ["MusicVolume"] = "Music Volume",
        ["AnnouncerVolume"] = "Announcer Volume",
        ["SfxVolume"] = "Sound Effects Volume",
        ["VoiceVolume"] = "Voice Volume",
        ["AmbienceVolume"] = "Ambience Volume",
        ["PingsVolume"] = "Pings Volume",

        // Video (local)
        ["WindowMode"] = "Window Mode",
        ["Width"] = "Resolution Width",
        ["Height"] = "Resolution Height",
        ["WaitForVerticalSync"] = "V-Sync",
        ["ShadowQuality"] = "Shadow Quality",
        ["FrameCapType"] = "Frame Rate Cap",
        ["EnvironmentQuality"] = "Environment Quality",
        ["EffectsQuality"] = "Effects Quality",
        ["CharacterQuality"] = "Character Quality",
        ["EnableFXAA"] = "Anti-Aliasing",
    };

    private static readonly Dictionary<string, int> Order =
        Known.Keys.Select((name, index) => (name, index)).ToDictionary(x => x.name, x => x.index, StringComparer.Ordinal);

    public static string For(SettingKey key) =>
        Known.TryGetValue(key.Name, out var label) ? label : SplitWords(key.Name);

    /// <summary>Display order: known settings in their natural order (Q, W, E, R…), then everything else.</summary>
    public static int Rank(SettingKey key) => Order.TryGetValue(key.Name, out var rank) ? rank : int.MaxValue;

    private static string SplitWords(string name)
    {
        var text = name.StartsWith("evt", StringComparison.Ordinal) && name.Length > 3 && char.IsUpper(name[3]) ? name[3..] : name;
        var sb = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var boundary = i > 0 && (
                (char.IsUpper(c) && (char.IsLower(text[i - 1]) || (i + 1 < text.Length && char.IsLower(text[i + 1]) && char.IsUpper(text[i - 1]))))
                || (char.IsDigit(c) && !char.IsDigit(text[i - 1])));
            if (boundary) sb.Append(' ');
            sb.Append(i == 0 ? char.ToUpperInvariant(c) : c);
        }
        return sb.ToString().Replace('_', ' ');
    }
}
