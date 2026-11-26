using System.Text.Json.Nodes;
using LolConfigSync.Core.Lcu;

namespace LolConfigSync.Core.Tests.Fakes;

/// <summary>Canned GET responses (404 when absent) and a record of every write.</summary>
public sealed class FakeLcuClient : ILcuClient
{
    public Dictionary<string, string> Responses { get; } = [];
    public Dictionary<string, byte[]> Binary { get; } = [];

    public Task<byte[]> GetBytesAsync(string path, CancellationToken ct = default) =>
        Binary.TryGetValue(path, out var bytes) ? Task.FromResult(bytes) : throw new LcuException(404, path);
    public List<(HttpMethod Method, string Path, string? Body)> Sent { get; } = [];

    public Task<JsonNode?> GetAsync(string path, CancellationToken ct = default) =>
        Responses.TryGetValue(path, out var json)
            ? Task.FromResult(JsonNode.Parse(json))
            : throw new LcuException(404, path);

    public Task SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct = default)
    {
        Sent.Add((method, path, body?.ToJsonString()));
        return Task.CompletedTask;
    }
}
