using System.Globalization;
using System.Text.RegularExpressions;

namespace Provenance.Common.Feeds;

// "Oct. 12, 2026 22:00 to 23:30 (PDT)" style article times -> local time. A date with no time stays as it is
public static class ArticleTimes
{
    private static readonly Dictionary<string, int> ZoneMinutes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PDT"] = -7 * 60, ["PST"] = -8 * 60, ["MDT"] = -6 * 60, ["MST"] = -7 * 60,
        ["CDT"] = -5 * 60, ["CST"] = -6 * 60, ["EDT"] = -4 * 60, ["EST"] = -5 * 60,
        ["JST"] = 9 * 60, ["GMT"] = 0, ["UTC"] = 0, ["BST"] = 60, ["CET"] = 60, ["CEST"] = 120,
    };

    private static readonly string[] Months =
        ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    private const string Time = @"(\d{1,2}):(\d{2})\s*(?:([ap])\.?\s*m\.?)?";

    private static readonly Regex Pattern = new(
        @"(?:(?:Mon|Tues|Wednes|Thurs|Fri|Satur|Sun)day,\s*)?" +
        @"(?<mon>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sept?|Oct|Nov|Dec)[a-z]*\.?\s+(?<day>\d{1,2}),\s*(?<year>\d{4})" +
        @",?\s*(?:at\s+)?(?<t1>" + Time + @")" +
        @"(?:\s*(?:to|-|\u2013|~)\s*(?<t2>" + Time + @"))?" +
        @"\s*\((?<tz>[A-Z]{3,4})\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TimeOnly = new("^" + Time + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string ToLocal(string text, TimeZoneInfo? local = null, CultureInfo? culture = null)
    {
        if (string.IsNullOrEmpty(text)) return text;
        local ??= TimeZoneInfo.Local;
        culture ??= CultureInfo.CurrentCulture;
        return Pattern.Replace(text, m =>
        {
            if (!ZoneMinutes.TryGetValue(m.Groups["tz"].Value, out var offset)) return m.Value;
            int month = Array.IndexOf(Months, m.Groups["mon"].Value[..3].ToLowerInvariant()) + 1;
            if (month <= 0 || !int.TryParse(m.Groups["day"].Value, out var day) ||
                !int.TryParse(m.Groups["year"].Value, out var year)) return m.Value;
            if (Moment(year, month, day, m.Groups["t1"].Value, offset) is not { } start) return m.Value;
            var a = TimeZoneInfo.ConvertTime(start, local);
            var result = $"{a.ToString("D", culture)} {a.ToString("t", culture)}";
            if (m.Groups["t2"].Success && Moment(year, month, day, m.Groups["t2"].Value, offset) is { } end0)
            {
                // the end shares the date, so one before the start ran past midnight
                var end = end0 < start ? end0.AddDays(1) : end0;
                var b = TimeZoneInfo.ConvertTime(end, local);
                result += b.Date == a.Date
                    ? $" to {b.ToString("t", culture)}"
                    : $" to {b.ToString("D", culture)} {b.ToString("t", culture)}";
            }
            return result;
        });
    }

    private static DateTimeOffset? Moment(int year, int month, int day, string time, int offsetMinutes)
    {
        var t = TimeOnly.Match(time.Trim());
        if (!t.Success) return null;
        int h = int.Parse(t.Groups[1].Value), min = int.Parse(t.Groups[2].Value);
        if (t.Groups[3].Success)
        {
            bool pm = char.ToLowerInvariant(t.Groups[3].Value[0]) == 'p';
            if (h == 12) h = pm ? 12 : 0;
            else if (pm) h += 12;
        }
        if (h > 23 || min > 59) return null;
        try { return new DateTimeOffset(year, month, day, h, min, 0, TimeSpan.FromMinutes(offsetMinutes)); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
