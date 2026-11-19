using Provenance.Common.PolFormat;

namespace Provenance.Common.Feeds;

public static class PlayOnlineSite
{
    private static string RegionToken(PolRegion r) =>
        r switch { PolRegion.Europe => "ff11eu", PolRegion.Japan => "ff11", _ => "ff11us" };

    public static string ServicePage(PolRegion region) => $"https://www.playonline.com/{RegionToken(region)}/service/";

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
}
