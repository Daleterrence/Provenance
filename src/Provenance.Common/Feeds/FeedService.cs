using Provenance.Common.Settings;
using Serilog;
using Provenance.Common.PolFormat;

namespace Provenance.Common.Feeds;

// the feed worker keeps one edge-cached copy for every launcher, so POL serves each page about once per cache period
public sealed class FeedService(ISettingsService settings, FeedClient? client = null)
{
    public const string Worker = "https://news.provenancexi.app";

    private readonly FeedClient _client = client ?? new();
    private PolRegion? _loadedRegion;

    public event EventHandler? RegionChanged;

    public PolRegion Region => Resolve(settings.Current.FeedRegion, settings.DetectedRegion);

    public static PolRegion Resolve(string? choice, PolRegion? installed) =>
        (choice ?? "auto").Trim().ToLowerInvariant() switch
        {
            "na" => PolRegion.UnitedStates,
            "eu" => PolRegion.Europe,
            "jp" => PolRegion.Japan,
            _ => installed ?? PolRegion.UnitedStates,
        };

    public void NotifyRegionSettingChanged()
    {
        var region = Region;
        if (_loadedRegion == region) return;
        Log.Information("Feed region changed to {Region}", FeedClient.Code(region).ToUpperInvariant());
        RegionChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<(FeedResult news, FeedResult status)> LoadAsync(CancellationToken ct = default)
    {
        var region = Region;
        _loadedRegion = region;
        Log.Information("Loading feeds for {Region} from {Source}", FeedClient.Code(region).ToUpperInvariant(), Worker);
        var news = _client.GetAsync(region, FeedKind.News, Worker, ct);
        var status = _client.GetAsync(region, FeedKind.Status, Worker, ct);
        await Task.WhenAll(news, status);
        return (news.Result, status.Result);
    }

    public Task<FeedFetch<WorldStatus>> LoadWorldsAsync(CancellationToken ct = default) =>
        _client.GetWorldsAsync(Region, Worker, ct);

    // the window never loads a URL itself, so it never fetches from a host other than POL's
    public async Task<IReadOnlyList<FeedItem>> WithLocalImagesAsync(IReadOnlyList<FeedItem> items) =>
        await Task.WhenAll(items.Select(async it =>
            it.ImageUrl is not { Length: > 0 } img ? it
                : it with { ImageUrl = await BannerImages.LocalAsync(img).ConfigureAwait(false) })).ConfigureAwait(false);

    public string ServicePageUrl => PlayOnlineSite.ServicePage(Region);
}
