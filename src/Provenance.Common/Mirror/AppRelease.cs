using System.Security.Cryptography;

namespace Provenance.Common.Mirror;

// What the publisher signs for each app release. Its own key signs the mirror's objects too and the envelope's magic
// isn't signed, so Kind is what keeps a statement from passing as one of those (none of them has it)
public sealed record AppRelease(int Format, string Kind, string AppId, string Channel, string Version, string FileName, long Size,
                                string Sha256, DateTimeOffset Published)
{
    public const string ReleaseKind = "app-release";
    public const string App = "Provenance";
    public const string Stable = "stable";
    public const string Beta = "beta";
    public const int MaxBytes = 64 * 1024;

    // the name vpk pack gives a full package, and the name the update server has it under
    public static string PackageName(string version, string channel) => $"{App}-{version}-{channel}-full.nupkg";

    public static string StatementName(string packageName) => packageName + ".sig";

    public byte[] ToBytes() => MirrorJson.Pack(this);

    public static AppRelease FromBytes(ReadOnlySpan<byte> data)
    {
        var v = MirrorJson.Unpack<AppRelease>(data);
        if (v.Format != 1 || v.Kind != ReleaseKind || MirrorJson.Blank(v.AppId) || MirrorJson.Blank(v.Channel) || MirrorJson.Blank(v.Version)
            || MirrorJson.Blank(v.FileName) || v.Size <= 0 || !MirrorJson.IsSha256(v.Sha256) || v.Published == default)
            throw new InvalidDataException("app release statement has a bad field");
        return v;
    }

    // null for anything that isn't signed by a trusted key or doesn't read
    public static AppRelease? FromSigned(ReadOnlySpan<byte> blob, IReadOnlyDictionary<string, ECDsa> trusted)
    {
        if (blob.Length > MaxBytes || !SignedBlob.TryOpen(blob, trusted, out var payload)) return null;
        try { return FromBytes(payload); }
        catch (InvalidDataException) { return null; }
    }

    // Null when the package is exactly the release the statement signs, on a channel this copy takes, otherwise why not.
    // The app, the publisher and the release check all share it, so none passes what another refuses
    public static string? Check(ReadOnlySpan<byte> blob, IReadOnlyDictionary<string, ECDsa> trusted, bool installedIsPrerelease,
                                string assetVersion, string assetFileName, bool assetIsFull, string? packagePath)
    {
        if (blob.Length > MaxBytes) return $"the statement is {blob.Length} bytes, over the {MaxBytes} allowed";
        if (!SignedBlob.TryOpen(blob, trusted, out var payload))
            return $"the statement isn't signed by a key this copy trusts (key id {SignedBlob.KeyId(blob) ?? "unreadable"})";
        AppRelease s;
        try { s = FromBytes(payload); }
        catch (InvalidDataException e) { return $"the statement doesn't read: {e.Message}"; }
        if (s.AppId != App) return $"the statement is for {s.AppId}, not {App}";
        if (!assetIsFull) return $"{assetFileName} isn't a full package";
        var expected = PackageName(assetVersion, s.Channel);
        if (!string.Equals(s.FileName, assetFileName, StringComparison.Ordinal) || !string.Equals(assetFileName, expected, StringComparison.Ordinal))
            return $"the statement names {s.FileName} and the feed {assetFileName}, where version {assetVersion} on {s.Channel} is {expected}";
        if (!string.Equals(s.Version, assetVersion, StringComparison.Ordinal)) return $"the statement is for {s.Version}, the feed offers {assetVersion}";
        if (s.Channel != Stable && !(installedIsPrerelease && s.Channel == Beta)) return $"the statement is for the {s.Channel} channel, which this copy doesn't take";
        if (packagePath is null || !File.Exists(packagePath)) return $"{packagePath ?? s.FileName} isn't there";
        try
        {
            using var fs = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length != s.Size) return $"{s.FileName} is {fs.Length} bytes, the statement says {s.Size}";
            var sha = Convert.ToHexStringLower(SHA256.HashData(fs));
            if (sha != s.Sha256) return $"{s.FileName} has SHA-256 {sha}, the statement says {s.Sha256}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"{packagePath} can't be read: {e.Message}"; }
        return null;
    }
}
