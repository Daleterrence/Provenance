using System.Diagnostics;
using Provenance.Common.Game;
using Provenance.Common.PolFormat;
using Serilog;

namespace Provenance.Common.Patcher;

// same two moments as the viewer: its own update at each sign-in and FFXI's when Play's pressed
public static class PolUpdates
{
    // freeFiles ends my waiting pol.exe before any file changes
    public static PatchResult UpdateViewer(string viewerDir, PolRegion region, IProgress<PatchProgress>? progress, Action freeFiles,
                                           CancellationToken ct) =>
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
                if (!PolUpdater.QueuedWorkPending(viewerDir)) return;
                BeforeChange();
                PolUpdater.ApplyQueued(viewerDir);
            }
            // a stale queue (.tmp2) mustn't overwrite what the mirror writes below
            ApplyQueued();
            var r = GameUpdate.Run(PatchProduct.Viewer(viewerDir, region), region, "viewer", progress, BeforeChange, ct);
            // up to date or not, an earlier run might've died between the update and these steps
            ApplyQueued();
            Log.Information("PlayOnline Viewer update check: {Result}", r);
            return r;
        });

    public static PatchResult UpdateFfxi(string ffxiDir, string viewerDir, PolRegion region, IProgress<PatchProgress>? progress,
                                         CancellationToken ct) =>
        GameFilesLock.Locked(GameFilesLock.Job.GameUpdate, ct, progress, () =>
        {
            var r = GameUpdate.Run(PatchProduct.Ffxi(ffxiDir, viewerDir, region), region, "ffxi", progress, () =>
            {
                if (GameRunning())
                    throw Shown.Mark(new InvalidOperationException(
                        "FINAL FANTASY XI needs an update, but the game is running. Close every FINAL FANTASY XI window, then press Play again."));
            }, ct);
            Log.Information("FINAL FANTASY XI update check: {Result}", r);
            return r;
        });

    public static bool ViewerRunning() => Process.GetProcessesByName("pol").Any(p =>
    {
        using (p) return PolProcess.IsViewerPol(p);
    });

    public static bool GameRunning() => Process.GetProcessesByName("pol").Any(p =>
    {
        using (p)
        {
            if (!PolProcess.IsViewerPol(p)) return false;
            try { return p.Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Equals("FFXiMain.dll", StringComparison.OrdinalIgnoreCase)); }
            catch { return true; }   // can't tell, so treat it as the game
        }
    });
}
