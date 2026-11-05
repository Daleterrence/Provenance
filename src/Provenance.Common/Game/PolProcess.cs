using System.Diagnostics;
using Provenance.Common.PolFormat;

namespace Provenance.Common.Game;

// Anything can be renamed pol.exe, so the image path must be an installed viewer's. Start times are UTC so a
// time-zone change can't make a live process look new.
public static class PolProcess
{
    public static bool IsViewerPol(Process p)
    {
        try
        {
            if (!p.ProcessName.Equals("pol", StringComparison.OrdinalIgnoreCase)) return false;
            if (ProcessTree.ImagePath(p.Id) is not { } image) return false;
            return new[] { PolRegion.UnitedStates, PolRegion.Europe, PolRegion.Japan }.Any(region =>
                PolPaths.TryGetInstallFolder(region, PolPaths.ViewerProduct) is { } dir &&
                string.Equals(Path.GetFullPath(Path.Combine(dir, "pol.exe")), Path.GetFullPath(image), StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    public static long StartUtcTicks(Process p)
    {
        try { return p.StartTime.ToUniversalTime().Ticks; }
        catch { return 0; }
    }
}
