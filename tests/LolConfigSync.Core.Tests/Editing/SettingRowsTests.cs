using System.Text.Json.Nodes;
using LolConfigSync.Core.Editing;
using LolConfigSync.Core.Model;

namespace LolConfigSync.Core.Tests.Editing;

public class SettingRowsTests
{
    private static SetupSnapshot Setup() => new()
    {
        GameSettings = JsonNode.Parse("""{"HUD":{"GlobalScale":0.5,"FlipMiniMap":true},"Volume":{"MasterVolume":0.3}}""")!.AsObject(),
        InputSettings = JsonNode.Parse("""{"GameEvents":{"evtCastSpell1":"[q]"}}""")!.AsObject(),
        VideoSettings = JsonNode.Parse("""{"Performance":{"ShadowQuality":"4"}}""")!.AsObject(),
    };

    [Fact]
    public void Build_makes_row_per_key_with_section()
    {
        var rows = SettingRows.Build(Setup(), new AppSettings());

        Assert.Equal(5, rows.Count);
        Assert.Equal(SyncSection.Sound, rows.Single(r => r.Key.Name == "MasterVolume").Section);
        Assert.Equal(SyncSection.Hotkeys, rows.Single(r => r.Key.Name == "evtCastSpell1").Section);
        Assert.Equal(SyncSection.Video, rows.Single(r => r.Key.Name == "ShadowQuality").Section);
        Assert.Equal("0.5", rows.Single(r => r.Key.Name == "GlobalScale").Value);
        Assert.All(rows, r => Assert.True(r.Sync));
    }

    [Fact]
    public void Build_marks_excluded_rows()
    {
        var settings = new AppSettings();
        settings.ExcludedKeys.Add("game:HUD.GlobalScale");

        Assert.False(SettingRows.Build(Setup(), settings).Single(r => r.Key.Name == "GlobalScale").Sync);
    }

    [Fact]
    public void Apply_keeps_bool_and_number_types()
    {
        var setup = Setup();
        var rows = SettingRows.Build(setup, new AppSettings());
        rows.Single(r => r.Key.Name == "FlipMiniMap").Value = "false";
        rows.Single(r => r.Key.Name == "GlobalScale").Value = "0.75";
        rows.Single(r => r.Key.Name == "evtCastSpell1").Value = "[a]";

        var rejected = SettingRows.ApplyTo(setup, new AppSettings(), rows);

        Assert.Empty(rejected);
        Assert.Equal("""{"HUD":{"GlobalScale":0.75,"FlipMiniMap":false},"Volume":{"MasterVolume":0.3}}""", setup.GameSettings.ToJsonString());
        Assert.Equal("[a]", setup.InputSettings["GameEvents"]!["evtCastSpell1"]!.GetValue<string>());
    }

    [Fact]
    public void Invalid_number_edit_is_rejected()
    {
        var setup = Setup();
        var rows = SettingRows.Build(setup, new AppSettings());
        rows.Single(r => r.Key.Name == "GlobalScale").Value = "abc";
        rows.Single(r => r.Key.Name == "FlipMiniMap").Value = "maybe";

        var rejected = SettingRows.ApplyTo(setup, new AppSettings(), rows);

        Assert.Equal(2, rejected.Count);
        Assert.Equal(0.5, setup.GameSettings["HUD"]!["GlobalScale"]!.GetValue<double>());
        Assert.True(setup.GameSettings["HUD"]!["FlipMiniMap"]!.GetValue<bool>());
    }

    [Fact]
    public void Unchecking_sync_adds_exclusion_and_checking_removes_it()
    {
        var setup = Setup();
        var settings = new AppSettings();
        settings.ExcludedKeys.Add("input:GameEvents.evtCastSpell1");
        var rows = SettingRows.Build(setup, settings);
        rows.Single(r => r.Key.Name == "GlobalScale").Sync = false;
        rows.Single(r => r.Key.Name == "evtCastSpell1").Sync = true;

        SettingRows.ApplyTo(setup, settings, rows);

        Assert.Contains("game:HUD.GlobalScale", settings.ExcludedKeys);
        Assert.DoesNotContain("input:GameEvents.evtCastSpell1", settings.ExcludedKeys);
    }

    [Fact]
    public void Row_raises_property_changed()
    {
        var row = SettingRows.Build(Setup(), new AppSettings())[0];
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.Value = "x";
        row.Sync = false;

        Assert.Equal(["Value", "IsModified", "Sync"], changed);
    }

    [Fact]
    public void Known_settings_keep_natural_order_before_unknown_ones()
    {
        var setup = new SetupSnapshot
        {
            InputSettings = JsonNode.Parse("""
                {"GameEvents":{"evtZzzCustom":"[z]","evtCastSpell3":"[e]","evtCastSpell1":"[q]","evtCastSpell4":"[r]","evtCastSpell2":"[w]"}}
                """)!.AsObject(),
        };

        var labels = SettingRows.Build(setup, new AppSettings()).Select(r => r.Label);

        Assert.Equal(["Ability Q", "Ability W", "Ability E", "Ability R", "Zzz Custom"], labels);
    }

    [Fact]
    public void Build_marks_boolean_rows()
    {
        var rows = SettingRows.Build(Setup(), new AppSettings());

        Assert.True(rows.Single(r => r.Key.Name == "FlipMiniMap").IsBoolean);
        Assert.False(rows.Single(r => r.Key.Name == "GlobalScale").IsBoolean);
    }

    [Theory]
    [InlineData(SettingsKind.Game, "HUD", "SomeNewThing", "Some New Thing")]
    [InlineData(SettingsKind.Input, "GameEvents", "evtToggleFooBar", "Toggle Foo Bar")]
    [InlineData(SettingsKind.Game, "HUD", "GlobalScale", "HUD Scale")]
    [InlineData(SettingsKind.Input, "GameEvents", "evtCastSpell1", "Ability Q")]
    public void Labels_are_friendly(SettingsKind kind, string section, string name, string expected) =>
        Assert.Equal(expected, SettingLabels.For(new SettingKey(kind, section, name)));
}
