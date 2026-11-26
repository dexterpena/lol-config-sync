using System.Text.Json.Nodes;
using LolConfigSync.Core.Sync;

namespace LolConfigSync.Core.Tests.Sync;

public class RunePagePlannerTests
{
    private static JsonObject Page(long id, string name) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["primaryStyleId"] = 8100,
        ["subStyleId"] = 8300,
        ["selectedPerkIds"] = new JsonArray(8112, 8139),
        ["lastModified"] = 123,
        ["isActive"] = true,
        ["current"] = true,
    };

    [Fact]
    public void Planner_fills_up_to_capacity_and_reports_skipped()
    {
        JsonArray main = [Page(1, "Page1"), Page(2, "Page2"), Page(3, "Page3"), Page(4, "Page4")];
        JsonArray target = [Page(10, "Old1"), Page(11, "Old2")];

        var plan = RunePagePlanner.Plan(main, target, capacity: 3);

        Assert.Equal([10L, 11L], plan.DeleteIds);
        Assert.Equal(["Page1", "Page2", "Page3"], plan.Create.Select(p => p["name"]!.GetValue<string>()));
        Assert.Equal(["Page4"], plan.Skipped);
    }

    [Fact]
    public void Planner_strips_server_fields()
    {
        var plan = RunePagePlanner.Plan([Page(1, "Page1")], [], capacity: 5);

        var created = Assert.Single(plan.Create);
        Assert.False(created.ContainsKey("id"));
        Assert.False(created.ContainsKey("lastModified"));
        Assert.False(created.ContainsKey("isActive"));
        Assert.False(created["current"]!.GetValue<bool>());
        Assert.Equal(8100, created["primaryStyleId"]!.GetValue<int>());
        Assert.Equal(2, created["selectedPerkIds"]!.AsArray().Count);
    }

    [Fact]
    public void Reserved_slots_are_left_free()
    {
        JsonArray main = [Page(1, "Page1"), Page(2, "Page2"), Page(3, "Page3")];

        var plan = RunePagePlanner.Plan(main, [], capacity: 3, reservedSlots: 1);

        Assert.Equal(["Page1", "Page2"], plan.Create.Select(p => p["name"]!.GetValue<string>()));
        Assert.Equal(["Page3"], plan.Skipped);
        Assert.Equal(2, plan.UsableSlots);
    }

    [Theory]
    [InlineData("Porofessor - Ahri Mid", true)]
    [InlineData("poro: Jinx", true)]
    [InlineData("Ahri Poro build", false)]
    [InlineData("My Page", false)]
    public void Tool_pages_are_recognised_by_prefix(string name, bool expected) =>
        Assert.Equal(expected, RunePagePlanner.IsToolPage(Page(1, name), ["Porofessor", "Poro"]));

    [Fact]
    public void Zero_capacity_skips_everything() =>
        Assert.Equal(["Page1"], RunePagePlanner.Plan([Page(1, "Page1")], [], capacity: 0).Skipped);
}
