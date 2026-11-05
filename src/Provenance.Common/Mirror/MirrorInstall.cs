using System.Collections.Concurrent;
using System.Net.Sockets;
using Provenance.Common.Install;
using Provenance.Common.Patcher;
using Provenance.Common.PolFormat;

namespace Provenance.Common.Mirror;

public sealed record StagedProduct(string Slug, PatchProduct Product, string Version, bool Current);

internal sealed record InstallData(MirrorPointer Pointer, string Version, string ViewerMsi, string FfxiMsi);

public sealed class MirrorUnusableException(string message, IReadOnlyList<string> files) : Exception(message)
{
    public IReadOnlyList<string> Files { get; } = files;
}

internal static class MirrorFailure
{
    public static bool IsLocal(Exception ex) =>
        ex is UnauthorizedAccessException
        || (ex is IOException && ex is not HttpIOException
            && ex.InnerException is not (SocketException or HttpIOException));
}

// installer data goes to a staging folder laid out like the unpacked installer, game files straight to where the
// packages put them
public sealed class MirrorInstall(MirrorClient client, string region, Action<string>? log = null)
{
    private const string InstallSlug = "install";
    private const string PrepareStage = "Preparing";
    public const string PrepareText = "Getting the download ready...";
    // no detail line while downloading, the stage already says what's coming down (the reports carry Download instead)
    public const string DownloadText = "";
    private const string ScanText = "Checking the files already downloaded...";

    internal static (string Slug, string Code, string ProductCode, string KeyFile, string Name)[] Products(PolRegion region)
    {
        var (viewer, ffxi) = GameInstaller.ProductCodesFor(region);
        return
        [
            ("viewer", PolPaths.ViewerProduct, viewer, "pol.exe", "PlayOnline Viewer"),
            ("ffxi", PolPaths.FfxiProduct, ffxi, "FFXiMain.dll", "FINAL FANTASY XI"),
        ];
    }

    private PolRegion Region => MirrorLayout.RegionOf(region);

    public Func<PatchProduct, CancellationToken, string> ServerVersion { get; init; } = (p, ct) => new PolUpdater(p).Check(ct).Server;

    public Func<string, string?> RegisteredFolder { get; init; } = code => PolPaths.TryGetInstallFolder(MirrorLayout.RegionOf(region), code);

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
        var (pointer, _, viewerMsi, ffxiMsi) = data;

        var products = Products(Region);
        var folders = new string[products.Length];
        var targets = new List<string>[products.Length];
        for (int i = 0; i < products.Length; i++)
        {
            var (slug, code, productCode, keyFile, name) = products[i];
            var msi = slug == "viewer" ? viewerMsi : ffxiMsi;
            if (ReadPackage(msi, installRoot, keyFile, name, out var folder, out targets[i]) is { } unusable)
                return Skip<string>(unusable);
            // RunInstallAsync keeps an earlier install, so the files have to go there and it has to be complete
            if (RegisteredFolder(code) is { } registered)
            {
                if (!Same(registered, folder)) return Skip<string>($"{name} is already registered in {registered}");
                if (!File.Exists(Path.Combine(folder, keyFile)))
                    return Skip<string>($"{name} is registered in {registered}, but {keyFile} is missing there");
            }
            else if (InstalledByWindowsInstaller(productCode))
                return Skip<string>($"{name} is installed with Windows Installer, but its install folder is not registered");
            folders[i] = folder;
        }

        var viewerDir = folders[0];
        var plans = new List<(StagedProduct Product, MirrorIndex Index, string Name)>();
        for (int i = 0; i < products.Length; i++)
        {
            var (slug, _, _, _, name) = products[i];
            // the region's hive, its Interface value keys patch.ver
            var product = slug == "viewer" ? PatchProduct.Viewer(folders[i], Region) : PatchProduct.Ffxi(folders[i], viewerDir, Region);
            progress?.Report(new InstallProgress(PrepareStage, $"Checking the {name} version...", -1, 0, 0));
            string? server = null;
            try { server = await Task.Run(() => ServerVersion(product, ct), ct); }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException)
            {
                log?.Invoke($"the {name} version check failed ({ex.Message}); installing the mirror's newest version");
            }
            var index = server is null ? null : await client.GetIndexAsync(region, slug, server, ct);
            if (index is null)
            {
                if (!pointer.Versions.TryGetValue(slug, out var newest))
                    return Skip<string>($"the mirror pointer names no {slug} version");
                index = await client.GetIndexAsync(region, slug, newest, ct);
                if (index is null) return Skip<string>($"the {slug} index {newest} is missing or failed verification");
                if (server is not null) log?.Invoke($"the mirror does not have {name} {server} yet; installing {newest}, and the first update brings it current");
            }
            if (slug == "ffxi" && index.Version == server) GameIndex = index;
            if (Unsafe(folders[i], index) is { } unsafePath) return Skip<string>(unsafePath);
            // Apply needs every file its package installs, a missing one would only stop it after the whole download
            if (Unlisted(folders[i], targets[i], index) is { } unlisted)
            {
                var reason = $"the {slug} index {index.Version} does not list {unlisted}, which the {name} package installs";
                log?.Invoke(reason);
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
        foreach (var s in Staged) new PolUpdater(s.Product).WriteLocalVersion(s.Version);
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
        if (Unsafe(stagingDir, installIndex) is { } bad) return Skip<InstallData>(bad);
        if (include is not null) installIndex = installIndex with { Files = installIndex.Files.Where(include).ToList() };

        await FetchAsync([(stagingDir, installIndex, "installer", false)], progress, ct);
        try
        {
            var (viewerMsi, ffxiMsi) = GameInstaller.FindPackages(stagingDir);
            return new InstallData(pointer, installVersion, viewerMsi, ffxiMsi);
        }
        catch (FileNotFoundException ex) { return Skip<InstallData>($"the install data {installVersion} is unusable: {ex.Message}"); }
    }

