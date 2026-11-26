using System.Text.Json.Nodes;

namespace LolConfigSync.Core.Sync;

public sealed record RunePagePlan(IReadOnlyList<long> DeleteIds, IReadOnlyList<JsonObject> Create, IReadOnlyList<string> Skipped);

/// <summary>Plans replacing an account's rune pages with the main setup's pages.</summary>
public static class RunePagePlanner
{
    private static readonly string[] PageFields = ["name", "primaryStyleId", "subStyleId", "selectedPerkIds"];

    public static RunePagePlan Plan(JsonArray mainPages, JsonArray targetEditablePages, int capacity)
    {
        var deletes = targetEditablePages
            .OfType<JsonObject>()
            .Select(p => p["id"]?.GetValue<long>())
            .OfType<long>()
            .ToList();

        var pages = mainPages.OfType<JsonObject>().ToList();
        var keep = Math.Max(0, capacity);
        var create = pages.Take(keep).Select(Strip).ToList();
        var skipped = pages.Skip(keep).Select(p => p["name"]?.GetValue<string>() ?? "(unnamed)").ToList();
        return new RunePagePlan(deletes, create, skipped);
    }

    private static JsonObject Strip(JsonObject page)
    {
        var clean = new JsonObject();
        foreach (var field in PageFields)
        {
            if (page[field] is { } value) clean[field] = value.DeepClone();
        }
        clean["current"] = false;
        return clean;
    }
}
