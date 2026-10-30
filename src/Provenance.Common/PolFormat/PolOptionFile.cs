using System.Buffers.Binary;

namespace Provenance.Common.PolFormat;

// pub\homeNN\option.bin: polcore's 8-byte block cipher (all-zero key), then a block with the length and a checksum.
// Records from +8, 0x20 each: name, type at +0xa (0 is a number), min/max at +0xc/+0x10, value at +0x18
public static class PolOptionFile
{
    // 0 is on, 1 off
    public const string ChatFilter = "GOPN1_HF";
    // minutes, -1 is off
    public const string AutoDisconnect = "GOPN1_AO";

    public const int MaxSize = 1024 * 1024;

    private const int RecordSize = 0x20;

    private static readonly ulong[] Keys = KeyTable();
    private static readonly byte[] KeySums = Keys.Select(k => (byte)BitConverter.GetBytes(k).Sum(b => b)).ToArray();

    private static ulong[] KeyTable()
    {
        Span<byte> b = stackalloc byte[8];
        b[0] = 0x45;
        for (int i = 1; i < 8; i++)
            b[i] = (byte)((b[i - 1] << 2) ^ (byte)(b[i - 1] - 0x2c) ^ 0x45);
        var keys = new ulong[32];
        keys[0] = BinaryPrimitives.ReadUInt64LittleEndian(b);
        for (int n = 1; n < keys.Length; n++) keys[n] = keys[n - 1] * 5;
        return keys;
    }

    private static ulong Mix(uint at)
    {
        ulong t = at;
        for (int i = 0; i < 3; i++) t = (t << 10) | at;
        return t + 0xa1652347;
    }

    // null when it isn't one (wrong size, or the checksum's off)
    public static byte[]? Decrypt(ReadOnlySpan<byte> file)
    {
        if (file.Length < 16 || file.Length > MaxSize || file.Length % 8 != 0) return null;
        var plain = new byte[file.Length];
        uint sum = 0;
        for (int at = 0; at < file.Length; at += 8)
        {
            DecryptBlock(file.Slice(at, 8), plain.AsSpan(at, 8), (uint)at);
            if (at < file.Length - 8) sum += (uint)(plain[at] + plain[at + 4]);
        }
        int length = file.Length - 8;
        if (BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(length)) != length ||
            BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(length + 4)) != sum) return null;
        return plain[..length];
    }

    public static byte[] Encrypt(ReadOnlySpan<byte> plain)
    {
        if (plain.Length == 0 || plain.Length % 8 != 0 || plain.Length > MaxSize - 8)
            throw new ArgumentException("option.bin data has to be whole 8-byte blocks", nameof(plain));
        var data = new byte[plain.Length + 8];
        plain.CopyTo(data);
        uint sum = 0;
        for (int at = 0; at < plain.Length; at += 8) sum += (uint)(plain[at] + plain[at + 4]);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(plain.Length), (uint)plain.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(plain.Length + 4), sum);
        var file = new byte[data.Length];
        for (int at = 0; at < data.Length; at += 8) EncryptBlock(data.AsSpan(at, 8), file.AsSpan(at, 8), (uint)at);
        return file;
    }

    // SE's byte step is a 256-entry table that turns out to be +0x78, done once per key byte
    private static void DecryptBlock(ReadOnlySpan<byte> src, Span<byte> dst, uint at)
    {
        int sel = (int)(at >> 3) & 0x1f;
        byte prev = (byte)((at >> 3) ^ 0x45);
        Span<byte> d = stackalloc byte[8];
        for (int i = 0; i < 8; i++)
        {
            d[i] = (byte)((src[i] ^ prev) + 0xc0 - KeySums[sel]);
            prev = src[i];
        }
        ulong k = Keys[sel];
        ulong x = ((BinaryPrimitives.ReadUInt64LittleEndian(d) - k) ^ Mix(at)) ^ k;
        BinaryPrimitives.WriteUInt64LittleEndian(dst, (x >> 32) | (x << 32));
    }

    private static void EncryptBlock(ReadOnlySpan<byte> src, Span<byte> dst, uint at)
    {
        int sel = (int)(at >> 3) & 0x1f;
        ulong k = Keys[sel];
        ulong p = BinaryPrimitives.ReadUInt64LittleEndian(src);
        ulong x = ((((p >> 32) | (p << 32)) ^ k) ^ Mix(at)) + k;
        Span<byte> d = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(d, x);
        byte prev = (byte)((at >> 3) ^ 0x45);
        for (int i = 0; i < 8; i++)
        {
            dst[i] = (byte)((byte)(d[i] - 0xc0 + KeySums[sel]) ^ prev);
            prev = dst[i];
        }
    }

    private static int Find(byte[] plain, string name)
    {
        if (plain.Length < 8 || name.Length != 8) return -1;
        int count = BinaryPrimitives.ReadInt32LittleEndian(plain);
        if (count <= 0 || count > (plain.Length - 8) / RecordSize) return -1;
        for (int i = 0; i < count; i++)
        {
            int at = 8 + i * RecordSize;
            bool same = true;
            for (int c = 0; c < 8 && same; c++) same = plain[at + c] == name[c];
            if (same) return BinaryPrimitives.ReadInt16LittleEndian(plain.AsSpan(at + 0xa)) == 0 ? at : -1;
        }
        return -1;
    }

    public static int? Read(byte[] plain, string name) =>
        Find(plain, name) is var at and >= 0 ? BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(at + 0x18)) : null;

    // the same range check polcore does, where a 0..0 range means anything goes
    public static bool Write(byte[] plain, string name, int value)
    {
        int at = Find(plain, name);
        if (at < 0) return false;
        int min = BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(at + 0xc));
        int max = BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(at + 0x10));
        if ((min != 0 || max != 0) && (value < min || value > max)) return false;
        BinaryPrimitives.WriteInt32LittleEndian(plain.AsSpan(at + 0x18), value);
        return true;
    }
}
