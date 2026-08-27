using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SonarQuickMixer.Settings;

namespace SonarQuickMixer.Tray;

internal static class TrayIconProvider
{
    private const string AccentResource = "tray-accent.ico";
    private const string WhiteResource = "tray-white.ico";
    private const string DarkResource = "tray-dark.ico";

    // Matches FluentDark AccentColor / SurfaceColor used by UpdateNotificationDot.
    private static readonly Color UpdateBadgeFill = Color.FromArgb(0x60, 0xCD, 0xFF);
    private static readonly Color UpdateBadgeBorder = Color.FromArgb(0x20, 0x20, 0x20);

    public static Icon Load(TrayIconStyle style, bool showUpdateBadge = false)
    {
        var baseIcon = LoadBaseIcon(style);
        if (!showUpdateBadge)
        {
            return baseIcon;
        }

        try
        {
            return WithUpdateBadge(baseIcon);
        }
        finally
        {
            baseIcon.Dispose();
        }
    }

    private static Icon LoadBaseIcon(TrayIconStyle style)
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

    private static Icon WithUpdateBadge(Icon source)
    {
        var size = Math.Max(source.Width, 16);
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            graphics.DrawIcon(source, new Rectangle(0, 0, size, size));

            // ~1/3 of icon, top-right — same relative placement as UpdateNotificationDot.
            var diameter = Math.Max(5, (int)Math.Round(size * 0.34));
            var x = size - diameter - 1;
            const int y = 1;

            using var fill = new SolidBrush(UpdateBadgeFill);
            using var border = new Pen(UpdateBadgeBorder, Math.Max(1f, size / 16f));
            graphics.FillEllipse(fill, x, y, diameter, diameter);
            graphics.DrawEllipse(border, x, y, diameter, diameter);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
