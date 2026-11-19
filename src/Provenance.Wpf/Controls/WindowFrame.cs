using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Provenance.Wpf.Services;
using Provenance.Wpf.Win32;

namespace Provenance.Wpf.Controls;

internal static class WindowFrame
{
    public const double Radius = 8;
    private const string Edge = "Border.Window";

    // WindowChrome gives these windows a window region, which DWM rounds fine from 24H2 on, older builds get the square frame
    internal static bool Rounded { get; set; } = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100);

    public static void Apply(Window w)
    {
        if (w.AllowsTransparency) return;
        var hwnd = new WindowInteropHelper(w).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(Hook);
        if (Rounded && RoundCorners(hwnd))
        {
            // the docs only promise DWM stops rounding a maximised window, not that the line goes too
            Action edge = () => DwmEdge(w, hwnd, ((SolidColorBrush)w.FindResource(Edge)).Color);
            edge();
            w.StateChanged += (_, _) => edge();
            AppTheme.Changed += edge;
            w.Closed += (_, _) => AppTheme.Changed -= edge;
            return;
        }
        w.SetResourceReference(Control.BorderBrushProperty, Edge);
        WpfEdge(w, VisualTreeHelper.GetDpi(w));
        w.StateChanged += (_, _) => WpfEdge(w, VisualTreeHelper.GetDpi(w));
        w.DpiChanged += (_, e) => WpfEdge(w, e.NewDpi);
    }

    private static bool RoundCorners(IntPtr hwnd)
    {
        int round = DWMWCP_ROUND;
        return Dwmapi.DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int)) == 0;
    }

    private static void DwmEdge(Window w, IntPtr hwnd, Color edge)
    {
        int colour = w.WindowState == WindowState.Maximized ? DWMWA_COLOR_NONE : edge.R | edge.G << 8 | edge.B << 16;
        Dwmapi.DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colour, sizeof(int));
    }

    // whole device pixels, or at 150% the line and everything inside it sits on half pixels and goes soft
    private static void WpfEdge(Window w, DpiScale dpi)
    {
        double line = Math.Max(1, Math.Round(dpi.DpiScaleX, MidpointRounding.AwayFromZero)) / dpi.DpiScaleX;
        w.BorderThickness = new Thickness(w.WindowState == WindowState.Maximized ? 0 : line);
    }

    internal static Rect WorkAreaOf(Window w)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(w);
            IntPtr monitor;
            if (new WindowInteropHelper(w).Handle is { } hwnd && hwnd != IntPtr.Zero) monitor = User32.MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            else if (!double.IsNaN(w.Left) && !double.IsNaN(w.Top))
                monitor = User32.MonitorFromPoint(new POINT { X = (int)(w.Left * dpi.DpiScaleX), Y = (int)(w.Top * dpi.DpiScaleY) }, MonitorDefaultToNearest);
            else return SystemParameters.WorkArea;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (monitor == IntPtr.Zero || !User32.GetMonitorInfo(monitor, ref mi)) return SystemParameters.WorkArea;
            return new Rect(mi.rcWork.Left / dpi.DpiScaleX, mi.rcWork.Top / dpi.DpiScaleY,
                            (mi.rcWork.Right - mi.rcWork.Left) / dpi.DpiScaleX, (mi.rcWork.Bottom - mi.rcWork.Top) / dpi.DpiScaleY);
        }
        catch (Exception) { return SystemParameters.WorkArea; }
    }

    // a maximised borderless window covers the taskbar too, which I somehow missed more than once, confirmed blind
    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MonitorDefaultToNearest = 2;

    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
        var monitor = User32.MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !User32.GetMonitorInfo(monitor, ref mi)) return IntPtr.Zero;
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.ptMaxPosition.X = mi.rcWork.Left - mi.rcMonitor.Left;
        mmi.ptMaxPosition.Y = mi.rcWork.Top - mi.rcMonitor.Top;
        mmi.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
        mmi.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
        // a window that fills the whole monitor counts as full screen, so an auto-hide taskbar won't pop up over it
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
        return IntPtr.Zero;
    }

    private static (IntPtr Monitor, long At, int Bars) _lastBars;

    private static int AutoHideBars(IntPtr monitor, RECT rc)
    {
        long now = Environment.TickCount64;
        if (_lastBars.Monitor == monitor && now - _lastBars.At < 2000) return _lastBars.Bars;
        int bars = 0;
        for (int edge = AbeLeft; edge <= AbeBottom; edge++)
        {
            var bar = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), uEdge = edge, rc = rc };
            if (Shell32.SHAppBarMessage(ABM_GETAUTOHIDEBAREX, ref bar) != IntPtr.Zero) bars |= 1 << edge;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
}
