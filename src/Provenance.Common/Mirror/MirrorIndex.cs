namespace Provenance.Common.Mirror;

public sealed record MirrorEntry(string Path, long Size, string Sha256, long CompressedSize, string? Pack = null, long Offset = 0);

public sealed record MirrorPack(string Sha256, long Size);

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
                bool same = sha == f.Sha256 && off == f.Offset && len == f.CompressedSize;
                if (!same && f.Offset < end) throw new InvalidDataException("packed mirror index has overlapping entries");
                (sha, off, len) = (f.Sha256, f.Offset, f.CompressedSize);
                end = Math.Max(end, f.Offset + f.CompressedSize);
            }
        }
    }
}
