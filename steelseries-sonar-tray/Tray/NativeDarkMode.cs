using System.Runtime.InteropServices;

namespace SonarQuickMixer.Tray;

/// <summary>
/// Opts the process into the Windows dark theme for system-drawn UI such as
/// native popup menus and tooltips. WPF paints its own controls, so the app
/// windows are unaffected.
/// The uxtheme entry points below are undocumented, but they have been stable
/// since Windows 10 1903 and are still used by Windows 11 applications.
/// </summary>
internal static class NativeDarkMode
{
    private const int SetPreferredAppModeOrdinal = 135;
    private const int FlushMenuThemesOrdinal = 136;
    private const int PreferredAppModeForceDark = 2;

    private static bool _enabled;

    /// <summary>Must be called before the first window is created.</summary>
    public static void Enable()
    {
        if (_enabled || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
        {
            return;
        }

        _enabled = true;

        var uxtheme = LoadLibraryW("uxtheme.dll");
        if (uxtheme == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (TryGetExport<SetPreferredAppModeDelegate>(uxtheme, SetPreferredAppModeOrdinal, out var setPreferredAppMode))
            {
                _ = setPreferredAppMode(PreferredAppModeForceDark);
            }

            if (TryGetExport<FlushMenuThemesDelegate>(uxtheme, FlushMenuThemesOrdinal, out var flushMenuThemes))
            {
                _ = flushMenuThemes();
            }
        }
        finally
        {
            _ = FreeLibrary(uxtheme);
        }
    }

    private static bool TryGetExport<TDelegate>(IntPtr module, int ordinal, out TDelegate result)
        where TDelegate : Delegate
    {
        var address = GetProcAddress(module, ordinal);
        if (address == IntPtr.Zero)
        {
            result = default!;
            return false;
        }

        result = Marshal.GetDelegateForFunctionPointer<TDelegate>(address);
        return true;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int SetPreferredAppModeDelegate(int mode);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int FlushMenuThemesDelegate();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string fileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, int ordinal);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr module);
}
