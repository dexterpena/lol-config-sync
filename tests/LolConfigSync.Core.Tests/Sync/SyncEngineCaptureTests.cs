using System.Text.Json.Nodes;
using LolConfigSync.Core.Model;
using LolConfigSync.Core.Sync;
using LolConfigSync.Core.Tests.Fakes;
using static LolConfigSync.Core.Tests.Fakes.EngineHarness;

namespace LolConfigSync.Core.Tests.Sync;

public sealed class SyncEngineCaptureTests : IDisposable
{
    private const string GameUri = "/lol-game-settings/v1/game-settings";
    private readonly EngineHarness _h = new();

    public void Dispose() => _h.Dispose();

    /// <summary>Logs Alt in, pushes the main setup, and waits out the settle window.</summary>
    private async Task<FakeLeagueGateway> PushedAltAsync()
    {
        _h.SeedMainSetup();
        var gw = new FakeLeagueGateway(Alt, AltState());
        await _h.Engine.OnConnectedAsync(gw, default);
        _h.Time.Advance(TimeSpan.FromSeconds(6));
        return gw;
    }

    private async Task AdvanceAndFlushAsync(TimeSpan by)
    {
        _h.Time.Advance(by);
        await _h.Engine.PendingFlush;
    }

    private double MainScale() => _h.Store.LoadMainSetup()!.GameSettings["HUD"]!["GlobalScale"]!.GetValue<double>();

    [Fact]
    public async Task Game_setting_change_captured_after_debounce()
    {
        await PushedAltAsync();

        await _h.Engine.HandleLcuEventAsync(Event(GameUri, Obj("""{"HUD":{"GlobalScale":0.9}}""")), default);
        await AdvanceAndFlushAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0.5, MainScale());

