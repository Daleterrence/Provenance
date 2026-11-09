using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Game;
using Provenance.Common.Install;
using Provenance.Common.Login;
using Provenance.Common.Mirror;
using Provenance.Common.Patcher;
using Provenance.Common.PolFormat;
using Provenance.Common.Settings;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Install;
using Serilog;

namespace Provenance.Wpf.ViewModels.Main;

// Repair game files lives on the signed-out cards. A signed-in pol.exe holds the viewer and game files open, and
// ending it would sign the account out
public partial class RepairButtonViewModel : ObservableObject
{
    private const string Hint = "Checks every game file and replaces any that are damaged. It'll ask you before it replaces anything.";
    internal static string LockedText(GameFilesLock.Job? holder) =>
        $"Another copy of Provenance is {GameFilesLock.Doing(holder)}. Repair can start once it's finished.";
    internal const string GameRunningText = "FINAL FANTASY XI is running. Close it to repair the game files.";

    internal static string NotYetText(PolRegion region) =>
        $"Repair isn't available for the {PolPaths.VersionName(region)} version of PlayOnline yet.";

    private readonly IDialogs _dialogs;
    private readonly IWindowManager _windows;
    private readonly ISettingsService _settings;
    private readonly PolSession _pol;
    private readonly NewsViewModel _news;
    private readonly Func<bool> _loggedInOrLaunching;
    private readonly Func<bool> _multiRunning;
    private readonly Action _installChanged;
    private readonly Action _largeAddressAwareChanged;

    internal DispatcherTimer? _repairProbe;
    internal bool SessionEnding { get; set; }
    private bool _probing;

    public RepairButtonViewModel(IDialogs dialogs, IWindowManager windows, ISettingsService settings, PolSession pol,
                                 NewsViewModel news, Func<bool> loggedInOrLaunching, Func<bool> multiRunning,
                                 Action installChanged, Action largeAddressAwareChanged)
    {
        _dialogs = dialogs;
        _windows = windows;
        _settings = settings;
        _pol = pol;
        _news = news;
        _loggedInOrLaunching = loggedInOrLaunching;
        _multiRunning = multiRunning;
        _installChanged = installChanged;
        _largeAddressAwareChanged = largeAddressAwareChanged;
        _repairProbe = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _repairProbe.Tick += async (_, _) => await ProbeAsync();
        _repairProbe.Start();
    }

    // while the repair window's up
    [ObservableProperty] private bool _repairing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BlockedReason), nameof(Tooltip))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _available;

    // the region's text when the mirror has no copy to repair it from (Japan for now)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BlockedReason), nameof(Tooltip))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string? _notYet;

    // what the probe last saw: another copy holding the update lock, or the game running
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BlockedReason), nameof(Tooltip))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string? _busyReason;

    public string? BlockedReason =>
        !Available ? null
        : NotYet is { } notYet ? notYet
        : _pol.Pid is not null || SessionEnding ? "Repair can start once you're logged out of PlayOnline."
        : _multiRunning() ? "Repair can start once Provenance has finished starting your accounts."
        : BusyReason;

    public string Tooltip => BlockedReason ?? Hint;

    // A broken install can have lost FFXiMain.dll, so it only needs both folders registered. A region the mirror has no
    // copy of is shown but off
    internal static (bool Available, string? NotYet) State(PolRegion? region, Func<PolRegion, GameProduct, string?> folder)
    {
        var r = region ?? PolRegion.UnitedStates;
        bool available = folder(r, GameProduct.Viewer) is not null && folder(r, GameProduct.Game) is not null;
        return (available, MirrorLayout.RegionFolder(r) is null ? NotYetText(r) : null);
    }

    internal void SetRegion(PolRegion? region)
    {
        var (available, notYet) = State(region, PolPaths.TryGetInstallFolder);
        NotYet = notYet;
        Available = available;
    }

    // Pid and the end of a sign-out have no change notice of their own
    internal void Refresh()
    {
        OnPropertyChanged(nameof(BlockedReason));
        OnPropertyChanged(nameof(Tooltip));
        StartCommand.NotifyCanExecuteChanged();
        _largeAddressAwareChanged();
    }

    private bool CanStart() => Available && !Repairing && !_loggedInOrLaunching() && BlockedReason is null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        // pre-starts are held off for the whole repair, so no background Prestart lands a pol.exe on top of it
        var vm = new RepairViewModel(_dialogs, _settings.DetectedRegion, _pol.HoldPrestarts, () =>
        {
            // still held, so a pol.exe the repair replaced gets it back before anything pre-starts
            try { LargeAddressAwareSwitch.Apply(_settings, "after the repair"); }
            finally { _pol.ReleasePrestarts(); }
        });
        Repairing = true;
        StartCommand.NotifyCanExecuteChanged();
        try { _windows.ShowRepair(vm); }
        finally
        {
            Repairing = false;
            Refresh();
            _installChanged();
        }
    }

    // when the window comes forward, and every few seconds while a signed-out card's up
    public async Task ProbeAsync()
    {
        // a multi-account run holds the update lock in this copy, which isn't "another copy"
        if (_probing || Repairing || !_news.WindowShown || !Available || NotYet is not null || _loggedInOrLaunching() || _multiRunning()) return;
        _probing = true;
        try { BusyReason = await Task.Run(Busy); }
        finally { _probing = false; }
    }

    // the lock's only taken for a moment, and only when it's free
    private static string? Busy()
    {
        try
        {
            if (!GameFilesLock.LockFree()) return LockedText(GameFilesLock.Holder());
            if (PolUpdates.GameRunning()) return GameRunningText;
        }
        catch (Exception e) { Log.Debug(e, "Could not check whether a repair could start"); }
        return null;
    }
}
