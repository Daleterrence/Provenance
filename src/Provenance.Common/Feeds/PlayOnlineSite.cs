using Provenance.Common.PolFormat;

namespace Provenance.Common.Feeds;

// POL's site addresses, and the feed worker's category and badge keys
public static class PlayOnlineSite
{
    private static string RegionToken(PolRegion r) =>
        r switch { PolRegion.Europe => "ff11eu", PolRegion.Japan => "ff11", _ => "ff11us" };

    public static string ServicePage(PolRegion region) => $"https://www.playonline.com/{RegionToken(region)}/service/";

    // https on POL's own site only. A feed doesn't get to make this PC fetch from anywhere else
    public static bool IsPlayOnlineUrl(Uri u) =>
        u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.Equals("playonline.com", StringComparison.OrdinalIgnoreCase) ||
         u.Host.EndsWith(".playonline.com", StringComparison.OrdinalIgnoreCase));

    public static Uri? UpgradeBannerUrl(Uri u)
    {
        if (!u.IsAbsoluteUri || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)) return null;
        var https = u.Scheme == Uri.UriSchemeHttps ? u : new UriBuilder(u) { Scheme = Uri.UriSchemeHttps, Port = u.IsDefaultPort ? -1 : u.Port }.Uri;
        return IsPlayOnlineUrl(https) ? https : null;
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
