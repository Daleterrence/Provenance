using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Game;
using Provenance.Common.Install;
using Provenance.Common.Login;
using Provenance.Common.Patcher;
using Provenance.Common.PolFormat;
using Provenance.Common.Settings;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Install;
using Serilog;

namespace Provenance.Wpf.ViewModels.Main;

public partial class RepairButtonViewModel : ObservableObject
{
    internal static string LockedText(GameFilesLock.Job? holder) =>
        $"Another copy of Provenance is {GameFilesLock.Doing(holder)}. Repair can start once it's finished.";
    internal const string GameRunningText = "FINAL FANTASY XI is running. Close it to repair the game files.";

    private readonly IDialogs _dialogs;
    private readonly IWindowManager _windows;
    private readonly ISettingsService _settings;
    private readonly PolSession _pol;
    private readonly SessionState _session;
    private readonly Func<bool> _windowShown;
    private readonly Action _installChanged;

    private readonly DispatcherTimer _repairProbe;
    private bool _probing;

    public RepairButtonViewModel(IDialogs dialogs, IWindowManager windows, ISettingsService settings, PolSession pol,
                                 SessionState session, Func<bool> windowShown, Action installChanged)
    {
        _dialogs = dialogs;
        _windows = windows;
        _settings = settings;
        _pol = pol;
        _session = session;
        _windowShown = windowShown;
        _installChanged = installChanged;
        _repairProbe = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _repairProbe.Tick += async (_, _) => await ProbeAsync();
        _repairProbe.Start();
        session.PhaseChanged += Refresh;
    }

    [ObservableProperty] private bool _repairing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BlockedReason))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _available;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BlockedReason))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string? _busyReason;

    public string? BlockedReason =>
        !Available ? null
        : _session.InMultiRun ? "Repair can start once Provenance has finished starting your accounts."
        : _pol.Pid is not null || _session.Phase == SessionPhase.LoggingOut ? "Repair can start once you're logged out of PlayOnline."
        : BusyReason;

    internal static bool State(PolRegion? region, Func<PolRegion, GameProduct, string?> folder)
    {
        var r = region ?? PolRegion.UnitedStates;
        return folder(r, GameProduct.Viewer) is not null && folder(r, GameProduct.Game) is not null;
    }

    internal void SetRegion(PolRegion? region) => Available = State(region, PolPaths.TryGetInstallFolder);

    internal void Refresh()
    {
        OnPropertyChanged(nameof(BlockedReason));
        StartCommand.NotifyCanExecuteChanged();
    }

    private bool CanStart() => Available && !Repairing && !_session.Launching && !_session.LoggedIn && BlockedReason is null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        var vm = new RepairViewModel(_dialogs, _settings.DetectedRegion, _pol.Prestarter.HoldPrestarts,
                                     () => LargeAddressAwareSwitch.Apply(_settings, "after the repair"));
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

    internal void StopProbing() => _repairProbe.Stop();
    internal bool Probing => _probing;

    public async Task ProbeAsync()
    {
        if (_probing || Repairing || !_windowShown() || !Available || _session.LoggedIn || _session.Launching || _session.InMultiRun) return;
        _probing = true;
        try { BusyReason = await Task.Run(Busy); }
        finally { _probing = false; }
    }

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
