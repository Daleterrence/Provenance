using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Provenance.Wpf.Views;

// DWM rounds every window and draws a thin Border.Window line round it. Where it can't, WPF draws the line on square
// corners. Either way the line goes while the window's maximised
internal static class WindowFrame
{
    public const double Radius = 8;   // DWM's own corner for a normal window, in DIPs
    private const string Edge = "Border.Window";

    // WindowChrome gives these windows a window region, which DWM rounds fine from 24H2 on. The docs say it can't, so
    // older builds get the square frame instead
    internal static bool Rounded { get; set; } = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100);

    public static void Attach(Window w)
    {
        // the layered ones draw their own corners, border and shadow round a see-through margin, so DWM stays out
        if (w.AllowsTransparency)
        {
            if (!Rounded && w.Content is Border root) root.CornerRadius = new CornerRadius(0);
            return;
        }
        w.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(Hook);
            if (Rounded && RoundCorners(hwnd))
            {
                var edge = ((SolidColorBrush)w.FindResource(Edge)).Color;
                // the docs only promise DWM stops rounding a maximised window, not that the line goes too
                DwmEdge(w, hwnd, edge);
                w.StateChanged += (_, _) => DwmEdge(w, hwnd, edge);
                return;
            }
            w.SetResourceReference(Control.BorderBrushProperty, Edge);
            WpfEdge(w, VisualTreeHelper.GetDpi(w));
            w.StateChanged += (_, _) => WpfEdge(w, VisualTreeHelper.GetDpi(w));
            w.DpiChanged += (_, e) => WpfEdge(w, e.NewDpi);
        };
    }

    private static bool RoundCorners(IntPtr hwnd)
    {
        int round = DWMWCP_ROUND;
        return DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int)) == 0;
    }

    private static void DwmEdge(Window w, IntPtr hwnd, Color edge)
    {
        int colour = w.WindowState == WindowState.Maximized ? DWMWA_COLOR_NONE : edge.R | edge.G << 8 | edge.B << 16;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colour, sizeof(int));
    }

    // whole device pixels, or at 150% the line and everything inside it sits on half pixels and goes soft
    private static void WpfEdge(Window w, DpiScale dpi)
    {
        double line = Math.Max(1, Math.Round(dpi.DpiScaleX, MidpointRounding.AwayFromZero)) / dpi.DpiScaleX;
        w.BorderThickness = new Thickness(w.WindowState == WindowState.Maximized ? 0 : line);
    }

    // a maximised borderless window covers the taskbar too, which took me far too long to notice
    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MonitorDefaultToNearest = 2;

    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref mi)) return IntPtr.Zero;
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        // the position's relative to the monitor
        mmi.ptMaxPosition.X = mi.rcWork.Left - mi.rcMonitor.Left;
        mmi.ptMaxPosition.Y = mi.rcWork.Top - mi.rcMonitor.Top;
        mmi.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
        mmi.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
        // a window that fills the whole monitor counts as full screen, and an auto-hide taskbar won't pop up over
        // one, so it stops a pixel short on that edge
        int bars = SameRect(mi.rcWork, mi.rcMonitor) ? AutoHideBars(monitor, mi.rcMonitor) : 0;
        for (int edge = AbeLeft; edge <= AbeBottom; edge++)
        {
            if ((bars & 1 << edge) == 0) continue;
            if (edge == AbeLeft) mmi.ptMaxPosition.X++;
            if (edge == AbeTop) mmi.ptMaxPosition.Y++;
            if (edge is AbeLeft or AbeRight) mmi.ptMaxSize.X--;
            else mmi.ptMaxSize.Y--;
        }
        Marshal.StructureToPtr(mmi, lParam, true);
        return IntPtr.Zero;   // not handled, so WPF still applies MinWidth and MinHeight
    }

    private static (IntPtr Monitor, long At, int Bars) _lastBars;

    // this message comes in on every move and resize too, and each ask is a trip to the taskbar, so it's kept a bit
    private static int AutoHideBars(IntPtr monitor, RECT rc)
    {
        long now = Environment.TickCount64;
        if (_lastBars.Monitor == monitor && now - _lastBars.At < 2000) return _lastBars.Bars;
        int bars = 0;
        for (int edge = AbeLeft; edge <= AbeBottom; edge++)
        {
            var bar = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), uEdge = edge, rc = rc };
            if (SHAppBarMessage(ABM_GETAUTOHIDEBAREX, ref bar) != IntPtr.Zero) bars |= 1 << edge;
        }
        _lastBars = (monitor, now, bars);
        return bars;
    }

    private static bool SameRect(RECT a, RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWCP_ROUND = 2;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
    private const uint ABM_GETAUTOHIDEBAREX = 0x0B;
    private const int AbeLeft = 0, AbeTop = 1, AbeRight = 2, AbeBottom = 3;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public int uEdge; public RECT rc; public IntPtr lParam; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("shell32.dll")] private static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);
}
