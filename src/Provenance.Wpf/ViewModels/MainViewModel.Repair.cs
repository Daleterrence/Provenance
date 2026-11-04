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
    internal static string RepairLockedText(PolUpdates.Job? holder) =>
        $"Another copy of Provenance is {PolUpdates.Doing(holder)}. Repair can start once it's finished.";
    internal const string RepairGameRunningText = "FINAL FANTASY XI is running. Close it to repair the game files.";

    internal static string RepairNotYetText(PolRegion region) =>
        $"Repair isn't available for the {PolPaths.VersionName(region)} version of PlayOnline yet.";

    internal DispatcherTimer? _repairProbe;
    internal bool _sessionEnding;
    private bool _probing;
    // while the repair window's up
    [ObservableProperty] private bool _repairing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepairBlockedReason), nameof(RepairTooltip))]
    [NotifyCanExecuteChangedFor(nameof(RepairGameFilesCommand))]
    private bool _repairAvailable;

    // the region's text when the mirror has no copy to repair it from (Japan for now)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepairBlockedReason), nameof(RepairTooltip))]
    [NotifyCanExecuteChangedFor(nameof(RepairGameFilesCommand))]
    private string? _repairNotYet;

    // what the probe last saw: another copy holding the update lock, or the game running
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepairBlockedReason), nameof(RepairTooltip))]
    [NotifyCanExecuteChangedFor(nameof(RepairGameFilesCommand))]
    private string? _repairBusyReason;

    public string? RepairBlockedReason =>
        !RepairAvailable ? null
        : RepairNotYet is { } notYet ? notYet
        : _pol.Pid is not null || _sessionEnding ? "Repair can start once you're logged out of PlayOnline."
        : MultiRunning ? "Repair can start once Provenance has finished starting your accounts."
        : RepairBusyReason;

    public string RepairTooltip => RepairBlockedReason ?? RepairHint;

    // A broken install can have lost FFXiMain.dll, so it only needs both folders registered. A region the mirror has no
    // copy of is shown but off
    internal static (bool Available, string? NotYet) RepairState(PolRegion? region, bool haveKeys, Func<PolRegion, string, string?> folder)
    {
        var r = region ?? PolRegion.UnitedStates;
        bool available = haveKeys && folder(r, PolPaths.ViewerProduct) is not null && folder(r, PolPaths.FfxiProduct) is not null;
        return (available, MirrorLayout.RegionFolder(r) is null ? RepairNotYetText(r) : null);
    }

    private void RefreshRepairAvailable(PolRegion? region)
    {
        var (available, notYet) = RepairState(region, MirrorKeys.Trusted.Count > 0, PolPaths.TryGetInstallFolder);
        RepairNotYet = notYet;
        RepairAvailable = available;
    }

    // Pid and the end of a sign-out have no change notice of their own
    internal void RefreshRepair()
    {
        OnPropertyChanged(nameof(RepairBlockedReason));
        OnPropertyChanged(nameof(RepairTooltip));
        RepairGameFilesCommand.NotifyCanExecuteChanged();
        RefreshLargeAddressAware();
    }

    private bool CanRepair() => RepairAvailable && !Repairing && !IsLaunching && !SocialConnected && RepairBlockedReason is null;

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
        Repairing = true;
        RepairGameFilesCommand.NotifyCanExecuteChanged();
        try { w.ShowDialog(); }
        finally
        {
            Repairing = false;
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
        if (_probing || Repairing || !_windowShown || !RepairAvailable || RepairNotYet is not null || SocialConnected || IsLaunching || MultiRunning) return;
        _probing = true;
        try { RepairBusyReason = await Task.Run(RepairBusy); }
        finally { _probing = false; }
    }

    // the lock's only taken for a moment, and only when it's free
    private static string? RepairBusy()
    {
        try
        {
            if (!PolUpdates.LockFree()) return RepairLockedText(PolUpdates.Holder());
            if (PolUpdates.GameRunning()) return RepairGameRunningText;
        }
        catch (Exception e) { Log.Debug(e, "Could not check whether a repair could start"); }
        return null;
    }
}
