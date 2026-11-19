using System.Diagnostics;
using Provenance.Common.Game;
using Provenance.Common.Install;
using Provenance.Common.PolFormat;
using Serilog;

namespace Provenance.Common.Patcher;

public static class PolUpdates
{
    public static PatchResult UpdateViewer(string viewerDir, PolRegion region, IProgress<PatchProgress>? progress, Action freeFiles,
                                           CancellationToken ct, Action<bool>? afterQueued = null) =>
        UpdateViewer(viewerDir, progress, freeFiles, ct, afterQueued,
                     beforeChange => GameUpdate.Run(PatchProduct.Viewer(viewerDir, region), region, progress, beforeChange, ct, mirror: MirrorCheck.Shared));

    internal static PatchResult UpdateViewer(string viewerDir, IProgress<PatchProgress>? progress, Action freeFiles, CancellationToken ct,
                                             Action<bool>? afterQueued, Func<Action, PatchResult> check) =>
        GameFilesLock.Locked(GameFilesLock.Job.ViewerUpdate, ct, progress, () =>
        {
            void BeforeChange()
            {
                freeFiles();
                if (ViewerRunning())
                    throw Shown.Mark(new InvalidOperationException(
                        "PlayOnline needs an update, but another PlayOnline window from this install is open. Log that account out and close it, then log in again."));
            }
            void ApplyQueued()
            {
                if (!ViewerQueue.QueuedWorkPending(viewerDir)) return;
                BeforeChange();
                ViewerQueue.ApplyQueued(viewerDir);
            }
            bool noneQueued = !ViewerQueue.QueuedWorkPending(viewerDir);
            ApplyQueued();
            afterQueued?.Invoke(noneQueued);
            var r = check(BeforeChange);
            ApplyQueued();
            Log.Information("{Product} update check: {Result}", GameProduct.Viewer.Name, r);
            return r;
        });

    public static PatchResult UpdateFfxi(string ffxiDir, string viewerDir, PolRegion region, VersionCheck? known,
                                         IProgress<PatchProgress>? progress, CancellationToken ct) =>
        GameFilesLock.Locked(GameFilesLock.Job.GameUpdate, ct, progress, () =>
        {
            var r = GameUpdate.Run(PatchProduct.Ffxi(ffxiDir, viewerDir, region), region, progress, () =>
            {
                if (GameRunning())
                    throw Shown.Mark(new InvalidOperationException(
                        "FINAL FANTASY XI needs an update, but the game is running. Close every FINAL FANTASY XI window, then press Play again."));
            }, ct, known, mirror: MirrorCheck.Shared);
            Log.Information("{Product} update check: {Result}", GameProduct.Game.Name, r);
            return r;
        });

    public static VersionCheck CheckFfxi(string ffxiDir, string viewerDir, PolRegion region, IProgress<PatchProgress>? progress,
                                         CancellationToken ct) =>
        GameFilesLock.Locked(GameFilesLock.Job.GameUpdate, ct, progress, () =>
        {
            var product = PatchProduct.Ffxi(ffxiDir, viewerDir, region);
            progress?.Report(new PatchProgress(PatchPhase.Check, $"Checking for {product.Name} updates...", -1));
            return MirrorCheck.Shared.Check(new PolUpdater(product), ct);
        });

    internal static Func<ProcessStamp, bool> IsViewerPol { get; set; } = stamp => stamp.IsViewerPol();

    private static bool AnyPol(Func<Process, bool> match)
    {
        var pols = Process.GetProcessesByName("pol");
        try { return pols.Any(match); }
        finally { foreach (var p in pols) p.Dispose(); }
    }

    public static bool ViewerRunning() => AnyPol(p => ProcessStamp.Of(p.Id) is { } stamp && IsViewerPol(stamp));

    public static bool GameRunning() => AnyPol(p =>
    {
        if (ProcessStamp.Of(p.Id) is not { } stamp || !IsViewerPol(stamp)) return false;
        try { return p.Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Equals(GameProduct.Game.KeyFile, StringComparison.OrdinalIgnoreCase)); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return true;
        }
    });
}
