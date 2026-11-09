namespace Provenance.Wpf.ViewModels.Shared;

internal static class TextBits
{
    internal static string Seconds(TimeSpan t) => t.TotalSeconds < 60
        ? $"{t.TotalSeconds:0}s"
        : $"{(int)t.TotalMinutes}m {t.Seconds:00}s";

    internal static string Preview(string text, int max)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        if (text.Length <= max) return text;
        var cut = text.LastIndexOf(' ', max);
        return (cut > max / 2 ? text[..cut] : text[..max]).TrimEnd() + "...";
    }

    internal static string OneLine(string s) => s.Replace("\r\n", " ").Replace('\n', ' ');
}
