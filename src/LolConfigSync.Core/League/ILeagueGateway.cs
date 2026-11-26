using System.Text.Json.Nodes;

namespace LolConfigSync.Core.League;

/// <summary>A logged-in League account. <see cref="RiotId"/> is <c>gameName#tagLine</c>.</summary>
public sealed record Account(string Puuid, string RiotId, long SummonerId);

/// <summary>Typed access to the League client endpoints the app syncs.</summary>
public interface ILeagueGateway
{
    /// <summary>The logged-in account, or null when nobody is logged in.</summary>
    Task<Account?> GetCurrentAccountAsync(CancellationToken ct = default);
    Task<string> GetGameflowPhaseAsync(CancellationToken ct = default);

    /// <summary>Whether League has finished loading the account's game settings from Riot's servers.</summary>
    Task<bool> IsGameSettingsReadyAsync(CancellationToken ct = default);

    Task<JsonObject> GetGameSettingsAsync(CancellationToken ct = default);
    Task PatchGameSettingsAsync(JsonObject settings, CancellationToken ct = default);
    Task<JsonObject> GetInputSettingsAsync(CancellationToken ct = default);
    Task PatchInputSettingsAsync(JsonObject settings, CancellationToken ct = default);
    Task SaveGameSettingsAsync(CancellationToken ct = default);

    /// <summary>The account's own pages (editable and deletable), excluding presets.</summary>
    Task<JsonArray> GetEditableRunePagesAsync(CancellationToken ct = default);
    Task<int> GetRunePageCapacityAsync(CancellationToken ct = default);
    Task DeleteRunePageAsync(long id, CancellationToken ct = default);
    Task CreateRunePageAsync(JsonObject page, CancellationToken ct = default);

    Task<JsonArray> GetItemSetsAsync(Account account, CancellationToken ct = default);
    Task PutItemSetsAsync(Account account, JsonArray sets, CancellationToken ct = default);

    /// <summary><c>{data, schemaVersion}</c> for a client preference category, or null when absent.</summary>
    Task<JsonObject?> GetClientSettingAsync(string category, CancellationToken ct = default);
    Task PatchClientSettingAsync(string category, JsonObject value, CancellationToken ct = default);
}
