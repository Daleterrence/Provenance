using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common;
using Provenance.Common.Install;
using Provenance.Common.Security;
using Serilog;
using Provenance.Common.Mirror;
using Provenance.Common.Patcher;
using Provenance.Common.PolFormat;
using Provenance.Common.Progress;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Main;

namespace Provenance.Wpf.ViewModels.Install;

// Checks every file against the mirror's signed index and only replaces game files after a yes. The pol.exe callbacks
// stop my pre-started pol.exe (it holds viewer files) and keep it off until the repair's done
public partial class RepairViewModel(IDialogs dialogs, PolRegion? region, Action? stopWaitingPol = null, Action? restartWaitingPol = null) : ObservableObject
{
    internal const string CheckingText = "This may take a while.";
    internal const string NoIssuesText = "No issues were found with your FINAL FANTASY XI or PlayOnline Viewer installation data.";
    internal const string DoneText = "The repair completed successfully.";
    internal const string ModsWarning = "This will also remove any DAT modifications you have applied directly to the game files.";
    private const string GameRunningText = "FINAL FANTASY XI is running. Close every FINAL FANTASY XI window, then run the repair again.";
    private const string ViewerRunningText =
        "PlayOnline is running, either for a logged-in account or in another copy of Provenance. Log out and close it, then run the repair again.";

    private readonly UiThread _ui = new();
    private CancellationTokenSource? _cts;
    private volatile bool _changing;   // GameRepair says so right before the first game file changes
    private volatile bool _fixed;      // and once the registry pass has put something right, or DXSETUP's started
    private volatile bool _fixing;     // as the registry pass or DXSETUP starts
    internal bool _started;
    private DownloadMeter? _meter;
    private GameProduct? _product;     // the one GameRepair's last said it's working on
    private bool _repairing;           // the stage has moved from checking to repairing

    // Enter and Esc both answer no, the yes takes DAT mods away for good
    internal bool ConfirmRepair(int files, long bytes, bool outOfDate, int ffxiFiles) =>
        dialogs.Confirm("Repair game files?", Question(files, bytes, outOfDate, ffxiFiles), "CONTINUE", ConfirmStyle.SafeKeys);

