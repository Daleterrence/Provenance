using System.IO;
using System.Net.Http;
using System.Diagnostics;
using Provenance.Common;
using Provenance.Common.Locks;
using Provenance.Common.Mirror;
using Provenance.Common.Patcher;
using Provenance.Common.PolFormat;
using Serilog;

namespace Provenance.Wpf.Services;

// same two moments as the viewer: its own update at each sign-in and FFXI's when Play's pressed
public static class PolUpdates
{
    private static readonly HttpClient MirrorHttp = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly MirrorClient Mirror = new(MirrorHttp, MirrorKeys.Trusted);

    // freeFiles ends my waiting pol.exe before any file changes
    public static PatchResult UpdateViewer(string viewerDir, PolRegion region, IProgress<PatchProgress>? progress, Action freeFiles,
                                           CancellationToken ct) =>
        Locked(Job.ViewerUpdate, ct, progress, () =>
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
            var r = Update(PatchProduct.Viewer(viewerDir, region), region, "viewer", progress, BeforeChange, ct);
            // up to date or not, an earlier run might've died between the update and these steps
            ApplyQueued();
            Log.Information("PlayOnline Viewer update check: {Result}", r);
            return r;
        });

    public static PatchResult UpdateFfxi(string ffxiDir, string viewerDir, PolRegion region, IProgress<PatchProgress>? progress,
                                         CancellationToken ct) =>
        Locked(Job.GameUpdate, ct, progress, () =>
        {
            var r = Update(PatchProduct.Ffxi(ffxiDir, viewerDir, region), region, "ffxi", progress, () =>
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
    private static PatchResult Update(PatchProduct product, PolRegion installed, string slug, IProgress<PatchProgress>? progress,
                                      Action beforeChange, CancellationToken ct)
    {
        var mirrorRegion = MirrorUpdater.RegionFor(installed, slug);
        var updater = new PolUpdater(product) { KeepLargeAddressAware = true };
        progress?.Report(new PatchProgress(PatchPhase.Check, $"Checking for {product.Name} updates...", -1));
        var check = updater.Check(ct);
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
                outcome = MirrorUpdater.UpdateAsync(Mirror, mirrorRegion, slug, product.Folder, updater, check, Once, progress, ct,
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

    public enum Job { Install, ViewerUpdate, GameUpdate, Repair, LargeAddressAware }

    // a thread-held AdminLock, so call it from a worker and keep the work synchronous
    internal static T Locked<T>(Job job, CancellationToken ct, IProgress<PatchProgress>? progress, Func<T> work)
    {
        using var gate = Lock(job, ct, progress);
        return work();
    }

    // what goes after "Another copy of Provenance is". Null is a holder that hasn't said or has gone
    internal static string Doing(Job? holder) => holder switch
    {
        Job.Install => "installing FINAL FANTASY XI",
        Job.ViewerUpdate => "updating PlayOnline",
        Job.GameUpdate => "updating FINAL FANTASY XI",
        Job.Repair => "repairing the game files",
        Job.LargeAddressAware => "changing the Large Address Aware setting",
        _ => "using the game files",
    };

    internal static string WaitingText(Job? holder) => $"Waiting for another copy of Provenance to finish {Doing(holder)}...";

    // one lock for every product (installs too), and a copy that dies mid-update lets go with its process
    internal static IDisposable Lock(Job job, CancellationToken ct, IProgress<PatchProgress>? progress)
    {
        for (int waits = 0; ; waits++)
        {
            ct.ThrowIfCancellationRequested();
            if (TryLock(job, TimeSpan.FromMilliseconds(500), ct) is { } gate) return gate;
            // every 5 s, which a multi-account run's no-progress guard counts as progress
            if (waits % 10 == 0) progress?.Report(new PatchProgress(PatchPhase.Check, WaitingText(Holder()), -1));
        }
    }

    // a quick look, for whether a repair could start now. The short wait lets another copy's own quick look go by
    internal static bool LockFree()
    {
        using var gate = AdminLock.TryAcquire("update", TimeSpan.FromMilliseconds(150));
        return gate is not null;
    }

    // null when another update or repair still held it after the timeout
    internal static IDisposable? TryLock(Job job, TimeSpan timeout, CancellationToken ct = default) =>
        AdminLock.TryAcquire("update", timeout, ct) is { } gate ? new Holding(gate, job) : null;

    // the holder's pid and start time go in with the job, so a copy that died holding the lock can't speak for the next
    private static string RecordPath() => Path.ChangeExtension(AdminLock.PathFor("update"), ".job");

    internal static Job? Holder()
    {
        try
        {
            string text;
            using (var fs = new FileStream(RecordPath(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs)) text = reader.ReadToEnd();
            var parts = text.Split(' ');
            if (parts.Length != 3 || !int.TryParse(parts[0], out var pid) || !long.TryParse(parts[1], out var started)
                || !Enum.TryParse<Job>(parts[2], out var job) || !Enum.IsDefined(job)) return null;
            using var p = Process.GetProcessById(pid);
            return p.StartTime.ToUniversalTime().Ticks == started ? job : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception) { return null; }
    }

    private sealed class Holding : IDisposable
    {
        private readonly IDisposable _gate;
        private readonly string? _record;
        private int _disposed;

        public Holding(IDisposable gate, Job job)
        {
            _gate = gate;
            try
            {
                _record = RecordPath();
                using var me = Process.GetCurrentProcess();
                File.WriteAllText(_record, $"{Environment.ProcessId} {me.StartTime.ToUniversalTime().Ticks} {job}");
            }
            catch (Exception e) { Log.Debug(e, "Could not note what the update lock is held for"); }
        }

        // the record goes while the lock's still held, or it could take the next holder's with it
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { if (_record is not null) File.Delete(_record); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Debug(e, "Could not remove the update lock's note"); }
            _gate.Dispose();
        }
    }
}

public sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
