using System.Text.Json.Nodes;
using LolConfigSync.Core.Files;
using LolConfigSync.Core.Lcu;
using LolConfigSync.Core.League;
using LolConfigSync.Core.Sync;

namespace LolConfigSync.Core.Tests.Fakes;

/// <summary>One account's server-side state inside <see cref="FakeLeagueGateway"/>.</summary>
public sealed class FakeAccountState
{
    public JsonObject Game { get; set; } = [];
    public JsonObject Input { get; set; } = [];
    public JsonArray Pages { get; set; } = [];
    public int Capacity { get; set; } = 5;
    public JsonArray ItemSets { get; set; } = [];
    public Dictionary<string, JsonObject> Client { get; } = [];
}

/// <summary>In-memory League client: writes mutate state and are recorded in <see cref="Calls"/>.</summary>
public sealed class FakeLeagueGateway : ILeagueGateway
{
    private long _nextPageId = 1000;

    public Account? Account { get; private set; }
    public FakeAccountState State { get; private set; } = new();
    public string Phase { get; set; } = "Lobby";
    public List<string> Calls { get; } = [];

    /// <summary>"game", "input", "save", "runes", "itemsets", "client": writes in these categories throw.</summary>
    public HashSet<string> FailingCategories { get; } = [];

    public FakeLeagueGateway(Account? account = null, FakeAccountState? state = null)
    {
        Account = account;
        State = state ?? new FakeAccountState();
    }

    public void SwitchAccount(Account? account, FakeAccountState state)
    {
        Account = account;
        State = state;
    }

    public Task<Account?> GetCurrentAccountAsync(CancellationToken ct = default) => Task.FromResult(Account);
    public Task<string> GetGameflowPhaseAsync(CancellationToken ct = default) => Task.FromResult(Phase);

    public Task<JsonObject> GetGameSettingsAsync(CancellationToken ct = default) => Task.FromResult((JsonObject)State.Game.DeepClone());
    public Task<JsonObject> GetInputSettingsAsync(CancellationToken ct = default) => Task.FromResult((JsonObject)State.Input.DeepClone());

    public Task PatchGameSettingsAsync(JsonObject settings, CancellationToken ct = default) =>
        Write("game", $"PATCH game {settings.ToJsonString()}", () => SettingsDiff.MergeInto(State.Game, settings));

    public Task PatchInputSettingsAsync(JsonObject settings, CancellationToken ct = default) =>
        Write("input", $"PATCH input {settings.ToJsonString()}", () => SettingsDiff.MergeInto(State.Input, settings));

    public Task SaveGameSettingsAsync(CancellationToken ct = default) => Write("save", "POST save", () => { });

    public Task<JsonArray> GetEditableRunePagesAsync(CancellationToken ct = default) => Task.FromResult((JsonArray)State.Pages.DeepClone());
    public Task<int> GetRunePageCapacityAsync(CancellationToken ct = default) => Task.FromResult(State.Capacity);

    public Task DeleteRunePageAsync(long id, CancellationToken ct = default) =>
        Write("runes", $"DELETE rune {id}", () =>
        {
            var page = State.Pages.OfType<JsonObject>().First(p => p["id"]!.GetValue<long>() == id);
            State.Pages.Remove(page);
        });

    public Task CreateRunePageAsync(JsonObject page, CancellationToken ct = default) =>
        Write("runes", $"POST rune {page["name"]}", () =>
        {
            var created = (JsonObject)page.DeepClone();
            created["id"] = _nextPageId++;
            State.Pages.Add(created);
        });

    public Task<JsonArray> GetItemSetsAsync(Account account, CancellationToken ct = default) => Task.FromResult((JsonArray)State.ItemSets.DeepClone());

    public Task PutItemSetsAsync(Account account, JsonArray sets, CancellationToken ct = default) =>
        Write("itemsets", $"PUT itemsets {account.SummonerId}", () => State.ItemSets = (JsonArray)sets.DeepClone());

    public Task<JsonObject?> GetClientSettingAsync(string category, CancellationToken ct = default) =>
        Task.FromResult(State.Client.TryGetValue(category, out var v) ? (JsonObject?)v.DeepClone() : null);

    public Task PatchClientSettingAsync(string category, JsonObject value, CancellationToken ct = default) =>
        Write("client", $"PATCH client {category}", () => State.Client[category] = (JsonObject)value.DeepClone());

    private Task Write(string category, string call, Action apply)
    {
        if (FailingCategories.Contains(category)) throw new LcuException(500, category);
        Calls.Add(call);
        apply();
        return Task.CompletedTask;
    }
}

public sealed class FakeVideoFile : IVideoSettingsFile
{
    public JsonObject Values { get; set; } = [];
    public int Writes { get; private set; }

    public JsonObject Read() => (JsonObject)Values.DeepClone();

    public void Write(JsonObject values)
    {
        Writes++;
        SettingsDiff.MergeInto(Values, values);
    }
}
