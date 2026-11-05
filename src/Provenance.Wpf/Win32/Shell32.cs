using System.Runtime.InteropServices;

namespace Provenance.Wpf.Win32;

internal static partial class Shell32
{
    [DllImport("shell32.dll")]
    internal static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);
}

[StructLayout(LayoutKind.Sequential)]
internal struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public int uEdge; public RECT rc; public IntPtr lParam; }
