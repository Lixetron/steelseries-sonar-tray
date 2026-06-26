using Microsoft.Win32;

namespace SteelSeries.SonarTray;

public static class WindowsStartupRegistration
{
    private const string RegistryRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SteelSeries Sonar Tray";

    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKeyPath, writable: false);
            var value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public static bool TrySetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RegistryRunKeyPath);

            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    return false;
                }

                key.SetValue(ValueName, QuoteIfNeeded(exePath));
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Apply(bool enabled)
    {
        if (enabled)
        {
            TrySetEnabled(true);
            return;
        }

        if (IsRegistered())
        {
            TrySetEnabled(false);
        }
    }

    private static string QuoteIfNeeded(string path) =>
        path.Contains(' ') ? $"\"{path}\"" : path;
}
