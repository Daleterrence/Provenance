using System.Globalization;
using System.Net;
using System.Text.Json;
using Provenance.Common.PolFormat;
using static Provenance.Common.JsonFields;

namespace Provenance.Common.Feeds;

// the feed worker's JSON, with the last answer it gave kept for when it can't be reached
public sealed class FeedClient
{
    private readonly HttpClient _http;

    public FeedClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        HttpAgent.Set(_http);
    }

    internal static string Code(PolRegion r) => r switch { PolRegion.Europe => "eu", PolRegion.Japan => "jp", _ => "na" };

    public async Task<FeedResult> GetAsync(PolRegion region, FeedKind kind, string? workerBaseUrl, CancellationToken ct = default)
    {
        var path = $"feeds/{Code(region)}/{(kind == FeedKind.News ? "news" : "status")}.json";
        return new FeedResult(region, kind, await FromWorkerAsync(workerBaseUrl, path, ParseWorker, ct).ConfigureAwait(false));
    }

    // the worker 502s when it finds no worlds
    public Task<IReadOnlyList<WorldStatus>> GetWorldsAsync(PolRegion region, string? workerBaseUrl, CancellationToken ct = default) =>
        FromWorkerAsync(workerBaseUrl, $"worlds/{Code(region)}.json", ParseWorkerWorlds, ct);

    private async Task<IReadOnlyList<T>> FromWorkerAsync<T>(string? workerBaseUrl, string path,
                                                            Func<string, IReadOnlyList<T>> parse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workerBaseUrl)) return [];
        try
        {
            var url = $"{workerBaseUrl.TrimEnd('/')}/{path}";
            return await FetchAsync(url, parse, ct).ConfigureAwait(false);
        }
        catch when (!ct.IsCancellationRequested) { return []; }
    }

    internal static IReadOnlyList<FeedItem> ParseWorker(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var items = new List<FeedItem>();
        if (!doc.RootElement.TryGetProperty("items", out var arr)) return items;
        foreach (var it in arr.EnumerateArray())
        {
            DateTimeOffset? date = DateTimeOffset.TryParse(Str(it, "date"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
            items.Add(new FeedItem(Str(it, "title"), Str(it, "url"), date,
                it.TryGetProperty("image", out var im) && im.ValueKind == JsonValueKind.String ? im.GetString() : null,
                Str(it, "summary").Replace("\u2026", "..."), CategoryFromKey(Str(it, "category")),
                BadgeFromKey(Str(it, "badge"))));
        }
        return items;
    }

    private static InfoCategory CategoryFromKey(string? key) => key switch
    {
        "important" => InfoCategory.Important, "maintenance" => InfoCategory.Maintenance,
        "general" => InfoCategory.General, "events" => InfoCategory.Events, "updates" => InfoCategory.Updates,
        _ => InfoCategory.None,
    };

    private static InfoBadge BadgeFromKey(string? key) => key switch
    {
        "outage" => InfoBadge.Outage, "maintenance" => InfoBadge.Maintenance,
        "recovery" => InfoBadge.Recovery, "followup" => InfoBadge.FollowUp,
        _ => InfoBadge.None,
    };

    internal static IReadOnlyList<WorldStatus> ParseWorkerWorlds(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<WorldStatus>();
        if (doc.RootElement.TryGetProperty("worlds", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var w in arr.EnumerateArray())
                if (w.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    list.Add(new WorldStatus(n.GetString() ?? "",
                        w.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() ?? "unknown" : "unknown"));
        return list;
    }

    private async Task<T> FetchAsync<T>(string url, Func<string, T> parse, CancellationToken ct)
    {
        var cached = await FeedCache.ReadAsync(url, ct).ConfigureAwait(false);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (cached is { ETag: { Length: > 0 } etag }) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
            if (cached is { LastModified: { Length: > 0 } lm }) req.Headers.TryAddWithoutValidation("If-Modified-Since", lm);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotModified && cached != null) return parse(cached.Body);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var result = parse(body);
            FeedCache.Write(url, body, resp.Headers.ETag?.ToString(), resp.Content.Headers.LastModified?.ToString("R"));
            return result;
        }
        catch when (cached != null && !ct.IsCancellationRequested)
        {
            return parse(cached.Body);
        }
    }
}
