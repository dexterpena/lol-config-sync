using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LolConfigSync.Core.Updates;

/// <summary>A published release on GitHub.</summary>
public sealed record ReleaseInfo(
    Version Version,
    string Notes,
    string PageUrl,
    string? InstallerUrl,
    string? InstallerSha256,
    string? PortableUrl,
    string? PortableSha256);

/// <summary>Where releases are published.</summary>
public static class UpdateSource
{
    public const string Owner = "dexterpena";
    public const string Repo = "lol-config-manager";
    public static string ReleasesPage => $"https://github.com/{Owner}/{Repo}/releases";
}

/// <summary>Asks GitHub for the latest release of the app.</summary>
public sealed class UpdateChecker(HttpClient http, string owner, string repo)
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);
    private const string UserAgent = "LolConfigSync-Updater";

    /// <summary>The latest release, or null when nothing has been published yet.</summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repo}/releases/latest");
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject;
        var version = ParseTag(Text(json, "tag_name"));
        if (json is null || version is null) return null;

        var assets = (json["assets"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var installer = assets.FirstOrDefault(a => Text(a, "name").EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                                   && Text(a, "name").Contains("Setup", StringComparison.OrdinalIgnoreCase));
        var portable = assets.FirstOrDefault(a => Text(a, "name").EndsWith("-portable.zip", StringComparison.OrdinalIgnoreCase));
        return new ReleaseInfo(version, Text(json, "body"), Text(json, "html_url"),
            Url(installer), Sha256(installer), Url(portable), Sha256(portable));
    }

    public static bool IsNewer(Version current, Version latest) => Normalize(latest) > Normalize(current);

    /// <summary><c>v1.2.3</c> or <c>1.2.3</c> → 1.2.3; anything else → null.</summary>
    public static Version? ParseTag(string tag) =>
        Version.TryParse(tag.TrimStart('v', 'V'), out var v) ? v : null;

    public static bool IsCheckDue(DateTimeOffset? lastCheck, DateTimeOffset now) =>
        lastCheck is null || now - lastCheck >= CheckInterval;

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    private static string? Url(JsonObject? asset) => asset is null ? null : Text(asset, "browser_download_url");

    /// <summary>GitHub publishes <c>"digest": "sha256:&lt;hex&gt;"</c> for release assets.</summary>
    private static string? Sha256(JsonObject? asset)
    {
        var digest = asset is null ? "" : Text(asset, "digest");
        return digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest["sha256:".Length..] : null;
    }

    private static string Text(JsonObject? o, string key) =>
        o?[key]?.GetValueKind() == JsonValueKind.String ? o[key]!.GetValue<string>() : "";
}
