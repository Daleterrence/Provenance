using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Provenance.Common.Mirror;
using Provenance.Common.Patcher;
using Provenance.Common.PolFormat;
using Provenance.Wpf.Services;
using static System.FormattableString;

namespace Provenance.Wpf.ViewModels;

// Checks every file against the mirror's signed index and only replaces game files after a yes. The pol.exe callbacks
// stop my pre-started pol.exe (it holds viewer files) and keep it off until the repair's done
public partial class RepairViewModel(PolRegion? region, Action? stopWaitingPol = null, Action? restartWaitingPol = null) : ObservableObject
{
    internal const string CheckingStage = "Checking your FINAL FANTASY XI game data...";
    internal const string CheckingText = "This may take a while.";
    internal const string RepairingStage = "Repairing your FINAL FANTASY XI game data...";
    internal const string NoIssuesText = "No issues were found with your FINAL FANTASY XI or PlayOnline Viewer installation data.";
    internal const string FixedText = "Repair found an issue with your FINAL FANTASY XI installation and successfully fixed it.";
    internal const string ModsWarning = "This will also remove any DAT modifications you have applied directly to the game files.";
    private const string GameRunningText = "FINAL FANTASY XI is running. Close every FINAL FANTASY XI window, then run the repair again.";
    private const string ViewerRunningText =
        "PlayOnline is running, either for a logged-in account or in another copy of Provenance. Log out and close it, then run the repair again.";
    private static readonly HttpClient MirrorHttp = new() { Timeout = TimeSpan.FromSeconds(60) };

    private CancellationTokenSource? _cts;
    private volatile bool _changing;   // MirrorRepair says so right before the first game file changes
    private volatile bool _fixed;      // and once the registry pass has put something right, or DXSETUP's started
    private volatile bool _fixing;     // as the registry pass or DXSETUP starts
    internal bool _started;
    private DownloadMeter? _meter;

