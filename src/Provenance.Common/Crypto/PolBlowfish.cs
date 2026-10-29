using System.Security.Cryptography;

namespace Provenance.Common.Crypto;

// POL's Blowfish variant for the SEA login IRC channel (ci000.pol.com): MD5-seeded tables and an 8-byte keystream
// XORed over the data. polcore.dll at 0x10063EF0, 0x10064220 and 0x10064300
public sealed class PolBlowfish
{
    private readonly uint[] _p = new uint[18];
    private readonly uint[] _s0 = new uint[256], _s1 = new uint[256], _s2 = new uint[256], _s3 = new uint[256];

    // send keeps its keystream and counter across calls (state +0x50/+0x54/+0x60), receive restarts from the IV
    // (+0x58/+0x5C)
    private ulong _encState;
    private int _encCounter;
    private readonly ulong _iv;

    // key and IV come from the server's IRC 300 welcome line (polcore.dll at 0x10063EB0)
    public PolBlowfish(byte[] key, uint ivLo, uint ivHi)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0) throw new ArgumentException("key must be non-empty", nameof(key));
        RunKsa(key);
        _encState = _iv = ((ulong)ivHi << 32) | ivLo;
    }

    public static PolBlowfish FromZeroKey(uint ivLo, uint ivHi) => new(new byte[8], ivLo, ivHi);

    public void EncryptInPlace(byte[] src, int srcOff, byte[] dst, int dstOff, int len) =>
        (_encState, _encCounter) = Apply(src, srcOff, dst, dstOff, len, _encState, _encCounter);

    public void DecryptInPlace(byte[] src, int srcOff, byte[] dst, int dstOff, int len) =>
        Apply(src, srcOff, dst, dstOff, len, _iv, 0);

    private (ulong State, int Counter) Apply(byte[] src, int srcOff, byte[] dst, int dstOff, int len, ulong s, int counter)
    {
        for (int i = 0; i < len; i++)
        {
            if (counter == 0) s = EncryptBlock((uint)s, (uint)(s >> 32));
            byte input = src[srcOff + i];
            byte output = (byte)(input ^ (byte)(s >> (8 * counter)));
            counter = (counter + 1) & 7;
            // keeps the IRC line framing: a CR or LF on either side goes over as is, the counter still moves on
            dst[dstOff + i] = IsLineEnd(input) || IsLineEnd(output) ? input : output;
        }
        return (s, counter);
    }

    private static bool IsLineEnd(byte b) => b is 0x0A or 0x0D;

    private ulong EncryptBlock(uint L, uint R)
    {
        for (int i = 0; i < 16; i++)
        {
            L ^= _p[i];
            R ^= ((_s0[L >> 24] + _s1[(L >> 16) & 0xff]) ^ _s2[(L >> 8) & 0xff]) + _s3[L & 0xff];
            (L, R) = (R, L);
        }
        (L, R) = (R, L);
        L ^= _p[16];
        R ^= _p[17];
        return ((ulong)R << 32) | L;
    }

    // Blowfish's key schedule, but the starting tables are chained MD5s of the key instead of the digits of pi
    private void RunKsa(byte[] key)
    {
        const int size = 0x1000;
        var buf = new byte[size];
        int written = Math.Min(key.Length, size);
        Buffer.BlockCopy(key, 0, buf, 0, written);
        while (written < size)
        {
            var hash = MD5.HashData(buf.AsSpan(0, written));
            int chunk = Math.Min(16, size - written);
            Buffer.BlockCopy(hash, 0, buf, written, chunk);
            written += chunk;
        }

        // P then the S-boxes, big-endian. That runs 72 bytes past the end, so the last 18 entries wrap to the start
        uint[][] tables = [_p, _s0, _s1, _s2, _s3];
        uint At(int i) => buf[i & (size - 1)];
        int o = 0;
        foreach (var table in tables)
            for (int j = 0; j < table.Length; j++, o += 4)
                table[j] = At(o) << 24 | At(o + 1) << 16 | At(o + 2) << 8 | At(o + 3);

        int keyPos = 0;
        for (int i = 0; i < 18; i++)
        {
            uint x = 0;
            for (int j = 0; j < 4; j++) x = (x << 8) | key[keyPos++ % key.Length];
            _p[i] ^= x;
        }

        uint l = 0, r = 0;
        foreach (var table in tables)
            for (int j = 0; j < table.Length; j += 2)
            {
                var lr = EncryptBlock(l, r);
                table[j] = l = (uint)lr;
                table[j + 1] = r = (uint)(lr >> 32);
            }
    }
}
