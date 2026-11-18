using System.Buffers.Binary;

namespace Provenance.Common.Social;

// SE's own take on Blowfish, so no library will do: key bytes are sign-extended and the round function's mangled
internal sealed class SearchCipher
{
    private readonly uint[] _p = SearchCipherTable.Words[..18];
    private readonly uint[] _s = SearchCipherTable.Words[18..];

    public SearchCipher(ReadOnlySpan<byte> key)
    {
        int j = 0;
        for (int i = 0; i < 18; i++)
        {
            uint word = 0;
            for (int k = 0; k < 4; k++)
            {
                word = (word << 8) | (uint)(int)(sbyte)key[j];
                j = (j + 1) % key.Length;
            }
            _p[i] ^= word;
        }
        uint l = 0, r = 0;
        for (int i = 0; i < 18; i += 2)
        {
            Encrypt(ref l, ref r);
            _p[i] = l;
            _p[i + 1] = r;
        }
        for (int i = 0; i < 1024; i += 2)
        {
            Encrypt(ref l, ref r);
            _s[i] = l;
            _s[i + 1] = r;
        }
    }

    private uint Round(uint x) =>
        (((_s[256 + (int)((x >> 8) & 0xff)] & 1) ^ 32) + ((_s[768 + (int)(x >> 24)] & 1) ^ 32)) + _s[512 + (int)((x >> 16) & 0xff)] + _s[(int)(x & 0xff)];

    private void Encrypt(ref uint left, ref uint right)
    {
        uint l = left, r = right;
        for (int i = 0; i < 16; i++)
        {
            l ^= _p[i];
            r ^= Round(l);
            (l, r) = (r, l);
        }
        left = r ^ _p[17];
        right = l ^ _p[16];
    }

    private void Decrypt(ref uint left, ref uint right)
    {
        uint l = left, r = right;
        for (int i = 17; i > 1; i--)
        {
            l ^= _p[i];
            r ^= Round(l);
            (l, r) = (r, l);
        }
        left = r ^ _p[0];
        right = l ^ _p[1];
    }

    // whole 8 byte blocks from start, little endian, in place. A tail shorter than a block is left alone
    public void Run(Span<byte> bytes, int start, int length, bool decrypt)
    {
        for (int at = start; at + 8 <= start + length; at += 8)
        {
            uint l = BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
            uint r = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 4)..]);
            if (decrypt) Decrypt(ref l, ref r);
            else Encrypt(ref l, ref r);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[at..], l);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[(at + 4)..], r);
        }
    }
}
