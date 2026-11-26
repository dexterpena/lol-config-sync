using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using System.Text.Json.Nodes;

namespace LolConfigSync.Core.Lcu;

/// <summary><see cref="ILcuClient"/> over HTTPS to <c>127.0.0.1:{port}</c> with the lockfile's basic-auth password.</summary>
public sealed class LcuHttpClient : ILcuClient, IDisposable
{
    private readonly HttpClient _http;

    public LcuHttpClient(LockfileInfo lockfile, HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? CreateLoopbackHandler(), disposeHandler: true)
        {
            BaseAddress = new Uri($"https://127.0.0.1:{lockfile.Port}"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.Authorization = BasicAuth(lockfile);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public static AuthenticationHeaderValue BasicAuth(LockfileInfo lockfile) =>
        new("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"riot:{lockfile.Password}")));

    public async Task<JsonNode?> GetAsync(string path, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        return await ReadAsync(response, path, ct).ConfigureAwait(false);
    }

    public async Task SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await ReadAsync(response, path, ct).ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();

    private static async Task<JsonNode?> ReadAsync(HttpResponseMessage response, string path, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new LcuException((int)response.StatusCode, path, text);
        }
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    /// <summary>The League client uses a self-signed certificate; accept it only for loopback.</summary>
    private static SocketsHttpHandler CreateLoopbackHandler() => new()
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (sender, _, _, errors) =>
                errors == SslPolicyErrors.None || IsLoopbackRequest(sender),
        },
    };

    private static bool IsLoopbackRequest(object? sender) =>
        sender is SslStream { TargetHostName: "127.0.0.1" };
}
