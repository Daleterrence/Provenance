using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static Provenance.Common.JsonFields;

namespace Provenance.Common.Feeds;

// the feed worker's JSON when there's one, else POL's own RSS 1.0, normalised the same way the worker does it
public sealed class FeedClient
{
    private static readonly XNamespace Rss = "http://purl.org/rss/1.0/";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

    private static readonly IReadOnlyDictionary<string, string> ZoneOffset =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["JST"] = "+0900", ["GMT"] = "+0000", ["UTC"] = "+0000", ["UT"] = "+0000",
            ["PST"] = "-0800", ["PDT"] = "-0700", ["EST"] = "-0500", ["EDT"] = "-0400",
            ["CST"] = "-0600", ["CDT"] = "-0500", ["CET"] = "+0100", ["CEST"] = "+0200",
        };

    private const int MaxImageBytes = 8 * 1024 * 1024;

    // redirects by hand: one mustn't take a banner download off POL's site
    private static readonly HttpClient ImageHttp =
        new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };

    private readonly HttpClient _http;

    // the Server Status page was never changed from Shift_JIS after the website redesign, soooo here we are.
    static FeedClient() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public FeedClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd("Provenance/1.0"))
            _http.DefaultRequestHeaders.Add("User-Agent", "Provenance/1.0");
    }

    private static string RegionToken(FeedRegion r) =>
        r switch { FeedRegion.EU => "ff11eu", FeedRegion.JP => "ff11", _ => "ff11us" };

    public static string SourceUrl(FeedRegion r, FeedKind k) => k == FeedKind.News
        ? $"https://www.playonline.com/pcd2/topics/{RegionToken(r)}/topics.xml"
        : $"https://www.playonline.com/{RegionToken(r)}/polnews/news.xml";

    public static string LatestUrl(FeedRegion r) =>
        $"https://www.playonline.com/pcd2/topics/{RegionToken(r)}/topics_latest.html";

    public static string ServicePage(FeedRegion region) => PlayOnlineInfo.ServicePage(RegionToken(region));

    public async Task<FeedResult> GetAsync(FeedRegion region, FeedKind kind, string? workerBaseUrl, CancellationToken ct = default)
    {
        var path = $"feeds/{region.ToString().ToLowerInvariant()}/{(kind == FeedKind.News ? "news" : "status")}.json";
        if (await FromWorkerAsync(workerBaseUrl, path, ParseWorker, ct).ConfigureAwait(false) is { } items)
            return new FeedResult(region, kind, items);
        try { return await Task.Run(() => GetDirectAsync(region, kind, ct), ct).ConfigureAwait(false); }
        catch { return FeedResult.Empty(region, kind); }
    }

    // the worker 502s when it finds no worlds, which lands on POL's page too
    public async Task<IReadOnlyList<WorldStatus>> GetWorldsAsync(FeedRegion region, string? workerBaseUrl, CancellationToken ct = default)
    {
        var path = $"worlds/{region.ToString().ToLowerInvariant()}.json";
        if (await FromWorkerAsync(workerBaseUrl, path, ParseWorkerWorlds, ct).ConfigureAwait(false) is { } worlds)
            return worlds;
        try
        {
            return await Task.Run(() => FetchAsync(PlayOnlineInfo.WorldsUrl, PlayOnlineInfo.ParseWorlds, ct), ct)
                .ConfigureAwait(false);
        }
        catch { return []; }
    }

    // null means go to POL. POL's lists are never empty, so an empty answer counts as trouble too
    private async Task<IReadOnlyList<T>?> FromWorkerAsync<T>(string? workerBaseUrl, string path,
                                                             Func<string, IReadOnlyList<T>> parse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workerBaseUrl)) return null;
        try
        {
            var url = $"{workerBaseUrl.TrimEnd('/')}/{path}";
            var list = await Task.Run(() => FetchAsync(url, parse, ct), ct).ConfigureAwait(false);
            return list.Count > 0 ? list : null;
        }
        catch when (!ct.IsCancellationRequested) { return null; }
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
                Str(it, "summary").Replace("\u2026", "..."), PlayOnlineInfo.CategoryFromKey(Str(it, "category")),
                PlayOnlineInfo.BadgeFromKey(Str(it, "badge"))));
        }
        return items;
    }

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

    private async Task<FeedResult> GetDirectAsync(FeedRegion region, FeedKind kind, CancellationToken ct)
    {
        var xmlTask = FetchAsync(SourceUrl(region, kind), ParseRdf, ct);
        // the Topics RSS has no header images and the Information RSS no categories, but the site's pages have both,
        // so those get fetched alongside and joined by link
        var latestTask = kind == FeedKind.News ? FetchAsync(LatestUrl(region), ParseBanners, ct) : null;
        var infoTasks = kind == FeedKind.Status
            ? PlayOnlineInfo.InfoUrls(RegionToken(region)).Select(u => FetchAsync(u, PlayOnlineInfo.ParseCategories, ct)).ToList()
            : null;
        var items = await xmlTask.ConfigureAwait(false);
        if (latestTask != null)
        {
            try { items = WithBanners(items, await latestTask.ConfigureAwait(false)); }
            catch { }   // banners are decoration
        }
        if (infoTasks != null)
        {
            var cats = new Dictionary<string, (InfoCategory Category, InfoBadge Badge)>();
            foreach (var task in infoTasks)
            {
                try { foreach (var kv in await task.ConfigureAwait(false)) cats.TryAdd(kv.Key, kv.Value); }   // info_top wins
                catch { }
            }
            items = items.Select(it => PlayOnlineInfo.LinkKey(it.Url) is { } k && cats.TryGetValue(k, out var c)
                                       ? it with { Category = c.Category, Badge = c.Badge } : it).ToList();
        }
        return new FeedResult(region, kind, items);
    }

    private async Task<T> FetchAsync<T>(string url, Func<string, T> parse, CancellationToken ct)
    {
        var cached = ReadCache(url);
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
            WriteCache(url, body, resp.Headers.ETag?.ToString(), resp.Content.Headers.LastModified?.ToString("R"));
            return result;
        }
        catch when (cached != null && !ct.IsCancellationRequested)
        {
            return parse(cached.Body);
        }
    }

    private sealed record CachedBody(string Body, string? ETag, string? LastModified);

    private static string? CacheDir(string name)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "Provenance", "cache", name);
    }

    private static string Sha256Hex(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private static string? CachePath(string url) =>
        CacheDir("feeds") is { } dir ? Path.Combine(dir, Sha256Hex(url) + ".cache") : null;

    // "ETag: ..." and "Last-Modified: ..." lines, a blank line, then the body
    private static CachedBody? ReadCache(string url)
    {
        try
        {
            if (CachePath(url) is not { } path || !File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            var split = text.IndexOf("\n\n", StringComparison.Ordinal);
            if (split < 0) return null;
            var head = text[..split].Split('\n');
            return new CachedBody(text[(split + 2)..], Header(head, "ETag:"), Header(head, "Last-Modified:"));
        }
        catch { return null; }

        static string? Header(string[] lines, string name)
        {
            var line = lines.LastOrDefault(l => l.StartsWith(name, StringComparison.Ordinal));
            return line?[(line.IndexOf(':') + 1)..].Trim() is { Length: > 0 } v ? v : null;
        }
    }

    private static void WriteCache(string url, string body, string? etag, string? lastModified)
    {
        string? tmp = null;
        try
        {
            if (CachePath(url) is not { } path) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tmp, $"ETag: {etag}\nLast-Modified: {lastModified}\n\n{body}");
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (tmp != null) File.Delete(tmp); } catch { }
        }
    }

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

    internal static async Task<byte[]?> GetImageBytesAsync(Uri u, CancellationToken ct)
    {
        for (int hop = 0; hop < 4; hop++)
        {
            if (UpgradeBannerUrl(u) is not { } safe) return null;
            u = safe;
            using var resp = await ImageHttp.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)resp.StatusCode is >= 300 and < 400)
            {
                if (resp.Headers.Location is not { } loc) return null;
                u = loc.IsAbsoluteUri ? loc : new Uri(u, loc);
                continue;   // the next pass upgrades it, or bails when it's left POL's site
            }
            if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentLength > MaxImageBytes) return null;
            // read in pieces and stop past the cap, so a reply with no length header never gets buffered whole
            await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            var chunk = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + n > MaxImageBytes) return null;
                ms.Write(chunk, 0, n);
            }
            return ms.ToArray();
        }
        return null;
    }

    // a topic's banner never changes, one already on disk needs no request
    public async Task<string?> LocalImageAsync(string url, CancellationToken ct = default)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u0) || UpgradeBannerUrl(u0) is not { } u) return null;
            if (CacheDir("images") is not { } dir) return null;
            var ext = Path.GetExtension(u.AbsolutePath).ToLowerInvariant();
            if (ext is not (".jpg" or ".jpeg" or ".png" or ".gif")) ext = ".img";
            var path = Path.Combine(dir, Sha256Hex(u.AbsoluteUri)[..32] + ext);
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }   // still in use, so the prune keeps it
                return path;
            }
            Directory.CreateDirectory(dir);
            var bytes = await GetImageBytesAsync(u, ct).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0 || bytes.Length > MaxImageBytes || !LooksLikeImage(bytes))
                return null;
            var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        catch { return null; }
    }

    internal static bool LooksLikeImage(byte[] b) =>
        b.Length >= 4 && ((b[0] == 0xFF && b[1] == 0xD8) || (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) ||
                          (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46));

    public static void ForgetImage(string? localPath)
    {
        try
        {
            if (localPath is null || CacheDir("images") is not { } dir) return;
            var full = Path.GetFullPath(localPath);
            if (full.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) File.Delete(full);
        }
        catch { }
    }

    public static void PruneImages(TimeSpan maxAge)
    {
        try
        {
            if (CacheDir("images") is not { } dir || !Directory.Exists(dir)) return;
            var cutoff = DateTime.UtcNow - maxAge;
            foreach (var f in Directory.EnumerateFiles(dir))
                if (File.GetLastWriteTimeUtc(f) < cutoff) { try { File.Delete(f); } catch { } }
        }
        catch { }
    }

    // each div.gutter has the link in h4.tx_topics_tl and the banner in img.tpimgL (the file name varies)
    public static IReadOnlyDictionary<string, string> ParseBanners(string html)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in html.Split("<div class=\"gutter\">").Skip(1))
        {
            var href = Regex.Match(chunk, @"class=""tx_topics_tl""[^>]*>\s*<a\s+href=""([^""]+)""", RegexOptions.IgnoreCase);
            var img = Regex.Match(chunk, @"<img\b(?=[^>]*\bclass=""tpimgL"")[^>]*\bsrc=""([^""]+)""", RegexOptions.IgnoreCase);
            if (href.Success && img.Success && PlayOnlineInfo.LinkKey(href.Groups[1].Value) is { } key &&
                PlayOnlineInfo.ToAbsolute(img.Groups[1].Value) is { } src)
                map.TryAdd(key, src.AbsoluteUri);
        }
        return map;
    }

    public static IReadOnlyList<FeedItem> WithBanners(IReadOnlyList<FeedItem> items, IReadOnlyDictionary<string, string> banners) =>
        items.Select(i => PlayOnlineInfo.LinkKey(i.Url) is { } k && banners.TryGetValue(k, out var b)
                          ? i with { ImageUrl = b } : i).ToList();

    public static IReadOnlyList<FeedItem> ParseRdf(string xml) =>
        XDocument.Parse(xml).Descendants(Rss + "item").Select(item =>
        {
            var link = (string?)item.Element(Rss + "link");
            if (string.IsNullOrWhiteSpace(link)) link = (string?)item.Attribute(Rdf + "about");
            var desc = (string?)item.Element(Rss + "description") ?? "";
            return new FeedItem(Decode((string?)item.Element(Rss + "title") ?? ""), (link ?? "").Trim(),
                                NormalizeDate((string?)item.Element(Dc + "date")), FirstImage(desc), Summarize(desc));
        }).ToList();

    public static DateTimeOffset? NormalizeDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        var m = Regex.Match(s, @"\b([A-Z]{2,4})\s*$");
        if (m.Success && ZoneOffset.TryGetValue(m.Groups[1].Value, out var off))
            s = Regex.Replace(s, @"\b[A-Z]{2,4}\s*$", off);
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto) ? dto : null;
    }

    private static string? FirstImage(string html)
    {
        var m = Regex.Match(html, @"<img[^>]+src\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var u = m.Groups[1].Value.Trim();
        return u.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? u : null;
    }

    private static string Summarize(string html, int max = 420)
    {
        var text = Decode(Regex.Replace(Regex.Replace(html, @"<br\s*/?>", " ", RegexOptions.IgnoreCase), @"<[^>]+>", ""));
        text = Regex.Replace(text, @"[=\-_*~]{3,}", " ");   // "=====" style separator lines
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > max ? text[..(max - 3)].TrimEnd() + "..." : text;
    }

    private static string Decode(string t) => t
        .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
        .Replace("&apos;", "'").Replace("&#39;", "'").Replace("&nbsp;", " ")
        .Replace("&amp;", "&").Trim();
}
