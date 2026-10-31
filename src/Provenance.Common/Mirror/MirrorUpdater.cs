using Provenance.Common.Patcher;
using Provenance.Common.PolFormat;

namespace Provenance.Common.Mirror;

// OnMirror false means the mirror can't serve this version and nothing changed. Otherwise Failed holds what didn't
// download (and patch.ver stays put).
public sealed record MirrorOutcome(bool OnMirror, IReadOnlyList<MirrorEntry> Failed, int Replaced);

public static class MirrorUpdater
{
    // null updates from POL
    public static string? RegionFor(PolRegion installed, string product) => MirrorLayout.UpdateFolder(installed, product);

    // patch.ver goes last and only when nothing failed, POL is the caller's fallback. keep is never written
    public static async Task<MirrorOutcome> UpdateAsync(MirrorClient client, string region, string product, string folder,
        PolUpdater updater, VersionCheck check, Action beforeChange, IProgress<PatchProgress>? progress, CancellationToken ct,
        Action<string, bool>? log = null, IReadOnlySet<string>? keep = null)
    {
        var none = new MirrorOutcome(false, [], 0);
        var next = await client.GetIndexAsync(region, product, check.Server, ct);
        if (next is null) return none;

        IReadOnlyList<MirrorEntry> work;
        try
        {
            work = await WorkAsync(client, region, product, folder, next, check.Local, keep, Bytes(PatchPhase.Scan, "Checking your files...", progress), ct);
        }
        catch (InvalidDataException ex)
        {
            log?.Invoke($"mirror index {product} {check.Server} is unusable: {ex.Message}", true);
            return none;
        }

        IReadOnlyList<MirrorEntry> failed = [];
        if (work.Count > 0)
        {
            beforeChange();
            progress?.Report(new PatchProgress(PatchPhase.Download, "Downloading updates...", 0));
            failed = await MirrorSync.DownloadAllAsync(client, region, folder, work, MirrorSync.DefaultParallel,
                Bytes(PatchPhase.Download, "Downloading updates...", progress), ct,
                (e, ex) => log?.Invoke($"mirror file {e.Path} failed: {ex.GetType().Name}: {ex.Message}", false));
        }
        if (failed.Count == 0) updater.WriteLocalVersion(check.Server);
        return new MirrorOutcome(true, failed, work.Count - failed.Count);
    }

    // the change from the installed version's index when the mirror has it, otherwise whatever doesn't match on disk.
    // Every path's checked before anything changes
    private static async Task<IReadOnlyList<MirrorEntry>> WorkAsync(MirrorClient client, string region, string product, string folder,
        MirrorIndex next, string local, IReadOnlySet<string>? keep, IProgress<(long Done, long Total)>? scanned, CancellationToken ct)
    {
        if (keep is { Count: > 0 }) next = next with { Files = next.Files.Where(e => !keep.Contains(e.Path)).ToList() };
        var old = local.Length > 0 ? await client.GetIndexAsync(region, product, local, ct) : null;
        IReadOnlyList<MirrorEntry> work;
        if (old is not null) work = MirrorSync.Diff(old, next);
        else
        {
            scanned?.Report((0, next.Files.Sum(f => f.Size)));
            work = await MirrorSync.ScanAsync(folder, next, scanned, ct, keepLargeAddressAware: true);
        }
        foreach (var e in work) MirrorSync.SafePath(folder, e.Path);
        return work;
    }

    // the bytes UpdateAsync would download into folder, against known or else the mirror's newest version. Null when
    // the mirror has no usable copy, so the update would come from POL
    public static async Task<long?> DownloadSizeAsync(MirrorClient client, string region, string product, string folder, string local,
        IReadOnlySet<string>? keep, MirrorIndex? known, IProgress<(long Done, long Total)>? scanned, CancellationToken ct)
    {
        var next = known;
        if (next is null)
        {
            var pointer = await client.GetPointerAsync(region, ct);
            if (pointer is null || pointer.Region != region || !pointer.Versions.TryGetValue(product, out var newest)) return null;
            next = await client.GetIndexAsync(region, product, newest, ct);
            if (next is null) return null;
        }
        try { return MirrorSync.DownloadSize(await WorkAsync(client, region, product, folder, next, local, keep, scanned, ct)); }
        catch (InvalidDataException) { return null; }
    }

    private static IProgress<(long Done, long Total)>? Bytes(PatchPhase phase, string text, IProgress<PatchProgress>? progress) =>
        progress is null ? null : new InlineProgress<(long Done, long Total)>(p => progress.Report(new PatchProgress(phase, text, p.Total > 0 ? (double)p.Done / p.Total : 1)));
}