    public Func<string, string, bool>? Ask { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Finished))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _running;

    public bool Finished => !Running && _started;

    [ObservableProperty] private string _stage = CheckingStage;
    [ObservableProperty] private string _status = CheckingText;
    [ObservableProperty] private bool _failed;
    [ObservableProperty] private bool _succeeded;
    [ObservableProperty] private double _fraction;
    [ObservableProperty] private bool _indeterminate = true;
    [ObservableProperty] private string _amount = "";
    [ObservableProperty] private string _timeLeft = "";

    public bool StopRequested => _cts?.IsCancellationRequested == true;

    // made on first use, on the UI thread. It's fed the bar's own bytes, so its percent is the bar's
    private DownloadMeter Meter => _meter ??= new DownloadMeter(text => Amount = text, text => TimeLeft = text);

    // replaced game files win, anything else fixed (registry, COM, DirectX, patch.ver) gets the general line
    internal static string ResultText(RepairResult result) => result switch
    {
        { FilesReplaced: 1 } => "File repair found an issue with 1 file and successfully replaced it.",
        { FilesReplaced: > 1 } => $"File repair found an issue with {result.FilesReplaced} files and successfully replaced them.",
        { OtherFixes: > 0 } => FixedText,
        _ => NoIssuesText,
    };

    // an update that's out but not in yet shows up as changed files too, so then they're "damaged or out of date". With
    // no file different there are no DAT changes to lose
    internal static string Question(int files, long bytes, bool outOfDate) => (files, outOfDate) switch
    {
        (0, _) => "Repair found an unfinished PlayOnline Viewer update and can finish it, downloading fresh copies of any files that need it.",
        (1, false) => $"Repair found an issue with 1 file ({Size(bytes)}) and can download a fresh copy to replace it. " + ModsWarning,
        (_, false) => $"Repair found an issue with {files} files ({Size(bytes)}) and can download fresh copies to replace them. " + ModsWarning,
        (1, true) => $"Repair found 1 file that's damaged or out of date ({Size(bytes)}) and can download a fresh copy to replace it. " + ModsWarning,
        _ => $"Repair found {files} files that are damaged or out of date ({Size(bytes)}) and can download fresh copies to replace them. " + ModsWarning,
    };

    internal static string Size(long bytes) =>
        bytes >= 1_000_000_000 ? Invariant($"{bytes / 1e9:0.0} GB") : bytes >= 1_000_000 ? Invariant($"{bytes / 1e6:0.0} MB")
        : Invariant($"{Math.Max(1, bytes / 1000)} KB");

    private enum Ending { NotOnMirror, Declined, Repaired }

    private sealed record Outcome(Ending Ending, RepairResult? Result = null);

    private sealed class RefusedException(string message) : Exception(message);

    public async Task RunAsync()
    {
        if (_started) return;
        _started = true;
        Running = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        bool stoppedPol = false;
        try
        {
            var installed = region ?? PolRegion.UnitedStates;
            if (MirrorLayout.RegionFolder(installed) is not { } mirrorRegion)
            {
                End("Repair isn't available", MainViewModel.RepairNotYetText(installed), failed: false);
                return;
            }
            var viewerDir = PolPaths.TryGetInstallFolder(installed, PolPaths.ViewerProduct);
            var ffxiDir = PolPaths.TryGetInstallFolder(installed, PolPaths.FfxiProduct);
            if (viewerDir is null || ffxiDir is null)
            {
                End("Nothing to repair", "Provenance couldn't find both the PlayOnline Viewer and FINAL FANTASY XI on this PC. Install the game first.", failed: true);
                return;
            }
            // the installers' INSTALLDIR is the viewer folder's grandparent (<root>\SquareEnix\PlayOnlineViewer)
            var installRoot = Directory.GetParent(viewerDir.TrimEnd('\\'))?.Parent?.FullName ?? Path.GetPathRoot(Path.GetFullPath(viewerDir))!;
            if (await Task.Run(() => PolUpdates.GameRunning(), ct)) { End("Repair can't start", GameRunningText, failed: true); return; }

            // the check keeps its own two lines, the steps only show once there's something to fix
            bool downloading = false, showSteps = false;
            IProgress<string> status = new Progress<string>(s =>
            {
                if (!showSteps || s == Status) return;
                Status = s;
                Indeterminate = true;
                Meter.Clear();
            });
            long checkTotal = 0;
            var bytes = new Progress<(long Done, long Total)>(p =>
            {
                long done = p.Done, total = p.Total;
                if (!downloading) checkTotal = total;
                // with nothing known wrong the package databases carry on the check's bar, one total by bytes
                else if (!showSteps) { done += checkTotal; total += checkTotal; }
                Indeterminate = false;
                // never back under one title, a retry that takes bytes back just holds the bar
                Fraction = Math.Max(Fraction, total > 0 ? (double)done / total : 1);
                // the line under it follows the held bar too, so a retry doesn't show a countdown under a bar that's stood still
                Meter.Update(Math.Clamp((long)(Fraction * total), done, Math.Max(done, total)), total, download: downloading);
            });
            var waiting = new Progress<PatchProgress>(p => Status = p.Text);
            // Normal, so the progress reports already queued land first and can't paint over what comes next
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            void OnUi(Action a) => dispatcher.Invoke(a, System.Windows.Threading.DispatcherPriority.Normal);
            var outcome = await Task.Run(() => PolUpdates.Locked(PolUpdates.Job.Repair, ct, waiting, () =>
            {
                OnUi(() => Status = CheckingText);
                // again under the lock, another copy may have started the game while this one waited
                if (PolUpdates.GameRunning()) throw new RefusedException(GameRunningText);
                // my waiting pol.exe holds viewer files, so it goes first like it does for a viewer update
                if (stopWaitingPol is not null)
                {
                    stoppedPol = true;
                    stopWaitingPol();
                    // a killed pol.exe can hang about in the process list for a moment
                    for (int i = 0; i < 10 && PolUpdates.ViewerRunning(); i++) ct.WaitHandle.WaitOne(300);
                }
                if (PolUpdates.ViewerRunning()) throw new RefusedException(ViewerRunningText);

                // the installer data runs as admin (DXSETUP), so each run gets its own admin-only folder
                var stagingRoot = SecureFiles.SubDir("repair");
                RemoveStaging(Directory.GetDirectories(stagingRoot));
                var staging = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
                try
                {
                    var repair = new MirrorRepair(new MirrorClient(MirrorHttp, MirrorKeys.Trusted), mirrorRegion)
                    {
                        StagingDir = staging,
                        Status = status.Report,
                        Log = m => Log.Information("Repair: {Message}", m),
                        Changing = () => _changing = true,
                        Fixed = () => _fixed = true,
                        Fixing = () => _fixing = true,
                    };
                    var plan = repair.PlanAsync(viewerDir, ffxiDir, bytes, ct).GetAwaiter().GetResult();
                    if (plan is null) return new Outcome(Ending.NotOnMirror);
                    Log.Information("Repair plan: {Count} files, {Bytes:N0} bytes, out-of-date patch.ver: {Stale}, viewer queue: {Queue}, " +
                                    "DirectX missing: {DirectX} (Viewer {Viewer}: {ViewerCount}, FFXI {Ffxi}: {FfxiCount})",
                                    plan.Count, plan.Bytes, plan.StaleVersions, plan.QueuePending, plan.DirectXMissing,
                                    plan.Versions["viewer"], plan.Work["viewer"].Count, plan.Versions["ffxi"], plan.Work["ffxi"].Count);
                    // only replacing game files asks, the registry and DirectX never touch game data
                    bool ask = plan.Count > 0 || plan.QueuePending;
                    if (ask)
                    {
                        bool yes = false;
                        OnUi(() => yes = Ask?.Invoke("Repair game files?", Question(plan.Count, plan.Bytes, plan.StaleVersions.Count > 0)) ?? false);
                        if (!yes) return new Outcome(Ending.Declined);
                    }
                    // the check or the question may have been sat there a while
                    if (PolUpdates.GameRunning()) throw new RefusedException(GameRunningText);
                    if (PolUpdates.ViewerRunning()) throw new RefusedException(ViewerRunningText);
                    bool known = ask || plan.StaleVersions.Count > 0 || plan.DirectXMissing;
                    OnUi(() =>
                    {
                        downloading = true;
                        if (!known) return;   // the bar carries on for the package databases
                        showSteps = true;
                        Stage = RepairingStage;
                        Status = "";
                        Indeterminate = true;
                        Fraction = 0;
                        Meter.Clear();
                    });
                    var result = repair.ApplyAsync(plan, viewerDir, ffxiDir, installRoot, bytes, ct).GetAwaiter().GetResult();
                    return new Outcome(Ending.Repaired, result);
                }
                finally { RemoveStaging([staging]); }
            }), ct);

            switch (outcome.Ending)
            {
                case Ending.NotOnMirror:
                    End("Repair isn't ready yet", "Repair can't check the latest game update yet. Try again later.", failed: false);
                    break;
                case Ending.Declined:
                    End("Nothing was changed", "Repair was cancelled before any files were changed.", failed: false);
                    break;
                default:
                    var result = outcome.Result!;
                    Log.Information("Repair finished: {Files} files replaced, {Other} other things fixed", result.FilesReplaced, result.OtherFixes);
                    Done(ResultText(result));
                    break;
            }
        }
        catch (RefusedException ex) { End("Repair can't start", ex.Message, failed: true); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            End("Repair stopped", Unchanged(), failed: false);
        }
        catch (MirrorUnusableException ex)
        {
            Log.Error("Repair: the mirror could not be used: {Reason}", ex.Message);
            End("Repair didn't finish", "Repair couldn't download what it needs. Try again later. " + Unchanged(), failed: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)   // a request that timed out
        {
            Log.Error("Repair: the mirror is unreachable: {Reason}", ex.Message);
            End("Repair didn't finish", "Repair couldn't connect to download what it needs. Check your internet connection, then try again. " + Unchanged(),
                failed: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Repair failed");
            End("Repair didn't finish", ErrorText.Of(ex, "Repair ran into a problem and stopped. " + Unchanged()), failed: true);
        }
        finally
        {
            if (stoppedPol && restartWaitingPol is not null)
            {
                try { await Task.Run(restartWaitingPol); }
                catch (Exception e) { Log.Warning(e, "Could not start the waiting pol.exe again after the repair"); }
            }
            Running = false;
        }
    }

    private bool CanCancel() => Running;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (_cts is not { IsCancellationRequested: false } cts) return;
        cts.Cancel();
        Stage = "Stopping...";
        Status = _changing || _fixed || _fixing ? "The repair will stop after its current step." : "Nothing was changed.";
    }

    private static void RemoveStaging(IEnumerable<string> dirs)
    {
        foreach (var dir in dirs)
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warning(e, "Could not remove {Dir}", dir); }
    }

    private string Unchanged() =>
        _changing ? "Some files might already have been replaced. Run the repair again to finish."
        : _fixed ? "Some fixes might already have been made. Run the repair again to finish."
        : "Nothing was changed.";

    // the result on its own, no heading over it
    internal void Done(string text)
    {
        End("", text, failed: false);
        Succeeded = true;
    }

    internal void End(string stage, string text, bool failed)
    {
        Stage = stage;
        Status = text;
        Failed = failed;
        Indeterminate = false;
        Meter.Clear();
    }
}
