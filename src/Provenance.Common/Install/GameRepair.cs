using Provenance.Common.Mirror;
using Provenance.Common.Patcher;
using Provenance.Common.PolFormat;
using Provenance.Common.Progress;
using Provenance.Common.Security;
using Serilog;
using Serilog.Events;

namespace Provenance.Common.Install;

public sealed record RepairPlan(IReadOnlyDictionary<string, IReadOnlyList<MirrorEntry>> Work, long Bytes,
                                IReadOnlyDictionary<string, MirrorIndex> Indexes)
{
    public int Count => Work.Values.Sum(w => w.Count);

    // products whose patch.ver doesn't hold the version the files were checked against, read the way POL reads it
    public IReadOnlyList<string> StaleVersions { get; init; } = [];

    // the viewer's own update left half done (patch.ini and .tmp2 files), it'd overwrite good files at the next launch
    public bool QueuePending { get; init; }

    public bool DirectXMissing { get; init; }

    public int Issues => Count + StaleVersions.Count + (QueuePending ? 1 : 0) + (DirectXMissing ? 1 : 0);
}

// FilesReplaced counts game files, OtherFixes the rest (registry, COM, the uninstall entry, DirectX, patch.ver, the queue)
public sealed record RepairResult(int FilesReplaced, int OtherFixes);

// repairs by size and SHA-256, not POL's signatures, and the caller holds the update lock. Nothing that's already
// right gets written again, the registry and shortcuts included
public sealed class GameRepair(MirrorClient client, string region)
{
    public const string DirectXFailed =
        "The DirectX runtime FINAL FANTASY XI needs couldn't be installed. Run the repair again.";

    public Func<PatchProduct, string, CancellationToken, string> ServerVersion { get; init; } = (p, local, ct) => new PolUpdater(p).Check(local, ct).Server;

    public Func<string, bool> InstalledByWindowsInstaller { get; init; } = MsiInstaller.IsInstalled;

    // the viewer's web cache index. It's installed empty and the viewer rewrites or loses it as it runs, so whatever's
    // there is fine, and a missing one only has to exist
    private static readonly string[] ViewerOwned = ["usr/all/url/cache/dcfat0.bin"];

    private static bool IsOwned(string slug, string path) =>
        slug == GameProduct.Viewer.Slug && ViewerOwned.Contains(path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);

    private static async Task<IReadOnlyList<MirrorEntry>> ScanAsync(string slug, string folder, MirrorIndex index, IProgress<ByteProgress>? progress, CancellationToken ct)
    {
        var damaged = await MirrorDownloads.ScanAsync(folder, index, progress, ct, keepLargeAddressAware: true);
        return damaged.Where(e => !IsOwned(slug, e.Path)).ToList();
    }

    private static void Say(string line) => Log.Information("Repair: {Message}", line);

    public Func<bool> DirectXPresent { get; init; } = GameInstaller.DirectXJun2010Present;

    // admin-only, DXSETUP runs from it as admin. Only skipInstallData gets away without one.
    public string? StagingDir { get; init; }

    // runs on worker threads
    public Action<string>? Status { get; init; }

    // once, right before the first game file changes
    public Action? Changing { get; init; }

    // once the registry pass has put something right (it can't be stopped half way, so this comes after it), and before
    // DXSETUP. Fixing is as each of those starts
    public Action? Fixed { get; init; }

    public Action? Fixing { get; init; }

    public string? MissingVersion { get; private set; }

    private bool _changing;

