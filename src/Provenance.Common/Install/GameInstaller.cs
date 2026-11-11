using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Win32;
using Provenance.Common.Game;
using Provenance.Common.PolFormat;
using Provenance.Common.Progress;
using Provenance.Common.Security;
using SharpCompress.Archives.Rar;
using SharpCompress.Readers;

namespace Provenance.Common.Install;

public sealed record GamePart(string FileName, Uri Url, long Size, string Md5);

// Fraction is -1 while there's nothing to measure. Download marks bytes coming over the network, the only ones that get
// a rate
public sealed record InstallProgress(string Stage, string Detail, double Fraction, long DoneBytes, long TotalBytes, bool Download = false,
                              GameProduct? Product = null);

public sealed record PartStep(GamePart Part, int Index, int Count, long DoneBefore, long Total);

// What the caller does at each step of DownloadAndExtractAsync. The progress ones are made fresh for every attempt
public sealed class DownloadSteps
{
    public Action? Downloading { get; init; }
    public Func<PartStep, IProgress<long>?>? Part { get; init; }
    public Action? Unpacking { get; init; }
    public Func<IProgress<ByteProgress>?>? UnpackProgress { get; init; }
    public Func<IProgress<ByteProgress>?>? CheckProgress { get; init; }
    public Action<DamagedPartException>? Damaged { get; init; }
}

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

public sealed class GameInstaller
{
    public const long RequiredFreeBytes = 25L * 1024 * 1024 * 1024;

    public static bool CanInstall(PolRegion region) => GameProduct.Installs(region);

    private const string NeedsAdmin =
        "Windows didn't let Provenance save where the game is installed.";

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
#pragma warning disable RS0030 // GameInstaller owns its client
        _http = http ?? new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None })
            { Timeout = Timeout.InfiniteTimeSpan };
