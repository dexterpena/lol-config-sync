using Microsoft.Win32;

namespace LolConfigSync.App.Platform;

/// <summary>Adds or removes the per-user "run at Windows login" entry.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LolConfigSync";

    public static void Apply(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, $"\"{exePath}\" --tray");
        else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName);
    }
}
