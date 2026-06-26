using System.Drawing;
using System.Reflection;
using Microsoft.Win32;

namespace SteelSeries.SonarTray;

internal static class TrayIconProvider
{
    private const string AccentResource = "tray-accent.ico";
    private const string WhiteResource = "tray-white.ico";
    private const string DarkResource = "tray-dark.ico";

    public static Icon Load(TrayIconStyle style)
    {
        var resolved = style == TrayIconStyle.Auto ? ResolveAutoStyle() : style;
        var resourceName = resolved switch
        {
            TrayIconStyle.White => WhiteResource,
            TrayIconStyle.Dark => DarkResource,
            _ => AccentResource
        };

        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is not null)
        {
            return new Icon(stream);
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    private static TrayIconStyle ResolveAutoStyle()
    {
        return IsWindowsLightTheme() ? TrayIconStyle.Dark : TrayIconStyle.Accent;
    }

    private static bool IsWindowsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int lightTheme && lightTheme == 1;
        }
        catch
        {
            return false;
        }
    }
}
