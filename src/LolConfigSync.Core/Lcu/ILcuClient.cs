using System.Text.Json.Nodes;

namespace LolConfigSync.Core.Lcu;

/// <summary>Raw access to the League client's local REST API.</summary>
public interface ILcuClient
{
    /// <summary>GETs <paramref name="path"/>. Throws <see cref="LcuException"/> on a non-2xx response.</summary>
    Task<JsonNode?> GetAsync(string path, CancellationToken ct = default);

    /// <summary>Sends a request with an optional JSON body. Throws <see cref="LcuException"/> on a non-2xx response.</summary>
    Task SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct = default);
}

public sealed class LcuException(int statusCode, string path, string? responseBody = null)
    : Exception($"League client returned {statusCode} for {path}")
{
    public int StatusCode { get; } = statusCode;
    public string Path { get; } = path;
    public string? ResponseBody { get; } = responseBody;
}
