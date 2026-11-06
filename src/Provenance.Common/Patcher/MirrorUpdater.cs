using Provenance.Common.Mirror;
using Provenance.Common.PolFormat;
using Provenance.Common.Progress;

namespace Provenance.Common.Patcher;

// OnMirror false means the mirror can't serve this version and nothing changed. Otherwise Failed holds what didn't
// download (and patch.ver stays put).
public sealed record MirrorOutcome(bool OnMirror, IReadOnlyList<MirrorEntry> Failed, int Replaced);

public static class MirrorUpdater
{
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
            log?.Invoke(ex.Message, true);
            return none;
        }

        IReadOnlyList<MirrorEntry> failed = [];
        if (work.Count > 0)
        {
            beforeChange();
            progress?.Report(new PatchProgress(PatchPhase.Download, "Downloading updates...", 0));
            failed = await MirrorDownloads.DownloadAllAsync(client, region, folder, work, MirrorDownloads.DefaultParallel,
                Bytes(PatchPhase.Download, "Downloading updates...", progress), ct,
                (e, ex) => log?.Invoke($"mirror file {e.Path} failed: {ex.GetType().Name}: {ex.Message}", false));
        }
        if (failed.Count == 0) PatchVer.Write(updater.Product, check.Server);
        return new MirrorOutcome(true, failed, work.Count - failed.Count);
    }

    // the change from the installed version's index when the mirror has it, otherwise whatever doesn't match on disk.
    // Every path in next is checked before anything's read or asked for
    private static async Task<IReadOnlyList<MirrorEntry>> WorkAsync(MirrorClient client, string region, string product, string folder,
        MirrorIndex next, string local, IReadOnlySet<string>? keep, IProgress<ByteProgress>? scanned, CancellationToken ct)
    {
        if (keep is { Count: > 0 }) next = next with { Files = next.Files.Where(e => !keep.Contains(e.Path)).ToList() };
        if (MirrorDownloads.Unusable(folder, next) is { } why) throw new InvalidDataException(why);
        var old = local.Length > 0 ? await client.GetIndexAsync(region, product, local, ct) : null;
        IReadOnlyList<MirrorEntry> work;
        if (old is not null) work = MirrorDownloads.Diff(old, next);
        else
        {
            scanned?.Report(new ByteProgress(0, next.Files.Sum(f => f.Size)));
            work = await MirrorDownloads.ScanAsync(folder, next, scanned, ct, keepLargeAddressAware: true);
        }
        return work;
    }

    // the bytes UpdateAsync would download to reach next, which has to be POL's current version (the mirror's can lag,
    // then POL updates it). Null when next is unusable
    public static async Task<long?> DownloadSizeAsync(MirrorClient client, string region, string product, string folder, string local,
        IReadOnlySet<string>? keep, MirrorIndex next, IProgress<ByteProgress>? scanned, CancellationToken ct)
    {
        try { return MirrorDownloads.DownloadSize(await WorkAsync(client, region, product, folder, next, local, keep, scanned, ct)); }
        catch (InvalidDataException) { return null; }
    }

    private static IProgress<ByteProgress>? Bytes(PatchPhase phase, string text, IProgress<PatchProgress>? progress) =>
        progress is null ? null : new InlineProgress<ByteProgress>(p => progress.Report(new PatchProgress(phase, text, p.Total > 0 ? (double)p.Done / p.Total : 1)));
}
