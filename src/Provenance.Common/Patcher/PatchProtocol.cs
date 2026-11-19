using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Provenance.Common.Patcher;

// u32 length, MD5(frame[8..])[0..4], "POLP", u32 command, body
public static class PatchProtocol
{
    public const int Manifest = 1, ManifestData = 2, Range = 3, RangeData = 4, Version = 7, VersionData = 8;
    public const int MaxRange = 0x10000;

    private static byte[] Frame(int cmd, int length)
    {
        var f = new byte[length];
        BinaryPrimitives.WriteInt32LittleEndian(f, length);
        "POLP"u8.CopyTo(f.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(12), cmd);
        return f;
    }

    private static byte[] Seal(byte[] f)
    {
        MD5.HashData(f.AsSpan(8)).AsSpan(0, 4).CopyTo(f.AsSpan(4));
        return f;
    }

    private static void Ids(Span<byte> at, string tag, string product)
    {
        Encoding.ASCII.GetBytes(tag.AsSpan(0, Math.Min(3, tag.Length)), at);
        Encoding.ASCII.GetBytes(product.AsSpan(0, 4), at[4..]);
    }

    public static byte[] ManifestRequest(string tag, string product)
    {
        var f = Frame(Manifest, 0x18);
        Ids(f.AsSpan(16), tag, product);
        return Seal(f);
    }

    public static byte[] RangeRequest(long offset, int length, string tag, string product, string path)
    {
        var p = Encoding.Latin1.GetBytes(path);
        var f = Frame(Range, p.Length + 0x25);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(16), checked((uint)offset));
        BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(20), length);
        Ids(f.AsSpan(24), tag, product);
        BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(32), p.Length + 1);
        p.CopyTo(f.AsSpan(36));
        return Seal(f);
    }

    public static byte[] VersionRequest(string tag, string product, string localVersion)
    {
        var f = Frame(Version, 0x58);
        Ids(f.AsSpan(16), tag, product);
        var v = Encoding.Latin1.GetBytes(localVersion);
        v.AsSpan(0, Math.Min(v.Length, 63)).CopyTo(f.AsSpan(24));
        return Seal(f);
    }

    public static void Check(ReadOnlySpan<byte> f, int cmd)
    {
        if (f.Length < 16 || BinaryPrimitives.ReadInt32LittleEndian(f) != f.Length || !f.Slice(8, 4).SequenceEqual("POLP"u8))
            throw new InvalidDataException("PlayOnline's update server sent a damaged reply.");
        if (!MD5.HashData(f[8..]).AsSpan(0, 4).SequenceEqual(f.Slice(4, 4)))
            throw new InvalidDataException("PlayOnline's update server sent a reply that failed its check.");
        if (BinaryPrimitives.ReadInt32LittleEndian(f[12..]) != cmd)
            throw new InvalidDataException($"PlayOnline's update server sent reply {BinaryPrimitives.ReadInt32LittleEndian(f[12..])}, not {cmd}.");
    }

    public sealed record VersionReply(string Server, string S0, string S1, string S2);

    public static VersionReply ParseVersion(byte[] f)
    {
        Check(f, VersionData);
        if (f.Length < 92) throw new InvalidDataException("PlayOnline's update server sent a short version reply.");
        int len = BinaryPrimitives.ReadInt32LittleEndian(f.AsSpan(88));
        if (len < 0 || len >= 0x100 || 92 + len > f.Length) throw new InvalidDataException("PlayOnline's update server sent a bad version.");
        var server = Encoding.Latin1.GetString(f, 92, len).TrimEnd('\0');
        var strings = Encoding.Latin1.GetString(f, 24, 64).Split('\0');
        string At(int i) => i < strings.Length ? strings[i] : "";
        return new VersionReply(server, At(0), At(1), At(2));
    }

    public static ReadOnlyMemory<byte> ParseManifest(byte[] f)
    {
        Check(f, ManifestData);
        if (f.Length < 21 || f[16] != 0x02) throw new InvalidDataException("PlayOnline's update server sent a manifest of an unknown kind.");
        return f.AsMemory(17);
    }

    public static ReadOnlyMemory<byte> ParseRange(byte[] f, int requested)
    {
        Check(f, RangeData);
        if (f.Length < 28) throw new InvalidDataException("PlayOnline's update server sent a short file part.");
        int len = BinaryPrimitives.ReadInt32LittleEndian(f.AsSpan(20));
        int pathLen = BinaryPrimitives.ReadInt32LittleEndian(f.AsSpan(24));
        if (len < 0 || len > MaxRange || len != requested || pathLen < 0 || 28L + pathLen + len > f.Length)
            throw new InvalidDataException("PlayOnline's update server sent a damaged file part.");
        return f.AsMemory(28 + pathLen, len);
    }
}
