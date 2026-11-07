using Provenance.Common.IO;
using Provenance.Common.Install;
using Provenance.Common.Mirror;

namespace Provenance.Common.PolFormat;

// the builds below are the ones Provenance ships with, and the mirror's signed list can add more. A fresh install has
// 1.18.00n till the viewer updates it
public static class ViewerBuild
{
    public enum State { Supported, Outdated, Unknown, Missing }

    public const string CurrentVersion = "1.18.15e";
    internal const string PolExeSha256 = "f496bfc7c26b33e42b7ccf263e29854725a8009deb784f0e6c508a6004f13f00";
    // the same file with Large Address Aware on, byte for byte what Windower's Tools page writes
    internal const string PolExeLaaSha256 = "f9ce70440b8639652355ff28d74871eb290119f3448938817bd0f684657cab2e";
    // JP's own compile of the same code (its .rdata sits 8 bytes lower), and its Large Address Aware twin
    internal const string JpPolExeSha256 = "0b5beb0198f0caafc252a0af64628fb3d1ae9b084ef772371b0e221b47ee5c3c";
    internal const string JpPolExeLaaSha256 = "bd93da61469706764f7e6469be4d8d7e1888a8bc1d907a80b2f572fb4f24c492";
    public const string UnsupportedText = "PlayOnline has been updated, and this version of Provenance doesn't support it yet. Check for a Provenance update.";
    internal const string InUseText = "The PlayOnline Viewer's files are in use or being changed. Close PlayOnline and try again.";

    // US and EU share a pol.exe, JP has its own, and each region's polcore is its own file
    private static readonly (PolRegion Region, string Off, string On)[] PolExes =
    [
        (PolRegion.UnitedStates, PolExeSha256, PolExeLaaSha256),
        (PolRegion.Europe, PolExeSha256, PolExeLaaSha256),
        (PolRegion.Japan, JpPolExeSha256, JpPolExeLaaSha256),
    ];

    private static readonly (PolRegion Region, string Sha256)[] Polcores =
    [
        (PolRegion.UnitedStates, "73b1864bf522d3aa6ec28e9f4bb226fcee956f7f4848a238c9c2d756dc703b06"),
        (PolRegion.Europe, "84c5329299bdf009192f12870d9ccd5809c3a3cefdad52efadf1c062bd7188bb"),
        (PolRegion.Japan, "93d34b5a7e1f74473f92b74125aca4f58cab8e56e0df2183ea3a0bbce548801d"),
    ];

    // Signed builds are checked as a pair, so one entry's pol.exe never goes with another's polcore
    internal sealed record Builds(IReadOnlyList<(PolRegion Region, string Off, string On)> PolExes,
                                  IReadOnlyList<(PolRegion Region, string Sha256)> Polcores,
                                  IReadOnlyList<MirrorBuild>? Signed = null);

    internal static readonly Builds Known = new(PolExes, Polcores);

    // a pol.exe (with or without Large Address Aware) and polcore Provenance ships with, for a region
    public static bool IsBuiltIn(PolRegion region, string polExeSha256, string polcoreSha256) =>
        Fits(Known, region, polExeSha256, () => polcoreSha256);

    public static string PolcorePath(PolRegion region) =>
        Path.Combine("viewer", "com", region == PolRegion.Europe ? "polcoreEU.dll" : "polcore.dll");

    // the region the pol.exe in viewerDir runs as, which picks the polcore it loads
    public static PolRegion RegionOf(string viewerDir) => PolEnv.Region(viewerDir) ?? PolRegion.UnitedStates;

    public static string? Version(string viewerDir)
    {
        try
        {
            var path = Path.Combine(viewerDir, "version.dat");
            return File.Exists(path) ? File.ReadAllText(path).Trim('\0', ' ', '\r', '\n') : null;
        }
        catch { return null; }
    }

    internal static State Check(string viewerDir) => Check(viewerDir, RegionOf(viewerDir), Known);

    internal static State Check(string viewerDir, PolRegion region, Builds known)
    {
        var pol = Path.Combine(viewerDir, GameProduct.Viewer.KeyFile);
        var core = Path.Combine(viewerDir, PolcorePath(region));
        if (!File.Exists(pol) || !File.Exists(core)) return State.Missing;
        if (Fits(known, region, FileHash.Sha256(pol), () => FileHash.Sha256(core))) return State.Supported;
        // viewer versions compare as text, "1.18.00n" < "1.18.15e"
        var v = Version(viewerDir);
        return v != null && string.CompareOrdinal(v, CurrentVersion) < 0 ? State.Outdated : State.Unknown;
    }

    internal static string? Problem(string viewerDir) => Problem(viewerDir, RegionOf(viewerDir), Known);

    internal static string? Problem(string viewerDir, PolRegion region, Builds known) => Check(viewerDir, region, known) switch
    {
        State.Supported => null,
        State.Missing => "Couldn't find the PlayOnline Viewer. Install FINAL FANTASY XI first.",
        State.Outdated => $"Your PlayOnline Viewer is version {Version(viewerDir)}, and it needs updating to {CurrentVersion} first.",
        // version.dat's right, it's pol.exe or polcore that isn't the file that version came with
        _ when Version(viewerDir) == CurrentVersion =>
            $"Your PlayOnline Viewer says it's version {CurrentVersion}, but its files don't match that version. " +
            (region == PolRegion.Japan ? "Reinstall the PlayOnline Viewer to fix them." : "Press Repair game files to fix them."),
        _ => UnsupportedText,
    };

    // held open while pol.exe starts: the files that got checked are the ones that run
    internal static IDisposable? Hold(string viewerDir, out string? problem) => Hold(viewerDir, RegionOf(viewerDir), Known, out problem);

    internal static IDisposable? Hold(string viewerDir, PolRegion region, Builds known, out string? problem)
    {
        var files = new List<FileStream>();
        try
        {
            foreach (var rel in new[] { GameProduct.Viewer.KeyFile, PolcorePath(region) })
                files.Add(new FileStream(Path.Combine(viewerDir, rel), FileMode.Open, FileAccess.Read, FileShare.Read));
            if (Fits(known, region, FileHash.Sha256(files[0]), () => FileHash.Sha256(files[1])))
            {
                problem = null;
                return new Holder(files);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        files.ForEach(f => f.Dispose());
        // whatever had the files shut can still have them for the second look
        try { problem = Problem(viewerDir, region, known) ?? InUseText; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { problem = InUseText; }
        return null;
    }

    private static bool Fits(Builds known, PolRegion region, string polSha, Func<string> coreSha)
    {
        var core = new Lazy<string>(coreSha);
        if (known.PolExes.Any(p => p.Region == region && (polSha == p.Off || polSha == p.On)) && known.Polcores.Contains((region, core.Value)))
            return true;
        return known.Signed?.Any(b => b.ToPolRegion() == region && (polSha == b.PolExe || polSha == b.PolExeLaa) && core.Value == b.Polcore) == true;
    }

    private sealed class Holder(List<FileStream> files) : IDisposable
    {
        public void Dispose() => files.ForEach(f => f.Dispose());
    }
}
