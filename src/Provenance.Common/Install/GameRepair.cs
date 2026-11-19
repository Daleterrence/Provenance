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

    public IReadOnlyList<string> StaleVersions { get; init; } = [];

    public bool QueuePending { get; init; }

    public bool DirectXMissing { get; init; }

    public int Issues => Count + StaleVersions.Count + (QueuePending ? 1 : 0) + (DirectXMissing ? 1 : 0);
}

public sealed record RepairedFiles(string Product, IReadOnlyList<string> Files);

public sealed record RepairResult(int FilesReplaced, int OtherFixes)
{
    public IReadOnlyList<RepairedFiles> Replaced { get; init; } = [];

    public IReadOnlyList<string> Fixes { get; init; } = [];
}

public sealed class GameRepair(MirrorClient client, string region)
{
    public const string DirectXFailed =
        "The DirectX runtime FINAL FANTASY XI needs couldn't be installed. Run the repair again.";

    public Func<PatchProduct, string, CancellationToken, string> ServerVersion { get; init; } = (p, local, ct) => new PolUpdater(p).Check(local, ct).Server;

    public Func<string, bool> InstalledByWindowsInstaller { get; init; } = MsiInstaller.IsInstalled;

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

    public string? StagingDir { get; init; }

    public Action<string>? Status { get; init; }

    public Action? Changing { get; init; }

    public Action? Fixed { get; init; }

    public Action? Fixing { get; init; }

    public Action<GameProduct>? Working { get; init; }

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
            Working?.Invoke(product.Game);
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
            Working?.Invoke(product.Game);
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

    public async Task<RepairResult> ApplyAsync(RepairPlan plan, string viewerDir, string ffxiDir, string installRoot,
                                               IProgress<ByteProgress>? progress, CancellationToken ct, bool skipInstallData = false)
    {
        _changing = false;
        var products = ProductsOf(viewerDir, ffxiDir);
        var viewer = products[0];

        long downloadTotal = products.Sum(p => MirrorDownloads.DownloadSize(WorkOf(plan, p.Game.Slug))), dataTotal = 0;
        var dataProgress = progress is null ? null : new InlineProgress<ByteProgress>(p =>
        {
            dataTotal = p.Total;
            progress.Report(new ByteProgress(p.Done, p.Total + downloadTotal));
        });
        var (packages, installDirectX) = skipInstallData ? ([], false) : await PrepareAsync(plan, products, installRoot, dataProgress, ct);

        var fixes = new List<string>();
        bool queued = false;
        void FinishQueue()
        {
            if (!ApplyViewerQueue(viewerDir) || queued) return;
            queued = true;
            fixes.Add($"Finished the last {GameProduct.Viewer.Name} update.");
        }
        FinishQueue();

        var replaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failed = new List<string>();
        long before = dataTotal;
        foreach (var product in products)
        {
            var slug = product.Game.Slug;
            var work = WorkOf(plan, slug);
            if (work.Count > 0) Working?.Invoke(product.Game);
            failed.AddRange(await DownloadAsync(slug, product, work, Offset(progress, before, dataTotal + downloadTotal), replaced, ct));
            before += MirrorDownloads.DownloadSize(work);
        }
        ThrowIfFailed(failed);
        CreateMissingOwned(plan, viewer);

        FinishQueue();
        if (queued)
        {
            Status?.Invoke($"Checking the {GameProduct.Viewer.Name} files again...");
            var again = await ScanAsync(GameProduct.Viewer.Slug, viewer.Folder, plan.Indexes[GameProduct.Viewer.Slug], null, ct);
            if (again.Count > 0) Say($"the PlayOnline Viewer's queued update files changed {again.Count} file(s); they are downloaded again");
            ThrowIfFailed(await DownloadAsync(GameProduct.Viewer.Slug, viewer, again, null, replaced, ct));
        }

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
            if (r.RegistryValues + r.Classes + r.TypeLibs + r.ProgIds > 0) fixes.Add($"Put back {p.Name}'s registry entries and COM classes.");
            if (r.Uninstall) fixes.Add($"Put back {p.Name}'s entry in Windows' installed programs.");
            if (r.Shortcuts > 0) fixes.Add($"Put back {p.Name}'s shortcuts.");
        }

        WriteVersions(plan, products, fixes);

        if (installDirectX)
        {
            var dxsetup = Path.Combine(StagingDir!, "redist", "Directx_Jun2010", "DXSETUP.exe");
            if (!File.Exists(dxsetup))
            {
                Say("the DirectX runtime is missing, and the install data has no DirectX installer");
                throw Shown.Mark(new InvalidOperationException(DirectXFailed));
            }
            Status?.Invoke("Installing the DirectX runtime...");
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
            fixes.Add("Installed the DirectX runtime.");
        }
        var files = products.Select(p => new RepairedFiles(p.Name, replaced
                .Where(r => r.StartsWith(p.Game.Slug + "/", StringComparison.Ordinal))
                .Select(r => r[(p.Game.Slug.Length + 1)..].Replace('/', '\\'))
                .Order(StringComparer.OrdinalIgnoreCase).ToList()))
            .Where(f => f.Files.Count > 0).ToList();
        return new RepairResult(replaced.Count, fixes.Count) { Replaced = files, Fixes = fixes };
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

    private void WriteVersions(RepairPlan plan, PatchProduct[] products, List<string> fixes)
    {
        foreach (var product in products)
        {
            var slug = product.Game.Slug;
            if (VersionRight(product, plan.Indexes[slug].Version)) continue;
            ChangingNow();
            PatchVer.Write(product, plan.Indexes[slug].Version);
            fixes.Add($"Updated the {product.Name} version file (patch.ver).");
        }
    }

    private async Task<List<string>> DownloadAsync(string slug, PatchProduct product, IReadOnlyList<MirrorEntry> work,
                                                   IProgress<ByteProgress>? progress, HashSet<string> replaced, CancellationToken ct)
    {
        if (work.Count == 0) return [];
        ChangingNow();
        PatchVer.Drop(product.Folder);
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
        Working?.Invoke(GameProduct.Viewer);
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

    private static bool VersionRight(PatchProduct product, string version)
    {
        try { return PatchVer.Read(product) == version; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }

    private PolRegion Region => MirrorLayout.RegionOf(region);

    private PatchProduct[] ProductsOf(string viewerDir, string ffxiDir) =>
        [PatchProduct.For(GameProduct.Viewer, viewerDir, viewerDir, Region), PatchProduct.For(GameProduct.Game, ffxiDir, viewerDir, Region)];

    private static IReadOnlyList<MirrorEntry> WorkOf(RepairPlan plan, string slug) => plan.Work.TryGetValue(slug, out var w) ? w : [];

    private static IProgress<ByteProgress>? Offset(IProgress<ByteProgress>? progress, long before, long total) =>
        progress is null ? null : new InlineProgress<ByteProgress>(p => progress.Report(new ByteProgress(before + p.Done, total)));
}
