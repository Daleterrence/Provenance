using System.Buffers.Binary;
using System.Text;
using Provenance.Common.Patcher;

namespace Provenance.Common.Social;

// POL's chat language filter: the viewer's own word list (data\dic\vulgar2.dic) run the way polcore runs it (svc 0x5a8
// builds it, svc 0x1660 masks). Matching is on the Shift-JIS bytes, a masked character shows as one '?'
public sealed class PolLanguageFilter
{
    private const byte Mask = (byte)'?';

    private readonly byte[] _d;
    private readonly Trie _words, _allowed;

    // two tries: +0x4a30 is the words to mask, +0x4a34 the ones that contain a word but are fine
    private readonly record struct Trie(int At, int Child, int Keys, int Tails, int Nodes, bool Table);

    private PolLanguageFilter(byte[] d)
    {
        _d = d;
        _words = TrieAt(BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x4a30)));
        _allowed = TrieAt(BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x4a34)));
    }

    private Trie TrieAt(int at)
    {
        if (at < 0 || at > _d.Length - 0x48f4) throw new InvalidDataException("vulgar2.dic has a word list outside the file");
        int Rel(int k) => at + 0x48f4 + BinaryPrimitives.ReadInt32LittleEndian(_d.AsSpan(at + k));
        return new Trie(at, Rel(0), Rel(4), Rel(8), Rel(12), BinaryPrimitives.ReadInt32LittleEndian(_d.AsSpan(at + 0x10)) != 0);
    }

    // null when the install hasn't got one, or it doesn't decrypt
    public static PolLanguageFilter? Load(string viewerDir)
    {
        try
        {
            var path = Path.Combine(viewerDir, "data", "dic", "vulgar2.dic");
            return File.Exists(path) && new FileInfo(path).Length < 4 * 1024 * 1024 ? FromFile(File.ReadAllBytes(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static PolLanguageFilter? FromFile(byte[] file)
    {
        // POL's file cipher under the key 01 00 00 00 00 00 00 00
        if (new PolFileCipher([1, 0, 0, 0, 0, 0, 0, 0]).Decrypt(file) is not { } r || r.Data.Length < 0x4a38) return null;
        try { return new PolLanguageFilter(r.Data); }
        catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException) { return null; }
    }

    public string Apply(string text)
    {
        int end = text.IndexOf('\0');
        if (end < 0) end = text.Length;
        if (end == 0) return text;
        var (s, at) = ToBytes(text, end);
        var plain = (byte[])s.Clone();
        try { MaskWords(s, at[end]); }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException) { return text; }
        var sb = new StringBuilder(text.Length);
        bool any = false;
        for (int i = 0; i < end; i++)
        {
            bool hit = false;
            for (int k = at[i]; k < at[i + 1]; k++) hit |= s[k] != plain[k];
            sb.Append(hit ? '?' : text[i]);
            any |= hit;
        }
        return any ? sb.Append(text, end, text.Length - end).ToString() : text;
    }

    // FUN_1004e4d0 in the viewer's mode (4, mask)
    private void MaskWords(byte[] s, int n)
    {
        int i = 0;
        while (i < n)
        {
            int k = Skip(s, i);
            if (k > 0)
            {
                i += k;
                continue;
            }
            if (Match(_allowed, s, i, n - i, out int m) < 0)
            {
                i += m + (Double(s, i + m) ? 2 : 1);
                continue;
            }
            int r = Match(_words, s, i, n - i, out m);
            if (r >= 0)
            {
                i += 1 + r;
                continue;
            }
            // SE's length: what the walk took, plus the size of the character it stopped at (not the first one)
            int len = m + (Double(s, i + m) ? 1 : 0) + 1;
            MaskRange(s, i, i + len);
            i += len;
        }
    }

    // mode 2: every letter in it becomes one mask byte, separators stay
    private void MaskRange(byte[] s, int p, int end)
    {
        while (p < end)
        {
            byte b0 = s[p];
            if (!Double(s, p))
            {
                if (!Bit(0x10, b0)) s[p] = Mask;
                p += 1;
                continue;
            }
            if (!(Bit(0x30, s[p + 1]) && Bit(0x10, b0)))
            {
                s[p] = Mask;
                s[p + 1] = 0;
            }
            p += 2;
        }
    }

    // FUN_1004e760: < 0 for a word, m is the bytes after its first character. 1 for a two-byte character no word
    // starts with
    private int Match(Trie t, byte[] s, int p, int len, out int m)
    {
        m = 0;
        int node, step;
        bool two = len >= 2 && Double(s, p);
        int c = Norm(s, p, t.Table);
        if (!two)
        {
            node = U16(t.At + 0x46f4 + (c & 0xffff) * 2);
            if (node == 0) return 0;
            step = 1;
        }
        else
        {
            node = U16(t.At + 0x14 + (_d[0x50 + (c & 0xff)] + _d[0x150 + ((c >> 8) & 0xff)] * 0x30) * 2);
            if (node == 0) return 1;
            step = 2;
        }
        int r = Walk(t, s, p + step, len - step, t.Nodes + node * 8, ref m, 0);
        return r == 0 ? (two ? 1 : 0) : r;
    }

    // FUN_1004e8e0: type 1 nodes branch on one character, type 2 hold the rest of a word (bytes negated)
    private int Walk(Trie t, byte[] s, int p, int len, int node, ref int m, int depth)
    {
        if (depth > 512) return 0;
        while (_d[node] == 1)
        {
            int i = 0, k = Skip(s, p);
            while (k > 0)
            {
                i += k;
                if (len <= i) return Miss(ref m);
                k = Skip(s, p + i);
            }
            ushort c = (ushort)Norm(s, p + i, t.Table);
            i += c < 0x100 ? 1 : 2;
            int idx = Find(c, t.Keys + BinaryPrimitives.ReadInt32LittleEndian(_d.AsSpan(node + 4)) * 2, _d[node + 1] - 1);
            if (idx < 0) return Miss(ref m);
            int child = U16(node + 2), next = child == 0 ? 0 : U16(t.Child + (child + idx) * 2);
            m += i;
            if (next == 0) return -1;
            len -= i;
            p += i;
            node = t.Nodes + next * 8;
        }
        if (_d[node] != 2) return 0;
        int tail = t.Tails + BinaryPrimitives.ReadInt32LittleEndian(_d.AsSpan(node + 4)), n = 0;
        while (_d[tail + n] != 0) n++;
        if (n > 0xfe) return Miss(ref m);
        var w = new byte[n + 2];
        for (int j = 0; j < n; j++) w[j] = (byte)-_d[tail + j];
        int at = 0, used = 0;
        while (w[at] != 0)
        {
            if (len <= used) return Miss(ref m);
            int k = Skip(s, p + used);
            if (k > 0)
            {
                used += k;
                continue;
            }
            if ((ushort)Norm(w, at, t.Table) != (ushort)Norm(s, p + used, t.Table)) return Miss(ref m);
            at += Double(w, at) ? 2 : 1;
            used += Double(s, p + used) ? 2 : 1;
        }
        m += used;
        int after = U16(node + 2), more = after == 0 ? 0 : U16(t.Child + after * 2);
        return more == 0 ? -1 : Walk(t, s, p + used, len - used, t.Nodes + more * 8, ref m, depth + 1);
    }

    private static int Miss(ref int m)
    {
        m = 0;
        return 0;
    }

    // FUN_1004ec00, over keys stored negated, compared as unsigned 16-bit
    private int Find(ushort key, int keys, int hi)
    {
        int lo = 0;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if ((ushort)-U16(keys + mid * 2) < key) lo = mid + 1;
            else hi = mid;
        }
        return (ushort)-U16(keys + lo * 2) == key ? lo : -1;
    }

    // FUN_1004e6e0: the bytes a separator (space, punctuation) takes up, 0 for a letter
    private int Skip(byte[] s, int p)
    {
        if (Double(s, p)) return Bit(0x30, s[p + 1]) && Bit(0x10, s[p]) ? 2 : 0;
        return Bit(0x10, s[p]) ? 1 : 0;
    }

    // FUN_1004e850: a single byte through +0x4930 (A-Z to a-z), a double one through +0x250 when the trie wants it
    private int Norm(byte[] s, int p, bool table)
    {
        if (!Double(s, p)) return _d[0x4930 + s[p]];
        return table ? U16(0x250 + (_d[0x50 + s[p]] + _d[0x150 + s[p + 1]] * 0x30) * 2) : s[p] | s[p + 1] << 8;
    }

    private bool Double(byte[] s, int p) => _d[0x50 + s[p]] != 0 && _d[0x150 + s[p + 1]] != 0;
    private bool Bit(int table, byte b) => ((_d[table + (b >> 3)] >> (b & 7)) & 1) != 0;
    private int U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(at));

    // polcore's own conversion (FUN_1004d4d0): ASCII as is, Shift-JIS rows 0x81-0x9f and 0xe0-0xea, SE's row 0x85 for
    // Windows-1252's top half, '?' for the rest. Where a character's in two rows the later one wins, like polcore's
    // search. at[i] is where text[i] starts, at[end] the length, and there's a zero pad on the end for the lookahead
    internal static (byte[] Bytes, int[] At) ToBytes(string text, int end)
    {
        var table = Sjis.Value;
        var s = new byte[end * 2 + 2];
        var at = new int[end + 1];
        int n = 0;
        for (int i = 0; i < end; i++)
        {
            at[i] = n;
            char c = text[i];
            if (c < 0x80) s[n++] = (byte)c;
            else if (table.TryGetValue(c, out var v))
            {
                s[n++] = (byte)(v >> 8);
                s[n++] = (byte)v;
            }
            else s[n++] = (byte)'?';
        }
        at[end] = n;
        return (s, at);
    }

    private static readonly Lazy<Dictionary<char, ushort>> Sjis = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        // the default (best fit) decoder, which is the only way .NET gives the NEC row's copies of nine maths symbols.
        // Anything it hasn't got comes out as U+30FB, which is only real at 0x8145
        var cp932 = Encoding.GetEncoding(932);
        var cp1252 = Encoding.GetEncoding(1252, EncoderFallback.ReplacementFallback, new DecoderReplacementFallback("\uffff"));
        var map = new Dictionary<char, ushort>();
        var pair = new byte[2];
        var one = new char[2];
        foreach (int lead in Enumerable.Range(0x81, 0x1f).Concat(Enumerable.Range(0xe0, 0x0b)))
        {
            if (lead == 0x85)
            {
                // SE's row, not cp932's: 0x80-0xbe at trail b - 0x40, 0xbf at 0x80, 0xc0-0xff at b - 0x21
                for (int b = 0x80; b <= 0xff; b++)
                {
                    pair[0] = (byte)b;
                    if (cp1252.GetChars(pair, 0, 1, one, 0) == 1 && one[0] != '\uffff' && one[0] is not (>= '\u0080' and <= '\u009f'))
                        map[one[0]] = (ushort)(0x8500 | (b <= 0xbe ? b - 0x40 : b == 0xbf ? 0x80 : b - 0x21));
                }
                continue;
            }
            for (int trail = 0x40; trail <= 0xfc; trail++)
            {
                if (trail == 0x7f) continue;
                pair[0] = (byte)lead;
                pair[1] = (byte)trail;
                if (cp932.GetChars(pair, 0, 2, one, 0) == 1 && (one[0] != '\u30fb' || (lead, trail) == (0x81, 0x45)))
                    map[one[0]] = (ushort)(lead << 8 | trail);
            }
        }
        return map;
    });
}
