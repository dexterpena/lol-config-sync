using System.Net;
using System.Security.Cryptography;
using LolConfigSync.Core.Updates;

namespace LolConfigSync.Core.Tests.Updates;

public sealed class UpdateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lcs-update-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private const string ReleaseJson = """
        {"tag_name":"v1.2.0","name":"1.2.0","body":"- Faster sync\n- Porofessor fix","html_url":"https://github.com/o/r/releases/tag/v1.2.0",
         "assets":[
           {"name":"LolConfigSync-Setup-1.2.0.exe","browser_download_url":"https://example/setup.exe","digest":"sha256:abc"},
           {"name":"LolConfigSync-1.2.0-portable.zip","browser_download_url":"https://example/portable.zip"}]}
        """;

    [Fact]
    public async Task Latest_release_is_parsed()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ReleaseJson);
        var checker = new UpdateChecker(new HttpClient(handler), "o", "r");

        var release = await checker.GetLatestAsync(default);

        Assert.Equal(new Version(1, 2, 0), release!.Version);
        Assert.Contains("Faster sync", release.Notes);
        Assert.Equal("https://example/setup.exe", release.InstallerUrl);
        Assert.Equal("abc", release.InstallerSha256);
        Assert.Equal("https://example/portable.zip", release.PortableUrl);
        Assert.Null(release.PortableSha256);
        Assert.Equal("https://api.github.com/repos/o/r/releases/latest", handler.Request!.RequestUri!.ToString());
        Assert.Equal("LolConfigSync-Updater", handler.Request.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task No_releases_yet_returns_null()
    {
        var checker = new UpdateChecker(new HttpClient(new StubHandler(HttpStatusCode.NotFound, "{}")), "o", "r");

        Assert.Null(await checker.GetLatestAsync(default));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.2.0", "1.1.9", false)]
    [InlineData("1.0.0.0", "1.0.0", false)]
    public void Newer_versions_are_detected(string current, string latest, bool expected) =>
        Assert.Equal(expected, UpdateChecker.IsNewer(Version.Parse(current), Version.Parse(latest)));

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v2.0", "2.0")]
    public void Tags_parse_to_versions(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateChecker.ParseTag(tag));

    [Fact]
    public void Installed_copy_is_detected_by_its_uninstaller()
    {
        Assert.Equal(InstallKind.Portable, InstallKinds.Detect(_dir));
        File.WriteAllText(Path.Combine(_dir, "unins000.exe"), "");
        Assert.Equal(InstallKind.Installed, InstallKinds.Detect(_dir));
    }

    [Fact]
    public void Downloads_are_verified_against_the_published_hash()
    {
        var file = Path.Combine(_dir, "a.bin");
        File.WriteAllBytes(file, [1, 2, 3]);
        var hash = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3 }));

        Assert.True(Updater.MatchesHash(file, hash));
        Assert.True(Updater.MatchesHash(file, null)); // no published hash: nothing to compare
        Assert.False(Updater.MatchesHash(file, "deadbeef"));
    }

    [Fact]
    public void Portable_swap_replaces_the_running_exe_and_keeps_the_old_one_aside()
    {
        var current = Path.Combine(_dir, "LolConfigSync.exe");
        var downloaded = Path.Combine(_dir, "new.exe");
        File.WriteAllText(current, "old");
        File.WriteAllText(downloaded, "new");

        Updater.SwapExecutable(current, downloaded);

        Assert.Equal("new", File.ReadAllText(current));
        Assert.Equal("old", File.ReadAllText(current + ".old"));
        Assert.False(File.Exists(downloaded));
    }

    [Fact]
    public void Leftover_old_exe_is_cleaned_up_on_next_start()
    {
        var old = Path.Combine(_dir, "LolConfigSync.exe.old");
        File.WriteAllText(old, "old");

        Updater.CleanupAfterUpdate(Path.Combine(_dir, "LolConfigSync.exe"));

        Assert.False(File.Exists(old));
    }

    [Fact]
    public void Daily_check_is_due_after_a_day()
    {
        var now = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

        Assert.True(UpdateChecker.IsCheckDue(null, now));
        Assert.False(UpdateChecker.IsCheckDue(now.AddHours(-3), now));
        Assert.True(UpdateChecker.IsCheckDue(now.AddHours(-25), now));
    }
}
