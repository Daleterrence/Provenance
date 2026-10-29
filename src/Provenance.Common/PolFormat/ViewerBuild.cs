using System.Security.Cryptography;

namespace Provenance.Common.PolFormat;

// the hook patches fixed places in pol.exe and polcore.dll, so only 1.18.15e US fits (another viewer build is gonna
// hurt). A fresh install has 1.18.00n till the viewer updates it
public static class ViewerBuild
{
    public enum State { Supported, Outdated, Unknown, Missing }

    public const string CurrentVersion = "1.18.15e";
    internal const string PolExeSha256 = "f496bfc7c26b33e42b7ccf263e29854725a8009deb784f0e6c508a6004f13f00";
    // the same file with Large Address Aware on, byte for byte what Windower's Tools page writes
    internal const string PolExeLaaSha256 = "f9ce70440b8639652355ff28d74871eb290119f3448938817bd0f684657cab2e";
    private const string PolcoreSha256 = "73b1864bf522d3aa6ec28e9f4bb226fcee956f7f4848a238c9c2d756dc703b06";
    internal const string InUseText = "The PlayOnline Viewer's files are in use or being changed. Close PlayOnline and try again.";

    public static string? Version(string viewerDir)
    {
        try
        {
            var path = Path.Combine(viewerDir, "version.dat");
            return File.Exists(path) ? File.ReadAllText(path).Trim('\0', ' ', '\r', '\n') : null;
        }
        catch { return null; }
    }

    public static State Check(string viewerDir)
    {
        var pol = Path.Combine(viewerDir, "pol.exe");
        var core = Path.Combine(viewerDir, "viewer", "com", "polcore.dll");
        if (!File.Exists(pol) || !File.Exists(core)) return State.Missing;
        if (Sha256(pol) is PolExeSha256 or PolExeLaaSha256 && Sha256(core) == PolcoreSha256) return State.Supported;
        // viewer versions compare as text, "1.18.00n" < "1.18.15e"
        var v = Version(viewerDir);
        return v != null && string.CompareOrdinal(v, CurrentVersion) < 0 ? State.Outdated : State.Unknown;
    }

    public static string? Problem(string viewerDir) => Check(viewerDir) switch
    {
        State.Supported => null,
        State.Missing => "Couldn't find the PlayOnline Viewer. Install FINAL FANTASY XI first.",
        State.Outdated => $"Your PlayOnline Viewer is version {Version(viewerDir)}, and it needs updating to {CurrentVersion} first.",
        _ => $"Provenance doesn't support this version of the PlayOnline Viewer ({Version(viewerDir) ?? "unknown"}) yet. " +
             "Check for a Provenance update.",
    };

    // held open while pol.exe starts: the files that got checked are the ones that run
    public static IDisposable? Hold(string viewerDir, out string? problem)
    {
        var files = new List<FileStream>();
        try
        {
            foreach (var rel in new[] { "pol.exe", Path.Combine("viewer", "com", "polcore.dll") })
                files.Add(new FileStream(Path.Combine(viewerDir, rel), FileMode.Open, FileAccess.Read, FileShare.Read));
            if (Convert.ToHexStringLower(SHA256.HashData(files[0])) is PolExeSha256 or PolExeLaaSha256 &&
                Convert.ToHexStringLower(SHA256.HashData(files[1])) == PolcoreSha256)
            {
                problem = null;
                return new Holder(files);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        files.ForEach(f => f.Dispose());
        // whatever had the files shut can still have them for the second look
        try { problem = Problem(viewerDir) ?? InUseText; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { problem = InUseText; }
        return null;
    }

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
