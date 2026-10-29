using System.Net;
using System.Text.RegularExpressions;

namespace Provenance.Common.Feeds;

public enum InfoCategory { None, Important, Maintenance, General, Events, Updates }

public enum InfoBadge { None, Outage, Maintenance, Recovery, FollowUp }

public sealed record WorldStatus(string Name, string Status)
{
    public string Label => Status switch
    {
        "up" => "Up",
        "partially-down" => "Partially down",
        "down" => "Down",
        "partial-maintenance" => "Partial maintenance",
        "maintenance" => "Maintenance",
        _ => "Unknown",
    };

    public bool IsUp => Status == "up";
}

// scraped the same way the feed worker does it (regex on html, I know, but it's the site's own markup or nothing)
public static class PlayOnlineInfo
{
    public const string WorldsUrl = "https://www.playonline.com/pcd/service/ff11index.html";

    public static string[] InfoUrls(string regionToken) =>
    [
        $"https://www.playonline.com/{regionToken}/info/info_top.shtml",
        $"https://www.playonline.com/{regionToken}/info/list_mnt.shtml",
    ];

    public static string ServicePage(string regionToken) => $"https://www.playonline.com/{regionToken}/service/";

    private static readonly Dictionary<string, InfoCategory> SectionImage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["infctl01.gif"] = InfoCategory.Important,
        ["infctl02.gif"] = InfoCategory.Maintenance, ["infctl021.gif"] = InfoCategory.Maintenance,
        ["infctl022.gif"] = InfoCategory.Maintenance, ["infstl01.gif"] = InfoCategory.Maintenance,
        ["infstl02.gif"] = InfoCategory.Maintenance,
        ["infctl03.gif"] = InfoCategory.General,
        ["infctl04.gif"] = InfoCategory.Events,
        ["infctl05.gif"] = InfoCategory.Updates,
    };

    private static readonly Dictionary<string, InfoBadge> BadgeImage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["infic01.gif"] = InfoBadge.Outage, ["infic02.gif"] = InfoBadge.Maintenance,
        ["infic03.gif"] = InfoBadge.Recovery, ["infic04.gif"] = InfoBadge.FollowUp,
    };

    private static readonly Dictionary<string, string> WorldStatusImage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["status_00.gif"] = "up", ["status_01.gif"] = "partially-down", ["status_02.gif"] = "down",
        ["status_03.gif"] = "partial-maintenance", ["status_04.gif"] = "maintenance",
    };

    private static readonly Regex InfoTokens = new(@"<img\b[^>]*\bsrc\s*=\s*[""']([^""']+)[""'][^>]*>|<a\b[^>]*>",
                                                    RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InfoClass = new(@"\bclass\s*=\s*[""'](?:[^""']*\s)?info(?:\s[^""']*)?[""']",
                                                   RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Href = new(@"\bhref\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex World = new(@"<h2\b[^>]*>((?:(?!</?h2\b)[\s\S])*)</h2>\s*<img\b[^>]*\bsrc\s*=\s*[""']([^""']+)[""']",
                                              RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static Uri? ToAbsolute(string? url)
    {
        url = (url ?? "").Trim();
        if (url.StartsWith("//")) url = "https:" + url;
        else if (url.StartsWith("/")) url = "https://www.playonline.com" + url;
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u : null;
    }

    // host minus www, plus the path, lower case, so http and https links match
    public static string? LinkKey(string? url)
    {
        if (ToAbsolute(url) is not { } u) return null;
        var host = u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;
        return (host + u.AbsolutePath).ToLowerInvariant();
    }

    // posts sit under section header and badge images, first one wins
    public static Dictionary<string, (InfoCategory Category, InfoBadge Badge)> ParseCategories(string html)
    {
        var map = new Dictionary<string, (InfoCategory, InfoBadge)>();
        InfoCategory section = InfoCategory.None;
        InfoBadge badge = InfoBadge.None;
        foreach (Match m in InfoTokens.Matches(html))
        {
            if (m.Groups[1].Success)
            {
                var file = m.Groups[1].Value.Split('/').Last();
                if (Regex.IsMatch(file, @"^inf(ctl|stl)\d+\.gif$", RegexOptions.IgnoreCase))
                {
                    section = SectionImage.TryGetValue(file, out var c) ? c : InfoCategory.None;
                    badge = InfoBadge.None;
                }
                else if (Regex.IsMatch(file, @"^infic\d+\.gif$", RegexOptions.IgnoreCase))
                    badge = BadgeImage.TryGetValue(file, out var b) ? b : InfoBadge.None;
                continue;
            }
            if (!InfoClass.IsMatch(m.Value)) continue;
            var href = Href.Match(m.Value);
            var key = href.Success ? LinkKey(WebUtility.HtmlDecode(href.Groups[1].Value)) : null;
            if (key != null && section != InfoCategory.None && !map.ContainsKey(key)) map[key] = (section, badge);
            badge = InfoBadge.None;
        }
        return map;
    }

    public static IReadOnlyList<WorldStatus> ParseWorlds(string html)
    {
        var list = new List<WorldStatus>();
        foreach (Match m in World.Matches(html))
        {
            var name = Regex.Replace(Regex.Replace(m.Groups[1].Value, @"<br\s*/?>", " ", RegexOptions.IgnoreCase), "<[^>]+>", "");
            name = Regex.Replace(WebUtility.HtmlDecode(name), @"\s+", " ").Trim();
            if (name.Length == 0) continue;
            var file = m.Groups[2].Value.Split('/').Last();
            list.Add(new WorldStatus(name, WorldStatusImage.TryGetValue(file, out var s) ? s : "unknown"));
        }
        return list;
    }

    public static InfoCategory CategoryFromKey(string? key) => key switch
    {
        "important" => InfoCategory.Important, "maintenance" => InfoCategory.Maintenance,
        "general" => InfoCategory.General, "events" => InfoCategory.Events, "updates" => InfoCategory.Updates,
        _ => InfoCategory.None,
    };

    public static InfoBadge BadgeFromKey(string? key) => key switch
    {
        "outage" => InfoBadge.Outage, "maintenance" => InfoBadge.Maintenance,
        "recovery" => InfoBadge.Recovery, "followup" => InfoBadge.FollowUp,
        _ => InfoBadge.None,
    };
}
