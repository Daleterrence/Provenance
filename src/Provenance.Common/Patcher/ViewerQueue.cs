using Provenance.Common.IO;
using Provenance.Common.PolFormat;
using Provenance.Common.Security;
using Provenance.Common.Win32;

namespace Provenance.Common.Patcher;

public static class ViewerQueue
{
    // SE's writer, in the ANSI code page like startpol.exe's reader
    internal static bool Queue(string viewerDir, string target) =>
        Kernel32.WritePrivateProfileStringA("Rename", target, target + ".tmp2", Path.Combine(viewerDir, "patch.ini"));

    // The viewer's next start, done here: startpol.exe /REPLACEPOLEXE moves queued .tmp2 files in (patch.ini ->
    // patch.bak), then pol.exe's boot step at 0x00410710 refreshes the DLLs. Throws if any are in use.
    public static void ApplyQueued(string viewerDir)
    {
        var ini = Path.Combine(viewerDir, "patch.ini");
        if (File.Exists(ini))
        {
            var queued = new List<(string Target, string Staged)>();
            foreach (var line in RenameSection(ini))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string target = line[..eq].Trim(), staged = line[(eq + 1)..].Trim();
                if (!PathInside.IsUnder(viewerDir, target) || !PathInside.IsUnder(viewerDir, staged))
                    throw new InvalidDataException($"patch.ini names a file outside the PlayOnline Viewer folder: {target}");
                if (File.Exists(staged)) queued.Add((target, staged));
            }
            // every copy before any delete, so a file in use leaves the whole queue to run again
            foreach (var (target, staged) in queued)
            {
                AtomicFile.ClearReadOnly(target);
                File.Copy(staged, target, overwrite: true);
            }
            foreach (var (_, staged) in queued) AtomicFile.DeleteReadOnly(staged);
            AtomicFile.ClearReadOnly(Path.Combine(viewerDir, "patch.bak"));
            File.Copy(ini, Path.Combine(viewerDir, "patch.bak"), overwrite: true);
            AtomicFile.DeleteReadOnly(ini);
        }
        foreach (var (staged, live) in BootCopies(viewerDir))
        {
            AtomicFile.ClearReadOnly(live);
            File.Copy(staged, live, overwrite: true);
        }
    }

    public static bool QueuedWorkPending(string viewerDir) =>
        File.Exists(Path.Combine(viewerDir, "patch.ini")) || BootCopies(viewerDir).Any();

    public static readonly IReadOnlyList<string> BootCopyNames = ["app.dll", "appEU.dll", "polcore.dll", "polcoreEU.dll"];

    // the two each region's viewer runs, the EU ones are its own builds
    public static IReadOnlyList<string> BootCopyNamesFor(PolRegion region) =>
        region == PolRegion.Europe ? ["appEU.dll", "polcoreEU.dll"] : ["app.dll", "polcore.dll"];

    public const string LiveFolder = "viewer/com/", BootCopyFolder = "patchfiles/PlayOnlineViewer/viewer/com/";

    // pol.exe's boot list (wide strings at 0x453134) and its test (0x004073d0), which only compares write times. A
    // different time means a copy every start, failing if a DLL's held
    private static IEnumerable<(string Staged, string Live)> BootCopies(string viewerDir) =>
        from name in BootCopyNames
        let staged = Path.Combine(viewerDir, BootCopyFolder.Replace('/', Path.DirectorySeparatorChar), name)
        let live = Path.Combine(viewerDir, LiveFolder.Replace('/', Path.DirectorySeparatorChar), name)
        where File.Exists(staged)
              && (!File.Exists(live) || File.GetLastWriteTimeUtc(staged) != File.GetLastWriteTimeUtc(live))
        select (staged, live);

    // Windows decodes it with the same code page that wrote it
    private static IEnumerable<string> RenameSection(string ini)
    {
        var buf = new char[32767];
        int n = Kernel32.GetPrivateProfileSectionW("Rename", buf, buf.Length, ini);
        return new string(buf, 0, n).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}
