using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Provenance.Wpf.Services;
using Provenance.Wpf.Win32;

namespace Provenance.Wpf.Controls;

internal static class TaskbarFlash
{
    internal static Action<Window, bool>? Seen;

    private static readonly HashSet<Window> Flashing = new();

    public static void Start(Window w)
    {
        if (Flashing.Add(w))
        {
            w.Activated += OnBroughtUp;
            w.Closed += OnBroughtUp;
        }
        Flash(w, true, 3 | 0x0C);   // FLASHW_ALL | FLASHW_TIMERNOFG
    }

    public static void StartIfBehind(Window w)
    {
        if (!w.IsVisible || w.WindowState == WindowState.Minimized || WindowOwner.Active() is not null) return;
        Start(w);
    }

    public static void StopWhenAppActivated(Window w)
    {
        EventHandler stop = (_, _) => StopIfFlashing(w);
#pragma warning disable RS0030
        if (Application.Current is not { } app) return;
        app.Activated += stop;
        w.Closed += (_, _) => app.Activated -= stop;
#pragma warning restore RS0030
    }

    public static void StopIfFlashing(Window w)
    {
        if (Forget(w)) Flash(w, false, 0);   // FLASHW_STOP
    }

    private static void OnBroughtUp(object? sender, EventArgs e)
    {
        if (sender is Window w) Forget(w);
    }

    private static bool Forget(Window w)
    {
        if (!Flashing.Remove(w)) return false;
        w.Activated -= OnBroughtUp;
        w.Closed -= OnBroughtUp;
        return true;
    }

    private static void Flash(Window w, bool start, uint flags)
    {
        Seen?.Invoke(w, start);
        var fi = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = new WindowInteropHelper(w).Handle,
            dwFlags = flags,
        };
        User32.FlashWindowEx(ref fi);
    }
}
