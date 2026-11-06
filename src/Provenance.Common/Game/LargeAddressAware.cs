using System.Buffers.Binary;
using Provenance.Common.IO;
using Provenance.Common.Install;
using Provenance.Common.PolFormat;

namespace Provenance.Common.Game;

// Windower's Large Address Aware tick box: bit 0x20 of pol.exe's COFF Characteristics, set on disk with no checksum fix,
// so the file comes out the same as Windower's
public static class LargeAddressAware
{
    public enum Result { Done, NotPe, UnknownBuild, InUse, Denied, Failed }

    private const byte Flag = 0x20;
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    // US and Europe share a pol.exe, so that pair is only in once
    internal static readonly (string Off, string On)[] Known = ViewerBuild.Known.PolExes.Select(p => (p.Off, p.On)).Distinct().ToArray();

    // null when the file can't be read or isn't a PE file
    public static bool? Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return BitAt(fs) is { } at ? (ByteAt(fs, at) & Flag) != 0 : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // SE's pol.exe, with or without the bit
    public static bool IsKnownBuild(string path) => Known.Any(pair => IsKnownBuild(path, pair));

    internal static bool IsKnownBuild(string path, (string Off, string On) known)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var sha = FileHash.Sha256(fs);
        return sha == known.Off || sha == known.On;
    }

    // the pair this pol.exe belongs to, or the first so an unknown file gets UnknownBuild
    public static Result Set(string path, bool on)
    {
        var pair = Known[0];
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var sha = FileHash.Sha256(fs);
            pair = Known.FirstOrDefault(k => sha == k.Off || sha == k.On, Known[0]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return Set(path, on, pair);
    }

    // hashed before and after the one byte, so only SE's pol.exe or Windower's copy of it ever gets written
    internal static Result Set(string path, bool on, (string Off, string On) known)
    {
        try
        {
            using var fs = OpenToWrite(path, known);
            if (BitAt(fs) is not { } at) return Result.NotPe;
            byte old = ByteAt(fs, at);
            bool was = (old & Flag) != 0;
            if (FileHash.Sha256(fs) != (was ? known.On : known.Off)) return Result.UnknownBuild;
            if (was == on) return Result.Done;
            try
            {
                Put(fs, at, (byte)(on ? old | Flag : old & ~Flag));
                if (FileHash.Sha256(fs) == (on ? known.On : known.Off)) return Result.Done;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            // anything wrong once the byte's gone out puts the old one back, best effort
            try { Put(fs, at, old); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return Result.Failed;
        }
        catch (IOException e) when (e.HResult is SharingViolation or LockViolation) { return Result.InUse; }
        catch (UnauthorizedAccessException) { return Result.Denied; }
        catch (IOException) { return Result.Failed; }
    }

    // SE's own patcher clears read-only on whatever it replaces, so a known pol.exe gets the same
    private static FileStream OpenToWrite(string path, (string Off, string On) known)
    {
        try { return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (UnauthorizedAccessException)
        {
            var attributes = File.GetAttributes(path);
            if (!attributes.HasFlag(FileAttributes.ReadOnly) || !Path.GetFileName(path).Equals(GameProduct.Viewer.KeyFile, StringComparison.OrdinalIgnoreCase) ||
                !IsKnownBuild(path, known))
                throw;
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }

    // the repair and install scans count Windower's pol.exe as intact, it's SE's file with the one bit set
    public static bool Matches(string relativePath, string listedSha256, string actualSha256) =>
        Known.Any(pair => Matches(relativePath, listedSha256, actualSha256, pair));

    internal static bool Matches(string relativePath, string listedSha256, string actualSha256, (string Off, string On) known) =>
        relativePath.Equals(GameProduct.Viewer.KeyFile, StringComparison.OrdinalIgnoreCase) && listedSha256 == known.Off && actualSha256 == known.On;

    // SE's bytes back, when path is pol.exe with only the bit set. POL's patcher compares those against its file list
    public static byte[]? Unpatched(string path) => Known.Select(pair => Unpatched(path, pair)).FirstOrDefault(b => b is not null);

    internal static byte[]? Unpatched(string path, (string Off, string On) known)
    {
        if (!Path.GetFileName(path).Equals(GameProduct.Viewer.KeyFile, StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (FileHash.Sha256(bytes) != known.On) return null;
            using var ms = new MemoryStream(bytes);
            if (BitAt(ms) is not { } at) return null;
            bytes[at] &= unchecked((byte)~Flag);
            return bytes;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // "MZ", e_lfanew at 0x3c, "PE\0\0" there, and the low byte of Characteristics 22 bytes on (0x146 in pol.exe)
    private static long? BitAt(Stream s)
    {
        Span<byte> b = stackalloc byte[4];
        if (s.Length < 0x40) return null;
        s.Position = 0;
        s.ReadExactly(b[..2]);
        if (b[0] != 'M' || b[1] != 'Z') return null;
        s.Position = 0x3c;
        s.ReadExactly(b);
        long pe = BinaryPrimitives.ReadInt32LittleEndian(b);
        if (pe < 0x40 || pe > s.Length - 24) return null;
        s.Position = pe;
        s.ReadExactly(b);
        return b is [(byte)'P', (byte)'E', 0, 0] ? pe + 22 : null;
    }

    private static byte ByteAt(Stream s, long at)
    {
        s.Position = at;
        return (byte)s.ReadByte();
    }

    private static void Put(FileStream fs, long at, byte value)
    {
        fs.Position = at;
        fs.WriteByte(value);
        fs.Flush(flushToDisk: true);
    }
}