#pragma warning restore RS0030
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
            throw Shown.Mark(new InvalidDataException(
                $"The installer part {part.FileName} didn't match the one this version of Provenance installs, please try again, " +
                "and if it keeps happening, check for a Provenance update."));
        }
        File.Move(partial, dest, overwrite: true);
    }

    // a part that fails its check is deleted and fetched once more
    public Task<string> DownloadAndExtractAsync(PolRegion region, string partsDir, string unpackedDir, DownloadSteps steps, CancellationToken ct) =>
        DownloadAndExtractAsync(PartsFor(region), partsDir, unpackedDir, steps, ct);

    internal async Task<string> DownloadAndExtractAsync(IReadOnlyList<GamePart> parts, string partsDir, string unpackedDir, DownloadSteps steps, CancellationToken ct)
    {
        long total = parts.Sum(p => p.Size);
        for (int attempt = 1; ; attempt++)
        {
            steps.Downloading?.Invoke();
            long doneBefore = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                var progress = steps.Part?.Invoke(new PartStep(parts[i], i, parts.Count, doneBefore, total));
                await DownloadPartAsync(parts[i], partsDir, progress, ct);
                doneBefore += parts[i].Size;
            }
            steps.Unpacking?.Invoke();
            try
            {
                return await ExtractAsync(parts, partsDir, unpackedDir, steps.UnpackProgress?.Invoke(), ct, steps.CheckProgress?.Invoke());
            }
            catch (DamagedPartException e) when (attempt < 2)
            {
                steps.Damaged?.Invoke(e);
            }
        }
    }

    // Each part stays open read-only from its MD5 check to the end, so the bytes checked are the bytes unpacked
    public Task<string> ExtractAsync(string downloadDir, string unpackedDir, PolRegion region,
                                     IProgress<ByteProgress>? progress, CancellationToken ct,
                                     IProgress<ByteProgress>? checkProgress = null) =>
        ExtractAsync(PartsFor(region), downloadDir, unpackedDir, progress, ct, checkProgress);

    internal Task<string> ExtractAsync(IReadOnlyList<GamePart> parts, string downloadDir, string unpackedDir,
                                       IProgress<ByteProgress>? progress, CancellationToken ct,
                                       IProgress<ByteProgress>? checkProgress = null) => Task.Run(() =>
    {
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
                    checkProgress?.Report(new ByteProgress(checkDone, checkTotal));
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

    private static string Unpack(List<FileStream> volumes, string unpackedDir, IProgress<ByteProgress>? progress, CancellationToken ct)
    {
        // emptied first so only what this run unpacked ever runs elevated
        if (Directory.Exists(unpackedDir)) Directory.Delete(unpackedDir, recursive: true);
        Directory.CreateDirectory(unpackedDir);
        var options = new ReaderOptions { LookForHeader = true, LeaveStreamOpen = true };
        using var archive = RarArchive.OpenArchive(volumes, options);
        var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
        long total = entries.Sum(e => e.Size), done = 0;
        var buf = new byte[1 << 20];
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            var dest = PathInside.Collapse(unpackedDir, e.Key!) ?? throw new InvalidDataException($"The installer archive holds an unsafe path: {e.Key}");
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
                    progress?.Report(new ByteProgress(done, total));
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
        var dism = Path.Combine(AppPaths.Windows,
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

        var polMsi = Package(unpackedDir, GameProduct.Viewer);
        await InstallPackageAsync(polMsi, GameProduct.Viewer, region, installRoot, Path.Combine(logDir, GameProduct.Viewer.LogFile),
                                  progress, warnings, ct, payloadInPlace);

        if (PolPaths.TryGetInstallFolder(region, GameProduct.Viewer) is { } viewer &&
            Directory.GetParent(viewer.TrimEnd('\\'))?.Parent is { } polRoot &&
            !PathInside.Same(polRoot.FullName, installRoot))
        {
            warnings.Add($"The {GameProduct.Viewer.Name} was already installed in {polRoot.FullName}, so {GameProduct.Game.Name} was installed there too.");
            installRoot = polRoot.FullName;
        }

        var ffxiMsi = Package(unpackedDir, GameProduct.Game);
        await InstallPackageAsync(ffxiMsi, GameProduct.Game, region, installRoot, Path.Combine(logDir, GameProduct.Game.LogFile),
                                  progress, warnings, ct, payloadInPlace);
        if (restartPending) warnings.Add("Restart Windows before you play, because DirectPlay (which the game needs) only turns on after a restart.");
        return warnings;
    }

    // June 2010 was the last DirectX runtime and the only source of these 32-bit files, so if they're here DXSETUP
    // would change nothing and just add minutes
    public static bool DirectXJun2010Present()
    {
        var sys = AppPaths.SystemX86;
        return new[] { "d3dx9_43.dll", "xinput1_3.dll", "D3DCompiler_43.dll", "d3dx11_43.dll", "XAudio2_7.dll", "X3DAudio1_7.dll" }
            .All(f => File.Exists(Path.Combine(sys, f)));
    }

    internal static async Task RunDirectXAsync(string dxsetup, List<string> warnings, CancellationToken ct, Action? stopping = null)
    {
        int code = await RunAsync(dxsetup, "/silent", ct, stopping);
        if (code != 0) warnings.Add($"The DirectX runtime installer stopped with code {code}. Game controllers in XInput mode might need it.");
    }

    private static void Say(IProgress<InstallProgress>? progress, string text, double fraction = -1, GameProduct? product = null) =>
        progress?.Report(new InstallProgress("Installing", text, fraction, 0, 0, Product: product));

    private static async Task InstallPackageAsync(string msi, GameProduct product, PolRegion region, string installRoot, string logPath,
                                                  IProgress<InstallProgress>? progress, List<string> warnings, CancellationToken ct,
                                                  bool payloadInPlace)
    {
        var name = product.Name;
        // The main file alone never counts, the mirror might have only just put it there. The InstallFolder value is
        // what marks a product installed, Apply writes it last.
        if (MsiInstaller.IsInstalled(product.ProductCode(region)) ||
            (PolPaths.TryGetInstallFolder(region, product) is { } dir && File.Exists(Path.Combine(dir, product.KeyFile))))
        {
            Say(progress, $"{name} is already installed.", 1, product);
            warnings.Add($"{name} was already installed, so it's been left as it is.");
            return;
        }
        Say(progress, $"Preparing {name}...", product: product);
        var report = new Progress<ByteProgress>(p => progress?.Report(new InstallProgress("Installing",
            $"Installing {name}: {ByteText.GbNumber(p.Done, 2)} of {ByteText.GbNumber(p.Total, 2)} GB",
            p.Total > 0 ? (double)p.Done / p.Total : -1, p.Done, p.Total, Product: product)));
        var (props, uninstall) = DirectApplySetup(msi, installRoot, product, region);
        var result = await Task.Run(() =>
        {
            using var pkg = new MsiPackage(msi);
            return pkg.Apply(installRoot, props, report, ct, uninstall: uninstall,
                             status: text => Say(progress, $"{name}: {text}", product: product), payloadInPlace: payloadInPlace);
        }, ct);
        File.WriteAllText(logPath, $"{name} applied directly: {result.Files:N0} files ({result.Bytes:N0} bytes), " +
                                   $"{result.RegistryValues} registry values, {result.Classes} COM classes, {result.TypeLibs} type libraries, " +
                                   $"{result.Shortcuts} shortcuts.\r\n");
    }

    internal static (Dictionary<string, string> Properties, MsiPackage.UninstallSetup Uninstall) DirectApplySetup(
        string msi, string installRoot, GameProduct product, PolRegion region)
    {
        var props = new Dictionary<string, string> { ["ALLUSERS"] = "1", [product.ShortcutProperty] = "1" };
        var uninstaller = Path.Combine(AppContext.BaseDirectory, "native", product.Uninstaller);
        if (!File.Exists(uninstaller)) throw Shown.Mark(new FileNotFoundException("Some of Provenance's own files are missing. Reinstall Provenance, then try again."));
        return (props, new MsiPackage.UninstallSetup(uninstaller, Path.Combine(installRoot, "Uninstall"), Path.GetDirectoryName(msi)! + "\\",
                                                     PolPaths.Hive(region), product.InstallValue, product.SettingsKey(region)));
    }

    public static (string Viewer, string Ffxi) FindPackages(string unpackedDir) =>
        (Package(unpackedDir, GameProduct.Viewer), Package(unpackedDir, GameProduct.Game));

    private static string Package(string root, GameProduct product) =>
        File.Exists(product.PackageIn(root)) ? product.PackageIn(root) : throw Shown.Mark(new FileNotFoundException(product.NoPackageText));

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
            var ffxi = FindGameFolder(folder, GameProduct.Game.KeyFile);
            var viewer = FindGameFolder(folder, GameProduct.Viewer.KeyFile);
            if (ffxi == null && viewer == null)
                return $"Couldn't find {GameProduct.Game.Name} in that folder.";
            // the viewer's own env.dat says which region's keys its pol.exe reads
            if (viewer != null && PolEnv.Region(viewer) is { } own) region = own;
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = baseKey.CreateSubKey(PolPaths.Hive(region) + @"\InstallFolder", writable: true)
                            ?? throw Shown.Mark(new InvalidOperationException(NeedsAdmin));
            if (viewer != null) key.SetValue(GameProduct.Viewer.InstallValue, viewer.TrimEnd('\\') + "\\", RegistryValueKind.String);
            if (ffxi != null) key.SetValue(GameProduct.Game.InstallValue, ffxi.TrimEnd('\\') + "\\", RegistryValueKind.String);
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
