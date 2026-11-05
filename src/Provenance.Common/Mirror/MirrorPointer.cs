namespace Provenance.Common.Mirror;

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
