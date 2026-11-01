using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Win32;
using Provenance.Common.Game;
using Provenance.Common.PolFormat;
using SharpCompress.Archives.Rar;
using SharpCompress.Readers;

namespace Provenance.Common.Install;

public sealed record GamePart(string FileName, Uri Url, long Size, string Md5);

// Fraction is -1 while there's nothing to measure
// Download marks bytes coming over the network, the only ones that get a rate
public sealed record InstallProgress(string Stage, string Detail, double Fraction, long DoneBytes, long TotalBytes, bool Download = false);

public sealed class DamagedPartException : IOException
{
    public DamagedPartException(string part)
        : base($"The installer part {part} was damaged, so Provenance deleted it. Press Try again to download a fresh copy.")
    {
        Part = part;
        Shown.Mark(this);
    }

    public string Part { get; }
}

public sealed class RestartRequiredException : InvalidOperationException
{
    public RestartRequiredException()
        : base("Windows needs a restart to finish turning on DirectPlay, which FINAL FANTASY XI needs. " +
               "Restart Windows, then open Provenance and continue the install.") => Shown.Mark(this);
}

public sealed class GameInstaller
{
    public const long RequiredFreeBytes = 25L * 1024 * 1024 * 1024;

    // each region's two MSIs, by ProductCode
    private static readonly (PolRegion Region, string Viewer, string Ffxi)[] ProductCodes =
    [
        (PolRegion.UnitedStates, "{81784E3A-1BDA-4743-B5F8-04E59DC7E031}", "{07EB4C8B-3869-49B4-8CF8-D6D9FB8C8026}"),
        (PolRegion.Europe, "{F65CC5FB-3089-483C-8504-14EA5F58DD1D}", "{549BFF68-9B3D-46BE-88E3-BD4F7C6DEB36}"),
        (PolRegion.Japan, "{E58BCEA5-A722-4FCA-89D5-C89E18B4E3FE}", "{55DAC7CD-71AF-40DA-9BE0-AB5358EA0069}"),
    ];

    public static bool Installs(PolRegion region) => ProductCodes.Any(p => p.Region == region);

    internal static (string Viewer, string Ffxi) ProductCodesFor(PolRegion region) =>
        ProductCodes.FirstOrDefault(p => p.Region == region) is { Viewer: not null } p
            ? (p.Viewer, p.Ffxi)
            : throw new ArgumentOutOfRangeException(nameof(region), region, "Provenance doesn't install this region.");

    internal const string PolShortcut = "MAKEPOLSHORTCUT", FfxiShortcut = "MAKEFFXISHORTCUT";
    internal const string PolUninstaller = "Provenance - PlayOnline Uninstall.exe",
                          FfxiUninstaller = "Provenance - FINAL FANTASY XI Uninstall.exe";
    private const string NeedsAdmin =
        "Windows didn't let Provenance save where the game is installed.";

    private const string ViewerFolder = "PlayOnline", FfxiFolder = "FINAL_FANTASY_XI";
    private static readonly string[] ViewerNames = ["PlayOnlineViewer.msi", "PlayOnlineViewer_us.msi"];
    private static readonly string[] FfxiNames = ["FINAL_FANTASY_XI.msi", "FinalFantasyXI_us_full.msi", "FinalFantasyXI_full.msi"];
    private const string NoViewerPackage = "The unpacked installer doesn't have a PlayOnline Viewer package in it.";
    private const string NoFfxiPackage = "The unpacked installer doesn't have a FINAL FANTASY XI package in it.";

    private static readonly (long Size, string Md5)[] UsParts =
    [
        (1_610_626_088, "b6bf91904599777f3ff3cfe63c0419e2"),
        (1_610_612_736, "3dc3a25d20b86de299bd003917ede236"),
        (1_610_612_736, "b8ae47380a27f7bd07294e8ea720ade7"),
        (1_610_612_736, "64407328b60bd4e178d137cb7a74caeb"),
        (1_291_746_416, "7105cd7b7886a0d7e3e1f82f65e0dc0d"),
    ];

    private static readonly (long Size, string Md5)[] EuParts =
    [
        (1_610_626_088, "6282afbbb044ba53e464f20fab116dd4"),
        (1_610_612_736, "cb28e6454f2c0d93ce5ab2575a746296"),
        (1_610_612_736, "ababf092c2e69e8e6476c1931deed2bb"),
        (1_610_612_736, "813f4505945070ce73d8236b1e7cd481"),
        (1_292_087_528, "3e42c7b22f3238d26b1b2a57d23a6440"),
    ];

