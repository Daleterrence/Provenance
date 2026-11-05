using Serilog;
using Provenance.Common.PolFormat;

namespace Provenance.Common.Install;

public static class InstallParts
{
    public static void RemoveDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (Exception e) { Log.Warning(e, "Could not remove {Dir}", dir); }
    }

    public static void RemoveParts(string work, PolRegion region)
    {
        var unpacked = Path.Combine(work, "unpacked");
        if (Directory.Exists(unpacked)) RemoveDir(unpacked);
        foreach (var name in GameInstaller.PartsFor(region).SelectMany(p => new[] { p.FileName, p.FileName + ".part" }))
            try { File.Delete(Path.Combine(work, name)); } catch (Exception e) { Log.Warning(e, "Could not remove {Part}", name); }
    }
}
