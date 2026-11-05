using System.Buffers.Binary;

namespace Provenance.Common.Patcher;

// POL's file cipher (polcore.dll 0x100081e0 decrypt, 0x100081a0 encrypt). 8-byte blocks plus a trailer (a value
// and a plaintext checksum), each through a byte layer and a 64-bit layer.
public sealed class PolFileCipher
{
    private readonly byte[] _ctx = new byte[0x100];

    public PolFileCipher(ReadOnlySpan<byte> key8)
    {
        uint k0 = BinaryPrimitives.ReadUInt32LittleEndian(key8);
        uint k1 = BinaryPrimitives.ReadUInt32LittleEndian(key8[4..]);
        BinaryPrimitives.WriteUInt32LittleEndian(_ctx, (k1 >> 16) | (k1 << 16));
        BinaryPrimitives.WriteUInt32LittleEndian(_ctx.AsSpan(4), (k0 << 8) | (k0 >> 24));
        _ctx[0] = (byte)(_ctx[0] + 0x45);
        for (int i = 1; i < 8; i++)
        {
            byte b = (byte)(_ctx[i] + _ctx[i - 1] - 0x2C);
            _ctx[i] = (byte)(b ^ (byte)(_ctx[i - 1] << 2) ^ 0x45);
        }
        for (int i = 0; i < 31; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(_ctx.AsSpan((i + 1) * 8),
                unchecked(BinaryPrimitives.ReadUInt64LittleEndian(_ctx.AsSpan(i * 8)) * 5));
    }

    public (byte[] Data, uint Value)? Decrypt(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 16 || blob.Length % 8 != 0) return null;
        uint checksum = 0;
        var data = new byte[blob.Length - 8];
        for (int off = 0; off < data.Length; off += 8)
        {
            DecryptBlock(blob.Slice(off, 8), data.AsSpan(off, 8), (uint)off);
            checksum += (uint)data[off] + data[off + 4];
        }
        Span<byte> trailer = stackalloc byte[8];
        DecryptBlock(blob[^8..], trailer, (uint)data.Length);
        if (BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]) != checksum) return null;
        return (data, BinaryPrimitives.ReadUInt32LittleEndian(trailer));
    }

    public byte[] Encrypt(ReadOnlySpan<byte> data, uint value)
    {
        if (data.Length % 8 != 0) throw new ArgumentException("The data must be a multiple of 8 bytes.", nameof(data));
        var blob = new byte[data.Length + 8];
        uint checksum = 0;
        for (int off = 0; off < data.Length; off += 8)
        {
            EncryptBlock(data.Slice(off, 8), blob.AsSpan(off, 8), (uint)off);
            checksum += (uint)data[off] + data[off + 4];
        }
        Span<byte> trailer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, value);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[4..], checksum);
        EncryptBlock(trailer, blob.AsSpan(data.Length, 8), (uint)data.Length);
        return blob;
    }

    private void DecryptBlock(ReadOnlySpan<byte> src, Span<byte> dst, uint counter)
    {
        Span<byte> tmp = stackalloc byte[8];
        int b = (int)((counter >> 3) & 0x1F) * 8;
        byte add = ByteLayerAdd(b);
        byte prev = (byte)((counter >> 3) ^ 0x45);
        for (int i = 0; i < 8; i++)
        {
            tmp[i] = (byte)((src[i] ^ prev) + add);
            prev = src[i];
        }
        ulong k = BinaryPrimitives.ReadUInt64LittleEndian(_ctx.AsSpan(b));
        ulong x = unchecked(BinaryPrimitives.ReadUInt64LittleEndian(tmp) - k) ^ CounterMix(counter) ^ k;
        // the halves are stored swapped
        BinaryPrimitives.WriteUInt32LittleEndian(dst, (uint)(x >> 32));
        BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], (uint)x);
    }

    private void EncryptBlock(ReadOnlySpan<byte> src, Span<byte> dst, uint counter)
    {
        int b = (int)((counter >> 3) & 0x1F) * 8;
        ulong k = BinaryPrimitives.ReadUInt64LittleEndian(_ctx.AsSpan(b));
        ulong x = ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(src) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(src[4..]);
        Span<byte> tmp = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(tmp, unchecked((x ^ k ^ CounterMix(counter)) + k));
        byte add = ByteLayerAdd(b);
        byte prev = (byte)((counter >> 3) ^ 0x45);
        for (int i = 0; i < 8; i++)
        {
            dst[i] = (byte)((byte)(tmp[i] - add) ^ prev);
            prev = dst[i];
        }
    }

    // the byte layer does "x = sbox(x) - key[j]" eight times with sbox(x) = x + 0x78. Eight rounds of "encryption"
    // that fold into one add, nice one SE
    private byte ByteLayerAdd(int b)
    {
        int s = 0;
        for (int j = 0; j < 8; j++) s += 0x78 - _ctx[b + j];
        return (byte)s;
    }

    private static ulong CounterMix(uint counter)
    {
        ulong t = counter;
        for (int i = 0; i < 3; i++) t = (t << 10) | counter;
        return unchecked(t + 0xA1652347UL);
    }
}
