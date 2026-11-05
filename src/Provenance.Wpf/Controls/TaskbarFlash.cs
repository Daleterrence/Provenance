using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Provenance.Wpf.Win32;

namespace Provenance.Wpf.Controls;

// Start keeps going till the window comes to the front. Stop's the only other way to clear it, the taskbar button
// stays highlighted otherwise
internal static class TaskbarFlash
{
    internal static Action<Window, bool>? Seen;   // tests watch the calls, nothing else sets it

    public static void Start(Window w) => Flash(w, true, 3 | 0x0C);   // FLASHW_ALL | FLASHW_TIMERNOFG

    public static void Stop(Window w) => Flash(w, false, 0);   // FLASHW_STOP

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
