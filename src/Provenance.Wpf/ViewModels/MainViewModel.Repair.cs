using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Provenance.Common.Mirror;
using Provenance.Common.PolFormat;
using Provenance.Wpf.Services;
using Provenance.Wpf.Views;

namespace Provenance.Wpf.ViewModels;

// Repair game files lives on the signed-out cards. A signed-in pol.exe holds the viewer and game files open, and
// ending it would sign the account out
public partial class MainViewModel
{
    private const string RepairHint = "Checks every game file and replaces any that are damaged. It'll ask you before it replaces anything.";
    internal const string RepairLockedText = "Another copy of Provenance is updating or repairing the game files. Repair can start once it's finished.";
    internal const string RepairGameRunningText = "FINAL FANTASY XI is running. Close it to repair the game files.";

    private DispatcherTimer? _repairProbe;
    private bool _repairing, _sessionEnding, _probing;

    // the mirror only has the North American client. A broken install can have lost FFXiMain.dll, so it only needs
    // both folders registered
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepairBlockedReason), nameof(RepairTooltip))]
    [NotifyCanExecuteChangedFor(nameof(RepairGameFilesCommand))]
    private bool _repairAvailable;

    // what the probe last saw: another copy holding the update lock, or the game running
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepairBlockedReason), nameof(RepairTooltip))]
    [NotifyCanExecuteChangedFor(nameof(RepairGameFilesCommand))]
    private string? _repairBusyReason;

    public string? RepairBlockedReason =>
        !RepairAvailable ? null
        : _pol.Pid is not null || _sessionEnding ? "Repair can start once you're logged out of PlayOnline."
        : MultiRunning ? "Repair can start once Provenance has finished starting your accounts."
        : RepairBusyReason;

    public string RepairTooltip => RepairBlockedReason ?? RepairHint;

    private void RefreshRepairAvailable(PolRegion? region) =>
        RepairAvailable = MirrorKeys.Trusted.Count > 0 && (region is null or PolRegion.UnitedStates)
                          && PolPaths.TryGetInstallFolder(PolRegion.UnitedStates, PolPaths.ViewerProduct) is not null
                          && PolPaths.TryGetInstallFolder(PolRegion.UnitedStates, PolPaths.FfxiProduct) is not null;

    // Pid and the end of a sign-out have no change notice of their own
    private void RefreshRepair()
    {
        OnPropertyChanged(nameof(RepairBlockedReason));
        OnPropertyChanged(nameof(RepairTooltip));
        RepairGameFilesCommand.NotifyCanExecuteChanged();
        RefreshLargeAddressAware();
    }

    private bool CanRepair() => RepairAvailable && !_repairing && !IsLaunching && !SocialConnected && RepairBlockedReason is null;

    [RelayCommand(CanExecute = nameof(CanRepair))]
    private void RepairGameFiles()
    {
        // pre-starts are held off for the whole repair, so no background Prestart lands a pol.exe on top of it
        var w = new RepairWindow(new RepairViewModel(_settings.DetectedRegion, _pol.HoldPrestarts, () =>
        {
            // still held, so a pol.exe the repair replaced gets it back before anything pre-starts
            try { LargeAddressAwareSwitch.Apply(_settings, "after the repair"); }
            finally { _pol.ReleasePrestarts(); }
        }));
        if (Application.Current.MainWindow is { IsVisible: true } owner) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _repairing = true;
        RepairGameFilesCommand.NotifyCanExecuteChanged();
        try { w.ShowDialog(); }
        finally
        {
            _repairing = false;
            RefreshRepair();
            RefreshInstallState();
        }
    }

    private void StartRepairProbe()
    {
        _repairProbe = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _repairProbe.Tick += async (_, _) => await ProbeRepairAsync();
        _repairProbe.Start();
    }

    // when the window comes forward, and every few seconds while a signed-out card's up
    public async Task ProbeRepairAsync()
    {
        // a multi-account run holds the update lock in this copy, which isn't "another copy"
        if (_probing || _repairing || !_windowShown || !RepairAvailable || SocialConnected || IsLaunching || MultiRunning) return;
        _probing = true;
        try { RepairBusyReason = await Task.Run(RepairBusy); }
        finally { _probing = false; }
    }

    // the lock's only taken for a moment, and only when it's free
    private static string? RepairBusy()
    {
        try
        {
            if (!PolUpdates.LockFree()) return RepairLockedText;
            if (PolUpdates.GameRunning()) return RepairGameRunningText;
        }
        catch (Exception e) { Log.Debug(e, "Could not check whether a repair could start"); }
        return null;
    }
}
