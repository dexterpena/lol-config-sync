using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace LolConfigSync.Core.Updates;

public enum InstallKind
{
    /// <summary>Installed with the setup program (has an uninstaller next to it).</summary>
    Installed,

    /// <summary>Extracted from the portable zip.</summary>
    Portable,
}

public static class InstallKinds
{
    public static InstallKind Detect(string exeDir) =>
        File.Exists(Path.Combine(exeDir, "unins000.exe")) ? InstallKind.Installed : InstallKind.Portable;
}

/// <summary>Downloads a release and replaces the running app with it.</summary>
public sealed class Updater(HttpClient http)
{
    private const string ExeName = "LolConfigSync.exe";

    /// <summary>
    /// Downloads the right package for this copy, verifies it, and starts the replacement. Returns true when
    /// the caller should exit now so the new version can take over.
    /// </summary>
    public async Task<bool> UpdateAsync(ReleaseInfo release, string currentExe, IProgress<string>? progress, CancellationToken ct)
    {
        var exeDir = Path.GetDirectoryName(currentExe)!;
        var work = Directory.CreateTempSubdirectory("lolconfigsync-update-").FullName;
        if (InstallKinds.Detect(exeDir) == InstallKind.Installed)
        {
            if (release.InstallerUrl is null) throw new InvalidOperationException("This release has no installer to download.");
            progress?.Report("Downloading the new version…");
            var setup = await DownloadAsync(release.InstallerUrl, Path.Combine(work, "LolConfigSync-Setup.exe"), release.InstallerSha256, ct)
                .ConfigureAwait(false);

            // Wait a moment so this app has exited (and released its mutex), then install silently and relaunch.
            progress?.Report("Installing…");
            var args = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER /RELAUNCH=1 \"/DIR={exeDir}\"";
            StartAfterDelay($"\"{setup}\" {args}");
            return true;
        }

        if (release.PortableUrl is null) throw new InvalidOperationException("This release has no portable download.");
        progress?.Report("Downloading the new version…");
        var zip = await DownloadAsync(release.PortableUrl, Path.Combine(work, "portable.zip"), release.PortableSha256, ct).ConfigureAwait(false);
        var newExe = Path.Combine(work, ExeName);
        using (var archive = ZipFile.OpenRead(zip))
        {
            var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals(ExeName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException("The download doesn't contain LolConfigSync.exe.");
            entry.ExtractToFile(newExe, overwrite: true);
        }

        progress?.Report("Restarting…");
        SwapExecutable(currentExe, newExe);
        StartAfterDelay($"\"{currentExe}\"");
        return true;
    }

    /// <summary>True when the file's SHA-256 matches <paramref name="expectedHex"/> (or no hash was published).</summary>
    public static bool MatchesHash(string file, string? expectedHex)
    {
        if (string.IsNullOrWhiteSpace(expectedHex)) return true;
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Windows lets a running exe be renamed but not overwritten: move it aside to <c>.old</c>, then put the
    /// new one in its place. The <c>.old</c> file is removed on the next start.
    /// </summary>
    public static void SwapExecutable(string currentExe, string newExe)
    {
        var old = currentExe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(currentExe, old);
        try
        {
            File.Move(newExe, currentExe);
        }
        catch
        {
            File.Move(old, currentExe); // put the working version back
            throw;
        }
    }

    public static void CleanupAfterUpdate(string currentExe)
    {
        try
        {
            File.Delete(currentExe + ".old");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private async Task<string> DownloadAsync(string url, string path, string? sha256, CancellationToken ct)
    {
        using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(path);
            await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
        }
        if (!MatchesHash(path, sha256))
        {
            File.Delete(path);
            throw new InvalidDataException("The download didn't match the published checksum, so it wasn't installed.");
        }
        return path;
    }

    /// <summary>Runs a command a couple of seconds from now, hidden, after this process has had time to exit.</summary>
    private static void StartAfterDelay(string command) =>
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & start \"\" {command}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
}
