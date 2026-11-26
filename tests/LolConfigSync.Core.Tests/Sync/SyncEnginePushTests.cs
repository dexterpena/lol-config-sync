using System.Text.Json.Nodes;
using LolConfigSync.Core.League;
using LolConfigSync.Core.Model;
using LolConfigSync.Core.Sync;
using LolConfigSync.Core.Tests.Fakes;
using static LolConfigSync.Core.Tests.Fakes.EngineHarness;

namespace LolConfigSync.Core.Tests.Sync;

public sealed class SyncEnginePushTests : IDisposable
{
    private readonly EngineHarness _h = new();

    public void Dispose() => _h.Dispose();

    private FakeLeagueGateway AltGateway() => new(Alt, AltState());

    [Fact]
    public async Task First_run_prompt_yes_adopts_account()
    {
        var gw = new FakeLeagueGateway(Main, MainState());
        _h.Video.Values = Obj("""{"Performance":{"ShadowQuality":"2"}}""");
        Account? asked = null;
        _h.Engine.FirstRunPrompt = a => { asked = a; return Task.FromResult(true); };

        await _h.Engine.OnConnectedAsync(gw, default);

        var main = _h.Store.LoadMainSetup()!;
        Assert.Equal(Main, asked);
        Assert.Equal("Dex#NA1", main.CapturedFromRiotId);
        Assert.Equal(0.5, main.GameSettings["HUD"]!["GlobalScale"]!.GetValue<double>());
        Assert.Equal("[q]", main.InputSettings["GameEvents"]!["evtCastSpell1"]!.GetValue<string>());
        Assert.Equal("2", main.VideoSettings["Performance"]!["ShadowQuality"]!.GetValue<string>());
        Assert.Equal("MainPage", main.RunePages[0]!["name"]!.GetValue<string>());
        Assert.Empty(gw.Calls);
    }

    [Fact]
    public async Task First_run_prompt_no_does_nothing()
    {
        var gw = new FakeLeagueGateway(Main, MainState());
        _h.Engine.FirstRunPrompt = _ => Task.FromResult(false);

        await _h.Engine.OnConnectedAsync(gw, default);

        Assert.Null(_h.Store.LoadMainSetup());
        Assert.Empty(gw.Calls);
    }

    [Fact]
    public async Task Login_pushes_filtered_setup_and_saves()
    {
        _h.SeedMainSetup();
        _h.SetSettings(s => s.EnabledSections.Remove(SyncSection.Sound));
        var gw = AltGateway();

        await _h.Engine.OnConnectedAsync(gw, default);

        var patch = gw.Calls.Single(c => c.StartsWith("PATCH game"));
        Assert.Contains("HUD", patch);
        Assert.DoesNotContain("Volume", patch);
        Assert.Contains("POST save", gw.Calls);
        Assert.Equal(0.5, gw.State.Game["HUD"]!["GlobalScale"]!.GetValue<double>());
        Assert.Equal(0.8, gw.State.Game["Volume"]!["MasterVolume"]!.GetValue<double>());
        Assert.Equal("[q]", gw.State.Input["GameEvents"]!["evtCastSpell1"]!.GetValue<string>());
        Assert.Equal(["Synced your setup to Alt#EUW"], _h.Notifications);
        Assert.Equal(SyncState.Synced, _h.Engine.Status.State);
        Assert.Equal(Alt, _h.Engine.Status.Account);
    }

    [Fact]
    public async Task Excluded_key_never_pushed()
    {
        _h.SeedMainSetup();
        _h.SetSettings(s => s.ExcludedKeys.Add("game:General.CursorScale"));
        var gw = AltGateway();

        await _h.Engine.OnConnectedAsync(gw, default);

        Assert.DoesNotContain("CursorScale", gw.Calls.Single(c => c.StartsWith("PATCH game")));
        Assert.Equal(2, gw.State.Game["General"]!["CursorScale"]!.GetValue<int>());
    }

