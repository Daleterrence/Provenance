using System.Runtime.InteropServices;

namespace Provenance.Launcher.Win32;

internal static class User32
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