    public async Task<RepairPlan?> PlanAsync(string viewerDir, string ffxiDir, IProgress<ByteProgress>? progress, CancellationToken ct)
    {
        MissingVersion = null;
        var found = new List<(PatchProduct Product, MirrorIndex Index)>();
        var stale = new List<string>();
        foreach (var product in ProductsOf(viewerDir, ffxiDir))
        {
            var slug = product.Game.Slug;
            Status?.Invoke($"Checking the {product.Name} version...");
            var local = LocalVersion(product);
            var server = await Task.Run(() => ServerVersion(product, local, ct), ct);
            var index = await client.GetIndexAsync(region, slug, server, ct);
            if (index is null)
            {
                MissingVersion = server;
                Say($"the mirror has no {slug} index {server} (missing or failed verification)");
                return null;
            }
            found.Add((product, index));
            if (!VersionRight(product, index.Version)) stale.Add(slug);
        }
        foreach (var (product, index) in found)
            if (MirrorDownloads.Unusable(product.Folder, index) is { } why) throw new MirrorUnusableException(why, []);

        long total = found.Sum(f => f.Index.Files.Sum(e => e.Size)), before = 0;
        var work = new Dictionary<string, IReadOnlyList<MirrorEntry>>();
        foreach (var (product, index) in found)
        {
            var slug = product.Game.Slug;
            Status?.Invoke($"Checking the {product.Name} files...");
            work[slug] = await ScanAsync(slug, product.Folder, index, Offset(progress, before, total), ct);
            before += index.Files.Sum(e => e.Size);
        }
        return new RepairPlan(work, work.Values.Sum(w => w.Sum(e => e.Size)), found.ToDictionary(f => f.Product.Game.Slug, f => f.Index))
        {
            StaleVersions = stale,
            QueuePending = ViewerQueue.QueuedWorkPending(viewerDir),
            DirectXMissing = !DirectXPresent(),
        };
    }

    // installer data first, so a mirror that can't serve it changes no game file. The registry and DirectX get checked
    // every time, whatever the files said
    public async Task<RepairResult> ApplyAsync(RepairPlan plan, string viewerDir, string ffxiDir, string installRoot,
                                               IProgress<ByteProgress>? progress, CancellationToken ct, bool skipInstallData = false)
    {
        _changing = false;
        var products = ProductsOf(viewerDir, ffxiDir);
        var viewer = products[0];

        // the installer data and the replacement files are one bar, the data's size is only known once it starts
        long downloadTotal = products.Sum(p => MirrorDownloads.DownloadSize(WorkOf(plan, p.Game.Slug))), dataTotal = 0;
        var dataProgress = progress is null ? null : new InlineProgress<ByteProgress>(p =>
        {
            dataTotal = p.Total;
            progress.Report(new ByteProgress(p.Done, p.Total + downloadTotal));
        });
        var (packages, installDirectX) = skipInstallData ? ([], false) : await PrepareAsync(plan, products, installRoot, dataProgress, ct);

        // a stale queue (.tmp2) goes first, or it'd overwrite a repaired file at the next launch
        bool queued = ApplyViewerQueue(viewerDir);
        int other = queued ? 1 : 0;

        var replaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failed = new List<string>();
        long before = dataTotal;
        foreach (var product in products)
        {
            var slug = product.Game.Slug;
            var work = WorkOf(plan, slug);
            failed.AddRange(await DownloadAsync(slug, product, work, Offset(progress, before, dataTotal + downloadTotal), replaced, ct));
            before += MirrorDownloads.DownloadSize(work);
        }
        ThrowIfFailed(failed);
        CreateMissingOwned(plan, viewer);

        // the viewer's patchfiles copies might have been among them, and whatever the queue changed has to match the
        // index too before patch.ver claims the version
        queued |= ApplyViewerQueue(viewerDir);
        if (queued)
        {
            Status?.Invoke($"Checking the {GameProduct.Viewer.Name} files again...");
            var again = await ScanAsync(GameProduct.Viewer.Slug, viewer.Folder, plan.Indexes[GameProduct.Viewer.Slug], null, ct);
            if (again.Count > 0) Say($"the Viewer's queued update files changed {again.Count} file(s); they are downloaded again");
            // no share of the bar for these, it'd have to start again from nothing
            ThrowIfFailed(await DownloadAsync(GameProduct.Viewer.Slug, viewer, again, null, replaced, ct));
        }

        // only what's missing or wrong goes back, the game's own settings stay as they are
        if (packages.Count > 0) Fixing?.Invoke();
        foreach (var p in packages)
        {
            Status?.Invoke($"Checking how {p.Name} is set up in Windows...");
            var r = await Task.Run(() =>
            {
                using var pkg = new MsiPackage(p.Msi);
                return pkg.Apply(installRoot, p.Properties, null, ct, uninstall: p.Uninstall, payloadInPlace: true, keepExisting: true);
            }, ct);
            Say($"{p.Name} put back what was missing or wrong: {r.RegistryValues} registry values, {r.Classes} COM classes, " +
                        $"{r.ProgIds} ProgIDs, {r.TypeLibs} type libraries, uninstall entry: {r.Uninstall}");
            if (r.Written > 0) Fixed?.Invoke();
            other += r.Written;
        }

        other += WriteVersions(plan, products);

        // last, the game files are right by now and a failure here has its own message
        if (installDirectX)
        {
            var dxsetup = Path.Combine(StagingDir!, "redist", "Directx_Jun2010", "DXSETUP.exe");
            if (!File.Exists(dxsetup))
            {
                Say("the DirectX runtime is missing, and the install data has no DirectX installer");
                throw Shown.Mark(new InvalidOperationException(DirectXFailed));
            }
            Status?.Invoke("Installing the DirectX runtime...");
            // DXSETUP runs to the end even after a stop, so it's fixing something from the start
            Fixing?.Invoke();
            Fixed?.Invoke();
            var warnings = new List<string>();
            await GameInstaller.RunDirectXAsync(dxsetup, warnings, ct);
            foreach (var w in warnings) Say(w);
            if (!DirectXPresent())
            {
                Say("the DirectX runtime is still missing after DXSETUP");
                throw Shown.Mark(new InvalidOperationException(DirectXFailed));
            }
            other++;
        }
        return new RepairResult(replaced.Count, other);
    }

