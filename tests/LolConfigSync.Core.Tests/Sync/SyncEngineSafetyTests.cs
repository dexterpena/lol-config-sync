using System.Text.Json.Nodes;
using LolConfigSync.Core.Hosting;
using LolConfigSync.Core.League;
using LolConfigSync.Core.Model;
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

    // Who is logged in
    [Fact]
    public async Task Status_names_the_logged_in_account_even_before_a_main_setup_exists()
    {
        _h.Engine.FirstRunPrompt = (_, _) => Task.FromResult(false);

        await _h.Engine.OnConnectedAsync(new FakeLeagueGateway(Alt, AltState()), default);
        await _h.Engine.PendingPrompt;

        Assert.Equal(Alt, _h.Engine.Status.Account);
    }

    [Fact]
    public async Task Profile_changes_on_the_same_account_update_the_status_without_resyncing()
    {
        _h.SeedMainSetup();
        var gw = new FakeLeagueGateway(Alt, AltState());
        await _h.Engine.OnConnectedAsync(gw, default);
        var calls = gw.Calls.Count;

        await _h.Engine.HandleLcuEventAsync(Event("/lol-summoner/v1/current-summoner",
            Obj("""{"puuid":"puuid-alt","gameName":"Alt","tagLine":"EUW","summonerId":2,"profileIconId":29,"summonerLevel":101}""")), default);

        Assert.Equal(29, _h.Engine.Status.Account!.ProfileIconId);
        Assert.Equal(101, _h.Engine.Status.Account.SummonerLevel);
        Assert.Equal(calls, gw.Calls.Count);
    }

    // Backup before any change
    [Fact]
    public async Task Nothing_is_changed_when_the_backup_cannot_be_saved()
    {
        _h.SeedMainSetup();
        File.WriteAllText(Path.Combine(_h.Store.RootDir, "backups"), "not a folder"); // makes saving a backup fail
        var gw = new FakeLeagueGateway(Alt, AltState());
        _h.Video.Values = Obj("""{"Performance":{"ShadowQuality":"1"}}""");

        await _h.Engine.OnConnectedAsync(gw, default);

        Assert.Empty(gw.Calls);
        Assert.Equal(0, _h.Video.Writes);
        Assert.Equal(SyncState.Error, _h.Engine.Status.State);
        Assert.Contains(_h.Engine.Status.Warnings, w => w.Contains("backup"));
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

    [Fact]
    public async Task Login_event_cancels_previous_push_before_the_queue_is_drained()
    {
        _h.SeedMainSetup();
        var gw = new FakeLeagueGateway(Main, MainState()) { GamePatchGate = new() };
        var session = new LeagueLoginSession();
        var push = _h.Engine.OnConnectedAsync(gw, session, default);
        Assert.False(push.IsCompleted);
        var staleLogin = session.Token;

        gw.SwitchAccount(Alt, AltState());
        var loginEvent = Event(LeagueGateway.CurrentSummoner, Obj("""{"puuid":"puuid-alt"}"""));
        var nextLogin = session.Observe(loginEvent);
        await push.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("[a]", gw.State.Input["GameEvents"]!["evtCastSpell1"]!.GetValue<string>());
        gw.GamePatchGate.SetResult();
        await _h.Engine.HandleObservedEventAsync(loginEvent, session, nextLogin, default);

        var original = _h.Store.ListBackups().Single(b => b.IsOriginal && b.Puuid == Alt.Puuid);
        Assert.Equal("[a]", _h.Store.LoadBackup(original.Path).InputSettings["GameEvents"]!["evtCastSpell1"]!.GetValue<string>());
        Assert.Equal("[q]", gw.State.Input["GameEvents"]!["evtCastSpell1"]!.GetValue<string>());

        _h.Time.Advance(TimeSpan.FromSeconds(6));
        var stale = Event(GameUri, Obj("""{"HUD":{"GlobalScale":0.99}}"""));
        await _h.Engine.HandleObservedEventAsync(stale, session, staleLogin, default);
        await FlushAsync();
        Assert.Equal(0.5, MainScale());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Undo_before_capture_keeps_latest_hotkey_and_other_changes(bool accountSpecific)
    {
        _h.SeedMainSetup();
        var key = SettingKey.Parse("input:GameEvents.evtCastSpell1");
        var saved = accountSpecific ? "[z]" : "[q]";
        if (accountSpecific)
        {
            var overrides = _h.Store.LoadOverrides();
            overrides.Set(Alt.Puuid, Alt.RiotId, key, saved);
            _h.Store.SaveOverrides(overrides);
        }
        await _h.Engine.OnConnectedAsync(new FakeLeagueGateway(Alt, AltState()), default);
        _h.Time.Advance(TimeSpan.FromSeconds(6));
        await _h.Engine.HandleLcuEventAsync(Event(InputUri, Obj("""{"GameEvents":{"evtCastSpell1":"[w]","other":"[e]"}}""")), default);
        await _h.Engine.HandleLcuEventAsync(Event(InputUri, new JsonObject { ["GameEvents"] = new JsonObject { ["evtCastSpell1"] = saved } }), default);
        await FlushAsync();

        var input = _h.Store.LoadMainSetup()!.InputSettings["GameEvents"]!;
        Assert.Equal("[q]", input["evtCastSpell1"]!.GetValue<string>());
        Assert.Equal("[e]", input["other"]!.GetValue<string>());
        if (accountSpecific) Assert.Equal(saved, _h.Store.LoadOverrides().Get(Alt.Puuid, key)!.GetValue<string>());
    }

    [Fact]
    public async Task Original_recovers_only_the_previously_unreadable_parts()
    {
        _h.SeedMainSetup();
        var gw = new FakeLeagueGateway(Alt, AltState());
        gw.FailingReads.Add("input");
        await _h.Engine.OnConnectedAsync(gw, default);
        var original = _h.Store.ListBackups().Single(b => b.IsOriginal);
        Assert.Contains(SnapshotPart.Input, _h.Store.LoadBackup(original.Path).UnreadableParts!);

        gw.FailingReads.Clear();
        _h.Time.Advance(TimeSpan.FromSeconds(6));
        await _h.Engine.PushMainSetupAsync(default);
        Assert.Equal("[q]", gw.State.Input["GameEvents"]!["evtCastSpell1"]!.GetValue<string>());
        var result = await _h.Engine.RestoreBackupAsync(original.Path, default);

        Assert.Empty(result.Warnings);
        Assert.Equal("[a]", gw.State.Input["GameEvents"]!["evtCastSpell1"]!.GetValue<string>());
        Assert.Equal(0.9, gw.State.Game["HUD"]!["GlobalScale"]!.GetValue<double>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Restore_uses_all_slots_including_tool_pages_and_restores_empty_lists(bool hadPages)
    {
        _h.SeedMainSetup();
        var state = AltState();
        state.Capacity = 2;
        state.Pages = hadPages ? [Page(1, "Original"), Page(2, "Porofessor: Ahri")] : [];
        if (hadPages) state.Pages[0]!["current"] = true;
        state.ItemSets.Clear();
        await _h.Engine.OnConnectedAsync(new FakeLeagueGateway(Alt, state), default);
        Assert.Single(state.ItemSets);
        Assert.Single(state.Pages);
        var original = _h.Store.ListBackups().Single(b => b.IsOriginal);

        var result = await _h.Engine.RestoreBackupAsync(original.Path, default);

        Assert.Empty(result.Warnings);
        Assert.Equal(hadPages ? new[] { "Original", "Porofessor: Ahri" } : [], state.Pages.Select(p => p!["name"]!.GetValue<string>()));
        if (hadPages) Assert.True(state.Pages[0]!["current"]!.GetValue<bool>());
        Assert.Empty(state.ItemSets);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Restore_leaves_collections_alone_when_the_backup_cannot_confirm_they_were_empty(bool legacy)
    {
        _h.SeedMainSetup();
        var gw = new FakeLeagueGateway(Alt, AltState());
        gw.FailingReads.UnionWith(["itemsets", "runes"]);
        await _h.Engine.OnConnectedAsync(gw, default);
        var path = _h.Store.ListBackups().Single(b => !b.IsOriginal).Path;
        if (legacy)
        {
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            json.Remove("UnreadableParts");
            File.WriteAllText(path, json.ToJsonString());
        }
        gw.FailingReads.Clear();
        _h.Time.Advance(TimeSpan.FromSeconds(6));
        var result = await _h.Engine.RestoreBackupAsync(path, default);

        Assert.Contains(result.Warnings, warning => warning.Contains("Rune pages"));
        Assert.Contains(result.Warnings, warning => warning.Contains("Item sets"));
        Assert.Equal("AltPage", Assert.Single(gw.State.Pages)!["name"]!.GetValue<string>());
        Assert.Equal("AltSet", Assert.Single(gw.State.ItemSets)!["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Restore_keeps_current_pages_if_the_complete_backup_no_longer_fits()
    {
        _h.SeedMainSetup();
        var state = AltState();
        state.Pages.Add(Page(8, "Another original"));
        await _h.Engine.OnConnectedAsync(new FakeLeagueGateway(Alt, state), default);
        var original = _h.Store.ListBackups().Single(b => b.IsOriginal);
        state.Capacity = 1;

        var result = await _h.Engine.RestoreBackupAsync(original.Path, default);

        Assert.Contains(result.Warnings, warning => warning.Contains("more rune pages"));
        Assert.Equal("MainPage", Assert.Single(state.Pages)!["name"]!.GetValue<string>());
    }
}
