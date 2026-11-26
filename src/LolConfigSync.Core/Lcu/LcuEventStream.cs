using System.Net.WebSockets;
using System.Text;

namespace LolConfigSync.Core.Lcu;

/// <summary>Subscribes to all League client JSON API events over its local WebSocket.</summary>
public sealed class LcuEventStream(LockfileInfo lockfile) : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;

    public event Action<LcuEvent>? Received;
    public event Action? Closed;

    public async Task ConnectAsync(CancellationToken ct)
    {
        _socket.Options.SetRequestHeader("Authorization", LcuHttpClient.BasicAuth(lockfile).ToString());
        _socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true; // URI below is always loopback
        _socket.Options.AddSubProtocol("wamp");
        await _socket.ConnectAsync(new Uri($"wss://127.0.0.1:{lockfile.Port}/"), ct).ConfigureAwait(false);
        await _socket.SendAsync(Encoding.UTF8.GetBytes(WampMessage.SubscribeAll), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;

                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                if (WampMessage.Parse(text) is { } e) Received?.Invoke(e);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            Closed?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
        if (_receiveLoop is not null) await _receiveLoop.ConfigureAwait(false);
        _socket.Dispose();
        _cts.Dispose();
    }
}