    internal static string? ReadPackage(string msi, string installRoot, string keyFile, string name, out string folder, out List<string> targets)
    {
        folder = "";
        targets = [];
        try
        {
            using var pkg = new MsiPackage(msi);
            if (pkg.Supported() is { } missing) return $"the {name} package uses {missing}";
            targets = pkg.Targets(installRoot).Select(t => t.Path).ToList();
        }
        catch (InvalidDataException ex) { return $"the {name} package cannot be read: {ex.Message}"; }
        var dirs = targets.Where(t => Path.GetFileName(t).Equals(keyFile, StringComparison.OrdinalIgnoreCase))
            .Select(t => Path.GetDirectoryName(t)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (dirs.Count != 1) return $"the {name} package installs {dirs.Count} copies of {keyFile}";
        folder = dirs[0];
        if (!Inside(installRoot, folder)) return $"the {name} package installs outside {installRoot}";
        return null;
    }

    private T? Skip<T>(string reason) where T : class
    {
        SkipReason = reason;
        return null;
    }

    private static string? Unsafe(string folder, MirrorIndex index) =>
        MirrorSync.FirstUnsafe(folder, index.Files) is { } why
            ? $"the {index.Product} index {index.Version} is unusable: {why}" : null;

    private static string? Unlisted(string folder, IEnumerable<string> targets, MirrorIndex index)
    {
        var listed = new HashSet<string>(index.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
        {
            if (!Inside(folder, t)) return t;
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
            work.Add(await MirrorSync.ScanAsync(folder, index, Bytes(progress, PrepareStage, ScanText, index.Files.Sum(f => f.Size)), ct,
                                                keepLargeAddressAware: true));
        }
        for (int i = 0; i < targets.Count; i++)
        {
            var (folder, index, name, isProduct) = targets[i];
            var stage = isProduct ? $"Downloading {name}" : "Downloading the installer data";
            long total = MirrorSync.DownloadSize(work[i]);
            progress?.Report(new InstallProgress(stage, DownloadText, 0, 0, total, Download: true));
            // drop patch.ver before touching a folder, so a stopped run never claims a version
            var version = Path.Combine(folder, "patch.ver");
            if (isProduct && work[i].Count > 0 && File.Exists(version)) File.Delete(version);
            var reasons = new ConcurrentDictionary<string, Exception>(StringComparer.OrdinalIgnoreCase);
            var failed = await MirrorSync.DownloadAllAsync(client, region, folder, work[i], MirrorSync.DefaultParallel,
                Bytes(progress, stage, DownloadText, total, download: true), ct,
                (e, ex) =>
                {
                    reasons[e.Path] = ex;
                    log?.Invoke($"{index.Product} file {e.Path} failed: {ex.GetType().Name}: {ex.Message}");
                },
                onRetry: (e, attempt, ex) => log?.Invoke($"{index.Product} file {e.Path} attempt {attempt} failed, trying again: {ex.GetType().Name}: {ex.Message}"));
            if (failed.Count > 0)
            {
                var files = failed.Select(e => e.Path).ToList();
                var names = MirrorSync.NameList(files);
                // a write that failed here (disk full, a locked file) is the PC's to fix, SE's parts would fail too
                if (files.Any(f => reasons.TryGetValue(f, out var ex) && MirrorFailure.IsLocal(ex)))
                    throw Shown.Mark(new IOException($"{files.Count} {name} {(files.Count == 1 ? "file" : "files")} couldn't be written: {names}. Free up some space or close whatever's using them, then press Try again."));
                throw new MirrorUnusableException($"{files.Count} {name} file(s) could not be downloaded: {names}.", files);
            }
        }
    }

    private static IProgress<(long Done, long Total)>? Bytes(IProgress<InstallProgress>? progress, string stage, string text, long total,
                                                             bool download = false) =>
        progress is null ? null : new InlineProgress<(long Done, long Total)>(p => progress.Report(new InstallProgress(stage, text,
            total > 0 ? (double)p.Done / total : 1, p.Done, total, download)));

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal static bool Same(string a, string b) => string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);

    internal static bool Inside(string root, string path)
    {
        var r = Full(root);
        if (!r.EndsWith(Path.DirectorySeparatorChar)) r += Path.DirectorySeparatorChar;
        return Full(path).StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }
}
