using System.Net;
using System.Net.Http;
using Serilog;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Provenance.Wpf.Services;

// betas go on the beta channel, the rest on stable. A beta copy reads both, so it moves up to a newer stable
// release (Velopack never offers an older one)
internal sealed class ChannelSource(string url, SemanticVersion? installed, IFileDownloader? downloader = null)
    : SimpleWebSource(url, downloader)
{
    public const string Stable = "stable";
    public const string Beta = "beta";

    // the channel Velopack passes in is the one this copy was packed with, the version already says the same
    public override async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
                                                                 Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        if (installed?.IsPrerelease != true)
            return await base.GetReleaseFeed(logger, appId, Stable, stagingId, latestLocalRelease);
        var feeds = await Task.WhenAll(StableOrNothing(logger, appId, stagingId, latestLocalRelease),
                                       base.GetReleaseFeed(logger, appId, Beta, stagingId, latestLocalRelease));
        return new VelopackAssetFeed { Assets = [.. feeds.SelectMany(f => f.Assets)] };
    }

    // the stable feed can be missing while there are only betas. Any other missing feed means the server's wrong, so that fails the check
    private async Task<VelopackAssetFeed> StableOrNothing(IVelopackLogger logger, string? appId, Guid? stagingId,
                                                          VelopackAsset? latestLocalRelease)
    {
        try { return await base.GetReleaseFeed(logger, appId, Stable, stagingId, latestLocalRelease); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            Log.Information("No stable release on the update server yet");
            return new VelopackAssetFeed();
        }
    }
}
