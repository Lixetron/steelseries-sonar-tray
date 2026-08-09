using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SonarQuickMixer.Views;

/// <summary>
/// Asks DWM for a dark title bar / frame on standard (non-custom-chrome) windows.
/// WPF does not pick this up from FluentDark resources alone.
/// </summary>
internal static class WindowDarkMode
{
    // Pre-20H1 builds used 19; 20H1+ use 20.
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;

    public static void TryEnable(Window window)
    {
        void Apply()
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var useDark = 1;
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int)) != 0)
            {
                _ = DwmSetWindowAttribute(
                    hwnd,
                    DwmwaUseImmersiveDarkModeBefore20H1,
                    ref useDark,
                    sizeof(int));
            }
        }

        if (window.IsInitialized && new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            Apply();
            return;
        }

        window.SourceInitialized += (_, _) => Apply();
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attr,
        ref int attrValue,
        int attrSize);
}
