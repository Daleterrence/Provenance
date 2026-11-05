using System.Text;

namespace Provenance.Common.PolFormat;

// what POL's Shift-JIS can carry. SE's table is cp932 bar a few odd spots, which is near enough to keep emoji and the
// like out before the hook turns them into geta marks
public static class ShiftJis
{
    private static readonly Encoding Cp932;

    static ShiftJis()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp932 = Encoding.GetEncoding(932);
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
