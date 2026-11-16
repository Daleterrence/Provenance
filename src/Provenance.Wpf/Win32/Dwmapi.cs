using System.Runtime.InteropServices;

namespace Provenance.Wpf.Win32;

internal static class Dwmapi
{
    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
