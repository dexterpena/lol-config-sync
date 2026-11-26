using System.Net;
using System.Text.Json.Nodes;
using LolConfigSync.Core.Lcu;

namespace LolConfigSync.Core.Tests.Lcu;

public class LcuHttpClientTests
{
    private sealed class StubHandler(HttpStatusCode status, string body = "null") : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private static readonly LockfileInfo Lockfile = new(1234, 54321, "abcPW", "https");

    [Fact]
    public async Task Sends_basic_auth_to_loopback_port()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"a":1}""");
        using var client = new LcuHttpClient(Lockfile, handler);

        var result = await client.GetAsync("/lol-x");

        Assert.Equal("https://127.0.0.1:54321/lol-x", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Basic cmlvdDphYmNQVw==", handler.Request.Headers.Authorization!.ToString());
        Assert.Equal(1, result!["a"]!.GetValue<int>());
    }

    [Fact]
    public async Task Non_success_throws_LcuException_with_status()
    {
        using var client = new LcuHttpClient(Lockfile, new StubHandler(HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<LcuException>(() => client.GetAsync("/lol-missing"));

        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("/lol-missing", ex.Path);
    }

    [Fact]
    public async Task Patch_sends_json_body()
    {
        var handler = new StubHandler(HttpStatusCode.NoContent, "");
        using var client = new LcuHttpClient(Lockfile, handler);

        await client.SendAsync(HttpMethod.Patch, "/lol-game-settings/v1/game-settings", new JsonObject { ["HUD"] = new JsonObject { ["GlobalScale"] = 0.5 } });

        Assert.Equal(HttpMethod.Patch, handler.Request!.Method);
        Assert.Equal("""{"HUD":{"GlobalScale":0.5}}""", handler.RequestBody);
        Assert.Equal("application/json", handler.Request.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Get_bytes_returns_raw_body()
    {
        using var client = new LcuHttpClient(Lockfile, new StubHandler(HttpStatusCode.OK, "abc"));

        Assert.Equal("abc"u8.ToArray(), await client.GetBytesAsync("/img.jpg"));
    }

    [Fact]
    public async Task Empty_success_body_returns_null()
    {
        using var client = new LcuHttpClient(Lockfile, new StubHandler(HttpStatusCode.NoContent, ""));

        Assert.Null(await client.GetAsync("/lol-empty"));
    }
}
