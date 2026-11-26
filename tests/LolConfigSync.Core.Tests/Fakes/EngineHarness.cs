using System.Text.Json.Nodes;
using LolConfigSync.Core.League;
using LolConfigSync.Core.Lcu;
using LolConfigSync.Core.Model;
using LolConfigSync.Core.Storage;
using LolConfigSync.Core.Sync;
using Microsoft.Extensions.Time.Testing;

namespace LolConfigSync.Core.Tests.Fakes;

/// <summary>A SyncEngine wired to fakes, a temp store, and a controllable clock.</summary>
public sealed class EngineHarness : IDisposable
{
    public static readonly Account Main = new("puuid-main", "Dex#NA1", 1);
    public static readonly Account Alt = new("puuid-alt", "Alt#EUW", 2);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lcs-engine-" + Guid.NewGuid().ToString("N"));

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
    public FakeVideoFile Video { get; } = new();
    public AppDataStore Store { get; }
    public SyncEngine Engine { get; }
    public List<string> Notifications { get; } = [];

    public EngineHarness()
    {
        Store = new AppDataStore(_root);
        Engine = new SyncEngine(Video, Store, Time);
        Engine.Notify += Notifications.Add;
    }

    public static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    public static FakeAccountState MainState() => new()
    {
        Game = Obj("""{"HUD":{"GlobalScale":0.5},"Volume":{"MasterVolume":0.3},"General":{"CursorScale":1}}"""),
        Input = Obj("""{"GameEvents":{"evtCastSpell1":"[q]"}}"""),
        Pages = [Obj("""{"id":1,"name":"MainPage","primaryStyleId":8100,"subStyleId":8300,"selectedPerkIds":[1,2]}""")],
        ItemSets = [Obj("""{"title":"MainSet"}""")],
    };

    public static FakeAccountState AltState() => new()
    {
        Game = Obj("""{"HUD":{"GlobalScale":0.9},"Volume":{"MasterVolume":0.8},"General":{"CursorScale":2}}"""),
        Input = Obj("""{"GameEvents":{"evtCastSpell1":"[a]"}}"""),
        Pages = [Obj("""{"id":7,"name":"AltPage","primaryStyleId":8000,"subStyleId":8200,"selectedPerkIds":[3]}""")],
        ItemSets = [Obj("""{"title":"AltSet"}""")],
    };

    /// <summary>Saves a main setup captured from <see cref="Main"/>'s state.</summary>
    public SetupSnapshot SeedMainSetup(Action<SetupSnapshot>? tweak = null)
    {
        var s = MainState();
        var snapshot = new SetupSnapshot
        {
            GameSettings = s.Game,
            InputSettings = s.Input,
            VideoSettings = Obj("""{"Performance":{"ShadowQuality":"4"}}"""),
            RunePages = s.Pages,
            ItemSets = s.ItemSets,
            ClientSettings = Obj("""{"lol-perks":{"data":{"x":1},"schemaVersion":1}}"""),
            CapturedAt = Time.GetUtcNow(),
            CapturedFromPuuid = Main.Puuid,
            CapturedFromRiotId = Main.RiotId,
        };
        tweak?.Invoke(snapshot);
        Store.SaveMainSetup(snapshot);
        return snapshot;
    }

    public void SetSettings(Action<AppSettings> change)
    {
        var settings = Store.LoadSettings();
        change(settings);
        Store.SaveSettings(settings);
    }

    public static LcuEvent Event(string uri, JsonNode? data, string type = "Update") => new(uri, type, data);

    public void Dispose()
    {
        Engine.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
