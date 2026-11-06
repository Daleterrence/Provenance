using System.Collections.Concurrent;
using System.Net.Sockets;
using Provenance.Common.Mirror;
using Provenance.Common.Patcher;
using Provenance.Common.PolFormat;
using Provenance.Common.Progress;
using Provenance.Common.Security;
using Serilog;
using Serilog.Events;

namespace Provenance.Common.Install;

public sealed record StagedProduct(string Slug, PatchProduct Product, string Version, bool Current);

internal sealed record InstallData(MirrorPointer Pointer, string Version, string ViewerMsi, string FfxiMsi)
{
    public string MsiFor(GameProduct product) => product == GameProduct.Viewer ? ViewerMsi : FfxiMsi;
}

// installer data goes to a staging folder laid out like the unpacked installer, game files straight to where the
// packages put them
public sealed class InstallFromMirror(MirrorClient client, string region, string logName = "Mirror install",
                                      LogEventLevel logLevel = LogEventLevel.Warning)
{
    private void Say(string line) => Log.Write(logLevel, "{Source}: {Message}", logName, line);

    private const string InstallSlug = "install";
    private const string PrepareStage = "Preparing";
    public const string PrepareText = "Getting the download ready...";
    // no detail line while downloading, the stage already says what's coming down (the reports carry Download instead)
    public const string DownloadText = "";
    private const string ScanText = "Checking the files already downloaded...";

    private PolRegion Region => MirrorLayout.RegionOf(region);

    public Func<PatchProduct, CancellationToken, string> ServerVersion { get; init; } = (p, ct) => new PolUpdater(p).Check(ct).Server;

    public Func<GameProduct, string?> RegisteredFolder { get; init; } = product => PolPaths.TryGetInstallFolder(MirrorLayout.RegionOf(region), product);

    public Func<string, bool> InstalledByWindowsInstaller { get; init; } = MsiInstaller.IsInstalled;

    public string? SkipReason { get; private set; }

    public IReadOnlyList<StagedProduct> Staged { get; private set; } = [];

    // the game's file list when StageAsync found the mirror on POL's current version, so sizing the first update after
    // SE's parts fetches nothing again. Null when that update could come from POL
    public MirrorIndex? GameIndex { get; private set; }

    // null with SkipReason when the mirror can't serve it or an install's in the way, nothing under installRoot gets
    // touched then
    public async Task<string?> StageAsync(string stagingDir, string installRoot, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        Staged = [];
        SkipReason = null;
        GameIndex = null;
        if (await FetchInstallDataAsync(stagingDir, null, progress, ct) is not { } data) return null;
        var pointer = data.Pointer;

        var products = GameProduct.All;
        var folders = new string[products.Count];
        var targets = new List<string>[products.Count];
        for (int i = 0; i < products.Count; i++)
        {
            var product = products[i];
            var name = product.Name;
            var msi = data.MsiFor(product);
            if (ReadPackage(msi, installRoot, product, out var folder, out targets[i]) is { } unusable)
                return Skip<string>(unusable);
            // RunInstallAsync keeps an earlier install, so the files have to go there and it has to be complete
            if (RegisteredFolder(product) is { } registered)
            {
                if (!PathInside.Same(registered, folder)) return Skip<string>($"{name} is already registered in {registered}");
                if (!File.Exists(Path.Combine(folder, product.KeyFile)))
                    return Skip<string>($"{name} is registered in {registered}, but {product.KeyFile} is missing there");
            }
            else if (InstalledByWindowsInstaller(product.ProductCode(Region)))
                return Skip<string>($"{name} is installed with Windows Installer, but its install folder is not registered");
            folders[i] = folder;
        }

        var viewerDir = folders[0];
        var plans = new List<(StagedProduct Product, MirrorIndex Index, string Name)>();
        for (int i = 0; i < products.Count; i++)
        {
            var (slug, name) = (products[i].Slug, products[i].Name);
            // the region's hive, its Interface value keys patch.ver
            var product = PatchProduct.For(products[i], folders[i], viewerDir, Region);
            progress?.Report(new InstallProgress(PrepareStage, $"Checking the {name} version...", -1, 0, 0));
            string? server = null;
            try { server = await Task.Run(() => ServerVersion(product, ct), ct); }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException)
            {
                Say($"the {name} version check failed ({ex.Message}); installing the mirror's newest version");
            }
            var index = server is null ? null : await client.GetIndexAsync(region, slug, server, ct);
            if (index is null)
            {
                if (!pointer.Versions.TryGetValue(slug, out var newest))
                    return Skip<string>($"the mirror pointer names no {slug} version");
                index = await client.GetIndexAsync(region, slug, newest, ct);
                if (index is null) return Skip<string>($"the {slug} index {newest} is missing or failed verification");
                if (server is not null) Say($"the mirror does not have {name} {server} yet; installing {newest}, and the first update brings it current");
            }
            if (products[i] == GameProduct.Game && index.Version == server) GameIndex = index;
            if (MirrorDownloads.Unusable(folders[i], index) is { } unsafePath) return Skip<string>(unsafePath);
            // Apply needs every file its package installs, a missing one would only stop it after the whole download
            if (Unlisted(folders[i], targets[i], index) is { } unlisted)
            {
                var reason = $"the {slug} index {index.Version} does not list {unlisted}, which the {name} package installs";
                Say(reason);
                return Skip<string>(reason);
            }
            plans.Add((new StagedProduct(slug, product, index.Version, index.Version == server), index, name));
        }

        await FetchAsync(plans.Select(p => (p.Product.Product.Folder, p.Index, p.Name, true)).ToList(), progress, ct);

        // pol.exe's boot step now, the downloads gave viewer\com\*.dll and their patchfiles copies different times
        if (PolUpdater.QueuedWorkPending(viewerDir)) PolUpdater.ApplyQueued(viewerDir);
        Staged = plans.Select(p => p.Product).ToList();
        return stagingDir;
    }

    // last of all, so a stopped install never has a patch.ver claiming a version
    public void WriteVersions()
    {
        foreach (var s in Staged) PatchVer.Write(s.Product, s.Version);
    }

    internal async Task<InstallData?> FetchInstallDataAsync(string stagingDir, Func<MirrorEntry, bool>? include,
                                                            IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        SkipReason = null;
        progress?.Report(new InstallProgress(PrepareStage, PrepareText, -1, 0, 0));
        var pointer = await client.GetPointerAsync(region, ct);
        if (pointer is null || pointer.Region != region) return Skip<InstallData>($"the mirror has no valid pointer for {region}");
        if (!pointer.Versions.TryGetValue(InstallSlug, out var installVersion))
            return Skip<InstallData>("the mirror pointer names no install data");
        var installIndex = await client.GetIndexAsync(region, InstallSlug, installVersion, ct);
        if (installIndex is null) return Skip<InstallData>($"the install index {installVersion} is missing or failed verification");
        if (MirrorDownloads.Unusable(stagingDir, installIndex) is { } bad) return Skip<InstallData>(bad);
        if (include is not null) installIndex = installIndex with { Files = installIndex.Files.Where(include).ToList() };

        await FetchAsync([(stagingDir, installIndex, "installer", false)], progress, ct);
        try
        {
            var (viewerMsi, ffxiMsi) = GameInstaller.FindPackages(stagingDir);
            return new InstallData(pointer, installVersion, viewerMsi, ffxiMsi);
        }
        catch (FileNotFoundException ex) { return Skip<InstallData>($"the install data {installVersion} is unusable: {ex.Message}"); }
    }

    internal static string? ReadPackage(string msi, string installRoot, GameProduct product, out string folder, out List<string> targets)
    {
        var (name, keyFile) = (product.Name, product.KeyFile);
        folder = "";
        targets = [];
        try
        {
            using var pkg = new MsiPackage(msi);
            if (pkg.Supported() is { } missing) return $"the {name} package uses {missing}";
            targets = pkg.Targets(installRoot).Select(t => t.Path).ToList();
        }
        catch (InvalidDataException ex) { return $"the {name} package cannot be read: {ex.Message}"; }
        return MsiPackage.ProductFolder(targets, installRoot, keyFile, out folder) is { } problem ? $"the {name} package {problem}" : null;
    }

    private T? Skip<T>(string reason) where T : class
    {
        SkipReason = reason;
        return null;
    }

    private static string? Unlisted(string folder, IEnumerable<string> targets, MirrorIndex index)
    {
        var listed = new HashSet<string>(index.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
        {
            if (!PathInside.IsUnder(folder, t)) return t;
            var rel = Path.GetRelativePath(folder, t).Replace('\\', '/');
            if (!listed.Contains(rel)) return rel;
        }
        return null;
    }

    private async Task FetchAsync(IReadOnlyList<(string Folder, MirrorIndex Index, string Name, bool Product)> targets,
                                  IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var work = new List<IReadOnlyList<MirrorEntry>>();
        foreach (var (folder, index, _, _) in targets)
        {
            progress?.Report(new InstallProgress(PrepareStage, ScanText, -1, 0, 0));
            work.Add(await MirrorDownloads.ScanAsync(folder, index, Bytes(progress, PrepareStage, ScanText, index.Files.Sum(f => f.Size)), ct,
                                                keepLargeAddressAware: true));
        }
        for (int i = 0; i < targets.Count; i++)
        {
            var (folder, index, name, isProduct) = targets[i];
            var stage = isProduct ? $"Downloading {name}" : "Downloading the installer data";
            long total = MirrorDownloads.DownloadSize(work[i]);
            progress?.Report(new InstallProgress(stage, DownloadText, 0, 0, total, Download: true));
            // drop patch.ver before touching a folder, so a stopped run never claims a version
            if (isProduct && work[i].Count > 0) PatchVer.Drop(folder);
            var reasons = new ConcurrentDictionary<string, Exception>(StringComparer.OrdinalIgnoreCase);
            var failed = await MirrorDownloads.DownloadAllAsync(client, region, folder, work[i], MirrorDownloads.DefaultParallel,
                Bytes(progress, stage, DownloadText, total, download: true), ct,
                (e, ex) =>
                {
                    reasons[e.Path] = ex;
                    Say($"{index.Product} file {e.Path} failed: {ex.GetType().Name}: {ex.Message}");
                },
                onRetry: (e, attempt, ex) => Say($"{index.Product} file {e.Path} attempt {attempt} failed, trying again: {ex.GetType().Name}: {ex.Message}"));
            if (failed.Count > 0)
            {
                var files = failed.Select(e => e.Path).ToList();
                var names = MirrorDownloads.NameList(files);
                // a write that failed here (disk full, a locked file) is the PC's to fix, SE's parts would fail too
                if (files.Any(f => reasons.TryGetValue(f, out var ex) && MirrorFailure.IsLocal(ex)))
                    throw Shown.Mark(new IOException($"{files.Count} {name} {(files.Count == 1 ? "file" : "files")} couldn't be written: {names}. Free up some space or close whatever's using them, then press Try again."));
                throw new MirrorUnusableException($"{files.Count} {name} file(s) could not be downloaded: {names}.", files);
            }
        }
    }

    private static IProgress<ByteProgress>? Bytes(IProgress<InstallProgress>? progress, string stage, string text, long total,
                                                             bool download = false) =>
        progress is null ? null : new InlineProgress<ByteProgress>(p => progress.Report(new InstallProgress(stage, text,
            total > 0 ? (double)p.Done / total : 1, p.Done, total, download)));
}
