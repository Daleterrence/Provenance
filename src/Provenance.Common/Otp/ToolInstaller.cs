using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Provenance.Common.IO;
using Provenance.Common.Security;
using Serilog;

namespace Provenance.Common.Otp;

public enum ToolInstall { Installed, DownloadFailed, CheckFailed, CantCheck, TooOld, NotSupported }

public static class ToolInstaller
{
    private const string OnePasswordVersionUrl = "https://app-updates.agilebits.com/check/1/0/CLI2/en/2.0.0/N";
    private const string BitwardenDownloadUrl = "https://vault.bitwarden.com/download/?app=cli&platform=windows";
    // bw-windows till 2026.10, bw-bit-windows since
    private static readonly Regex BitwardenZip =
        new(@"^https://github\.com/bitwarden/clients/releases/download/cli-v([0-9]{1,6}\.[0-9]{1,6}\.[0-9]{1,6})/bw-(?:[a-z0-9]{1,16}-)?windows-\1\.zip$");

    internal static long MaxZip = 200L << 20, MaxExe = 500L << 20;
    internal static TimeSpan StallLimit = TimeSpan.FromSeconds(30);

    private static HttpMessageHandler? _handlerForTest;
    private static HttpClient? _http, _httpNoRedirect;

    internal static void SetHttpForTest(HttpMessageHandler? handler)
    {
        _handlerForTest = handler;
        _http = _httpNoRedirect = null;
    }

    internal static bool Supported(string tool) => tool switch
    {
        ToolFacts.OnePassword => OnePasswordArch() is not null,
        ToolFacts.Bitwarden => RuntimeInformation.OSArchitecture == Architecture.X64 ||
                     (RuntimeInformation.OSArchitecture == Architecture.Arm64 && Environment.OSVersion.Version.Build >= 22000),
        _ => false,
    };

    // there's no ARM64 build, the 32-bit one runs on ARM Windows
    private static string? OnePasswordArch() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.X86 or Architecture.Arm64 => "386",
        _ => null,
    };

    public static async Task<ToolInstall> InstallAsync(string tool, IProgress<double>? progress, CancellationToken ct)
    {
        if (!Supported(tool))
        {
            Log.Information("{Name}'s command-line tool isn't made for this Windows ({Arch})", ToolFacts.Name(tool), RuntimeInformation.OSArchitecture);
            return ToolInstall.NotSupported;
        }
        IDisposable? held = null;
        try
        {
            held = await Task.Run(() => AdminLock.TryAcquire(ToolFacts.LockName(tool), TimeSpan.FromMinutes(10), ct), ct);
            if (held is null) return ToolInstall.DownloadFailed;
            var own = Path.Combine(ToolFinder.Dir, tool + ".exe");
            if (await Task.Run(() =>
                {
                    using var file = File.Exists(own) ? ToolSignature.Open(own) : null;
                    return file is not null && ToolFinder.CheckToRun(file, own, tool) == Verdict.Genuine;
                }, ct))
                return ToolInstall.Installed;
            var (version, url) = await LatestAsync(tool, ct);
            return await FetchAndSwapAsync(tool, version, url, progress, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "Downloading {Name}'s command-line tool failed", ToolFacts.Name(tool));
            return ToolInstall.DownloadFailed;
        }
        finally { held?.Dispose(); }
    }

    public static void UpdateInBackground(string tool) =>
        _ = Task.Run(async () =>
        {
            try { await UpdateIfDueAsync(tool, CancellationToken.None); }
            catch (Exception ex) { Log.Warning(ex, "Checking for a newer {Name} command-line tool failed", ToolFacts.Name(tool)); }
        });

    internal static async Task UpdateIfDueAsync(string tool, CancellationToken ct)
    {
        var dir = ToolFinder.Dir;
        if (!File.Exists(Path.Combine(dir, tool + ".exe"))) return;
        if (!ToolNote.IsDue(ToolNote.Read(dir, tool))) return;
        using var held = AdminLock.TryAcquire(ToolFacts.LockName(tool), TimeSpan.Zero, ct);
        if (held is null) return;
        var state = ToolNote.Read(dir, tool);
        if (!ToolNote.IsDue(state)) return;
        ToolNote.Write(dir, tool, new ToolNote(state?.Version ?? "", DateTime.UtcNow));
        var (version, url) = await LatestAsync(tool, ct);
        if (Version.TryParse(state?.Version, out var have) && Version.TryParse(version, out var latest) && latest <= have) return;
        var result = await FetchAndSwapAsync(tool, version, url, null, ct);
        Log.Information("{Name} command-line tool update to {Version}: {Result}", ToolFacts.Name(tool), version, result);
    }

    private static HttpClient Http => _http ??= Client(follow: true);
    private static HttpClient HttpNoRedirect => _httpNoRedirect ??= Client(follow: false);

    private static HttpClient Client(bool follow)
    {
#pragma warning disable RS0030 // ToolInstaller owns its clients
        var c = _handlerForTest is { } h ? new HttpClient(h, disposeHandler: false)
              : new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = follow });
