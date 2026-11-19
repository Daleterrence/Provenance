namespace Provenance.Wpf.Controls;

public static class NoBreak
{
    private const char Space = (char)0xA0;
    private const int LongestQuoted = 40;

    private static readonly string[] Names =
    [
        "PlayOnline Viewer", "FINAL FANTASY XI", "Square Enix", "Log In Automatically", "Start Game Automatically",
    ];

    public static string Glue(string text)
    {
        foreach (var name in Names)
            if (text.Contains(name, StringComparison.Ordinal)) text = text.Replace(name, name.Replace(' ', Space), StringComparison.Ordinal);
        int open = text.IndexOf('"');
        while (open >= 0)
        {
            int close = text.IndexOf('"', open + 1);
            if (close < 0) break;
            if (close - open - 1 <= LongestQuoted) text = string.Concat(text.AsSpan(0, open), text.AsSpan(open, close - open + 1).ToString().Replace(' ', Space), text.AsSpan(close + 1));
            open = text.IndexOf('"', close + 1);
        }
        return text;
    }
}
