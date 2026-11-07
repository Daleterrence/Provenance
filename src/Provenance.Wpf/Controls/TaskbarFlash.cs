using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Provenance.Wpf.Win32;

namespace Provenance.Wpf.Controls;

// Start keeps going till the window comes to the front. StopIfFlashing's the only other way to clear it, the taskbar
// button stays highlighted otherwise
internal static class TaskbarFlash
{
    internal static Action<Window, bool>? Seen;   // tests watch the calls, nothing else sets it

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

    public static void StopIfFlashing(Window w)
    {
        if (Forget(w)) Flash(w, false, 0);   // FLASHW_STOP
    }

    // coming to the front stops the flash on its own, this only forgets it
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
