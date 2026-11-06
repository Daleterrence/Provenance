using System.Globalization;
using System.Text;

namespace Provenance.Common.Game.ThirdParty;

// what a row on the Addons and plugins page shows. Each platform's own bits sit next to it, not in it
public sealed record AddonListing(string Key, string Name, string? Version, string? Author, string? Description, string? Location)
{
    public static AddonListing Named(string key) => new(key, key, null, null, null, null);

    // no control, format or line break characters, so a name can't bring a bidi override, a zero-width space or a tag
    // character into the UI. Runes, since tags are surrogate pairs
    public static string? Clean(string? s)
    {
        if (s is null) return null;
        var sb = new StringBuilder(s.Length);
        foreach (var r in s.EnumerateRunes())
            if (!Rune.IsControl(r) && Rune.GetUnicodeCategory(r) is not (UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))
                sb.Append(r.ToString());
        var t = sb.ToString().Trim();
        if (t.Length > 500) t = char.IsHighSurrogate(t[499]) ? t[..499] : t[..500];
        return t.Length == 0 ? null : t;
    }
}
