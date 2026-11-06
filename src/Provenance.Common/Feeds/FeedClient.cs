using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Provenance.Common.IO;
using Provenance.Common.PolFormat;
using static Provenance.Common.JsonFields;

namespace Provenance.Common.Feeds;

// the feed worker's JSON, and the last answer it gave when it can't be reached
public sealed class FeedClient
{
    private const int MaxImageBytes = 8 * 1024 * 1024;

    // redirects by hand: one mustn't take a banner download off POL's site
    private static readonly HttpClient ImageHttp =
        new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };

    private readonly HttpClient _http;

    public FeedClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd("Provenance/1.0"))
            _http.DefaultRequestHeaders.Add("User-Agent", "Provenance/1.0");
    }

    internal static string Code(PolRegion r) => r switch { PolRegion.Europe => "eu", PolRegion.Japan => "jp", _ => "na" };

    private static string RegionToken(PolRegion r) =>
        r switch { PolRegion.Europe => "ff11eu", PolRegion.Japan => "ff11", _ => "ff11us" };

    public static string ServicePage(PolRegion region) => $"https://www.playonline.com/{RegionToken(region)}/service/";

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
            return await Task.Run(() => FetchAsync(url, parse, ct), ct).ConfigureAwait(false);
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
        return AppPaths.UserData is { } root ? Path.Combine(root, "cache", name) : null;
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
        try
        {
            if (CachePath(url) is not { } path) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.Write(path, $"ETag: {etag}\nLast-Modified: {lastModified}\n\n{body}");
        }
        catch { }
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
            await AtomicFile.WriteAsync(path, bytes, ct).ConfigureAwait(false);
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
}
