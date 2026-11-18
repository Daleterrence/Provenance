using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Provenance.Common.Social;

// Area is the zone id and Id the character's, packed the way the request packs it. Name is kept but not shown yet. The
// flags are 0 when the entry doesn't have them
internal readonly record struct SearchEntry(uint Id, int Area, string Name, uint Flags1 = 0, uint Flags2 = 0, uint Language = 0);

internal static class SearchFlags
{
    public const uint Anon = 0x4000;
    public const uint InParty = 0x2000;
    public const uint PartyLeader = 0x0008;
    // an alliance leader bit isn't known yet, PartyState has no room for it until it is
}

internal sealed record SearchPacket(bool Final, int Total, IReadOnlyList<SearchEntry> Entries);

// FFXI's search server as the game talks to it, for the one thing we ask: the list of characters by id
internal static class SearchPackets
{
    public const int Port = 54002;
    public const int MaxIds = 20;
    public const int MaxPackets = 16;
    private const int MaxReply = 8192;
    private const int Header = 0x18;
    private const int Trailer = 0x14;

    private static readonly byte[] StaticKey = "0s=m<1IZ2zBCc8{~"u8.ToArray();

    // the same length and layout as the game's, padding up to a multiple of 64. The game's padding is whatever its buffer
    // held, ours is zeros
    public static byte[] BuildIdList(IReadOnlyList<uint> ids, ReadOnlySpan<byte> keyHalf, ReadOnlySpan<byte> x)
    {
        if (ids.Count is 0 or > MaxIds) throw new ArgumentOutOfRangeException(nameof(ids));
        int used = 0x12 + 4 * ids.Count;
        int padded = (used + 12 + 63) / 64 * 64;
        var buf = new byte[padded + 12];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)buf.Length);
        "IXFF"u8.CopyTo(buf.AsSpan(4));
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8), (ushort)used);
        buf[0x0a] = 0x80;
        buf[0x0b] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x10), (ushort)ids.Count);
        for (int i = 0; i < ids.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x12 + 4 * i), ids[i]);
        x.CopyTo(buf.AsSpan(padded - 12));
        MD5.HashData(buf.AsSpan(8, padded - 16), buf.AsSpan(padded - 8, 16));
        new SearchCipher(Key(keyHalf)).Run(buf, 8, padded, false);
        keyHalf.CopyTo(buf.AsSpan(padded + 8));
        return buf;
    }

    private static byte[] Key(ReadOnlySpan<byte> half, ReadOnlySpan<byte> x = default)
    {
        var all = new byte[StaticKey.Length + half.Length + x.Length];
        StaticKey.CopyTo(all, 0);
        half.CopyTo(all.AsSpan(StaticKey.Length));
        x.CopyTo(all.AsSpan(StaticKey.Length + half.Length));
        return MD5.HashData(all);
    }

    // the length word of a reply packet, or 0 when it isn't one we'd read
    public static int ReplyLength(ReadOnlySpan<byte> first4)
    {
        if (first4.Length < 4) return 0;
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(first4);
        return length is >= Header + Trailer and <= MaxReply ? (int)length : 0;
    }

    // one whole packet, the way it came off the wire. Nothing here throws on bad bytes, it just says no
    public static bool TryRead(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> x, out SearchPacket? read)
    {
        read = null;
        int length = ReplyLength(packet);
        if (length == 0 || packet.Length != length) return false;
        var buf = packet.ToArray();
        new SearchCipher(Key(buf.AsSpan(length - 4, 4), x)).Run(buf, 8, length - 12, true);
        if (!MD5.HashData(buf.AsSpan(8, length - Trailer - 8)).AsSpan().SequenceEqual(buf.AsSpan(length - Trailer, 16))) return false;
        if (buf[0x0b] != 0x81) return false;
        int size = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(8));
        if (size != 0 && size < Header || size > length - Trailer) return false;
        var entries = new List<SearchEntry>();
        int at = Header;
        while (at < size)
        {
            int body = buf[at];
            if (body == 0 || at + 1 + body > size) break;
            if (ReadEntry(buf.AsSpan(at + 1, body)) is { } entry) entries.Add(entry);
            at += 1 + body;
        }
        read = new SearchPacket(buf[0x0a] == 0x80, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0x0e)), entries);
        return true;
    }

    // fields are bit packed most significant bit first, each behind a 5 bit tag. A tag we don't know the width of ends the entry
    private static SearchEntry? ReadEntry(ReadOnlySpan<byte> body)
    {
        var bits = new Bits(body);
        uint? id = null;
        int? area = null;
        string name = "";
        uint flags1 = 0, flags2 = 0, language = 0;
        while (bits.Left >= 8)
        {
            if (!bits.Take(5, out var tag)) break;
            uint value;
            switch (tag)
            {
                case 0:
                {
                    if (!bits.Take(4, out var count)) return Entry();
                    var chars = new char[count];
                    for (int i = 0; i < count; i++)
                    {
                        if (!bits.Take(7, out var c)) return Entry();
                        chars[i] = (char)c;
                    }
                    name = new string(chars);
                    continue;
                }
                case 1:
                    if (!bits.Take(10, out value)) return Entry();
                    area = (int)value;
                    continue;
                case 6:
                    if (!bits.Take(16, out flags1)) return Entry();
                    continue;
                case 8:
                    if (!bits.Take(20, out value)) return Entry();
                    id = value;
                    continue;
                case 0x16:
                    if (!bits.Take(32, out flags2)) return Entry();
                    continue;
                case 0x17:
                    if (!bits.Take(16, out language)) return Entry();
                    continue;
            }
            int skip = SkippedBits(tag);
            if (skip == 0 || !bits.Skip(skip)) break;
        }
        return Entry();

        SearchEntry? Entry() => id is { } i && area is { } a ? new SearchEntry(i, a, name, flags1, flags2, language) : null;
    }

    // job is main then sub at 5 bits each, level main then sub at 8, and master level 8
    private static int SkippedBits(uint tag) => tag switch
    {
        0x02 => 2,
        0x03 => 10,
        0x04 => 16,
        0x05 => 4,
        0x07 => 8,
        0x0d => 120,
        0x0e => 32,
        0x10 => 8,
        0x11 => 32,
        _ => 0,
    };

    private ref struct Bits(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private int _at;

        public readonly int Left => _bytes.Length * 8 - _at;

        public bool Take(int count, out uint value)
        {
            value = 0;
            if (count > Left) return false;
            for (int i = 0; i < count; i++, _at++)
                value = (value << 1) | (uint)((_bytes[_at >> 3] >> (7 - (_at & 7))) & 1);
            return true;
        }

        public bool Skip(int count)
        {
            if (count > Left) return false;
            _at += count;
            return true;
        }
    }
}
