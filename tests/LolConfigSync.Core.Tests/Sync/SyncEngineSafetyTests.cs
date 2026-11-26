using System.Text.Json.Nodes;
using LolConfigSync.Core.League;
using LolConfigSync.Core.Sync;
using LolConfigSync.Core.Tests.Fakes;
using static LolConfigSync.Core.Tests.Fakes.EngineHarness;

namespace LolConfigSync.Core.Tests.Sync;

/// <summary>Regression tests for the whole-branch review findings: nothing an account does may corrupt the main setup.</summary>
public sealed class SyncEngineSafetyTests : IDisposable
{
    private const string GameUri = "/lol-game-settings/v1/game-settings";
    private const string InputUri = "/lol-game-settings/v1/input-settings";
    private const string PagesUri = "/lol-perks/v1/pages";
    private readonly EngineHarness _h = new();

    public void Dispose() => _h.Dispose();

    private async Task FlushAsync()
    {
        _h.Time.Advance(TimeSpan.FromSeconds(4));
        await _h.Engine.PendingFlush;
    }

    private static JsonObject Page(long id, string name) =>
        Obj($$"""{"id":{{id}},"name":"{{name}}","primaryStyleId":8100,"subStyleId":8300,"selectedPerkIds":[1]}""");

    private string[] MainPageNames() =>
        _h.Store.LoadMainSetup()!.RunePages.Select(p => p!["name"]!.GetValue<string>()).ToArray();

    private double MainScale() => _h.Store.LoadMainSetup()!.GameSettings["HUD"]!["GlobalScale"]!.GetValue<double>();

    // Critical 1
    [Fact]
    public async Task Shortfall_account_capture_keeps_main_pages_beyond_capacity()
    {
        _h.SeedMainSetup(s => s.RunePages = [Page(1, "MainPage"), Page(2, "Second"), Page(3, "Third")]);
        var state = AltState();
        state.Capacity = 2; // one slot is kept free for Porofessor, so one page fits
        var gw = new FakeLeagueGateway(Alt, state);
        await _h.Engine.OnConnectedAsync(gw, default);
        _h.Time.Advance(TimeSpan.FromSeconds(6));

        gw.State.Pages[0]!["name"] = "Edited";
        await _h.Engine.HandleLcuEventAsync(Event(PagesUri, new JsonArray()), default);
        await FlushAsync();

        Assert.Equal(["Edited", "Second", "Third"], MainPageNames());
    }

    // Porofessor
    [Fact]
    public async Task Push_always_leaves_one_rune_slot_free()
    {
        _h.SeedMainSetup(s => s.RunePages = [Page(1, "First"), Page(2, "Second")]);
        var state = AltState();
        state.Capacity = 2;
        var gw = new FakeLeagueGateway(Alt, state);

        await _h.Engine.OnConnectedAsync(gw, default);

        Assert.Equal(["First"], gw.State.Pages.Select(p => p!["name"]!.GetValue<string>()));
        Assert.Contains(_h.Engine.Status.Warnings, w => w.Contains("Second") && w.Contains("Porofessor"));
    }

    [Fact]
    public async Task Porofessor_page_is_never_captured_into_main()
    {
        var gw = new FakeLeagueGateway(Alt, AltState());
        _h.SeedMainSetup();
        await _h.Engine.OnConnectedAsync(gw, default);
        _h.Time.Advance(TimeSpan.FromSeconds(6));

        gw.State.Pages.Add(Page(77, "Porofessor - Ahri Mid"));
        await _h.Engine.HandleLcuEventAsync(Event(PagesUri, new JsonArray()), default);
        await FlushAsync();

        Assert.Equal(["MainPage"], MainPageNames());
    }

    [Fact]
    public async Task Porofessor_page_in_the_free_slot_is_left_alone_when_pages_match()
    {
        _h.SeedMainSetup();
        var state = MainState();
        state.Pages.Add(Page(77, "Porofessor - Ahri Mid"));
        var gw = new FakeLeagueGateway(Main, state);

        await _h.Engine.OnConnectedAsync(gw, default);

        Assert.DoesNotContain(gw.Calls, c => c.Contains("rune"));
    }

