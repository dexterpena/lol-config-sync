using System.Text.Json.Nodes;
using LolConfigSync.Core.Model;
using LolConfigSync.Core.Sync;
using LolConfigSync.Core.Tests.Fakes;
using static LolConfigSync.Core.Tests.Fakes.EngineHarness;

namespace LolConfigSync.Core.Tests.Sync;

public sealed class AccountOverridesTests : IDisposable
{
    private const string GameUri = "/lol-game-settings/v1/game-settings";
    private readonly EngineHarness _h = new();

    public void Dispose() => _h.Dispose();

    private void SetAltOverride(string key, JsonNode value)
    {
        var all = _h.Store.LoadOverrides();
        all.Set(Alt.Puuid, Alt.RiotId, SettingKey.Parse(key), value);
        _h.Store.SaveOverrides(all);
    }

    private async Task FlushAsync()
    {
        _h.Time.Advance(TimeSpan.FromSeconds(4));
        await _h.Engine.PendingFlush;
    }

    [Fact]
    public void Overrides_apply_on_top_of_settings_for_their_kind_only()
    {
        var all = new AccountOverrides();
        all.Set("p", "Alt#EUW", SettingKey.Parse("game:HUD.GlobalScale"), 0.7);
        all.Set("p", "Alt#EUW", SettingKey.Parse("input:GameEvents.evtCastSpell1"), "[z]");
        var game = Obj("""{"HUD":{"GlobalScale":0.5,"ChatScale":50}}""");

        var result = all.ApplyTo(game, SettingsKind.Game, "p");

        Assert.Equal("""{"HUD":{"GlobalScale":0.7,"ChatScale":50}}""", result.ToJsonString());
        Assert.Equal(0.5, game["HUD"]!["GlobalScale"]!.GetValue<double>()); // input untouched
    }

    [Fact]
    public void Overrides_round_trip_through_the_store()
    {
        SetAltOverride("game:HUD.GlobalScale", 0.7);

        var loaded = _h.Store.LoadOverrides();

        Assert.Equal(0.7, loaded.Get(Alt.Puuid, SettingKey.Parse("game:HUD.GlobalScale"))!.GetValue<double>());
        Assert.Equal("Alt#EUW", loaded.Accounts[Alt.Puuid].RiotId);
    }

    [Fact]
    public async Task Push_applies_an_accounts_own_values_only_to_that_account()
    {
        _h.SeedMainSetup();
        SetAltOverride("game:HUD.GlobalScale", 0.7);
        var alt = new FakeLeagueGateway(Alt, AltState());
        var main = new FakeLeagueGateway(Main, AltState());

        await _h.Engine.OnConnectedAsync(alt, default);
        await _h.Engine.OnConnectedAsync(main, default);

        Assert.Equal(0.7, alt.State.Game["HUD"]!["GlobalScale"]!.GetValue<double>());
        Assert.Equal(0.5, main.State.Game["HUD"]!["GlobalScale"]!.GetValue<double>());
    }

    [Fact]
    public async Task Own_value_is_pushed_even_when_the_setting_is_otherwise_not_synced()
    {
        _h.SeedMainSetup();
        _h.SetSettings(s => s.ExcludedKeys.Add("game:HUD.GlobalScale"));
        SetAltOverride("game:HUD.GlobalScale", 0.7);
        var alt = new FakeLeagueGateway(Alt, AltState());

        await _h.Engine.OnConnectedAsync(alt, default);

        Assert.Equal(0.7, alt.State.Game["HUD"]!["GlobalScale"]!.GetValue<double>());
    }

    [Fact]
    public async Task Changing_an_accounts_own_setting_in_game_updates_that_account_not_main()
    {
        _h.SeedMainSetup();
        SetAltOverride("game:HUD.GlobalScale", 0.7);
        await _h.Engine.OnConnectedAsync(new FakeLeagueGateway(Alt, AltState()), default);
        _h.Time.Advance(TimeSpan.FromSeconds(6));

        await _h.Engine.HandleLcuEventAsync(Event(GameUri, Obj("""{"HUD":{"GlobalScale":0.8},"General":{"CursorScale":3}}""")), default);
        await FlushAsync();

        var main = _h.Store.LoadMainSetup()!;
        Assert.Equal(0.5, main.GameSettings["HUD"]!["GlobalScale"]!.GetValue<double>());
        Assert.Equal(3, main.GameSettings["General"]!["CursorScale"]!.GetValue<int>());
        Assert.Equal(0.8, _h.Store.LoadOverrides().Get(Alt.Puuid, SettingKey.Parse("game:HUD.GlobalScale"))!.GetValue<double>());
    }

    [Fact]
    public async Task Editor_save_stores_main_and_account_values_together_and_applies_once()
    {
        _h.SeedMainSetup();
        var alt = new FakeLeagueGateway(Alt, AltState());
        await _h.Engine.OnConnectedAsync(alt, default);
        alt.Calls.Clear();
        var edited = _h.Store.LoadMainSetup()!;
        edited.GameSettings["General"]!["CursorScale"] = 4;
        var overrides = new AccountOverrides();
        overrides.Set(Alt.Puuid, Alt.RiotId, SettingKey.Parse("game:HUD.GlobalScale"), 0.66);

        await _h.Engine.SaveEditedSetupAsync(edited, _h.Store.LoadSettings(), default, overrides);

        Assert.Single(alt.Calls, c => c.StartsWith("PATCH game"));
        Assert.Equal(0.66, alt.State.Game["HUD"]!["GlobalScale"]!.GetValue<double>());
        Assert.Equal(4, alt.State.Game["General"]!["CursorScale"]!.GetValue<int>());
        Assert.True(_h.Store.LoadOverrides().Has(Alt.Puuid, SettingKey.Parse("game:HUD.GlobalScale")));
    }

    [Fact]
    public async Task Saving_own_values_for_the_logged_in_account_applies_them_right_away()
    {
        _h.SeedMainSetup();
        var alt = new FakeLeagueGateway(Alt, AltState());
        await _h.Engine.OnConnectedAsync(alt, default);
        var overrides = _h.Store.LoadOverrides();
        overrides.Set(Alt.Puuid, Alt.RiotId, SettingKey.Parse("game:HUD.GlobalScale"), 0.66);

        await _h.Engine.SaveAccountOverridesAsync(overrides, default);

        Assert.Equal(0.66, alt.State.Game["HUD"]!["GlobalScale"]!.GetValue<double>());
        Assert.Equal(0.66, _h.Store.LoadOverrides().Get(Alt.Puuid, SettingKey.Parse("game:HUD.GlobalScale"))!.GetValue<double>());
    }
}