    internal bool ConfirmStop() => dialogs.Confirm("Stop the repair?",
        "The repair will stop after its current step. Run it again later to finish it.", "STOP", ConfirmStyle.Destructive);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Finished))]
    [NotifyPropertyChangedFor(nameof(ShowDetailsButton))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _running;

    public bool Finished => !Running && _started;

    [ObservableProperty] private string _stage = StageText(null, false);
    [ObservableProperty] private string _status = CheckingText;
    [ObservableProperty] private bool _failed;
    [ObservableProperty] private bool _succeeded;
    [ObservableProperty] private double _fraction;
    [ObservableProperty] private bool _indeterminate = true;
    [ObservableProperty] private string _amount = "";
    [ObservableProperty] private string _timeLeft = "";

    public bool StopRequested => _cts?.IsCancellationRequested == true;

    // made on first use, on the UI thread. It's fed the bar's own bytes, so its percent is the bar's
    private DownloadMeter Meter => _meter ??= new DownloadMeter(text => Amount = text, text => TimeLeft = text, fraction => Fraction = fraction);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDetailsButton))]
    private string _details = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsButtonText))]
    private bool _showDetails;

    public bool ShowDetailsButton => Finished && Details.Length > 0;

    public string DetailsButtonText => ShowDetails ? "HIDE DETAILS" : "SHOW DETAILS";

    [RelayCommand]
    private void ToggleDetails() => ShowDetails = !ShowDetails;

    internal static string StageText(GameProduct? product, bool repairing) =>
        $"{(repairing ? "Repairing" : "Checking")} your {(product is null ? "game" : product.Name)} files...";

    // a stop has put its own words up, a product starting after it mustn't take them back
    internal void ShowProduct(GameProduct product)
    {
        if (!Running || StopRequested) return;
        _product = product;
        Stage = StageText(product, _repairing);
    }

    internal void StartRepairing()
    {
        _repairing = true;
        Stage = StageText(_product, true);
    }

    internal static string ResultText(RepairResult result) => result.FilesReplaced > 0 || result.OtherFixes > 0 ? DoneText : NoIssuesText;

    // the replaced files under each product, then the rest one to a line
    internal static string DetailsText(RepairResult result)
    {
        var lines = new List<string>();
        foreach (var product in result.Replaced)
        {
            if (lines.Count > 0) lines.Add("");
            lines.Add(product.Product);
            lines.AddRange(product.Files.Select(f => "  " + f));
        }
        if (result.Fixes.Count > 0 && lines.Count > 0) lines.Add("");
        lines.AddRange(result.Fixes);
        return string.Join(Environment.NewLine, lines);
    }

    // an update that's out but not in yet shows up as changed files too, so then they're "damaged or out of date". With
    // no file different there are no DAT changes to lose
    // the DAT warning only when one of the files is the game's
    internal static string Question(int files, long bytes, bool outOfDate, int ffxiFiles)
    {
        var size = ByteText.Size(bytes, kb: true);
        var warning = ffxiFiles > 0 ? " " + ModsWarning : "";
        return (files, outOfDate) switch
        {
            (0, _) => "Repair found an unfinished PlayOnline Viewer update and can finish it, downloading fresh copies of any files that need it.",
            (1, false) => $"Repair found an issue with 1 file ({size}) and can download a fresh copy to replace it." + warning,
            (_, false) => $"Repair found an issue with {files} files ({size}) and can download fresh copies to replace them." + warning,
            (1, true) => $"Repair found 1 file that's damaged or out of date ({size}) and can download a fresh copy to replace it." + warning,
            _ => $"Repair found {files} files that are damaged or out of date ({size}) and can download fresh copies to replace them." + warning,
        };
    }

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
                End("Repair isn't available", RepairButtonViewModel.NotYetText(installed), failed: false);
                return;
            }
            var viewerDir = PolPaths.TryGetInstallFolder(installed, GameProduct.Viewer);
            var ffxiDir = PolPaths.TryGetInstallFolder(installed, GameProduct.Game);
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
            var bytes = new Progress<ByteProgress>(p =>
            {
                long done = p.Done, total = p.Total;
                if (!downloading) checkTotal = total;
                // with nothing known wrong the package databases carry on the check's bar, one total by bytes
                else if (!showSteps) { done += checkTotal; total += checkTotal; }
                Indeterminate = false;
                // never back under one title, the line under it follows the held bar too
                Meter.Update(done, total, total > 0 ? (double)done / total : 1, download: downloading);
            });
            var waiting = new Progress<PatchProgress>(p => Status = p.Text);
            var outcome = await Task.Run(() => GameFilesLock.Locked(GameFilesLock.Job.Repair, ct, waiting, () =>
            {
                _ui.Run(() => Status = CheckingText);
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
                var stagingRoot = ProtectedFolders.SubDir("repair");
                RemoveStaging(Directory.GetDirectories(stagingRoot));
                var staging = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
                try
                {
                    var repair = new GameRepair(MirrorClient.Shared, mirrorRegion)
                    {
                        StagingDir = staging,
                        Status = status.Report,
                        Changing = () => _changing = true,
                        Fixed = () => _fixed = true,
                        Fixing = () => _fixing = true,
                        Working = p => _ui.Run(() => ShowProduct(p)),
                    };
                    var plan = repair.PlanAsync(viewerDir, ffxiDir, bytes, ct).GetAwaiter().GetResult();
                    if (plan is null) return new Outcome(Ending.NotOnMirror);
                    Log.Information("Repair plan: {Count} files, {Bytes:N0} bytes, out-of-date patch.ver: {Stale}, viewer queue: {Queue}, " +
                                    "DirectX missing: {DirectX} (Viewer {Viewer}: {ViewerCount}, FFXI {Ffxi}: {FfxiCount})",
                                    plan.Count, plan.Bytes, plan.StaleVersions, plan.QueuePending, plan.DirectXMissing,
                                    plan.Indexes[GameProduct.Viewer.Slug].Version, plan.Work[GameProduct.Viewer.Slug].Count,
                                    plan.Indexes[GameProduct.Game.Slug].Version, plan.Work[GameProduct.Game.Slug].Count);
                    // only replacing game files asks, the registry and DirectX never touch game data
                    bool ask = plan.Count > 0 || plan.QueuePending;
                    if (ask)
                    {
                        bool yes = false;
                        _ui.Run(() => yes = ConfirmRepair(plan.Count, plan.Bytes, plan.StaleVersions.Count > 0, plan.Work[GameProduct.Game.Slug].Count));
                        if (!yes) return new Outcome(Ending.Declined);
                    }
                    // the check or the question may have been sat there a while
                    if (PolUpdates.GameRunning()) throw new RefusedException(GameRunningText);
                    if (PolUpdates.ViewerRunning()) throw new RefusedException(ViewerRunningText);
                    bool known = ask || plan.StaleVersions.Count > 0 || plan.DirectXMissing;
                    _ui.Run(() =>
                    {
                        downloading = true;
                        if (!known) return;   // the bar carries on for the package databases
                        showSteps = true;
                        StartRepairing();
                        Status = "";
                        Indeterminate = true;
                        Meter.Restart();
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
                    Done(ResultText(result), DetailsText(result));
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
    internal void Done(string text, string details = "")
    {
        End("", text, failed: false);
        Details = details;
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