    [Fact]
    public async Task Adopt_leaves_out_porofessor_pages()
    {
        var state = MainState();
        state.Pages.Add(Page(77, "Porofessor - Ahri Mid"));
        _h.Engine.FirstRunPrompt = (_, _) => Task.FromResult(true);

        await _h.Engine.OnConnectedAsync(new FakeLeagueGateway(Main, state), default);
        await _h.Engine.PendingPrompt;

        Assert.Equal(["MainPage"], MainPageNames());
    }

    // Important 3
    [Fact]
    public async Task Failed_game_push_blocks_game_capture_but_not_hotkeys()
    {
        _h.SeedMainSetup();
        var gw = new FakeLeagueGateway(Alt, AltState());
        gw.FailingCategories.Add("game");
        await _h.Engine.OnConnectedAsync(gw, default);
        _h.Time.Advance(TimeSpan.FromSeconds(6));

        await _h.Engine.HandleLcuEventAsync(Event(GameUri, (JsonObject)gw.State.Game.DeepClone()), default);
        await _h.Engine.HandleLcuEventAsync(Event(InputUri, Obj("""{"GameEvents":{"evtCastSpell1":"[z]"}}""")), default);
        await FlushAsync();

        var main = _h.Store.LoadMainSetup()!;
        Assert.Equal(0.5, main.GameSettings["HUD"]!["GlobalScale"]!.GetValue<double>());
        Assert.Equal("[z]", main.InputSettings["GameEvents"]!["evtCastSpell1"]!.GetValue<string>());
    }

    [Fact]
    public async Task Failed_rune_create_does_not_empty_main_pages()
    {
        _h.SeedMainSetup();
        var gw = new FakeLeagueGateway(Alt, AltState());
        gw.FailingCategories.Add("runes-create");
        await _h.Engine.OnConnectedAsync(gw, default);
        _h.Time.Advance(TimeSpan.FromSeconds(6));

        await _h.Engine.HandleLcuEventAsync(Event(PagesUri, new JsonArray()), default);
        await FlushAsync();

        Assert.Equal(["MainPage"], MainPageNames());
    }

    [Fact]
    public async Task Identical_rune_pages_are_not_rewritten()
    {
        _h.SeedMainSetup();
        var gw = new FakeLeagueGateway(Main, MainState());

        await _h.Engine.OnConnectedAsync(gw, default);

        Assert.DoesNotContain(gw.Calls, c => c.Contains("rune"));
    }

    // Important 4
    [Fact]
    public async Task Summoner_change_stops_capture_even_if_lookup_fails()
    {
        _h.SeedMainSetup();
        var gw = new FakeLeagueGateway(Alt, AltState());
        await _h.Engine.OnConnectedAsync(gw, default);
        _h.Time.Advance(TimeSpan.FromSeconds(6));

        gw.SwitchAccount(null, AltState());
        await _h.Engine.HandleLcuEventAsync(Event("/lol-summoner/v1/current-summoner", Obj("""{"puuid":"someone-else"}""")), default);
        await _h.Engine.HandleLcuEventAsync(Event(GameUri, Obj("""{"HUD":{"GlobalScale":0.9}}""")), default);
        await FlushAsync();

        Assert.Equal(0.5, MainScale());
        Assert.Null(_h.Engine.Status.Account);
    }

