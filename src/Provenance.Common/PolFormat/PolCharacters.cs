using System.Text;

namespace Provenance.Common.PolFormat;

public static class PolCharacters
{
    public const string CantSend = "PlayOnline can't send some of those characters.";
    public const string LeftOut = "PlayOnline can't send some of those characters, so they were left out.";

    // a bit per UTF-16 unit polcore's table turns into real Shift-JIS, the same in every region.
    // anything else ends up as geta marks at best, so it's never sent
    private static readonly byte[] Table = Load();

    public static bool Carries(char c) => (Table[c >> 3] & (1 << (c & 7))) != 0;

    public static bool Allowed(char c, bool newlines) => c is '\r' or '\n' ? newlines : c >= ' ' && c != '\x7f' && Carries(c);

    public static bool All(string text) => FullWidth(text).All(c => Allowed(c, newlines: false));

    // the viewer's own way with half-width katakana (polcore service 0x1714): full-width, and a voicing mark after one joins it
    public static string FullWidth(string text)
    {
        int i = text.AsSpan().IndexOfAnyInRange('\uFF61', '\uFF9F');
        if (i < 0) return text;
        var made = new StringBuilder(text, 0, i, text.Length);
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (c is < '\uFF61' or > '\uFF9F')
            {
                made.Append(c);
                continue;
            }
            char next = i + 1 < text.Length ? text[i + 1] : '\0';
            char joined = next == '\uFF9E' ? Joined(Voiced, c) : next == '\uFF9F' ? Joined(SemiVoiced, c) : '\0';
            if (joined != '\0')
            {
                made.Append(joined);
                i++;
            }
            else made.Append(FullForms[c - '\uFF61']);
        }
        return made.ToString();
    }

    public static string Keep(string text, bool newlines) => Keep(text, newlines, out _);

    public static string Keep(string text, bool newlines, out bool dropped) => Filter(text, c => Allowed(c, newlines), out dropped);

    // for request writers: their own cleaning turns control characters into spaces, so those are left to it
    public static string Sendable(string text) => Filter(text, c => c < ' ' || (c != '\x7f' && Carries(c)), out _);

    private static string Filter(string text, Func<char, bool> keep, out bool dropped)
    {
        dropped = false;
        text = FullWidth(text);
        StringBuilder? kept = null;
        for (int i = 0; i < text.Length; i++)
        {
            if (keep(text[i]))
            {
                kept?.Append(text[i]);
                continue;
            }
            dropped = true;
            kept ??= new StringBuilder(text, 0, i, text.Length);
        }
        return kept?.ToString() ?? text;
    }

    private const string FullForms =
        "\u3002\u300C\u300D\u3001\u30FB\u30F2\u30A1\u30A3\u30A5\u30A7\u30A9\u30E3\u30E5\u30E7\u30C3\u30FC" +
        "\u30A2\u30A4\u30A6\u30A8\u30AA\u30AB\u30AD\u30AF\u30B1\u30B3\u30B5\u30B7\u30B9\u30BB\u30BD\u30BF" +
        "\u30C1\u30C4\u30C6\u30C8\u30CA\u30CB\u30CC\u30CD\u30CE\u30CF\u30D2\u30D5\u30D8\u30DB\u30DE\u30DF" +
        "\u30E0\u30E1\u30E2\u30E4\u30E6\u30E8\u30E9\u30EA\u30EB\u30EC\u30ED\u30EF\u30F3\u309B\u309C";
    private const string Voiced =
        "\uFF73\u30F4\uFF76\u30AC\uFF77\u30AE\uFF78\u30B0\uFF79\u30B2\uFF7A\u30B4\uFF7B\u30B6\uFF7C\u30B8" +
        "\uFF7D\u30BA\uFF7E\u30BC\uFF7F\u30BE\uFF80\u30C0\uFF81\u30C2\uFF82\u30C5\uFF83\u30C7\uFF84\u30C9" +
        "\uFF8A\u30D0\uFF8B\u30D3\uFF8C\u30D6\uFF8D\u30D9\uFF8E\u30DC";
    private const string SemiVoiced = "\uFF8A\u30D1\uFF8B\u30D4\uFF8C\u30D7\uFF8D\u30DA\uFF8E\u30DD";

    private static char Joined(string pairs, char c)
    {
        for (int i = 0; i < pairs.Length; i += 2)
            if (pairs[i] == c) return pairs[i + 1];
        return '\0';
    }

    private static byte[] Load()
    {
        using var s = typeof(PolCharacters).Assembly.GetManifestResourceStream("Provenance.Common.PolFormat.PolCharacters.bin")
                      ?? throw new InvalidOperationException("The table of characters PlayOnline can carry is missing from the build.");
        var table = new byte[0x10000 / 8];
        s.ReadExactly(table);
        return table;
    }
}
