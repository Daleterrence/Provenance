using System.IO.Compression;
using System.Text.Json;

namespace Provenance.Common.Mirror;

public sealed record MirrorEntry(string Path, long Size, string Sha256, long CompressedSize, string? Pack = null, long Offset = 0);

public sealed record MirrorPack(string Sha256, long Size);

// format 1 has an object per file, format 2 adds packs
public sealed record MirrorIndex(int Format, string Region, string Product, string Version, DateTimeOffset Published,
                                 string Codec, IReadOnlyList<MirrorEntry> Files, IReadOnlyList<MirrorPack>? Packs = null)
{
    public const int PackedFormat = 2;

    public byte[] ToBytes() => MirrorJson.Pack(this);

    public static MirrorIndex FromBytes(ReadOnlySpan<byte> data)
    {
        var v = MirrorJson.Unpack<MirrorIndex>(data);
        if ((v.Format != 1 && v.Format != PackedFormat) || v.Codec != "br" || MirrorJson.Blank(v.Region) || MirrorJson.Blank(v.Product)
            || MirrorJson.Blank(v.Version) || v.Files is null)
            throw new InvalidDataException("mirror index has a bad header");
        foreach (var f in v.Files)
            if (f is null || MirrorJson.Blank(f.Path) || f.Size < 0 || f.CompressedSize < 0 || !MirrorJson.IsSha256(f.Sha256))
                throw new InvalidDataException("mirror index has a bad entry");
        if (v.Format == PackedFormat) CheckPacked(v);
        return v;
    }

    private static void CheckPacked(MirrorIndex v)
    {
        if (v.Packs is null) throw new InvalidDataException("packed mirror index has no packs");
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var p in v.Packs)
            if (p is null || !MirrorJson.IsSha256(p.Sha256) || p.Size < 0 || !sizes.TryAdd(p.Sha256, p.Size))
                throw new InvalidDataException("packed mirror index has a bad pack");
        foreach (var byPack in v.Files.GroupBy(f => f.Pack ?? throw new InvalidDataException("packed mirror index has an entry without a pack")))
        {
            if (!sizes.TryGetValue(byPack.Key, out var packSize))
                throw new InvalidDataException("packed mirror index names an unlisted pack");
            long end = 0, len = 0, off = 0;
            string? sha = null;
            foreach (var f in byPack.OrderBy(f => f.Offset).ThenBy(f => f.CompressedSize))
            {
                if (f.Offset < 0 || f.Offset > packSize - f.CompressedSize)
                    throw new InvalidDataException("packed mirror index has an entry outside its pack");
                // identical content can share one location, anything else can't overlap
                bool same = sha == f.Sha256 && off == f.Offset && len == f.CompressedSize;
                if (!same && f.Offset < end) throw new InvalidDataException("packed mirror index has overlapping entries");
                (sha, off, len) = (f.Sha256, f.Offset, f.CompressedSize);
                end = Math.Max(end, f.Offset + f.CompressedSize);
            }
        }
    }
}

public sealed record MirrorPointer(int Format, string Region, DateTimeOffset Published, IReadOnlyDictionary<string, string> Versions)
{
    public byte[] ToBytes() => MirrorJson.Pack(this);

    public static MirrorPointer FromBytes(ReadOnlySpan<byte> data)
    {
        var v = MirrorJson.Unpack<MirrorPointer>(data);
        if (v.Format != 1 || MirrorJson.Blank(v.Region) || v.Versions is null)
            throw new InvalidDataException("mirror pointer has a bad header");
        foreach (var kv in v.Versions)
            if (MirrorJson.Blank(kv.Key) || MirrorJson.Blank(kv.Value))
                throw new InvalidDataException("mirror pointer has a bad version");
        return v;
    }
}

internal static class MirrorJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static byte[] Pack<T>(T value)
    {
        using var ms = new MemoryStream();
        using (var br = new BrotliStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            JsonSerializer.Serialize(br, value, Options);
        return ms.ToArray();
    }

    public static bool Blank(string? s) => string.IsNullOrEmpty(s);

    public static bool IsSha256(string? s) =>
        s is { Length: 64 } && s.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    // No size cap on the decompression, it only runs once SignedBlob.TryOpen has passed. Only InvalidDataException
    // gets out.
    public static T Unpack<T>(ReadOnlySpan<byte> data) where T : class
    {
        try
        {
            using var input = new MemoryStream(data.ToArray());
            using var br = new BrotliStream(input, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<T>(br, Options) ?? throw new InvalidDataException("mirror JSON is null");
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("mirror data is malformed: " + ex.Message, ex);
        }
    }
}
