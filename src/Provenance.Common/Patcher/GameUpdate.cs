using Provenance.Common.Install;
using Provenance.Common.Mirror;
using Provenance.Common.PolFormat;
using Serilog;

namespace Provenance.Common.Patcher;

public static class GameUpdate
{
    internal static PatchResult Run(PatchProduct product, PolRegion installed, IProgress<PatchProgress>? progress,
                                    Action beforeChange, CancellationToken ct, VersionCheck? known = null, IPatchConnector? connector = null)
    {
        var slug = product.Game.Slug;
        var mirrorRegion = MirrorLayout.UpdateFolder(installed, slug);
        var updater = new PolUpdater(product) { KeepLargeAddressAware = true, Connector = connector ?? DirectPatchConnector.Instance };
        if (known is null) progress?.Report(new PatchProgress(PatchPhase.Check, $"Checking for {product.Name} updates...", -1));
        var check = known ?? updater.Check(ct);
        if (check.UpToDate) return PatchResult.UpToDate;

        bool changed = false;
        void Once() { if (!changed) { beforeChange(); changed = true; } }

        MirrorOutcome outcome;
        try
        {
            if (mirrorRegion is null)
            {
                Log.Information("Update {Product}: the patch mirror has no copy of it for this region; using PlayOnline", product.Name);
                outcome = new MirrorOutcome(false, [], 0);
            }
            else
                outcome = MirrorUpdater.UpdateAsync(MirrorClient.Shared, mirrorRegion, slug, product.Folder, updater, check, Once, progress, ct,
                    (msg, isError) =>
                    {
                        if (isError) Log.Error("Update {Product}: {Message}", product.Name, msg);
                        else Log.Warning("Update {Product}: {Message}", product.Name, msg);
                    }, MirrorLayout.OwnFiles(installed, slug)).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            Log.Information("Update {Product}: mirror unreachable ({Reason})", product.Name, ex.Message);
            outcome = new MirrorOutcome(false, [], 0);
        }

        // the list comes once the mirror's answered, so a dead one isn't waited on twice
        if (outcome.OnMirror && product.Game == GameProduct.Viewer) SignedBuilds.RefreshAsync(MirrorClient.Shared, ct).GetAwaiter().GetResult();

        if (!outcome.OnMirror)
        {
            Log.Information("Update {Product}: mirror 0 files, fallback all", product.Name);
            return updater.Run(progress, ct, Once, check);
        }
        Log.Information("Update {Product}: mirror {Replaced} files, fallback {Count}", product.Name, outcome.Replaced, outcome.Failed.Count);
        if (outcome.Failed.Count == 0) return outcome.Replaced > 0 ? PatchResult.Updated : PatchResult.UpToDate;

        var names = new HashSet<string>(outcome.Failed.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var failedList = MirrorDownloads.NameList(outcome.Failed.Select(f => f.Path).ToList());
        Log.Warning("Update {Product}: PlayOnline fallback for {Files}", product.Name, failedList);
        // the file list's in the log line above, so a failure goes up as it is, marked and with its HResult
        var r = new PolUpdater(product, n => names.Contains(n.Replace('\\', '/'))) { KeepLargeAddressAware = true }.Run(progress, ct, Once, check);
        // the mirror already replaced files, so it's an update even when POL had nothing left to do
        return r == PatchResult.UpToDate && outcome.Replaced > 0 ? PatchResult.Updated : r;
    }
}
