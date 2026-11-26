using System.Text.Json;
using System.Text.Json.Nodes;
using LolConfigSync.Core.Lcu;
using LolConfigSync.Core.League;

namespace LolConfigSync.Core.Hosting;

/// <summary>Tracks login changes at WebSocket receipt, even while the event consumer is busy syncing.</summary>
internal sealed class LeagueLoginSession
{
    private readonly object _gate = new();
    private CancellationTokenSource? _login = new();
    private string? _puuid;
    private bool _known;

    public CancellationToken Token
    {
        get { lock (_gate) return _login?.Token ?? new CancellationToken(true); }
    }

    public CancellationToken Observe(LcuEvent e)
    {
        if (e.Uri != LeagueGateway.CurrentSummoner) return Token;
        var puuid = SummonerPuuid(e);
        CancellationTokenSource previous;
        CancellationToken next;
        lock (_gate)
        {
            if (_login is null) return new CancellationToken(true);
            if (_known && _puuid == puuid) return _login.Token;
            previous = _login;
            _login = new();
            next = _login.Token;
            _known = true;
            _puuid = puuid;
        }
        previous.Cancel();
        previous.Dispose();
        return next;
    }

    internal static string? SummonerPuuid(LcuEvent e) =>
        e.EventType != "Delete" && e.Data is JsonObject summoner && summoner["puuid"]?.GetValueKind() == JsonValueKind.String
            ? summoner["puuid"]!.GetValue<string>() : null;

    public bool Confirm(string puuid, CancellationToken generation)
    {
        lock (_gate)
        {
            if (_login is null || _login.Token != generation || (_known && _puuid != puuid)) return false;
            _known = true;
            _puuid = puuid;
            return true;
        }
    }

    public void Close()
    {
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _login;
            _login = null;
        }
        previous?.Cancel();
        previous?.Dispose();
    }
}
