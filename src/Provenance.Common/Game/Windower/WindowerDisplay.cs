using System.Runtime.InteropServices;

namespace Provenance.Common.Game.Windower;

public readonly record struct WindowerSizes(int Width, int Height, int UIWidth, int UIHeight, float UIScale)
{
    public static WindowerSizes For(float uiScale, int width, int height, Func<(int Width, int Height)>? display = null)
    {
        if (width == 0 || height == 0) (width, height) = (display ?? WindowerDisplay.Primary)();
        int uw = Truncate(width, uiScale), uh = Truncate(height, uiScale);
        return uw <= 0 || uh <= 0 ? new(width, height, width, height, 1f) : new(width, height, uw, uh, uiScale);
    }

    // .NET Framework's x86 JIT divides in double precision and truncates without rounding to float first, so 1100 / 1.1
    // is 999, not 1000 (because of course it is). Out of range or NaN gives 0x80000000
    internal static int Truncate(int size, float scale)
    {
        double q = size / (double)scale;
        return double.IsNaN(q) || q >= 2147483648.0 || q <= -2147483649.0 ? int.MinValue : (int)q;
    }
}

public static class WindowerDisplay
{
    private const int EnumRegistrySettings = -2;
    private const int AttachedToDesktop = 0x1, PrimaryDevice = 0x4, MirroringDriver = 0x8;

    public static (int Width, int Height) Primary()
    {
        var dev = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
        for (int i = 0; EnumDisplayDevicesW(null, i, ref dev, 0); i++, dev = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() })
        {
            if ((dev.StateFlags & AttachedToDesktop) == 0 || (dev.StateFlags & MirroringDriver) != 0 || (dev.StateFlags & PrimaryDevice) == 0)
                continue;
            var dm = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
            if (EnumDisplaySettingsW(dev.DeviceName, EnumRegistrySettings, ref dm) && dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0)
                return (dm.dmPelsWidth, dm.dmPelsHeight);
            break;
        }
        return FfxiRegistry.PrimaryDisplay();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? device, int devNum, ref DisplayDevice displayDevice, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? deviceName, int modeNum, ref DevMode devMode);
}
