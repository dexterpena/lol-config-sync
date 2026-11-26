using System.Text.Json.Nodes;
using LolConfigSync.Core.Model;
using LolConfigSync.Core.Sync;

namespace LolConfigSync.Core.Tests.Sync;

public class SettingsFilterTests
{
    private static JsonObject Gs() => JsonNode.Parse("""
        {"HUD":{"GlobalScale":0.5,"FlipMiniMap":true},"Volume":{"MasterVolume":0.3},"General":{"CursorScale":1}}
        """)!.AsObject();

    [Fact]
    public void Disabled_section_is_dropped()
    {
        var app = new AppSettings();
        app.EnabledSections.Remove(SyncSection.Sound);

        var result = SettingsFilter.Apply(Gs(), SettingsKind.Game, app);

        Assert.False(result.ContainsKey("Volume"));
        Assert.True(result.ContainsKey("HUD"));
    }

    [Fact]
    public void Excluded_key_is_dropped_and_empty_section_removed()
    {
        var app = new AppSettings();
        app.ExcludedKeys.Add("game:General.CursorScale");

        var result = SettingsFilter.Apply(Gs(), SettingsKind.Game, app);

        Assert.False(result.ContainsKey("General"));
        Assert.Equal(0.5, result["HUD"]!["GlobalScale"]!.GetValue<double>());
    }

    [Fact]
    public void Filter_does_not_mutate_input()
    {
        var input = Gs();
        var app = new AppSettings();
        app.EnabledSections.Clear();

        SettingsFilter.Apply(input, SettingsKind.Game, app);

        Assert.True(input.ContainsKey("HUD"));
    }

    [Fact]
    public void Input_sections_map_to_hotkeys() =>
        Assert.Equal(SyncSection.Hotkeys, SectionMap.SectionFor(SettingsKind.Input, "GameEvents"));

    [Theory]
    [InlineData("Volume", SyncSection.Sound)]
    [InlineData("Voice", SyncSection.Sound)]
    [InlineData("General", SyncSection.Game)]
    [InlineData("HUD", SyncSection.Interface)]
    [InlineData("Chat", SyncSection.Interface)]
    public void Game_sections_map_per_spec(string section, SyncSection expected) =>
        Assert.Equal(expected, SectionMap.SectionFor(SettingsKind.Game, section));

    [Fact]
    public void Video_kind_maps_to_video() =>
        Assert.Equal(SyncSection.Video, SectionMap.SectionFor(SettingsKind.Video, "General"));

    [Fact]
    public void SettingKey_round_trips()
    {
        var key = new SettingKey(SettingsKind.Input, "GameEvents", "evtCastSpell1");
        Assert.Equal("input:GameEvents.evtCastSpell1", key.ToString());
        Assert.Equal(key, SettingKey.Parse("input:GameEvents.evtCastSpell1"));
    }

    [Fact]
    public void New_settings_enable_all_sections() =>
        Assert.Equal(Enum.GetValues<SyncSection>().Length, new AppSettings().EnabledSections.Count);
}
