using System.Security.Cryptography;

namespace Provenance.Common.PolFormat;

// the hook patches fixed places in pol.exe and polcore, so only the exact build fits (another viewer build is gonna
// hurt). A fresh install has 1.18.00n till the viewer updates it. SE pushed a new build 2026-11-09 (file version
// still 1.18.13, pol.exe grew to 2,333,440 bytes) and native/pol_hook.c was rewritten for its offsets, with no
// fallback for the pre-2026-11-09 bytes - so this is a cutover, not an addition, and both layers have to agree on
// which build is supported, region by region. The hashes below cover US, EU and JP. US and EU's pol.exe is the same
// file byte for byte. JP's pol.exe and polcore aren't, despite being the same size, the code's identical but a
// handful of embedded data addresses shift by a few bytes between locale builds (the hook's own signatures wildcard
// those, this hash gate can't)
public static class ViewerBuild
{
    public enum State { Supported, Outdated, Unknown, Missing }

    public const string CurrentVersion = "1.18.15e";
    internal const string PolExeSha256 = "38058db285a99f188a4b7cae6f0df061cd556ea7b7527089e992574e981fdfd9";
    // the same file with Large Address Aware on, byte for byte what Windower's Tools page writes
    internal const string PolExeLaaSha256 = "6ee1b42f68d63e6f87b3108dfa028cbba77df7278cea7f033fc073b21590a886";
    internal const string JpPolExeSha256 = "cc4d5458828bd658d5c376e9d6be2e5957aee182a89fb6177679be652fd37c0a";
    internal const string JpPolExeLaaSha256 = "02f3b99cbb15ebe1b59764e11d927236824e27956ddb7a269cb290ee61c8f56b";
    internal const string InUseText = "The PlayOnline Viewer's files are in use or being changed. Close PlayOnline and try again.";

    private static readonly (PolRegion Region, string Off, string On)[] PolExes =
    [
        (PolRegion.UnitedStates, PolExeSha256, PolExeLaaSha256),
        (PolRegion.Europe, PolExeSha256, PolExeLaaSha256),
        (PolRegion.Japan, JpPolExeSha256, JpPolExeLaaSha256),
    ];

    private static readonly (PolRegion Region, string Sha256)[] Polcores =
    [
        (PolRegion.UnitedStates, "3c07c1a34ea600413257e7ade72b4f451d539d79667bab5fa3793c369c468015"),
        (PolRegion.Europe, "b71e48d56c4218adbfe2132a5fa3573b0b047bf7f3e6c909b1b38d632bea094e"),
        (PolRegion.Japan, "bdba55ed820ebdf64533c7603ee09057032dd564c799ad7411be2e43d34db15e"),
    ];

    internal sealed record Builds(IReadOnlyList<(PolRegion Region, string Off, string On)> PolExes,
                                  IReadOnlyList<(PolRegion Region, string Sha256)> Polcores);

    internal static readonly Builds Known = new(PolExes, Polcores);

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

    public static State Check(string viewerDir) => Check(viewerDir, RegionOf(viewerDir), Known);

    internal static State Check(string viewerDir, PolRegion region, Builds known)
    {
        var pol = Path.Combine(viewerDir, "pol.exe");
        var core = Path.Combine(viewerDir, PolcorePath(region));
        if (!File.Exists(pol) || !File.Exists(core)) return State.Missing;
        if (Fits(known, region, Sha256(pol), () => Sha256(core))) return State.Supported;
        // viewer versions compare as text, "1.18.00n" < "1.18.15e"
        var v = Version(viewerDir);
        return v != null && string.CompareOrdinal(v, CurrentVersion) < 0 ? State.Outdated : State.Unknown;
    }

    public static string? Problem(string viewerDir) => Problem(viewerDir, RegionOf(viewerDir), Known);

    internal static string? Problem(string viewerDir, PolRegion region, Builds known) => Check(viewerDir, region, known) switch
    {
        State.Supported => null,
        State.Missing => "Couldn't find the PlayOnline Viewer. Install FINAL FANTASY XI first.",
        State.Outdated => $"Your PlayOnline Viewer is version {Version(viewerDir)}, and it needs updating to {CurrentVersion} first.",
        // version.dat's right, it's pol.exe or polcore that isn't the file that version came with
        _ when Version(viewerDir) == CurrentVersion =>
            $"Your PlayOnline Viewer says it's version {CurrentVersion}, but its files don't match that version. " +
            (region == PolRegion.Japan ? "Reinstall the PlayOnline Viewer to fix them." : "Press Repair game files to fix them."),
        _ =>$"Provenance doesn't support this version of the PlayOnline Viewer ({Version(viewerDir) ?? "unknown"}) yet. " +
             "Restart Provenance to check for an update.",
    };

    // held open while pol.exe starts: the files that got checked are the ones that run
    public static IDisposable? Hold(string viewerDir, out string? problem) => Hold(viewerDir, RegionOf(viewerDir), Known, out problem);

    internal static IDisposable? Hold(string viewerDir, PolRegion region, Builds known, out string? problem)
    {
        var files = new List<FileStream>();
        try
        {
            foreach (var rel in new[] { "pol.exe", PolcorePath(region) })
                files.Add(new FileStream(Path.Combine(viewerDir, rel), FileMode.Open, FileAccess.Read, FileShare.Read));
            if (Fits(known, region, Convert.ToHexStringLower(SHA256.HashData(files[0])), () => Convert.ToHexStringLower(SHA256.HashData(files[1]))))
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

    private static bool Fits(Builds known, PolRegion region, string polSha, Func<string> coreSha) =>
        known.PolExes.Any(p => p.Region == region && (polSha == p.Off || polSha == p.On)) && known.Polcores.Contains((region, coreSha()));

    private sealed class Holder(List<FileStream> files) : IDisposable
    {
        public void Dispose() => files.ForEach(f => f.Dispose());
    }

    private static string Sha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }
}
