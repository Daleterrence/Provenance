using Provenance.Common.Install;
using Provenance.Common.Mirror;
using Provenance.Common.PolFormat;
using Serilog;

namespace Provenance.Common.Patcher;

public static class GameUpdate
{
    internal static PatchResult Run(PatchProduct product, PolRegion installed, IProgress<PatchProgress>? progress,
                                    Action beforeChange, CancellationToken ct, VersionCheck? known = null, IPatchConnector? connector = null,
                                    MirrorCheck? mirror = null, MirrorClient? client = null)
    {
        client ??= MirrorClient.Shared;
        var slug = product.Game.Slug;
        var mirrorRegion = MirrorLayout.RegionFolder(installed);
        var updater = new PolUpdater(product) { KeepLargeAddressAware = true, Connector = connector ?? DirectPatchConnector.Instance };
        if (known is null) progress?.Report(new PatchProgress(PatchPhase.Check, $"Checking for {product.Name} updates...", -1));
        var check = known ?? (mirror is null ? updater.Check(ct) : mirror.Check(updater, ct));
        if (check.UpToDate) return PatchResult.UpToDate;

        bool changed = false;
        void Once() { if (!changed) { beforeChange(); changed = true; } }

        MirrorOutcome outcome;
        try
        {
            outcome = MirrorUpdater.UpdateAsync(client, mirrorRegion, slug, product.Folder, updater, check, Once, progress, ct,
                (msg, isError) =>
                {
                    if (isError) Log.Error("Update {Product}: {Message}", product.Name, msg);
                    else Log.Warning("Update {Product}: {Message}", product.Name, msg);
                }).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            Log.Information("Update {Product}: mirror unreachable ({Reason})", product.Name, ex.Message);
            outcome = new MirrorOutcome(false, [], 0);
        }

        if (outcome.OnMirror && product.Game == GameProduct.Viewer) SignedBuilds.RefreshAsync(client, ct).GetAwaiter().GetResult();

        if (!outcome.OnMirror)
        {
            Log.Information("Update {Product}: mirror 0 files, all files from PlayOnline", product.Name);
            return updater.Run(progress, ct, Once, check);
        }
        Log.Information("Update {Product}: mirror {Replaced} files, PlayOnline {Count}", product.Name, outcome.Replaced, outcome.Failed.Count);
        if (outcome.Failed.Count == 0) return outcome.Replaced > 0 ? PatchResult.Updated : PatchResult.UpToDate;

        if (outcome.SomeLocal)
        {
            var files = outcome.Failed.Select(f => f.Path).ToList();
            Log.Warning("Update {Product}: writes failed on this PC, not asking PlayOnline for {Files}", product.Name, MirrorDownloads.NameList(files));
            throw Shown.Mark(new IOException($"Provenance couldn't write {files.Count} {(files.Count == 1 ? "file" : "files")} while updating: {MirrorDownloads.NameList(files)}. Free up some space or close whatever's using the files, then try again."));
        }

        var names = new HashSet<string>(outcome.Failed.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var failedList = MirrorDownloads.NameList(outcome.Failed.Select(f => f.Path).ToList());
        Log.Warning("Update {Product}: PlayOnline fallback for {Files}", product.Name, failedList);
        var r = new PolUpdater(product, n => names.Contains(n.Replace('\\', '/'))) { KeepLargeAddressAware = true, Connector = connector ?? DirectPatchConnector.Instance }.Run(progress, ct, Once, check);
        return r == PatchResult.UpToDate && outcome.Replaced > 0 ? PatchResult.Updated : r;
    }
}
