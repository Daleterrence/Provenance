using System.Runtime.InteropServices;

namespace Provenance.Wpf.Win32;

internal static class Shell32
{
    [DllImport("shell32.dll")]
    internal static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);
}

[StructLayout(LayoutKind.Sequential)]
internal struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public int uEdge; public RECT rc; public IntPtr lParam; }

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NOTIFYICONDATAW
{
    public int cbSize;
    public IntPtr hWnd;
    public uint uID, uFlags, uCallbackMessage;
    public IntPtr hIcon;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
    public uint dwState, dwStateMask;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
    public uint uTimeout;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
    public uint dwInfoFlags;
    public Guid guidItem;
    public IntPtr hBalloonIcon;
}
