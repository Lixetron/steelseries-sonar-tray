using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace SonarQuickMixer;

internal static class TrayWindowPlacement
{
    private const double MarginDip = 8;

    private const uint AbmGetTaskbarPos = 5;
    private const uint AbeLeft = 0;
    private const uint AbeTop = 1;
    private const uint AbeRight = 2;
    private const uint AbeBottom = 3;

    private const uint MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint CbSize;
        public IntPtr HWnd;
        public uint UCallbackMessage;
        public uint UEdge;
        public Rect Rc;
        public int LParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll")]
    private static extern uint SHAppBarMessage(uint dwMessage, ref AppBarData pData);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(PointNative pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    public static void PlaceAboveTaskbar(Window window, System.Drawing.Point? anchorScreenPoint = null)
    {
        window.UpdateLayout();

        var windowWidth = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        var windowHeight = window.ActualHeight > 0 ? window.ActualHeight : window.Height;

        if (windowWidth <= 0)
        {
            windowWidth = 320;
        }

        if (windowHeight <= 0)
        {
            windowHeight = 360;
        }

        var screen = ResolveScreen(anchorScreenPoint);
        var scale = GetScaleForScreen(screen);
        var workArea = screen.WorkingArea;

        var workLeft = workArea.Left / scale;
        var workTop = workArea.Top / scale;
        var workRight = workArea.Right / scale;
        var workBottom = workArea.Bottom / scale;

        var (left, top) = GetPositionAboveTaskbar(windowWidth, windowHeight, scale, workLeft, workTop, workRight, workBottom);

        window.Left = Math.Max(workLeft + MarginDip, left);
        window.Top = Math.Max(workTop + MarginDip, top);
        window.Left = Math.Min(workRight - windowWidth - MarginDip, window.Left);
        window.Top = Math.Min(workBottom - windowHeight - MarginDip, window.Top);
    }

    private static WinForms.Screen ResolveScreen(System.Drawing.Point? anchorScreenPoint)
    {
        if (anchorScreenPoint.HasValue)
        {
            return WinForms.Screen.FromPoint(anchorScreenPoint.Value);
        }

        var cursor = WinForms.Control.MousePosition;
        return WinForms.Screen.FromPoint(cursor);
    }

    private static (double Left, double Top) GetPositionAboveTaskbar(
        double windowWidth,
        double windowHeight,
        double scale,
        double workLeft,
        double workTop,
        double workRight,
        double workBottom)
    {
        var appBarData = new AppBarData
        {
            CbSize = (uint)Marshal.SizeOf<AppBarData>()
        };

        _ = SHAppBarMessage(AbmGetTaskbarPos, ref appBarData);

        var taskbar = appBarData.Rc;
        var taskbarLeft = taskbar.Left / scale;
        var taskbarTop = taskbar.Top / scale;
        var taskbarRight = taskbar.Right / scale;
        var taskbarBottom = taskbar.Bottom / scale;

        return appBarData.UEdge switch
        {
            AbeBottom => (
                workRight - windowWidth - MarginDip,
                taskbarTop - windowHeight - MarginDip),
            AbeTop => (
                workRight - windowWidth - MarginDip,
                taskbarBottom + MarginDip),
            AbeLeft => (
                taskbarRight + MarginDip,
                workBottom - windowHeight - MarginDip),
            AbeRight => (
                taskbarLeft - windowWidth - MarginDip,
                workBottom - windowHeight - MarginDip),
            _ => (
                workRight - windowWidth - MarginDip,
                workBottom - windowHeight - MarginDip)
        };
    }

    private static double GetScaleForScreen(WinForms.Screen screen)
    {
        var center = new PointNative
        {
            X = screen.WorkingArea.Left + screen.WorkingArea.Width / 2,
            Y = screen.WorkingArea.Top + screen.WorkingArea.Height / 2
        };

        var monitor = MonitorFromPoint(center, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero &&
            GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0 &&
            dpiX > 0)
        {
            return dpiX / 96.0;
        }

        return 1.0;
    }
}
