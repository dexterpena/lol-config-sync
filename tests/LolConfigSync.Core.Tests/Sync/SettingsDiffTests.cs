using System.Text.Json.Nodes;
using LolConfigSync.Core.Sync;

namespace LolConfigSync.Core.Tests.Sync;

public class SettingsDiffTests
{
    private static JsonObject Gs() => JsonNode.Parse("""
        {"HUD":{"GlobalScale":0.5,"FlipMiniMap":true},"Volume":{"MasterVolume":0.3}}
        """)!.AsObject();

    [Fact]
    public void Diff_returns_only_changed_values()
    {
        var current = Gs();
        current["HUD"]!["GlobalScale"] = 0.7;

        var diff = SettingsDiff.Changes(current, Gs());

        Assert.Equal("""{"HUD":{"GlobalScale":0.7}}""", diff.ToJsonString());
    }

    [Fact]
    public void Diff_includes_keys_missing_from_baseline()
    {
        var current = Gs();
        current["Chat"] = new JsonObject { ["ChatScale"] = 50 };

        var diff = SettingsDiff.Changes(current, Gs());

        Assert.Equal("""{"Chat":{"ChatScale":50}}""", diff.ToJsonString());
    }

    [Fact]
    public void Identical_settings_have_empty_diff() =>
        Assert.Empty(SettingsDiff.Changes(Gs(), Gs()));

    [Fact]
    public void Merge_applies_changes()
    {
        var target = Gs();

        SettingsDiff.MergeInto(target, JsonNode.Parse("""{"HUD":{"GlobalScale":0.9},"Chat":{"ChatScale":50}}""")!.AsObject());

        Assert.Equal(0.9, target["HUD"]!["GlobalScale"]!.GetValue<double>());
        Assert.True(target["HUD"]!["FlipMiniMap"]!.GetValue<bool>());
        Assert.Equal(50, target["Chat"]!["ChatScale"]!.GetValue<int>());
    }
}