    private static IEnumerable<string> OwnedPaths(RepairPlan plan, PatchProduct product) =>
        plan.Indexes.TryGetValue(product.Game.Slug, out var index)
            ? index.Files.Where(e => IsOwned(product.Game.Slug, e.Path)).Select(e => MirrorDownloads.SafePath(product.Folder, e.Path))
            : [];

    private static void CreateMissingOwned(RepairPlan plan, PatchProduct product)
    {
        foreach (var path in OwnedPaths(plan, product).Where(p => !File.Exists(p)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Create(path).Dispose();
        }
    }

    // only where it isn't already right, DownloadAsync dropped it for a product it touched
    private int WriteVersions(RepairPlan plan, PatchProduct[] products)
    {
        int written = 0;
        foreach (var product in products)
        {
            var slug = product.Game.Slug;
            if (VersionRight(product, plan.Indexes[slug].Version)) continue;
            ChangingNow();
            PatchVer.Write(product, plan.Indexes[slug].Version);
            written++;
        }
        return written;
    }

    // drops patch.ver first so a stopped repair never claims a version
    private async Task<List<string>> DownloadAsync(string slug, PatchProduct product, IReadOnlyList<MirrorEntry> work,
                                                   IProgress<ByteProgress>? progress, HashSet<string> replaced, CancellationToken ct)
    {
        if (work.Count == 0) return [];
        ChangingNow();
        PatchVer.Drop(product.Folder);
        // one text for both products, so the time left carries on across them
        Status?.Invoke("Downloading replacement files...");
        var failed = await MirrorDownloads.DownloadAllAsync(client, region, product.Folder, work, MirrorDownloads.DefaultParallel, progress, ct,
            (e, ex) => Say($"{slug} file {e.Path} failed: {ex.GetType().Name}: {ex.Message}"));
        var bad = new HashSet<string>(failed.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var e in work) if (!bad.Contains(e.Path)) replaced.Add($"{slug}/{e.Path}");
        return failed.Select(e => e.Path).ToList();
    }

    private static void ThrowIfFailed(List<string> failed)
    {
        if (failed.Count == 0) return;
        var names = MirrorDownloads.NameList(failed);
        throw Shown.Mark(new IOException($"{failed.Count} {(failed.Count == 1 ? "file" : "files")} couldn't be repaired: {names}. Run the repair again."));
    }

    private sealed record Package(string Name, string Msi, Dictionary<string, string> Properties, MsiPackage.UninstallSetup Uninstall);

    // the install data (both packages, and the DirectX redist only when the runtime's missing), unless Windows Installer
    // owns both products and DirectX is in place, then nothing would use it. Writes only to the staging folder
    private async Task<(List<Package> Packages, bool InstallDirectX)> PrepareAsync(RepairPlan plan, PatchProduct[] products,
        string installRoot, IProgress<ByteProgress>? progress, CancellationToken ct)
    {
        if (StagingDir is null) throw new InvalidOperationException("GameRepair.StagingDir is not set.");
        bool installDirectX = plan.DirectXMissing;
        if (!installDirectX && GameProduct.All.All(g => InstalledByWindowsInstaller(g.ProductCode(Region))))
        {
            Say("the installer data isn't needed: Windows Installer has both products");
            return ([], false);
        }
        var install = new InstallFromMirror(client, region, "Repair", LogEventLevel.Information);
        Status?.Invoke("Downloading the installer data...");
        // the download only, the look over anything already in the staging folder is local and counts other bytes
        var bytes = progress is null ? null : new InlineProgress<InstallProgress>(p => { if (p is { Download: true, Fraction: >= 0 }) progress.Report(new ByteProgress(p.DoneBytes, p.TotalBytes)); });
        var data = await install.FetchInstallDataAsync(StagingDir,
                       installDirectX ? null : e => !e.Path.StartsWith("redist/", StringComparison.OrdinalIgnoreCase), bytes, ct)
                   ?? throw new MirrorUnusableException(install.SkipReason ?? "the mirror has no install data", []);

        var packages = new List<Package>();
        foreach (var game in GameProduct.All)
        {
            var (slug, name, productCode) = (game.Slug, game.Name, game.ProductCode(Region));
            var product = products.Single(p => p.Game == game);
            var msi = data.MsiFor(game);
            if (InstallFromMirror.ReadPackage(msi, installRoot, game, out var folder, out var targets) is { } unusable)
                throw new MirrorUnusableException(unusable, []);
            if (!PathInside.Same(folder, product.Folder))
            {
                Say($"{name} is not checked: its package installs to {folder}, the install is in {product.Folder}");
                continue;
            }
            if (InstalledByWindowsInstaller(productCode))
            {
                Say($"{name} is not checked: Windows Installer has product {productCode}");
                continue;
            }
            var coming = new HashSet<string>(WorkOf(plan, slug).Select(e => MirrorDownloads.SafePath(product.Folder, e.Path)).Concat(OwnedPaths(plan, product)),
                                             StringComparer.OrdinalIgnoreCase);
            if (targets.FirstOrDefault(t => !coming.Contains(Path.GetFullPath(t)) && !File.Exists(t)) is { } absent)
            {
                var rel = PathInside.IsUnder(product.Folder, absent) ? Path.GetRelativePath(product.Folder, absent).Replace('\\', '/') : absent;
                throw new MirrorUnusableException(
                    $"the {name} installer needs {rel}, which is missing, and the mirror's {slug} index {plan.Indexes[slug].Version} does not list it", [rel]);
            }
            var (props, uninstall) = GameInstaller.DirectApplySetup(msi, installRoot, game, Region);
            packages.Add(new Package(name, msi, props, uninstall));
        }
        return (packages, installDirectX);
    }

    private bool ApplyViewerQueue(string viewerDir)
    {
        if (!ViewerQueue.QueuedWorkPending(viewerDir)) return false;
        ChangingNow();
        Status?.Invoke($"Finishing the last {GameProduct.Viewer.Name} update...");
        ViewerQueue.ApplyQueued(viewerDir);
        Say($"applied the {GameProduct.Viewer.Name}'s queued update files");
        return true;
    }

    private void ChangingNow()
    {
        if (_changing) return;
        _changing = true;
        Changing?.Invoke();
    }

    private string LocalVersion(PatchProduct product)
    {
        try { return PatchVer.Read(product); }
        catch (InvalidDataException ex)
        {
            Say($"the {product.Name} patch.ver cannot be read ({ex.Message}); the version check sends none");
            return "";
        }
    }

    // POL tries the product id alone when the Interface-seeded key doesn't open it, so either one counts
    private static bool VersionRight(PatchProduct product, string version)
    {
        try { return PatchVer.Read(product) == version; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }

    private PolRegion Region => MirrorLayout.RegionOf(region);

    // the region's hive, POL keys patch.ver with its Interface value
    private PatchProduct[] ProductsOf(string viewerDir, string ffxiDir) =>
        [PatchProduct.For(GameProduct.Viewer, viewerDir, viewerDir, Region), PatchProduct.For(GameProduct.Game, ffxiDir, viewerDir, Region)];

    private static IReadOnlyList<MirrorEntry> WorkOf(RepairPlan plan, string slug) => plan.Work.TryGetValue(slug, out var w) ? w : [];

    private static IProgress<ByteProgress>? Offset(IProgress<ByteProgress>? progress, long before, long total) =>
        progress is null ? null : new InlineProgress<ByteProgress>(p => progress.Report(new ByteProgress(before + p.Done, total)));
}
