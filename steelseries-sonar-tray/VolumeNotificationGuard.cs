using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WinForms = System.Windows.Forms;

namespace SonarQuickMixer;

internal static class VolumeNotificationGuard
{
    private const int GwlExstyle = -20;
    private const int WsExNoactivate = 0x08000000;
    private const uint MonitorDefaultToNearest = 2;
    private const int FullscreenTolerancePx = 10;

    private enum UserNotificationState
    {
        QunsNotPresent = 1,
        QunsBusy = 2,
        QunsRunningD3DFullScreen = 3,
        QunsPresentationMode = 4,
        QunsAcceptsNotifications = 5,
        QunsQuietTime = 6
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int CbSize;
        public Rect RcMonitor;
        public Rect RcWork;
        public int DwFlags;
    }

    public static bool ShouldShowVolumeOverlay()
    {
        if (SHQueryUserNotificationState(out var notificationState) == 0)
        {
            if (notificationState is UserNotificationState.QunsRunningD3DFullScreen
                or UserNotificationState.QunsPresentationMode)
            {
                return false;
            }
        }

        return !IsForegroundWindowFullscreen();
    }

    public static void ApplyNoActivateStyle(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0)
        {
            return;
        }

        var exStyle = GetWindowLong(hwnd, GwlExstyle);
        SetWindowLong(hwnd, GwlExstyle, exStyle | WsExNoactivate);
    }

    public static (double Left, double Top) GetTopCenterPosition(Window window)
    {
        window.UpdateLayout();

        var width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        var height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
        if (width <= 0)
        {
            width = 248;
        }

        if (height <= 0)
        {
            height = 48;
        }

        var screen = ResolveTargetScreen();
        var scale = GetScaleForScreen(screen);
        var bounds = screen.Bounds;

        var left = bounds.Left / scale + ((bounds.Width / scale) - width) / 2;
        var top = bounds.Top / scale + 16;
        return (left, top);
    }

    public static void PlaceAtTopCenter(Window window)
    {
        var (left, top) = GetTopCenterPosition(window);
        window.Left = left;
        window.Top = top;
    }

    private static bool IsForegroundWindowFullscreen()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0 || hwnd == GetShellWindow())
        {
            return false;
        }

        if (IsOwnedByCurrentProcess(hwnd))
        {
            return false;
        }

        if (!GetWindowRect(hwnd, out var windowRect))
        {
            return false;
        }

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == 0)
        {
            return false;
        }

        var monitorInfo = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return false;
        }

        var monitorRect = monitorInfo.RcMonitor;
        return windowRect.Left <= monitorRect.Left + FullscreenTolerancePx
            && windowRect.Top <= monitorRect.Top + FullscreenTolerancePx
            && windowRect.Right >= monitorRect.Right - FullscreenTolerancePx
            && windowRect.Bottom >= monitorRect.Bottom - FullscreenTolerancePx
            && windowRect.Width >= monitorRect.Width - FullscreenTolerancePx * 2
            && windowRect.Height >= monitorRect.Height - FullscreenTolerancePx * 2;
    }

    private static bool IsOwnedByCurrentProcess(nint hwnd)
    {
        _ = GetWindowThreadProcessId(hwnd, out var processId);
        return processId == Environment.ProcessId;
    }

    private static WinForms.Screen ResolveTargetScreen()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd != 0)
        {
            return WinForms.Screen.FromHandle(hwnd);
        }

        var cursor = WinForms.Control.MousePosition;
        return WinForms.Screen.FromPoint(cursor);
    }

    private static double GetScaleForScreen(WinForms.Screen screen)
    {
        var center = new System.Drawing.Point(
            screen.Bounds.Left + screen.Bounds.Width / 2,
            screen.Bounds.Top + screen.Bounds.Height / 2);

        var monitor = MonitorFromPoint(new PointNative { X = center.X, Y = center.Y }, MonitorDefaultToNearest);
        if (monitor != 0 &&
            GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 &&
            dpiX > 0)
        {
            return dpiX / 96.0;
        }

        return 1.0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out UserNotificationState pquns);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(PointNative pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