    private static readonly (long Size, string Md5)[] JpParts =
    [
        (1_610_626_088, "942e20aef1d5991d6f559ee9730b8c32"),
        (1_610_612_736, "5849085e3e858960b0a75358043a3ee5"),
        (1_610_612_736, "6d5f5ec323e1b445ccaa1ea1b34028c5"),
        (1_610_612_736, "b5ad0163b42b5f0d868ef2c9570ff6e5"),
        (1_339_572_151, "62932811d80080e7cd72a830c0ce7c99"),
    ];

    // HttpClient's timeout is off for 1.6 GB parts, and a half-open connection would otherwise wait forever
    public static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;

    public GameInstaller(HttpClient? http = null)
    {
        _http = http ?? new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None })
            { Timeout = Timeout.InfiniteTimeSpan };
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd("Provenance/1.0"))
            _http.DefaultRequestHeaders.Add("User-Agent", "Provenance/1.0");
    }

    public static IReadOnlyList<GamePart> PartsFor(PolRegion region)
    {
        var (tag, table) = region switch
        {
            PolRegion.Europe => ("eu", EuParts), PolRegion.Japan => ("jp", JpParts), _ => ("us", UsParts),
        };
        return table.Select((p, i) =>
        {
            var name = $"FFXIFullSetup_{tag.ToUpperInvariant()}.part{i + 1}.{(i == 0 ? "exe" : "rar")}";
            return new GamePart(name, new Uri($"https://gdl.square-enix.com/ffxi/download/{tag}/{name}"), p.Size, p.Md5);
        }).ToList();
    }

    public static long TotalBytes(PolRegion region) => PartsFor(region).Sum(p => p.Size);

    // SE's CDN starts the ETag with the file's MD5 (handy, if a bit fragile to lean on, hence the fallback below)
    public async Task<(long size, string? md5)> HeadAsync(GamePart part, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Head, part.Url);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(IdleTimeout);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, limit.Token);
        res.EnsureSuccessStatusCode();
        long size = res.Content.Headers.ContentLength ?? 0;
        string? md5 = res.Headers.ETag?.Tag.Trim('"').Split(':')[0];
        return (size, string.IsNullOrEmpty(md5) ? null : md5);
    }

    // This runs as admin, so a part SE has replaced stops the install early. No answer's fine, the download's own MD5
    // check decides.
    public async Task CheckServerAsync(GamePart part, CancellationToken ct)
    {
        (long size, string? md5) server;
        try { server = await HeadAsync(part, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { return; }
        if (server.size > 0 && server.md5 is { Length: 32 } &&
            (server.size != part.Size || !server.md5.Equals(part.Md5, StringComparison.OrdinalIgnoreCase)))
            throw Shown.Mark(new InvalidOperationException(
                $"Square Enix has changed the installer file {part.FileName}, and this version of Provenance only installs the files it knows. " +
                "Check for a Provenance update."));
    }

    public async Task DownloadPartAsync(GamePart part, string destDir, IProgress<long>? bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, part.FileName);
        if (File.Exists(dest) && new FileInfo(dest).Length == part.Size)
        {
            bytes?.Report(part.Size);
            return;
        }
        var partial = dest + ".part";
        long have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (have > part.Size) { File.Delete(partial); have = 0; }

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        // only armed while waiting on SE, re-hashing a resumed .part can take a while
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, part.Url);
            if (have > 0) msg.Headers.Range = new RangeHeaderValue(have, null);
            idle.CancelAfter(IdleTimeout);
            using var res = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, idle.Token);
            idle.CancelAfter(Timeout.InfiniteTimeSpan);
            if (res.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) { File.Delete(partial); await DownloadPartAsync(part, destDir, bytes, ct); return; }
            res.EnsureSuccessStatusCode();
            bool resumed = have > 0 && res.StatusCode == HttpStatusCode.PartialContent;
            if (!resumed) have = 0;
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = resumed
                ? new FileStream(partial, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1 << 20, useAsync: true)
                : new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
            var buf = new byte[1 << 20];
            long total = have;
            int n;
            if (resumed)
                while ((n = await dst.ReadAsync(buf, ct)) > 0) md5.AppendData(buf, 0, n);
            while (true)
            {
                idle.CancelAfter(IdleTimeout);
                n = await src.ReadAsync(buf, idle.Token);
                idle.CancelAfter(Timeout.InfiniteTimeSpan);
                if (n == 0) break;
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                md5.AppendData(buf, 0, n);
                total += n;
                bytes?.Report(total);
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{part.FileName} stopped downloading, nothing came for {IdleTimeout.TotalSeconds:0} seconds", ex);
        }
        if (!Convert.ToHexString(md5.GetHashAndReset()).Equals(part.Md5, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            throw new InvalidDataException($"{part.FileName} failed its MD5 check after download.");
        }
        File.Move(partial, dest, overwrite: true);
    }

    // Each part stays open read-only from its MD5 check to the end, so the bytes checked are the bytes unpacked
    public Task<string> ExtractAsync(string downloadDir, string unpackedDir, PolRegion region,
                                     IProgress<(long done, long total)>? progress, CancellationToken ct,
                                     IProgress<(long done, long total)>? checkProgress = null) => Task.Run(() =>
    {
        var parts = PartsFor(region);
        var streams = new List<FileStream>();
        try
        {
            foreach (var p in parts)
            {
                var path = Path.Combine(downloadDir, p.FileName);
                if (!File.Exists(path)) throw Shown.Mark(new FileNotFoundException($"Couldn't find the installer part {p.FileName}. Press Try again to download it."));
                streams.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20));
            }
            long checkTotal = streams.Sum(f => f.Length), checkDone = 0;
            var hashBuf = new byte[1 << 20];
            for (int i = 0; i < parts.Count; i++)
            {
                using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                int n;
                while ((n = streams[i].Read(hashBuf, 0, hashBuf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    md5.AppendData(hashBuf, 0, n);
                    checkDone += n;
                    checkProgress?.Report((checkDone, checkTotal));
                }
                if (streams[i].Length != parts[i].Size ||
                    !Convert.ToHexString(md5.GetHashAndReset()).Equals(parts[i].Md5, StringComparison.OrdinalIgnoreCase))
                {
                    var bad = streams[i].Name;
                    streams.ForEach(f => f.Dispose());
                    streams.Clear();
                    File.Delete(bad);
                    throw new DamagedPartException(parts[i].FileName);
                }
                streams[i].Position = 0;
            }
            return Unpack(streams, unpackedDir, progress, ct);
        }
        finally
        {
            streams.ForEach(f => f.Dispose());
        }
    }, ct);

    private static string Unpack(List<FileStream> volumes, string unpackedDir, IProgress<(long done, long total)>? progress, CancellationToken ct)
    {
        // emptied first so only what this run unpacked ever runs elevated
        if (Directory.Exists(unpackedDir)) Directory.Delete(unpackedDir, recursive: true);
        Directory.CreateDirectory(unpackedDir);
        var root = Path.GetFullPath(unpackedDir).TrimEnd('\\') + "\\";
        var options = new ReaderOptions { LookForHeader = true, LeaveStreamOpen = true };
        using var archive = RarArchive.OpenArchive(volumes, options);
        var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
        long total = entries.Sum(e => e.Size), done = 0;
        var buf = new byte[1 << 20];
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.GetFullPath(Path.Combine(unpackedDir, e.Key!.Replace('/', '\\')));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The installer archive holds an unsafe path: {e.Key}");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using (var src = e.OpenEntryStream())
            using (var dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            {
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    dst.Write(buf, 0, n);
                    done += n;
                    progress?.Report((done, total));
                }
            }
            if (new FileInfo(dest).Length != e.Size)
                throw new InvalidDataException($"{e.Key} did not unpack completely.");
        }
        if (File.Exists(Path.Combine(unpackedDir, "FFXISetup.exe"))) return unpackedDir;
        return Directory.GetDirectories(unpackedDir).FirstOrDefault(d => File.Exists(Path.Combine(d, "FFXISetup.exe")))
               ?? throw new InvalidDataException("The unpacked installer has no FFXISetup.exe.");
    }

    public async Task<IReadOnlyList<string>> RunInstallAsync(string unpackedDir, string installRoot, string logDir, PolRegion region,
                                                             IProgress<InstallProgress>? progress, CancellationToken ct,
                                                             bool payloadInPlace = false)
    {
        var (viewerCode, ffxiCode) = ProductCodesFor(region);
        var warnings = new List<string>();
        Directory.CreateDirectory(installRoot);
        Directory.CreateDirectory(logDir);

        var dxsetup = Path.Combine(unpackedDir, "redist", "Directx_Jun2010", "DXSETUP.exe");
        if (File.Exists(dxsetup) && DirectXJun2010Present())
            Say(progress, "The DirectX runtime is already installed.");
        else if (File.Exists(dxsetup))
        {
            Say(progress, "Installing the DirectX runtime...");
            await RunDirectXAsync(dxsetup, warnings, ct, () => Say(progress, "The install will stop once the DirectX runtime has finished installing."));
        }

        // the FFXI package's own DISM step, Sysnative so a 32-bit host gets the 64-bit DISM
        var dism = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32", "dism.exe");
        bool restartPending = false;
        if (File.Exists(dism))
        {
            Say(progress, "Turning on DirectPlay...");
            int dp = await RunAsync(dism, "/online /Enable-Feature /all /FeatureName:DirectPlay /NoRestart", ct,
                                    () => Say(progress, "The install will stop once Windows has finished turning on DirectPlay."));
            if (dp == 3010) restartPending = true;
            else if (dp != 0) throw Shown.Mark(new InvalidOperationException($"Windows couldn't turn on DirectPlay, which FINAL FANTASY XI needs (DISM code {dp})."));
        }

        var polMsi = FindFirst(unpackedDir, ViewerFolder, ViewerNames)
                     ?? throw Shown.Mark(new FileNotFoundException(NoViewerPackage));
        await InstallPackageAsync(polMsi, viewerCode, region, PolPaths.ViewerProduct, "pol.exe", installRoot, PolShortcut,
                                  "PlayOnline Viewer", Path.Combine(logDir, "playonline.log"), progress, warnings, ct, restartPending: false,
                                  payloadInPlace);

        if (PolPaths.TryGetInstallFolder(region, PolPaths.ViewerProduct) is { } viewer &&
            Directory.GetParent(viewer.TrimEnd('\\'))?.Parent is { } polRoot &&
            !string.Equals(Path.TrimEndingDirectorySeparator(polRoot.FullName), Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot)),
                           StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"The PlayOnline Viewer was already installed in {polRoot.FullName}, so FINAL FANTASY XI was installed there too.");
            installRoot = polRoot.FullName;
        }

        var ffxiMsi = FindFirst(unpackedDir, FfxiFolder, FfxiNames) ?? throw Shown.Mark(new FileNotFoundException(NoFfxiPackage));
        await InstallPackageAsync(ffxiMsi, ffxiCode, region, PolPaths.FfxiProduct, "FFXiMain.dll", installRoot, FfxiShortcut,
                                  "FINAL FANTASY XI", Path.Combine(logDir, "ffxi.log"), progress, warnings, ct, restartPending,
                                  payloadInPlace);
        if (restartPending) warnings.Add("Restart Windows before you play, because DirectPlay (which the game needs) only turns on after a restart.");
        return warnings;
    }

    // June 2010 was the last DirectX runtime and the only source of these 32-bit files, so if they're here DXSETUP
    // would change nothing and just add minutes
    public static bool DirectXJun2010Present()
    {
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        return new[] { "d3dx9_43.dll", "xinput1_3.dll", "D3DCompiler_43.dll", "d3dx11_43.dll", "XAudio2_7.dll", "X3DAudio1_7.dll" }
            .All(f => File.Exists(Path.Combine(sys, f)));
    }

    internal static async Task RunDirectXAsync(string dxsetup, List<string> warnings, CancellationToken ct, Action? stopping = null)
    {
        int code = await RunAsync(dxsetup, "/silent", ct, stopping);
        if (code != 0) warnings.Add($"The DirectX runtime installer stopped with code {code}. Game controllers in XInput mode might need it.");
    }

    private static void Say(IProgress<InstallProgress>? progress, string text, double fraction = -1) =>
        progress?.Report(new InstallProgress("Installing", text, fraction, 0, 0));

    private static async Task InstallPackageAsync(string msi, string productCode, PolRegion region, string registryValue, string keyFile,
                                                  string installRoot, string shortcutProperty, string name, string logPath,
                                                  IProgress<InstallProgress>? progress, List<string> warnings, CancellationToken ct,
                                                  bool restartPending, bool payloadInPlace)
    {
        // The main file alone never counts, the mirror might have only just put it there. The InstallFolder value is
        // what marks a product installed, Apply writes it last.
        if (MsiInstaller.IsInstalled(productCode) ||
            (PolPaths.TryGetInstallFolder(region, registryValue) is { } dir && File.Exists(Path.Combine(dir, keyFile))))
        {
            Say(progress, $"{name} is already installed.", 1);
            warnings.Add($"{name} was already installed, so it's been left as it is.");
            return;
        }
        Say(progress, $"Preparing {name}...");
        string? unsupported;
        using (var pkg = new MsiPackage(msi)) unsupported = pkg.Supported();
        if (unsupported != null)
        {
            if (payloadInPlace)
                throw new NotSupportedException($"The {name} package uses {unsupported}, so it cannot be applied over files that are already in place.");
            // the FFXI package's own DISM step fails the whole run while DirectPlay's waiting for a restart
            if (restartPending) throw new RestartRequiredException();
            await RunMsiAsync(msi, installRoot, shortcutProperty, name, logPath, progress, warnings, ct);
            return;
        }
        var report = new Progress<(long done, long total)>(p => progress?.Report(new InstallProgress("Installing",
            $"Installing {name}: {p.done / 1073741824.0:0.00} of {p.total / 1073741824.0:0.00} GiB",
            p.total > 0 ? (double)p.done / p.total : -1, p.done, p.total)));
        var (props, uninstall) = DirectApplySetup(msi, installRoot, shortcutProperty, region);
        var result = await Task.Run(() =>
        {
            using var pkg = new MsiPackage(msi);
            return pkg.Apply(installRoot, props, report, ct, uninstall: uninstall,
                             status: text => Say(progress, $"{name}: {text}"), payloadInPlace: payloadInPlace);
        }, ct);
        File.WriteAllText(logPath, $"{name} applied directly: {result.Files:N0} files ({result.Bytes:N0} bytes), " +
                                   $"{result.RegistryValues} registry values, {result.Classes} COM classes, {result.TypeLibs} type libraries, " +
                                   $"{result.Shortcuts} shortcuts.\r\n");
    }

    internal static (Dictionary<string, string> Properties, MsiPackage.UninstallSetup Uninstall) DirectApplySetup(
        string msi, string installRoot, string shortcutProperty, PolRegion region)
    {
        var props = new Dictionary<string, string> { ["ALLUSERS"] = "1", [shortcutProperty] = "1" };
        bool pol = shortcutProperty == PolShortcut;
        var uninstaller = Path.Combine(AppContext.BaseDirectory, "native", pol ? PolUninstaller : FfxiUninstaller);
        if (!File.Exists(uninstaller)) throw Shown.Mark(new FileNotFoundException("Some of Provenance's own files are missing. Reinstall Provenance, then try again."));
        // the Japanese installs keep their settings under SQUARE, not SquareEnix
        var hive = PolPaths.Hive(region);
        var settings = pol ? $@"{hive}\{(region == PolRegion.Japan ? "SQUARE" : "SquareEnix")}\PlayOnlineViewer" : FfxiConfig.KeyPath(region);
        return (props, new MsiPackage.UninstallSetup(uninstaller, Path.Combine(installRoot, "Uninstall"), Path.GetDirectoryName(msi)! + "\\",
                                                     hive, pol ? PolPaths.ViewerProduct : PolPaths.FfxiProduct, settings));
    }

    private static async Task RunMsiAsync(string msi, string installRoot, string shortcutProperty, string name,
                                          string logPath, IProgress<InstallProgress>? progress, List<string> warnings,
                                          CancellationToken ct)
    {
        var props = new Dictionary<string, string>
            { ["ALLUSERS"] = "1", ["INSTALLDIR"] = installRoot.TrimEnd('\\') + "\\", [shortcutProperty] = "1" };
        var report = new Progress<MsiInstaller.Report>(r => progress?.Report(new InstallProgress(
            "Installing", r.RollingBack ? $"Undoing the {name} install..." :
                          r.Action.Length > 0 ? $"{name}: {r.Action}" : $"Installing {name}",
            r.Fraction ?? -1, 0, 0)));
        string? lastError = null;
        for (int attempt = 0; ; attempt++)
        {
            Say(progress, $"Installing {name}...");
            int code = await Task.Run(() => MsiInstaller.Install(msi, props, logPath, report, ct, e => lastError = e), CancellationToken.None);
            switch (code)
            {
                case MsiInstaller.Success:
                    return;
                case MsiInstaller.RebootRequired:
                case MsiInstaller.RebootInitiated:
                    warnings.Add($"Windows needs a restart to finish the {name} install.");
                    return;
                case MsiInstaller.UserExit when ct.IsCancellationRequested:
                    throw new OperationCanceledException(ct);
                case MsiInstaller.AlreadyRunning when attempt < 20:
                    Say(progress, "Waiting for another installation on this PC to finish...");
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    continue;
                default:
                    // Windows Installer's own words ride along for the log, the screen gets the code
                    throw Shown.Mark(new InvalidOperationException(
                        $"The {name} installer stopped with an error (Windows Installer code {code}). Its log is at {logPath}.",
                        lastError is { Length: > 0 } ? new Exception(lastError) : null));
            }
        }
    }

    public static (string Viewer, string Ffxi) FindPackages(string unpackedDir) =>
        (FindFirst(unpackedDir, ViewerFolder, ViewerNames) ?? throw Shown.Mark(new FileNotFoundException(NoViewerPackage)),
         FindFirst(unpackedDir, FfxiFolder, FfxiNames) ?? throw Shown.Mark(new FileNotFoundException(NoFfxiPackage)));

    private static string? FindFirst(string root, string sub, IEnumerable<string> names)
    {
        var dir = Path.Combine(root, sub);
        return names.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists)
               ?? (Directory.Exists(dir) ? Directory.GetFiles(dir, "*.msi").FirstOrDefault() : null);
    }

    // a stop waits for it to finish, DXSETUP or DISM killed part way can leave the DirectX runtime half installed
    internal static async Task<int> RunAsync(string exe, string args, CancellationToken ct, Action? stopping = null)
    {
        ct.ThrowIfCancellationRequested();
        using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true })
                      ?? throw Shown.Mark(new InvalidOperationException($"Windows couldn't start {Path.GetFileName(exe)}."));
        using (ct.Register(() => stopping?.Invoke()))
            await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        return p.ExitCode;
    }

    // null when it worked, else the error to show
    public static string? LocateExisting(string folder, PolRegion region, Action<Exception>? failed = null)
    {
        try
        {
            var ffxi = FindGameFolder(folder, "FFXiMain.dll");
            var viewer = FindGameFolder(folder, "pol.exe");
            if (ffxi == null && viewer == null)
                return "Couldn't find FINAL FANTASY XI in that folder.";
            // the viewer's own env.dat says which region's keys its pol.exe reads
            if (viewer != null && PolEnv.Region(viewer) is { } own) region = own;
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = baseKey.CreateSubKey(PolPaths.Hive(region) + @"\InstallFolder", writable: true)
                            ?? throw Shown.Mark(new InvalidOperationException(NeedsAdmin));
            if (viewer != null) key.SetValue(PolPaths.ViewerProduct, viewer.TrimEnd('\\') + "\\", RegistryValueKind.String);
            if (ffxi != null) key.SetValue(PolPaths.FfxiProduct, ffxi.TrimEnd('\\') + "\\", RegistryValueKind.String);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return NeedsAdmin;
        }
        catch (Exception ex)
        {
            failed?.Invoke(ex);
            return Shown.Is(ex) ? ex.Message : "Provenance couldn't save where the game is installed.";
        }
    }

    private static string? FindGameFolder(string start, string marker)
    {
        if (!Directory.Exists(start)) return null;
        if (File.Exists(Path.Combine(start, marker))) return start;
        return Directory.EnumerateDirectories(start).FirstOrDefault(d => File.Exists(Path.Combine(d, marker)))
               ?? Directory.EnumerateDirectories(start).SelectMany(SafeDirs)
                      .FirstOrDefault(e => File.Exists(Path.Combine(e, marker)));
    }

    private static IEnumerable<string> SafeDirs(string d)
    {
        try { return Directory.EnumerateDirectories(d); } catch { return []; }
    }
}
