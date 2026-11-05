using System.Runtime.InteropServices;
using System.Text;

namespace Provenance.Common.Win32;

internal static partial class User32
{
    [DllImport("user32.dll")]
    internal static extern bool AllowSetForegroundWindow(uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool EnumDisplayDevicesW(string? device, int devNum, ref DisplayDevice displayDevice, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool EnumDisplaySettingsW(string? deviceName, int modeNum, ref DevMode devMode);

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SystemParametersInfoW(uint action, uint param, out int value, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SystemParametersInfoW(uint action, uint param, IntPtr value, uint winIni);
}

internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DisplayDevice
{
    public int cb;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
    public int StateFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DevMode
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
    public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
    public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
    public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
    public short dmLogPixels;
    public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
    public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
}
