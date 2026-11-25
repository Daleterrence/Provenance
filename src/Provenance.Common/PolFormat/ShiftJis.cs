using System.Text;

namespace Provenance.Common.PolFormat;

public static class ShiftJis
{
    // an unmapped char counts as 2 bytes so one never slips under the limit, an emoji counts 4 in case polcore stands in for each half
    private static readonly Encoding Counting = Encoding.GetEncoding(932, new EncoderReplacementFallback("??"), DecoderFallback.ReplacementFallback);
    private static readonly Encoding Strict = Encoding.GetEncoding(932, new EncoderReplacementFallback(""), DecoderFallback.ReplacementFallback);

    public static int ByteCount(string text) => Counting.GetByteCount(text.Replace("\r", ""));

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
}
