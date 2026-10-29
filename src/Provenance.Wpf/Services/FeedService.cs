using Serilog;
using Provenance.Common.Feeds;
using Provenance.Common.PolFormat;

namespace Provenance.Wpf.Services;

// the feed worker keeps one edge-cached copy for every launcher, so POL serves each page about once per cache period
public sealed class FeedService(ISettingsService settings)
{
    public const string Worker = "https://news.provenancexi.app";

    private readonly FeedClient _client = new();
    private FeedRegion? _loadedRegion;

    public event EventHandler? RegionChanged;

    public FeedRegion Region => Resolve(settings.Current.FeedRegion, settings.DetectedRegion);

    public static FeedRegion Resolve(string? choice, PolRegion? installed) =>
        (choice ?? "auto").Trim().ToLowerInvariant() switch
        {
            "na" => FeedRegion.NA,
            "eu" => FeedRegion.EU,
            "jp" => FeedRegion.JP,
            _ => installed switch
            {
                PolRegion.Europe => FeedRegion.EU,
                PolRegion.Japan => FeedRegion.JP,
                _ => FeedRegion.NA,
            },
        };

    public void NotifyRegionSettingChanged()
    {
        var region = Region;
        if (_loadedRegion == region) return;
        Log.Information("Feed region changed to {Region}", region);
        RegionChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<(FeedResult news, FeedResult status)> LoadAsync(CancellationToken ct = default)
    {
        var region = Region;
        _loadedRegion = region;
        Log.Information("Loading feeds for {Region} from {Source}", region, Worker);
        var news = _client.GetAsync(region, FeedKind.News, Worker, ct);
        var status = _client.GetAsync(region, FeedKind.Status, Worker, ct);
        await Task.WhenAll(news, status);
        return (news.Result, status.Result);
    }

    public Task<IReadOnlyList<WorldStatus>> LoadWorldsAsync(CancellationToken ct = default) =>
        _client.GetWorldsAsync(Region, Worker, ct);

    // the window never loads a URL itself, so it never fetches from a host other than POL's
    public async Task<IReadOnlyList<FeedItem>> WithLocalImagesAsync(IReadOnlyList<FeedItem> items) =>
        await Task.WhenAll(items.Select(async it =>
            it.ImageUrl is not { Length: > 0 } img ? it
                : it with { ImageUrl = await _client.LocalImageAsync(img).ConfigureAwait(false) })).ConfigureAwait(false);

    public string ServicePageUrl => FeedClient.ServicePage(Region);
}
