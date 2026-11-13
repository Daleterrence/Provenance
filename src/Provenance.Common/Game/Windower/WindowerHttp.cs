using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Serilog;
using Provenance.Common.IO;

namespace Provenance.Common.Game.Windower;

public sealed record WindowerServers(Uri Files, Uri Res, Uri GitHubApi, Uri GitHubArchive)
{
    public static WindowerServers Default { get; } = new(new Uri("https://files.windower.net/4/"), new Uri("https://files.windower.net/res/"),
        new Uri("https://api.github.com/repos/Windower/Lua/"), new Uri("https://github.com/Windower/Lua/archive/"));
}

public enum WindowerFetchOutcome { Downloaded, NotModified, HttpError, Unreachable, HostOffline, Broken }

public readonly record struct WindowerFetch(WindowerFetchOutcome Outcome, string Host, int Status = 0)
{
    public bool Ok => Outcome == WindowerFetchOutcome.Downloaded;
}

// Requests shaped like Windower.exe's HttpWebRequest ones: its User-Agent and header order, no proxy, TLS 1.2. Only a
// 200 counts, like there
public sealed class WindowerHttp : IDisposable
{
    public static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(45);
    private const int MaxRedirects = 50;

    internal long MaxBytes { get; set; } = 16 * 1024 * 1024;
    internal long MaxDownloadBytes { get; set; } = 256 * 1024 * 1024;

    // what File.GetLastWriteTimeUtc gives for a file that isn't there, and Windower sends it as is
    public static readonly DateTime NoFile = DateTime.FromFileTimeUtc(0);

    // the Windower.exe the requests claim to come from when there isn't one in the folder yet
    public static readonly Version DefaultLauncher = new(4, 7, 9, 3);

    private readonly HttpClient _client;
    private readonly HashSet<string> _offline = new(StringComparer.OrdinalIgnoreCase);

    public WindowerHttp(HttpMessageHandler? handler = null)
    {
#pragma warning disable RS0030 // WindowerHttp owns its client
        _client = new HttpClient(handler ?? CreateHandler())
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
#pragma warning restore RS0030
    }

    public string UserAgent { get; set; } = UserAgentFor(DefaultLauncher);

    // hosts that didn't answer at all, by the site asked rather than where a redirect went. The archive's site is
    // github.com here, Windower switches off api.github.com for it
    public IReadOnlyCollection<string> OfflineHosts => _offline;

