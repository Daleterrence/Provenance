using System.Runtime.InteropServices;
using Provenance.Common.Win32;

namespace Provenance.Common.Game;

public static class Display
{
    private const int EnumCurrentSettings = -1, EnumRegistrySettings = -2;
    private const int AttachedToDesktop = 0x1, PrimaryDevice = 0x4, MirroringDriver = 0x8;

    // real pixels, not scaled by Windows' text size
    public static (int Width, int Height) Primary() =>
        Mode(null, EnumCurrentSettings) is { Width: > 0, Height: > 0 } m ? m : (1920, 1080);

    public static IEnumerable<(int Width, int Height)> Modes()
    {
        for (int i = 0; Mode(null, i) is { } m; i++) yield return m;
    }

    // Windower sizes the window from the registry mode of the primary device, not the current one
    public static (int Width, int Height) PrimaryAsWindowerSeesIt()
    {
        var dev = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
        for (int i = 0; User32.EnumDisplayDevicesW(null, i, ref dev, 0); i++, dev = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() })
        {
            if ((dev.StateFlags & AttachedToDesktop) == 0 || (dev.StateFlags & MirroringDriver) != 0 || (dev.StateFlags & PrimaryDevice) == 0)
                continue;
            if (Mode(dev.DeviceName, EnumRegistrySettings) is { Width: > 0, Height: > 0 } m) return m;
            break;
        }
        return Primary();
    }

    private static (int Width, int Height)? Mode(string? device, int modeNum)
    {
        var dm = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
        return User32.EnumDisplaySettingsW(device, modeNum, ref dm) ? (dm.dmPelsWidth, dm.dmPelsHeight) : null;
    }
}
