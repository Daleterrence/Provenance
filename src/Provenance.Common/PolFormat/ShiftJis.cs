using System.Text;

namespace Provenance.Common.PolFormat;

// SE's table is cp932 bar a few odd spots, near enough to keep emoji and the like out
// before the hook turns them into geta marks
public static class ShiftJis
{
    private static readonly Encoding Cp932 = Encoding.GetEncoding(932);

    // an unmapped char counts as 2 bytes, so one never slips under the limit. The fallback's per UTF-16 unit,
    // so an emoji counts 4 in case polcore stands in for each half
    private static readonly Encoding Counting = Encoding.GetEncoding(932, new EncoderReplacementFallback("??"), DecoderFallback.ReplacementFallback);
    private static readonly Encoding Strict = Encoding.GetEncoding(932, new EncoderReplacementFallback(""), DecoderFallback.ReplacementFallback);

    public static int ByteCount(string text) => Counting.GetByteCount(text.Replace("\r", ""));

    // a character Shift-JIS has as two bytes (Japanese, mostly), not one it hasn't got at all
    public static bool HasDoubleByte(string text) =>
        text.Any(c => !char.IsSurrogate(c) && Strict.GetByteCount(new[] { c }) == 2);

    public static string Fit(string text, int bytes)
    {
        int used = 0, i = 0;
        while (i < text.Length)
        {
            int len = char.IsSurrogatePair(text, i) ? 2 : 1;
            int n = text[i] == '\r' ? 0 : Counting.GetByteCount(text.ToCharArray(i, len));
            if (used + n > bytes) break;
            used += n;
            i += len;
        }
        return text[..i];
    }

    // a round trip, so best-fit swaps count as not fitting too
    public static bool Fits(string text) => Cp932.GetString(Cp932.GetBytes(text)) == text;

    // drops whatever doesn't fit, a whole surrogate pair at a time
    public static string Filter(string text, out bool dropped)
    {
        dropped = false;
        if (Fits(text)) return text;
        var sb = new StringBuilder(text.Length);
        foreach (var r in text.EnumerateRunes())
        {
            var s = r.ToString();
            if (Fits(s)) sb.Append(s);
            else dropped = true;
        }
        return sb.ToString();
    }
}
