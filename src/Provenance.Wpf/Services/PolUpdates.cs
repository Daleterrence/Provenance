using System.Net.Http;
using System.Diagnostics;
using Provenance.Common;
using Provenance.Common.Locks;
using Provenance.Common.Mirror;
using Provenance.Common.Patcher;
using Serilog;

namespace Provenance.Wpf.Services;

// same two moments as the viewer: its own update at each sign-in and FFXI's when Play's pressed
public static class PolUpdates
{
    private const string MirrorRegion = "us";
    private static readonly HttpClient MirrorHttp = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly MirrorClient Mirror = new(MirrorHttp, MirrorKeys.Trusted);
    private static int _noKeysLogged;

    // freeFiles ends our waiting pol.exe before any file changes
    public static PatchResult UpdateViewer(string viewerDir, IProgress<PatchProgress>? progress, Action freeFiles, CancellationToken ct) =>
        Locked(ct, progress, () =>
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
            var r = Update(PatchProduct.Viewer(viewerDir), "viewer", progress, BeforeChange, ct);
            // up to date or not, an earlier run might've died between the update and these steps
            ApplyQueued();
            Log.Information("PlayOnline Viewer update check: {Result}", r);
            return r;
        });

    public static PatchResult UpdateFfxi(string ffxiDir, string viewerDir, IProgress<PatchProgress>? progress, CancellationToken ct) =>
        Locked(ct, progress, () =>
        {
            var r = Update(PatchProduct.Ffxi(ffxiDir, viewerDir), "ffxi", progress, () =>
            {
                if (GameRunning())
                    throw Shown.Mark(new InvalidOperationException(
                        "FINAL FANTASY XI needs an update, but the game is running. Close every FINAL FANTASY XI window, then press Play again."));
            }, ct);
            Log.Information("FINAL FANTASY XI update check: {Result}", r);
            return r;
        });

    // One POL version check, then the patch mirror. POL serves whatever the mirror can't, and beforeChange runs once
    // at most.
    private static PatchResult Update(PatchProduct product, string slug, IProgress<PatchProgress>? progress, Action beforeChange, CancellationToken ct)
    {
        var updater = new PolUpdater(product) { KeepLargeAddressAware = true };
        progress?.Report(new PatchProgress(PatchPhase.Check, $"Checking for {product.Name} updates...", -1));
        var check = updater.Check(ct);
        if (check.UpToDate) return PatchResult.UpToDate;

        bool changed = false;
        void Once() { if (!changed) { beforeChange(); changed = true; } }

        MirrorOutcome outcome;
        try
        {
            if (MirrorKeys.Trusted.Count == 0)
            {
                if (Interlocked.Exchange(ref _noKeysLogged, 1) == 0) Log.Information("mirror keys not configured; using PlayOnline");
                outcome = new MirrorOutcome(false, [], 0);
            }
            else
                outcome = MirrorUpdater.UpdateAsync(Mirror, MirrorRegion, slug, product.Folder, updater, check, Once, progress, ct,
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

        if (!outcome.OnMirror)
        {
            Log.Information("Update {Product}: mirror 0 files, fallback all", product.Name);
            return updater.Run(progress, ct, Once, check);
        }
        Log.Information("Update {Product}: mirror {Replaced} files, fallback {Count}", product.Name, outcome.Replaced, outcome.Failed.Count);
        if (outcome.Failed.Count == 0) return outcome.Replaced > 0 ? PatchResult.Updated : PatchResult.UpToDate;

        var names = new HashSet<string>(outcome.Failed.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var failedList = MirrorSync.NameList(outcome.Failed.Select(f => f.Path).ToList());
        Log.Warning("Update {Product}: PlayOnline fallback for {Files}", product.Name, failedList);
        // the file list's in the log line above, so a failure goes up as it is, marked and with its HResult
        var r = new PolUpdater(product, n => names.Contains(n.Replace('\\', '/'))) { KeepLargeAddressAware = true }.Run(progress, ct, Once, check);
        // the mirror already replaced files, so it's an update even when POL had nothing left to do
        return r == PatchResult.UpToDate && outcome.Replaced > 0 ? PatchResult.Updated : r;
    }

    internal static bool ViewerRunning() => Process.GetProcessesByName("pol").Any(p =>
    {
        using (p) return PolProcess.IsViewerPol(p);
    });

    internal static bool GameRunning() => Process.GetProcessesByName("pol").Any(p =>
    {
        using (p)
        {
            if (!PolProcess.IsViewerPol(p)) return false;
            try { return p.Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Equals("FFXiMain.dll", StringComparison.OrdinalIgnoreCase)); }
            catch { return true; }   // can't tell, so treat it as the game
        }
    });

    // a thread-held AdminLock, so call it from a worker and keep the work synchronous
    internal static T Locked<T>(CancellationToken ct, IProgress<PatchProgress>? progress, Func<T> work)
    {
        using var gate = Lock(ct, progress);
        return work();
    }

    internal const string WaitingText = "Waiting for another copy of Provenance to finish updating or repairing the game...";

    // one lock for every product (installs too), and a copy that dies mid-update lets go with its process
    internal static IDisposable Lock(CancellationToken ct, IProgress<PatchProgress>? progress)
    {
        for (int waits = 0; ; waits++)
        {
            ct.ThrowIfCancellationRequested();
            if (TryLock(TimeSpan.FromMilliseconds(500), ct) is { } gate) return gate;
            // every 5 s, which a multi-account run's no-progress guard counts as progress
            if (waits % 10 == 0) progress?.Report(new PatchProgress(PatchPhase.Check, WaitingText, -1));
        }
    }

    // a quick look, for whether a repair could start now. The short wait lets another copy's own quick look go by
    internal static bool LockFree()
    {
        using var gate = AdminLock.TryAcquire("update", TimeSpan.FromMilliseconds(150));
        return gate is not null;
    }

    // null when another update or repair still held it after the timeout
    internal static IDisposable? TryLock(TimeSpan timeout, CancellationToken ct = default) => AdminLock.TryAcquire("update", timeout, ct);
}

public sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
