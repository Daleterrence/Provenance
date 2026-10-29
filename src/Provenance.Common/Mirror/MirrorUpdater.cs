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
        if (keep is { Count: > 0 }) next = next with { Files = next.Files.Where(e => !keep.Contains(e.Path)).ToList() };
        var old = check.Local.Length > 0 ? await client.GetIndexAsync(region, product, check.Local, ct) : null;

        IReadOnlyList<MirrorEntry> work;
        try
        {
            if (old is not null) work = MirrorSync.Diff(old, next);
            else
            {
                progress?.Report(new PatchProgress(PatchPhase.Scan, "Checking your files...", 0));
                work = await MirrorSync.ScanAsync(folder, next, Bytes(PatchPhase.Scan, "Checking your files...", progress), ct, keepLargeAddressAware: true);
            }
            foreach (var e in work) MirrorSync.SafePath(folder, e.Path);   // every path checked before beforeChange
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

    private static IProgress<(long Done, long Total)>? Bytes(PatchPhase phase, string text, IProgress<PatchProgress>? progress) =>
        progress is null ? null : new InlineProgress<(long Done, long Total)>(p => progress.Report(new PatchProgress(phase, text, p.Total > 0 ? (double)p.Done / p.Total : 1)));
}
