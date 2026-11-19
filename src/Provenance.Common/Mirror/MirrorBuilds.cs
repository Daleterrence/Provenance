using System.Security.Cryptography;
using Provenance.Common.PolFormat;

namespace Provenance.Common.Mirror;

public sealed record MirrorBuild(string Region, string Version, string PolExe, string PolExeLaa, string Polcore)
{
    public PolRegion? ToPolRegion() => Enum.GetNames<PolRegion>().Contains(Region) ? Enum.Parse<PolRegion>(Region) : null;
}

public sealed record MirrorBuilds(int Format, DateTimeOffset Published, IReadOnlyList<MirrorBuild> Builds)
{
    public byte[] ToBytes() => MirrorJson.Pack(this);

    public static MirrorBuilds FromBytes(ReadOnlySpan<byte> data)
    {
        var v = MirrorJson.Unpack<MirrorBuilds>(data);
        if (v.Format != 1 || v.Builds is null || v.Published == default) throw new InvalidDataException("mirror build list has a bad header");
        foreach (var b in v.Builds)
            if (b is null || b.ToPolRegion() is null || MirrorJson.Blank(b.Version) || !MirrorJson.IsSha256(b.PolExe)
                || !MirrorJson.IsSha256(b.PolExeLaa) || !MirrorJson.IsSha256(b.Polcore))
                throw new InvalidDataException("mirror build list has a bad entry");
        return v;
    }

    public static MirrorBuilds? FromSigned(ReadOnlySpan<byte> blob, IReadOnlyDictionary<string, ECDsa> trusted)
    {
        if (!SignedBlob.TryOpen(blob, trusted, out var payload)) return null;
        try { return FromBytes(payload); }
        catch (InvalidDataException) { return null; }
    }
}
