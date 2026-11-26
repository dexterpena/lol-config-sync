using System.Diagnostics.CodeAnalysis;

namespace LolConfigSync.Core.Lcu;

/// <summary>Connection details the League client writes to its <c>lockfile</c>: <c>name:pid:port:password:protocol</c>.</summary>
public sealed record LockfileInfo(int Pid, int Port, string Password, string Protocol)
{
    public static bool TryParse(string content, [NotNullWhen(true)] out LockfileInfo? info)
    {
        info = null;
        var parts = content.Trim().Split(':');
        if (parts.Length != 5) return false;
        if (!int.TryParse(parts[1], out var pid) || !int.TryParse(parts[2], out var port)) return false;
        if (port is <= 0 or > 65535 || parts[3].Length == 0) return false;
        info = new LockfileInfo(pid, port, parts[3], parts[4]);
        return true;
    }
}
