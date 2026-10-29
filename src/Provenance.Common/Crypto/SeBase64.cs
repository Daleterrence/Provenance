namespace Provenance.Common.Crypto;

// SE's Base64: shuffled alphabet, '@' and '_' instead of '+' and '/', never padded.
// Normal Base64 was too easy, I guess?
// polcore.dll at 0x10007A60 (encode) and 0x100078A0 (decode), alphabet at +0x65de0
public static class SeBase64
{
    public const string Alphabet =
        "TSG8IncW3HFKokOg79qzeCmZs2yBYEQVAUxR5rbwi4P@jMDLtpvad0f_J1hlN6uX";

    private static readonly byte[] DecodeTable = BuildDecodeTable();

    private static byte[] BuildDecodeTable()
    {
        var t = new byte[256];
        Array.Fill(t, (byte)0xFF);
        for (int i = 0; i < Alphabet.Length; i++) t[Alphabet[i]] = (byte)i;
        return t;
    }

    public static string Encode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return string.Empty;
        int tail = data.Length % 3;
        var buf = new char[data.Length / 3 * 4 + (tail == 0 ? 0 : tail + 1)];
        int oi = 0;
        for (int si = 0; si < data.Length; si += 3)
        {
            int n = Math.Min(3, data.Length - si);
            uint v = (uint)data[si] << 16;
            if (n > 1) v |= (uint)data[si + 1] << 8;
            if (n > 2) v |= data[si + 2];
            for (int k = 0; k <= n; k++) buf[oi++] = Alphabet[(int)((v >> (18 - 6 * k)) & 0x3F)];
        }
        return new string(buf);
    }

    public static byte[] Decode(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty) return [];
        int tail = s.Length % 4;
        if (tail == 1)
            throw new FormatException("SE-Base64 input length leaves 1 trailing char (impossible)");

        var buf = new byte[s.Length / 4 * 3 + (tail == 0 ? 0 : tail - 1)];
        int oi = 0;
        for (int si = 0; si < s.Length; si += 4)
        {
            int n = Math.Min(4, s.Length - si);
            uint v = 0;
            for (int k = 0; k < n; k++) v |= (uint)Lookup(s[si + k]) << (18 - 6 * k);
            for (int k = 0; k < n - 1; k++) buf[oi++] = (byte)(v >> (16 - 8 * k));
        }
        return buf;
    }

    private static byte Lookup(char c)
    {
        byte v = DecodeTable[c & 0xFF];
        if (v == 0xFF)
            throw new FormatException($"Character '{c}' (0x{(int)c:X2}) is not in the SE-Base64 alphabet");
        return v;
    }
}