    [Fact]
    public async Task Backup_written_before_push()
    {
        _h.SeedMainSetup();

        await _h.Engine.OnConnectedAsync(AltGateway(), default);

        var backup = Assert.Single(_h.Store.ListBackups());
        Assert.Equal(Alt.Puuid, backup.Puuid);
        var saved = _h.Store.LoadBackup(backup.Path);
        Assert.Equal(0.9, saved.GameSettings["HUD"]!["GlobalScale"]!.GetValue<double>());
        Assert.Equal("AltPage", saved.RunePages[0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Waits_for_idle_phase()
    {
        _h.SeedMainSetup();
        var gw = AltGateway();
        gw.Phase = "InProgress";

        await _h.Engine.OnConnectedAsync(gw, default);

        Assert.Empty(gw.Calls);
        Assert.Equal(SyncState.WaitingForIdle, _h.Engine.Status.State);

        gw.Phase = "Lobby";
        await _h.Engine.HandleLcuEventAsync(Event("/lol-gameflow/v1/gameflow-phase", JsonValue.Create("Lobby")), default);

        Assert.Contains(gw.Calls, c => c.StartsWith("PATCH game"));
        Assert.Equal(SyncState.Synced, _h.Engine.Status.State);
    }

    [Fact]
    public async Task Failing_category_is_isolated()
    {
        _h.SeedMainSetup();
        var gw = AltGateway();
        gw.FailingCategories.Add("runes");

        await _h.Engine.OnConnectedAsync(gw, default);

        Assert.Contains(gw.Calls, c => c.StartsWith("PATCH game"));
        Assert.Contains(gw.Calls, c => c.StartsWith("PATCH input"));
        Assert.Contains(gw.Calls, c => c.StartsWith("PUT itemsets"));
        Assert.Contains(gw.Calls, c => c.StartsWith("PATCH client"));
        Assert.Contains(_h.Engine.Status.Warnings, w => w.Contains("Rune pages"));
        Assert.Equal(SyncState.Synced, _h.Engine.Status.State);
    }

    [Fact]
    public async Task Rune_pages_replaced_on_target()
    {
        _h.SeedMainSetup();
        var gw = AltGateway();

        await _h.Engine.OnConnectedAsync(gw, default);

        Assert.Equal(["MainPage"], gw.State.Pages.Select(p => p!["name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Rune_capacity_shortfall_reported()
    {
        _h.SeedMainSetup(s => s.RunePages.Add(Obj("""{"id":2,"name":"SecondPage","primaryStyleId":8100,"subStyleId":8300,"selectedPerkIds":[1]}""")));
        var state = AltState();
        state.Capacity = 1;

        await _h.Engine.OnConnectedAsync(new FakeLeagueGateway(Alt, state), default);

        Assert.Contains(_h.Engine.Status.Warnings, w => w.Contains("SecondPage"));
    }

    [Fact]
    public async Task Video_written_when_enabled_and_differs()
    {
        _h.SeedMainSetup();
        _h.Video.Values = Obj("""{"Performance":{"ShadowQuality":"1"}}""");

        await _h.Engine.OnConnectedAsync(AltGateway(), default);

        Assert.Equal(1, _h.Video.Writes);
        Assert.Equal("4", _h.Video.Values["Performance"]!["ShadowQuality"]!.GetValue<string>());
    }

    [Fact]
    public async Task Video_not_written_when_equal_or_disabled()
    {
        _h.SeedMainSetup();
        _h.Video.Values = Obj("""{"Performance":{"ShadowQuality":"4"}}""");
        await _h.Engine.OnConnectedAsync(AltGateway(), default);
        Assert.Equal(0, _h.Video.Writes);

        _h.Video.Values = Obj("""{"Performance":{"ShadowQuality":"1"}}""");
        _h.SetSettings(s => s.EnabledSections.Remove(SyncSection.Video));
        await _h.Engine.PushMainSetupAsync(default);
        Assert.Equal(0, _h.Video.Writes);
    }

    [Fact]
    public async Task Account_switch_triggers_new_push()
    {
        _h.SeedMainSetup();
        var gw = new FakeLeagueGateway(Main, MainState());
        await _h.Engine.OnConnectedAsync(gw, default);

        gw.SwitchAccount(Alt, AltState());
        await _h.Engine.HandleLcuEventAsync(Event("/lol-summoner/v1/current-summoner", Obj("""{"puuid":"puuid-alt"}""")), default);

        Assert.Equal(2, _h.Store.ListBackups().Count);
        Assert.Equal(0.5, gw.State.Game["HUD"]!["GlobalScale"]!.GetValue<double>());
        Assert.Equal(Alt, _h.Engine.Status.Account);
    }

    [Fact]
    public async Task Disconnect_returns_to_waiting()
    {
        _h.SeedMainSetup();
        await _h.Engine.OnConnectedAsync(AltGateway(), default);

        _h.Engine.OnDisconnected();

        Assert.Equal(SyncState.WaitingForClient, _h.Engine.Status.State);
        Assert.Null(_h.Engine.Status.Account);
    }
}