        await AdvanceAndFlushAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0.9, MainScale());
        Assert.Equal("Alt#EUW", _h.Store.LoadMainSetup()!.CapturedFromRiotId);
    }

    [Fact]
    public async Task Events_before_push_completes_are_not_captured()
    {
        _h.SeedMainSetup();
        var gw = new FakeLeagueGateway(Alt, AltState()) { Phase = "InProgress" };
        await _h.Engine.OnConnectedAsync(gw, default);

        await _h.Engine.HandleLcuEventAsync(Event(GameUri, Obj("""{"HUD":{"GlobalScale":0.9}}""")), default);
        gw.Phase = "Lobby";
        await _h.Engine.HandleLcuEventAsync(Event("/lol-gameflow/v1/gameflow-phase", JsonValue.Create("Lobby")), default);
        await AdvanceAndFlushAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0.5, MainScale());
        Assert.Equal(0.5, gw.State.Game["HUD"]!["GlobalScale"]!.GetValue<double>());
    }

    [Fact]
    public async Task Events_within_settle_window_ignored()
    {
        _h.SeedMainSetup();
        await _h.Engine.OnConnectedAsync(new FakeLeagueGateway(Alt, AltState()), default);
        _h.Time.Advance(TimeSpan.FromSeconds(2));

        await _h.Engine.HandleLcuEventAsync(Event(GameUri, Obj("""{"HUD":{"GlobalScale":0.9}}""")), default);
        await AdvanceAndFlushAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0.5, MainScale());
    }

    [Fact]
    public async Task Excluded_and_disabled_keys_not_captured()
    {
        await PushedAltAsync();
        _h.SetSettings(s =>
        {
            s.ExcludedKeys.Add("game:HUD.GlobalScale");
            s.EnabledSections.Remove(SyncSection.Sound);
        });

        await _h.Engine.HandleLcuEventAsync(Event(GameUri, Obj("""{"HUD":{"GlobalScale":0.9},"Volume":{"MasterVolume":0.1}}""")), default);
        await AdvanceAndFlushAsync(TimeSpan.FromSeconds(4));

        var main = _h.Store.LoadMainSetup()!;
        Assert.Equal(0.5, main.GameSettings["HUD"]!["GlobalScale"]!.GetValue<double>());
        Assert.Equal(0.3, main.GameSettings["Volume"]!["MasterVolume"]!.GetValue<double>());
    }

    [Fact]
    public async Task Unchanged_values_produce_no_save()
    {
        await PushedAltAsync();
        var before = _h.Store.LoadMainSetup()!.CapturedAt;

        await _h.Engine.HandleLcuEventAsync(Event(GameUri, Obj("""{"HUD":{"GlobalScale":0.5}}""")), default);
        await AdvanceAndFlushAsync(TimeSpan.FromSeconds(4));

        Assert.Equal(before, _h.Store.LoadMainSetup()!.CapturedAt);
    }

    [Fact]
    public async Task Rune_pages_change_replaces_main_pages()
    {
        var gw = await PushedAltAsync();
        gw.State.Pages.Add(Obj("""{"id":99,"name":"NewPage","primaryStyleId":8000,"subStyleId":8100,"selectedPerkIds":[9]}"""));

        await _h.Engine.HandleLcuEventAsync(Event("/lol-perks/v1/pages", new JsonArray()), default);
        await AdvanceAndFlushAsync(TimeSpan.FromSeconds(4));

        Assert.Equal(["MainPage", "NewPage"], _h.Store.LoadMainSetup()!.RunePages.Select(p => p!["name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Item_set_and_allowlisted_client_category_captured()
    {
        var gw = await PushedAltAsync();
        gw.State.ItemSets = [Obj("""{"title":"Edited"}""")];
        gw.State.Client["lol-perks"] = Obj("""{"data":{"x":2},"schemaVersion":1}""");

        await _h.Engine.HandleLcuEventAsync(Event("/lol-item-sets/v1/item-sets/2/sets", null), default);
        await _h.Engine.HandleLcuEventAsync(Event("/lol-settings/v2/account/LCUPreferences/lol-perks", null), default);
        await AdvanceAndFlushAsync(TimeSpan.FromSeconds(4));

        var main = _h.Store.LoadMainSetup()!;
        Assert.Equal("Edited", main.ItemSets[0]!["title"]!.GetValue<string>());
        Assert.Equal(2, main.ClientSettings["lol-perks"]!["data"]!["x"]!.GetValue<int>());
    }

    [Fact]
    public async Task Non_allowlisted_client_category_ignored()
    {
        var gw = await PushedAltAsync();
        gw.State.Client["lol-loot"] = Obj("""{"data":{"seen":true},"schemaVersion":1}""");

        await _h.Engine.HandleLcuEventAsync(Event("/lol-settings/v2/account/LCUPreferences/lol-loot", null), default);
        await AdvanceAndFlushAsync(TimeSpan.FromSeconds(4));

        Assert.False(_h.Store.LoadMainSetup()!.ClientSettings.ContainsKey("lol-loot"));
    }

    [Fact]
    public async Task Video_captured_when_leaving_game()
    {
        await PushedAltAsync();
        await _h.Engine.HandleLcuEventAsync(Event("/lol-gameflow/v1/gameflow-phase", JsonValue.Create("InProgress")), default);
        _h.Video.Values = Obj("""{"Performance":{"ShadowQuality":"0"}}""");

        await _h.Engine.HandleLcuEventAsync(Event("/lol-gameflow/v1/gameflow-phase", JsonValue.Create("EndOfGame")), default);
        await AdvanceAndFlushAsync(TimeSpan.FromSeconds(4));

        Assert.Equal("0", _h.Store.LoadMainSetup()!.VideoSettings["Performance"]!["ShadowQuality"]!.GetValue<string>());
    }

    [Fact]
    public async Task Restore_rejects_other_account()
    {
        var gw = await PushedAltAsync();
        var mainBackup = _h.Store.SaveBackup(new SetupSnapshot { CapturedFromPuuid = Main.Puuid, CapturedFromRiotId = Main.RiotId, CapturedAt = _h.Time.GetUtcNow().AddDays(-1) });

        await Assert.ThrowsAsync<InvalidOperationException>(() => _h.Engine.RestoreBackupAsync(mainBackup, default));
    }

    [Fact]
    public async Task Restore_pushes_backup_values_unfiltered()
    {
        var gw = await PushedAltAsync();
        _h.SetSettings(s => s.EnabledSections.Remove(SyncSection.Sound));
        var altBackup = _h.Store.ListBackups().Single(b => b.Puuid == Alt.Puuid);

        await _h.Engine.RestoreBackupAsync(altBackup.Path, default);

        Assert.Equal(0.9, gw.State.Game["HUD"]!["GlobalScale"]!.GetValue<double>());
        Assert.Equal(0.8, gw.State.Game["Volume"]!["MasterVolume"]!.GetValue<double>());
        Assert.Equal(["AltPage"], gw.State.Pages.Select(p => p!["name"]!.GetValue<string>()));
        Assert.Equal(0.5, MainScale());
    }

    [Fact]
    public async Task Restored_values_are_not_captured_back()
    {
        var gw = await PushedAltAsync();
        await _h.Engine.RestoreBackupAsync(_h.Store.ListBackups().Single(b => b.Puuid == Alt.Puuid).Path, default);

        await _h.Engine.HandleLcuEventAsync(Event(GameUri, Obj("""{"HUD":{"GlobalScale":0.9}}""")), default);
        await AdvanceAndFlushAsync(TimeSpan.FromSeconds(4));

        Assert.Equal(0.5, MainScale());
    }

    [Fact]
    public async Task Adopt_replaces_main_setup()
    {
        await PushedAltAsync();
        var gw = new FakeLeagueGateway(Alt, AltState());
        await _h.Engine.OnConnectedAsync(gw, default);
        gw.State.Game["HUD"]!["GlobalScale"] = 0.77;

        await _h.Engine.AdoptCurrentAccountAsMainAsync(default);

        Assert.Equal(0.77, MainScale());
        Assert.Equal("Alt#EUW", _h.Store.LoadMainSetup()!.CapturedFromRiotId);
    }

    [Fact]
    public async Task Save_edited_setup_persists_and_pushes()
    {
        var gw = await PushedAltAsync();
        var edited = _h.Store.LoadMainSetup()!;
        edited.GameSettings["HUD"]!["GlobalScale"] = 0.33;
        var settings = _h.Store.LoadSettings();
        settings.ExcludedKeys.Add("input:GameEvents.evtCastSpell1");

        await _h.Engine.SaveEditedSetupAsync(edited, settings, default);

        Assert.Equal(0.33, MainScale());
        Assert.Contains("input:GameEvents.evtCastSpell1", _h.Store.LoadSettings().ExcludedKeys);
        Assert.Equal(0.33, gw.State.Game["HUD"]!["GlobalScale"]!.GetValue<double>());
    }
}