#pragma warning restore RS0030
        c.Timeout = TimeSpan.FromSeconds(60);
        HttpAgent.Set(c);
        return c;
    }

    private static async Task<(string Version, string Url)> LatestAsync(string tool, CancellationToken ct)
    {
        if (tool == ToolFacts.OnePassword)
        {
            var arch = OnePasswordArch() ?? throw new PlatformNotSupportedException();
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(OnePasswordVersionUrl, ct));
            var v = doc.RootElement.GetProperty("version").GetString() ?? "";
            if (ToolFinder.ParseVersion(v) is null) throw new InvalidDataException("1Password's version check didn't give a version.");
            return (v, $"https://cache.agilebits.com/dist/1P/op2/pkg/v{v}/op_windows_{arch}_v{v}.zip");
        }
        using var r = await HttpNoRedirect.GetAsync(BitwardenDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        var location = r.Headers.Location is { IsAbsoluteUri: true } l ? l.AbsoluteUri : "";
        var m = BitwardenZip.Match(location);
        if (!m.Success) throw new InvalidDataException($"Bitwarden's download link answered {(int)r.StatusCode} with no release zip.");
        return (m.Groups[1].Value, location);
    }

    private static async Task<ToolInstall> FetchAndSwapAsync(string tool, string version, string url, IProgress<double>? progress,
                                                            CancellationToken ct)
    {
        var dir = ToolFinder.Dir;
        Sweep(dir, tool);
        var stamp = ShortId.New(8);
        var zip = Path.Combine(dir, $"{tool}-new-{stamp}.zip");
        var exe = Path.Combine(dir, $"{tool}-new-{stamp}.exe");
        try
        {
            await DownloadAsync(url, zip, progress, ct);
            await Task.Run(() => ExtractExe(zip, tool + ".exe", exe), ct);
            var (verdict, got) = await Task.Run(() =>
            {
                using var held = ToolSignature.Open(exe);
                if (held is null) return (Verdict.CantCheck, (Version?)null);
                var v = ToolSignature.Check(held, exe, tool, online: true);
                return (v, v == Verdict.Genuine ? ToolFinder.VersionOf(exe) : null);
            }, ct);
            if (verdict != Verdict.Genuine)
            {
                Log.Warning("The {Name} command-line tool {Version} from {Url} didn't pass the signature check ({Verdict}), so it's deleted",
                            ToolFacts.Name(tool), version, url, verdict);
                return verdict == Verdict.NotGenuine ? ToolInstall.CheckFailed : ToolInstall.CantCheck;
            }
            var floor = File.Exists(Path.Combine(dir, tool + ".exe")) && ToolNote.NotedVersion(dir, tool) is { } have && have > ToolFacts.Oldest(tool) ? have : ToolFacts.Oldest(tool);
            if (got is null || got < floor)
            {
                Log.Warning("The {Name} command-line tool from {Url} says it's version {Got}, and nothing below {Floor} goes in, so it's deleted",
                            ToolFacts.Name(tool), url, got, floor);
                return got is null ? ToolInstall.CantCheck : ToolInstall.TooOld;
            }
            Swap(dir, tool, exe);
            ToolNote.Write(dir, tool, new ToolNote(got.ToString(), DateTime.UtcNow));
            Log.Information("Installed the {Name} command-line tool {Version}", ToolFacts.Name(tool), got);
            return ToolInstall.Installed;
        }
        finally
        {
            ToolFinder.TryDelete(zip);
            ToolFinder.TryDelete(exe);
        }
    }

    // HttpClient's timeout only covers the headers, so each read gets its own limit
    private static async Task DownloadAsync(string url, string to, IProgress<double>? progress, CancellationToken ct)
    {
        using var r = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        r.EnsureSuccessStatusCode();
        long? total = r.Content.Headers.ContentLength;
        if (total > MaxZip) throw new InvalidDataException($"The download is {total} bytes, more than a command-line tool should be.");
        await using var src = await r.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var buf = new byte[81920];
        long got = 0;
        while (true)
        {
            stall.CancelAfter(StallLimit);
            int n = await src.ReadAsync(buf, stall.Token);
            if (n == 0) break;
            got += n;
            if (got > MaxZip) throw new InvalidDataException("The download is bigger than a command-line tool should be.");
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            if (total > 0) progress?.Report(Math.Min(1, (double)got / total.Value));
        }
    }

    private static void ExtractExe(string zip, string name, string to)
    {
        using var archive = ZipFile.OpenRead(zip);
        var entry = archive.Entries.SingleOrDefault(e => e.FullName == name)
                    ?? throw new InvalidDataException($"The download has no {name} in it.");
        if (entry.Length > MaxExe) throw new InvalidDataException($"{name} in the download is bigger than it should be.");
        using var src = entry.Open();
        using var dst = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buf = new byte[81920];
        long got = 0;
        for (int n; (n = src.Read(buf)) > 0;)
        {
            got += n;
            if (got > MaxExe) throw new InvalidDataException($"{name} in the download is bigger than it should be.");
            dst.Write(buf, 0, n);
        }
    }

    private static void Swap(string dir, string tool, string fresh)
    {
        var final = Path.Combine(dir, tool + ".exe");
        try { File.Move(fresh, final, overwrite: true); }
        catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && File.Exists(final))
        {
            File.Move(final, Path.Combine(dir, $"{tool}-old-{ShortId.New(8)}.exe"));
            File.Move(fresh, final);
        }
    }

    private static void Sweep(string dir, string tool)
    {
        foreach (var pattern in new[] { $"{tool}-new-*", $"{tool}-old-*" })
            foreach (var f in Directory.EnumerateFiles(dir, pattern)) ToolFinder.TryDelete(f);
    }
}