    // redirects are followed by hand so every hop carries the same headers in the same order
    public static SocketsHttpHandler CreateHandler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        PreAuthenticate = false,
        ConnectTimeout = HeaderTimeout,
        ActivityHeadersPropagator = null,
        SslOptions = { EnabledSslProtocols = SslProtocols.Tls12 },
        ConnectCallback = ConnectAsync,
    };

    // .NET Framework sends Connection: Keep-Alive on a connection's first request only, so the request that opens one
    // gets it here (a lot of faff for one header)
    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var connection = context.InitialRequestMessage.Headers.Connection;
        if (!connection.Contains("Keep-Alive")) connection.Add("Keep-Alive");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // Windower/<major>.<minor> (Windows NT <os major>.<os minor>; WOW64), a launcher version that isn't known gets the
    // default. Windower.exe is always x86, so a 64-bit Windows always gets WOW64 whatever Provenance is built as
    public static string UserAgentFor(Version launcher)
    {
        if (launcher.Major == 0) launcher = DefaultLauncher;
        var os = Environment.OSVersion.Version;
        return $"Windower/{launcher.Major}.{launcher.Minor} (Windows NT {os.Major}.{os.Minor}{(Environment.Is64BitOperatingSystem ? "; WOW64" : "")})";
    }

    public static DateTime Modified(string file) => File.Exists(file) ? File.GetLastWriteTimeUtc(file) : NoFile;

    public static string HttpDate(DateTime utc) =>
        utc.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture);

    public async Task<WindowerFetch> DownloadAsync(Uri site, string url, string file, DateTime? modified, CancellationToken ct)
    {
        FileStream? output = null;
        DateTime? stamp = null;
        WindowerFetch result;
        try
        {
            result = await FetchAsync(site, url, modified, () => output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None),
                                      t => stamp = t, MaxDownloadBytes, ct);
            // on the handle, so the time can't land on something swapped in by name afterwards
            if (result.Ok && output is not null)
            {
                try { File.SetLastWriteTimeUtc(output.SafeFileHandle, stamp ?? DateTime.UtcNow); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
        }
        finally
        {
            if (output is not null) await output.DisposeAsync();
        }
        if (!result.Ok && output is not null)
        {
            FileDelete.Try(file);
        }
        return result;
    }

    public async Task<(WindowerFetch Fetch, byte[]? Body)> GetBytesAsync(Uri site, string url, DateTime? modified, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var r = await FetchAsync(site, url, modified, () => buffer, _ => { }, MaxBytes, ct);
        return (r, r.Ok ? buffer.ToArray() : null);
    }

    private async Task<WindowerFetch> FetchAsync(Uri site, string url, DateTime? modified, Func<Stream> open, Action<DateTime?> lastModified,
                                                 long cap, CancellationToken ct)
    {
        var host = site.Host;
        var uri = new Uri(site, url);
        var since = modified is { } d ? $" (If-Modified-Since: {HttpDate(d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : d)})" : "";
        void Say(string what) => Log.Debug("Windower update: {Line}", $"GET {uri}{since} -> {what}");
        if (_offline.Contains(host))
        {
            Say($"not sent, {host} didn't answer earlier");
            return new(WindowerFetchOutcome.HostOffline, host);
        }
        HttpResponseMessage? resp = null;
        try
        {
            for (int hop = 0; ; hop++)
            {
                using var req = Request(uri, modified);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(HeaderTimeout);
                try { resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, wait.Token); }
                catch (Exception e) when (e is HttpRequestException || (e is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    Say($"no answer ({e.GetType().Name})");
                    _offline.Add(host);
                    return new(WindowerFetchOutcome.Unreachable, host);
                }
                int code = (int)resp.StatusCode;
                if (code != 200) Say(code.ToString(CultureInfo.InvariantCulture));
                // HttpWebRequest stops at a 308, so that's an answer like any other
                if (code is 300 or 301 or 302 or 303 or 307 && resp.Headers.Location is { } to)
                {
                    if (hop >= MaxRedirects) return new(WindowerFetchOutcome.HttpError, uri.Host, code);
                    var next = to.IsAbsoluteUri ? to : new Uri(uri, to);
                    if (next.Scheme != Uri.UriSchemeHttps && (next.Scheme != Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                        return new(WindowerFetchOutcome.HttpError, uri.Host, code);
                    resp.Dispose();
                    resp = null;
                    uri = next;
                    continue;
                }
                break;
            }

            int status = (int)resp.StatusCode;
            if (status == 304) return new(WindowerFetchOutcome.NotModified, uri.Host, status);
            if (status != 200) return new(WindowerFetchOutcome.HttpError, uri.Host, status);
            lastModified(resp.Content.Headers.LastModified?.UtcDateTime);
            var expected = resp.Content.Headers.ContentLength;
            if (expected > cap)
            {
                Say($"200, {expected:N0} bytes is too big");
                return new(WindowerFetchOutcome.Broken, uri.Host, status);
            }
            long got = 0;
            await using var body = await resp.Content.ReadAsStreamAsync(ct);
            var output = open();
            var buf = new byte[4096];
            using var read = CancellationTokenSource.CreateLinkedTokenSource(ct);
            while (true)
            {
                read.CancelAfter(ReadTimeout);
                int n = await body.ReadAsync(buf, read.Token);
                if (n == 0) break;
                if (got + n > cap)
                {
                    Say($"200, too big, stopped at {cap:N0} bytes");
                    return new(WindowerFetchOutcome.Broken, uri.Host, status);
                }
                await output.WriteAsync(buf.AsMemory(0, n), ct);
                got += n;
            }
            await output.FlushAsync(ct);
            bool whole = expected is not { } want || want == got;
            Say($"200, {got:N0} bytes{(whole ? "" : $" of {expected:N0}")}");
            return new(whole ? WindowerFetchOutcome.Downloaded : WindowerFetchOutcome.Broken, uri.Host, status);
        }
        catch (Exception e) when (e is IOException or HttpRequestException || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            Say($"cut off ({e.GetType().Name})");
            return new(WindowerFetchOutcome.Broken, uri.Host, resp is null ? 0 : (int)resp.StatusCode);
        }
        finally
        {
            resp?.Dispose();
        }
    }

    // .NET Framework's order: User-Agent, If-Modified-Since, Host (then Connection, see ConnectAsync).
    // SocketsHttpHandler writes Host first unless it's set by hand
    private HttpRequestMessage Request(Uri uri, DateTime? modified)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, uri) { Version = HttpVersion.Version11 };
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (modified is { } m) req.Headers.TryAddWithoutValidation("If-Modified-Since", HttpDate(m.Kind == DateTimeKind.Local ? m.ToUniversalTime() : m));
        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost;
        req.Headers.Host = uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
        return req;
    }

    public void Dispose() => _client.Dispose();
}