    // Important 5
    [Fact]
    public async Task Adopt_keeps_previous_category_when_its_read_fails()
    {
        _h.SeedMainSetup();
        var gw = new FakeLeagueGateway(Alt, AltState());
        await _h.Engine.OnConnectedAsync(gw, default);
        gw.State.Game["HUD"]!["GlobalScale"] = 0.9;
        gw.FailingReads.Add("itemsets");

        await _h.Engine.AdoptCurrentAccountAsMainAsync(default);

        var main = _h.Store.LoadMainSetup()!;
        Assert.Equal(0.9, main.GameSettings["HUD"]!["GlobalScale"]!.GetValue<double>());
        Assert.Equal("MainSet", main.ItemSets[0]!["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Push_skips_category_whose_backup_read_failed()
    {
        _h.SeedMainSetup();
        var gw = new FakeLeagueGateway(Alt, AltState());
        gw.FailingReads.Add("game");

        await _h.Engine.OnConnectedAsync(gw, default);

        Assert.DoesNotContain(gw.Calls, c => c.StartsWith("PATCH game"));
        Assert.Contains(gw.Calls, c => c.StartsWith("PATCH input"));
        Assert.Contains(_h.Engine.Status.Warnings, w => w.Contains("Game settings"));
    }

    // Important 6
    [Fact]
    public async Task First_run_prompt_waits_until_out_of_game()
    {
        var gw = new FakeLeagueGateway(Main, MainState()) { Phase = "InProgress" };
        var asked = 0;
        _h.Engine.FirstRunPrompt = (_, _) => { asked++; return Task.FromResult(true); };

        await _h.Engine.OnConnectedAsync(gw, default);
        Assert.Equal(0, asked);

        gw.Phase = "Lobby";
        await _h.Engine.HandleLcuEventAsync(Event("/lol-gameflow/v1/gameflow-phase", JsonValue.Create("Lobby")), default);
        await _h.Engine.PendingPrompt;

        Assert.Equal(1, asked);
        Assert.Equal("Dex#NA1", _h.Store.LoadMainSetup()!.CapturedFromRiotId);
    }

    [Fact]
    public async Task Disconnect_while_prompt_open_cancels_it_and_does_not_adopt()
    {
        var answer = new TaskCompletionSource<bool>();
        var token = CancellationToken.None;
        _h.Engine.FirstRunPrompt = (_, ct) => { token = ct; return answer.Task; };
        await _h.Engine.OnConnectedAsync(new FakeLeagueGateway(Main, MainState()), default);

        await _h.Engine.OnDisconnectedAsync();
        answer.SetResult(true);
        await _h.Engine.PendingPrompt;

        Assert.True(token.IsCancellationRequested);
        Assert.Null(_h.Store.LoadMainSetup());
    }

    [Fact]
    public async Task Account_switch_while_prompt_open_does_not_adopt_the_other_account()
    {
        var answers = new Queue<TaskCompletionSource<bool>>();
        _h.Engine.FirstRunPrompt = (_, _) => { var t = new TaskCompletionSource<bool>(); answers.Enqueue(t); return t.Task; };
        var gw = new FakeLeagueGateway(Main, MainState());
        await _h.Engine.OnConnectedAsync(gw, default);
        var firstPrompt = _h.Engine.PendingPrompt;

        gw.SwitchAccount(Alt, AltState());
        await _h.Engine.HandleLcuEventAsync(Event("/lol-summoner/v1/current-summoner", Obj("""{"puuid":"puuid-alt"}""")), default);
        answers.Dequeue().SetResult(true);
        await firstPrompt;

        Assert.Null(_h.Store.LoadMainSetup());
    }

    // Important 7
    [Fact]
    public async Task Disconnect_during_push_does_not_report_synced()
    {
        _h.SeedMainSetup();
        var gw = new FakeLeagueGateway(Alt, AltState()) { GamePatchGate = new TaskCompletionSource() };

        var connect = _h.Engine.OnConnectedAsync(gw, default);
        await _h.Engine.OnDisconnectedAsync();
        await connect;

        Assert.Equal(SyncState.WaitingForClient, _h.Engine.Status.State);
        Assert.Empty(_h.Notifications);
    }

    // Important 9
    [Fact]
    public async Task Push_waits_for_league_to_load_settings()
    {
        _h.SeedMainSetup();
        var gw = new FakeLeagueGateway(Alt, AltState()) { SettingsReady = false };

        await _h.Engine.OnConnectedAsync(gw, default);
        Assert.Empty(gw.Calls);

        gw.SettingsReady = true;
        await _h.Engine.HandleLcuEventAsync(Event("/lol-game-settings/v1/ready", JsonValue.Create(true)), default);

        Assert.Contains(gw.Calls, c => c.StartsWith("PATCH game"));
    }

    [Fact]
    public async Task Settings_league_did_not_keep_are_not_captured_and_warn()
    {
        _h.SeedMainSetup();
        var gw = new FakeLeagueGateway(Alt, AltState());
        gw.IgnoredWrites.Add("game");
        await _h.Engine.OnConnectedAsync(gw, default);
        _h.Time.Advance(TimeSpan.FromSeconds(6));

        await _h.Engine.HandleLcuEventAsync(Event(GameUri, (JsonObject)gw.State.Game.DeepClone()), default);
        await FlushAsync();

        Assert.Equal(0.5, MainScale());
        Assert.Contains(_h.Engine.Status.Warnings, w => w.Contains("didn't keep"));
    }
}
