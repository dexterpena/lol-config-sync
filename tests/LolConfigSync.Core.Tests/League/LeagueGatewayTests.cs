using System.Text.Json.Nodes;
using LolConfigSync.Core.League;
using LolConfigSync.Core.Tests.Fakes;

namespace LolConfigSync.Core.Tests.League;

public class LeagueGatewayTests
{
    private readonly FakeLcuClient _lcu = new();
    private LeagueGateway Gateway => new(_lcu);

    [Fact]
    public async Task Current_account_maps_riot_id()
    {
        _lcu.Responses["/lol-summoner/v1/current-summoner"] = """{"puuid":"p","gameName":"Dex","tagLine":"NA1","summonerId":7}""";

        Assert.Equal(new Account("p", "Dex#NA1", 7), await Gateway.GetCurrentAccountAsync());
    }

    [Fact]
    public async Task Current_account_includes_icon_and_level()
    {
        _lcu.Responses["/lol-summoner/v1/current-summoner"] =
            """{"puuid":"p","gameName":"Dex","tagLine":"NA1","summonerId":7,"profileIconId":4568,"summonerLevel":312}""";

        var account = await Gateway.GetCurrentAccountAsync();

        Assert.Equal(4568, account!.ProfileIconId);
        Assert.Equal(312, account.SummonerLevel);
    }

    [Fact]
    public async Task Profile_icon_is_fetched_from_game_data()
    {
        _lcu.Binary["/lol-game-data/assets/v1/profile-icons/4568.jpg"] = [1, 2, 3];

        Assert.Equal([1, 2, 3], await Gateway.GetProfileIconAsync(4568));
        Assert.Null(await Gateway.GetProfileIconAsync(1));
    }

    [Fact]
    public async Task Not_logged_in_returns_null() =>
        Assert.Null(await Gateway.GetCurrentAccountAsync());

    [Fact]
    public async Task Gameflow_phase_is_unquoted()
    {
        _lcu.Responses["/lol-gameflow/v1/gameflow-phase"] = "\"Lobby\"";

        Assert.Equal("Lobby", await Gateway.GetGameflowPhaseAsync());
    }

    [Fact]
    public async Task Item_sets_put_uses_target_account_id()
    {
        var account = new Account("p", "Dex#NA1", 7);

        await Gateway.PutItemSetsAsync(account, [new JsonObject { ["title"] = "Set" }]);

        var (method, path, body) = Assert.Single(_lcu.Sent);
        Assert.Equal(HttpMethod.Put, method);
        Assert.Equal("/lol-item-sets/v1/item-sets/7/sets", path);
        var json = JsonNode.Parse(body!)!;
        Assert.Equal(7, json["accountId"]!.GetValue<long>());
        Assert.Equal("Set", json["itemSets"]![0]!["title"]!.GetValue<string>());
        Assert.True(json["timestamp"]!.GetValue<long>() > 0);
    }

    [Fact]
    public async Task Editable_rune_pages_filters_presets()
    {
        _lcu.Responses["/lol-perks/v1/pages"] = """
            [{"id":1,"name":"Mine","isEditable":true,"isDeletable":true},
             {"id":2,"name":"Preset","isEditable":false,"isDeletable":false}]
            """;

        var pages = await Gateway.GetEditableRunePagesAsync();

        Assert.Equal("Mine", Assert.Single(pages)!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Missing_collection_payloads_are_read_failures()
    {
        _lcu.Responses[LeagueGateway.RunePages] = "null";
        _lcu.Responses["/lol-item-sets/v1/item-sets/7/sets"] = "{}";
        _lcu.Responses[LeagueGateway.RuneInventory] = "{}";
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => Gateway.GetEditableRunePagesAsync());
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => Gateway.GetItemSetsAsync(new("p", "Alt", 7)));
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => Gateway.GetRunePageCapacityAsync());
    }

    [Fact]
    public async Task Rune_capacity_reads_owned_page_count()
    {
        _lcu.Responses["/lol-perks/v1/inventory"] = """{"ownedPageCount":3,"customPageCount":3}""";

        Assert.Equal(3, await Gateway.GetRunePageCapacityAsync());
    }

    [Fact]
    public async Task Settings_writes_hit_pinned_endpoints()
    {
        var g = Gateway;
        await g.PatchGameSettingsAsync(new JsonObject());
        await g.PatchInputSettingsAsync(new JsonObject());
        await g.SaveGameSettingsAsync();
        await g.DeleteRunePageAsync(5);
        await g.CreateRunePageAsync(new JsonObject());
        await g.PatchClientSettingAsync("lol-perks", new JsonObject());

        Assert.Equal(
            [
                "PATCH /lol-game-settings/v1/game-settings",
                "PATCH /lol-game-settings/v1/input-settings",
                "POST /lol-game-settings/v1/save",
                "DELETE /lol-perks/v1/pages/5",
                "POST /lol-perks/v1/pages",
                "PATCH /lol-settings/v2/account/LCUPreferences/lol-perks",
            ],
            _lcu.Sent.Select(s => $"{s.Method} {s.Path}"));
    }

    [Fact]
    public async Task Missing_client_setting_returns_null() =>
        Assert.Null(await Gateway.GetClientSettingAsync("lol-perks"));
}
