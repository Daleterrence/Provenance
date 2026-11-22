using System.Text.RegularExpressions;

namespace Provenance.Common.Logging;

public static partial class UserNames
{
    public const string Hidden = "<user>";

    private static readonly string[] NobodysFolders = ["Public", "Default", "Default User", "All Users"];
    private static readonly Regex? Own = OwnFolder(AppPaths.UserProfile);

    public static string Hide(string text) => Hide(text, Own);

    internal static string Hide(string text, string home) => Hide(text, OwnFolder(home));

    private static string Hide(string text, Regex? own)
    {
        if (!text.Contains('\\') && !text.Contains('/')) return text;
        if (own is not null) text = own.Replace(text, "${above}" + Hidden);
        return InUsers().Replace(text, HideName);
    }

    // a user folder that's been moved off C:\Users has nothing else to know it by
    private static Regex? OwnFolder(string home)
    {
        var parts = home.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        string above = string.Join(@"[\\/]{1,2}", parts[..^1].Select(Regex.Escape));
        return new Regex($@"(?<above>{above}[\\/]{{1,2}}){Regex.Escape(parts[^1])}(?![\w-])(?!\.\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string HideName(Match found)
    {
        string name = found.Groups["name"].Value, kept = name.TrimEnd('.', '\'', ')', ']');
        if (kept.Length == 0 || NobodysFolders.Contains(kept, StringComparer.OrdinalIgnoreCase)) return found.Value;
        return found.Groups["root"].Value + Hidden + name[kept.Length..];
    }

    // the name after X:\Users\ runs to a closing quote mark, the next slash, or three words when neither's there.
    // taking a word too many beats leaving one behind
    [GeneratedRegex("""
        (?<root>(?:(?<![\w.])[a-z]:|(?<![\w.:\\/])[\\/]{2}[^\\/\s]{1,64})[\\/]{1,2}Users[\\/]{1,2})
        (?:(?<name>[^\\/:*?"<>|\r\n]{1,40}?)(?='(?:[\s.,;)]|\z))
          |(?<name>[^\\/:*?"<>|\s]{1,40}(?:\x20[^\\/:*?"<>|\s]{1,40}){0,2}?)(?=[\\/])
          |(?<name>[^\\/:*?"<>|\s,;]{1,40}(?:\x20[^\\/:*?"<>|\s,;]{1,40}){0,2}))
        """, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex InUsers();
}
