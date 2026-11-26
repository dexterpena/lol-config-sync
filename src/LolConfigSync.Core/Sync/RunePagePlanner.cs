using System.Text.Json;
using System.Text.Json.Nodes;

namespace LolConfigSync.Core.Sync;

/// <param name="UsableSlots">How many pages the account can hold after keeping the reserved slots free.</param>
public sealed record RunePagePlan(IReadOnlyList<long> DeleteIds, IReadOnlyList<JsonObject> Create, IReadOnlyList<string> Skipped, int UsableSlots);

/// <summary>Plans replacing an account's rune pages with the main setup's pages.</summary>
public static class RunePagePlanner
{
    private static readonly string[] PageFields = ["name", "primaryStyleId", "subStyleId", "selectedPerkIds"];

    /// <param name="reservedSlots">Slots left empty for tools like Porofessor that create their own page in champ select.</param>
    public static RunePagePlan Plan(JsonArray mainPages, JsonArray targetEditablePages, int capacity, int reservedSlots = 0)
    {
        var deletes = targetEditablePages
            .OfType<JsonObject>()
            .Select(p => p["id"]?.GetValue<long>())
            .OfType<long>()
            .ToList();

        var pages = mainPages.OfType<JsonObject>().ToList();
        var usable = Math.Max(0, capacity - Math.Max(0, reservedSlots));
        var create = pages.Take(usable).Select(Strip).ToList();
        var skipped = pages.Skip(usable).Select(p => p["name"]?.GetValue<string>() ?? "(unnamed)").ToList();
        return new RunePagePlan(deletes, create, skipped, usable);
    }

    /// <summary>A page made by a rune tool (e.g. "Porofessor - Ahri Mid"), recognised by its name prefix.</summary>
    public static bool IsToolPage(JsonObject page, IEnumerable<string> prefixes)
    {
        if (page["name"]?.GetValueKind() != JsonValueKind.String) return false;
        var name = page["name"]!.GetValue<string>();
        return prefixes.Any(p => p.Length > 0 && name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The pages that aren't tool pages, cloned.</summary>
    public static JsonArray WithoutToolPages(JsonArray pages, IEnumerable<string> prefixes)
    {
        var list = prefixes.ToList();
        return [.. pages.OfType<JsonObject>().Where(p => !IsToolPage(p, list)).Select(p => p.DeepClone())];
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
