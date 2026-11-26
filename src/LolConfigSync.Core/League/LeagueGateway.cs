using System.Text.Json.Nodes;
using LolConfigSync.Core.Lcu;

namespace LolConfigSync.Core.League;

/// <summary><see cref="ILeagueGateway"/> over the LCU REST API.</summary>
public sealed class LeagueGateway(ILcuClient lcu) : ILeagueGateway
{
    public const string CurrentSummoner = "/lol-summoner/v1/current-summoner";
    public const string GameflowPhase = "/lol-gameflow/v1/gameflow-phase";
    public const string GameSettings = "/lol-game-settings/v1/game-settings";
    public const string InputSettings = "/lol-game-settings/v1/input-settings";
    public const string SaveSettings = "/lol-game-settings/v1/save";
    public const string RunePages = "/lol-perks/v1/pages";
    public const string RuneInventory = "/lol-perks/v1/inventory";
    public const string ItemSetsPrefix = "/lol-item-sets/v1/item-sets/";
    public const string ClientSettingsPrefix = "/lol-settings/v2/account/LCUPreferences/";

    public async Task<Account?> GetCurrentAccountAsync(CancellationToken ct = default)
    {
        var node = await GetOrNullAsync(CurrentSummoner, ct).ConfigureAwait(false);
        var puuid = node?["puuid"]?.GetValue<string>();
        if (string.IsNullOrEmpty(puuid)) return null;
        var riotId = $"{node!["gameName"]?.GetValue<string>()}#{node["tagLine"]?.GetValue<string>()}";
        return new Account(puuid, riotId, node["summonerId"]?.GetValue<long>() ?? 0);
    }

    public async Task<string> GetGameflowPhaseAsync(CancellationToken ct = default) =>
        (await lcu.GetAsync(GameflowPhase, ct).ConfigureAwait(false))?.GetValue<string>() ?? "None";

    public Task<JsonObject> GetGameSettingsAsync(CancellationToken ct = default) => GetObjectAsync(GameSettings, ct);
    public Task PatchGameSettingsAsync(JsonObject settings, CancellationToken ct = default) => lcu.SendAsync(HttpMethod.Patch, GameSettings, settings, ct);
    public Task<JsonObject> GetInputSettingsAsync(CancellationToken ct = default) => GetObjectAsync(InputSettings, ct);
    public Task PatchInputSettingsAsync(JsonObject settings, CancellationToken ct = default) => lcu.SendAsync(HttpMethod.Patch, InputSettings, settings, ct);
    public Task SaveGameSettingsAsync(CancellationToken ct = default) => lcu.SendAsync(HttpMethod.Post, SaveSettings, null, ct);

    public async Task<JsonArray> GetEditableRunePagesAsync(CancellationToken ct = default)
    {
        var pages = await lcu.GetAsync(RunePages, ct).ConfigureAwait(false) as JsonArray ?? [];
        return [.. pages
            .OfType<JsonObject>()
            .Where(p => p["isEditable"]?.GetValue<bool>() == true && p["isDeletable"]?.GetValue<bool>() == true)
            .Select(p => p.DeepClone())];
    }

    public async Task<int> GetRunePageCapacityAsync(CancellationToken ct = default) =>
        (await lcu.GetAsync(RuneInventory, ct).ConfigureAwait(false))?["ownedPageCount"]?.GetValue<int>() ?? 0;

    public Task DeleteRunePageAsync(long id, CancellationToken ct = default) => lcu.SendAsync(HttpMethod.Delete, $"{RunePages}/{id}", null, ct);
    public Task CreateRunePageAsync(JsonObject page, CancellationToken ct = default) => lcu.SendAsync(HttpMethod.Post, RunePages, page, ct);

    public async Task<JsonArray> GetItemSetsAsync(Account account, CancellationToken ct = default)
    {
        var node = await lcu.GetAsync(ItemSetsPath(account), ct).ConfigureAwait(false);
        return node?["itemSets"]?.DeepClone() as JsonArray ?? [];
    }

    public Task PutItemSetsAsync(Account account, JsonArray sets, CancellationToken ct = default) =>
        lcu.SendAsync(HttpMethod.Put, ItemSetsPath(account), new JsonObject
        {
            ["accountId"] = account.SummonerId,
            ["itemSets"] = sets.DeepClone(),
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }, ct);

    public async Task<JsonObject?> GetClientSettingAsync(string category, CancellationToken ct = default) =>
        await GetOrNullAsync(ClientSettingsPrefix + category, ct).ConfigureAwait(false) as JsonObject;

    public Task PatchClientSettingAsync(string category, JsonObject value, CancellationToken ct = default) =>
        lcu.SendAsync(HttpMethod.Patch, ClientSettingsPrefix + category, value, ct);

    private static string ItemSetsPath(Account account) => $"{ItemSetsPrefix}{account.SummonerId}/sets";

    private async Task<JsonObject> GetObjectAsync(string path, CancellationToken ct) =>
        await lcu.GetAsync(path, ct).ConfigureAwait(false) as JsonObject ?? [];

    private async Task<JsonNode?> GetOrNullAsync(string path, CancellationToken ct)
    {
        try
        {
            return await lcu.GetAsync(path, ct).ConfigureAwait(false);
        }
        catch (LcuException e) when (e.StatusCode == 404)
        {
            return null;
        }
    }
}
