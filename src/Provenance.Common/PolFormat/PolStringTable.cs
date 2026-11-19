using System.Text;

namespace Provenance.Common.PolFormat;

public static class PolStringTable
{
    public const int AgreementNotice = 0x7011, AgreementText = 0x7012, AgreementArticle8 = 0x7013;

    public static IReadOnlyDictionary<int, string>? Load(string viewerDir)
    {
        foreach (var path in new[] { Path.Combine(viewerDir, "viewer", "data", "common", "StringTable.bin"),
                                     Path.Combine(viewerDir, "data", "common", "StringTable.bin") })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var raw = Unpex(File.ReadAllBytes(path));
                return raw is null ? null : Table(raw);
            }
            catch { return null; }
        }
        return null;
    }

    // "PEX\0", the plain size at +4, then LSB-first LZ77 bits from +0x10 (0 = literal, 1 = 12-bit offset + 4-bit length + 2)
    public static byte[]? Unpex(byte[] d)
    {
        if (d.Length < 0x10 || d[0] != 'P' || d[1] != 'E' || d[2] != 'X' || d[3] != 0) return null;
        int n = BitConverter.ToInt32(d, 4);
        if (n <= 0 || n > 64 * 1024 * 1024) return null;
        var buf = new byte[0x1000 + n];
        int pos = 0x1000, end = 0x1000 + n;
        long bit = 0, bits = (long)(d.Length - 0x10) * 8;
        int Read(int k)
        {
            int v = 0;
            for (int i = 0; i < k; i++, bit++)
            {
                if (bit >= bits) throw new InvalidDataException("StringTable.bin ends early");
                v |= ((d[0x10 + (int)(bit >> 3)] >> (int)(bit & 7)) & 1) << i;
            }
            return v;
        }
        while (pos < end)
        {
            if (Read(1) == 0) buf[pos++] = (byte)Read(8);
            else
            {
                int off = Read(12), len = Read(4) + 2;
                for (int i = 0; i < len && pos < end; i++, pos++)
                    buf[pos] = buf[pos - 0x1000 + off];
            }
        }
        return buf.AsSpan(0x1000, n).ToArray();
    }

    public static Dictionary<int, string> Table(byte[] raw)
    {
        var map = new Dictionary<int, string>();
        int o = 0x10;
        while (o + 4 <= raw.Length)
        {
            int size = BitConverter.ToUInt16(raw, o), id = BitConverter.ToUInt16(raw, o + 2);
            if (size == 0 || o + size > raw.Length) break;
            var text = Encoding.Unicode.GetString(raw, o + 4, (size - 4) & ~1);
            int nul = text.IndexOf('\0');
            map[id] = nul >= 0 ? text[..nul] : text;
            o += size;
        }
        return map;
    }
}
