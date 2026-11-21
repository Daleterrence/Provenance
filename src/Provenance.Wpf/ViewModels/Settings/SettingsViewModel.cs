using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using Provenance.Common.Feeds;
using Provenance.Common.Game;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Game.Windower;
using Provenance.Common.Install;
using Provenance.Common.Patcher;
using Provenance.Common.Security;
using Provenance.Common.Settings;
using Serilog;
using Provenance.Common.PolFormat;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Main;
using Provenance.Wpf.ViewModels.Shared;

namespace Provenance.Wpf.ViewModels.Settings;

public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private static readonly string[] FeedRegionCodes = ["auto", "na", "eu", "jp"];

    private static readonly string[] AddonsTargetProperties =
    [
        nameof(HookSettings.Hook), nameof(HookSettings.WindowerPath), nameof(HookSettings.AshitaPath),
        nameof(HookSettings.WindowerProfile), nameof(HookSettings.AshitaBootConfig),
    ];

    private readonly IDialogs _dialogs;
    private readonly ISettingsService _settings;
    private readonly FeedService? _feeds;
    private readonly SettingsContext _context;
    private readonly SessionState _session;
    private readonly Func<IDisposable>? _holdPrestarts;
    private readonly Func<bool, RegistryKey?>? _gameKey;
    private bool _gameReady;

    public SettingsViewModel(IDialogs dialogs, ISettingsService settings, FeedService? feeds = null, SessionState? session = null,
                             Func<IDisposable>? holdPrestarts = null, Func<bool, RegistryKey?>? gameKey = null, Action? stopPrestart = null,
                             HandlesViewModel? handles = null, Func<bool>? gameRunning = null,
                             Func<string, string, WindowerUpdater>? windowerUpdater = null)
    {
        _session = session ??= new SessionState();
        _context = new SettingsContext(() => session.InMultiRun, () => session.PolInUse, gameRunning ?? PolUpdates.GameRunning);
        _dialogs = dialogs;
        Handles = handles ?? new HandlesViewModel(dialogs, () => null);
        _gameKey = gameKey;
        _settings = settings;
        Hooks = new HookSettings(dialogs, settings, _context, stopPrestart, GameFollows);
        Windower = new WindowerCard(Hooks, settings, dialogs, windowerUpdater);
        Ashita = new AshitaCard(Hooks);
        Multi = new MultiAccountSettings(Hooks, settings, _context);
        CharacterLogin = new AutoLoginSettings(settings);
        Hooks.Saved += SyncGame;
        Hooks.ProfileEdited += RetargetGameAfterEdit;
        Ashita.Renamed += AshitaRenamed;
        _holdPrestarts = holdPrestarts;
        _feeds = feeds;
        _feedRegionIndex = Math.Max(0, Array.IndexOf(FeedRegionCodes,
            (settings.Current.FeedRegion ?? "auto").Trim().ToLowerInvariant()));
        var region = settings.DetectedRegion;
        RegionText = region switch
        {
            PolRegion.UnitedStates => "North America",
            PolRegion.Europe => "Europe",
            PolRegion.Japan => "Japan",
            _ => "No PlayOnline install found",
        };
        string? Found(GameProduct product) => region is { } r ? PolPaths.TryGetInstallFolder(r, product) : null;
        string Folder(GameProduct product) => Found(product) ?? "Not found";
        OpenViewerFolder = new(() => Found(GameProduct.Viewer));
        OpenFfxiFolder = new(() => Found(GameProduct.Game));
        ViewerFolder = Folder(GameProduct.Viewer);
        PolOptionsStatus = region is { } r && PolPaths.TryGetInstallFolder(r, GameProduct.Viewer) is { } viewer && FolderPins.Linked(viewer)
            ? PolOptions.LinkedText : "";
        FfxiFolder = Folder(GameProduct.Game);
        _exitAfterGameExits = settings.Current.ExitAfterGameExits;
        _keepSignedInInTray = settings.Current.KeepSignedInInTray;
        _minimizeToTray = settings.Current.MinimizeToTray;
        _showTimestamps = settings.Current.ShowTimestamps;
        _themeIndex = Array.IndexOf(AppTheme.Names, AppTheme.Normalise(settings.Current.Theme));
        ThemeTiles = [.. AppTheme.Names.Select((name, i) => new ThemeTile(name, ThemeChoices[i], i, PickTheme) { Picked = i == _themeIndex })];
        Notifications = new NotificationSettings(settings);
        _largeAddressAwareOn = settings.Current.LargeAddressAware;
        _chatLanguageFilter = PolOptions.ChatFilter(settings.Current);
        _autoDisconnectIndex = Math.Max(0, Array.IndexOf(PolOptions.AutoDisconnectChoices, PolOptions.AutoDisconnect(settings.Current)));
        Backups = new BackupSettings(dialogs, settings, _context);
        Hooks.Load();
        Multi.Load();
        Game = new GameSettingsViewModel(dialogs, region, GameStoreForSelection(), _context.GameRunning);
        Game.Settings = this;
        _gameReady = true;
        Addons = new AddonsPluginsViewModel(settings, Hooks.PickedAddonsTarget);
        Hooks.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(HookSettings.IsAshita) or nameof(HookSettings.IsWindower))
            {
                OnPropertyChanged(nameof(ShowProfileSwitcher));
                OnPropertyChanged(nameof(ShowProfileTools));
            }
            if (AddonsTargetProperties.Contains(e.PropertyName)) Addons.TargetChanged();
        };
        Addons.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AddonsPluginsViewModel.SameListForEveryProfile)) OnPropertyChanged(nameof(ShowProfileSwitcher));
        };
        _blockedWas = session.LargeAddressAwareBlocked();
        session.PhaseChanged += OnPhaseChanged;
        session.RunChanged += MultiRunChanged;
    }

    public HookSettings Hooks { get; }
    public WindowerCard Windower { get; }
    public AshitaCard Ashita { get; }
    public MultiAccountSettings Multi { get; }
    public AutoLoginSettings CharacterLogin { get; }
    public GameSettingsViewModel Game { get; }
    public AddonsPluginsViewModel Addons { get; }
    public BackupSettings Backups { get; }
    public NotificationSettings Notifications { get; }
    public IReadOnlyList<ThemeTile> ThemeTiles { get; }
    public OpenFolder OpenViewerFolder { get; }
    public OpenFolder OpenFfxiFolder { get; }
    public OpenLogs Logs { get; } = new();
    public HandlesViewModel Handles { get; }
    public string RegionText { get; }
    public string ViewerFolder { get; }
    public string PolOptionsStatus { get; }
    public string FfxiFolder { get; }

    public string VersionText { get; } =
        "Version " + (typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "dev");

    public IReadOnlyList<string> FeedRegionChoices { get; } =
        new[] { "Automatic (matches your install)", "North America", "Europe", "Japan" };

    public IReadOnlyList<string> ThemeChoices { get; } = ["Dark", "Light"];

    public IReadOnlyList<string> AutoDisconnectChoices { get; } =
        PolOptions.AutoDisconnectChoices.Select(m => m < 0 ? "Off" : $"{m} minutes").ToList();

    [ObservableProperty] private bool _exitAfterGameExits;
    [ObservableProperty] private bool _keepSignedInInTray;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _showTimestamps;
    [ObservableProperty] private int _feedRegionIndex;

    partial void OnExitAfterGameExitsChanged(bool value) => _settings.SaveChange(s => s.ExitAfterGameExits = value);
    partial void OnKeepSignedInInTrayChanged(bool value) => _settings.SaveChange(s => s.KeepSignedInInTray = value);
    partial void OnMinimizeToTrayChanged(bool value) => _settings.SaveChange(s => s.MinimizeToTray = value);
    partial void OnShowTimestampsChanged(bool value)
    {
        _settings.SaveChange(s => s.ShowTimestamps = value);
        Timestamps.RaiseChanged();
    }

    [ObservableProperty] private bool _chatLanguageFilter;
    [ObservableProperty] private int _autoDisconnectIndex;

    partial void OnChatLanguageFilterChanged(bool value) => _settings.SaveChange(s =>
    {
        s.ChatLanguageFilter = value;
        s.AutoDisconnectMinutes ??= PolOptions.AutoDisconnect(s);
    });

    partial void OnAutoDisconnectIndexChanged(int value)
    {
        if (value < 0 || value >= PolOptions.AutoDisconnectChoices.Length) return;
        _settings.SaveChange(s =>
        {
            s.AutoDisconnectMinutes = PolOptions.AutoDisconnectChoices[value];
            s.ChatLanguageFilter ??= PolOptions.ChatFilter(s);
        });
    }

    [ObservableProperty] private int _themeIndex;

    partial void OnThemeIndexChanged(int value)
    {
        if (value < 0 || value >= AppTheme.Names.Length) return;
        _settings.SaveChange(s => s.Theme = AppTheme.Names[value]);
        AppTheme.Apply(AppTheme.Names[value]);
        foreach (var tile in ThemeTiles) tile.Picked = tile.Index == value;
    }

    private void PickTheme(int index) => ThemeIndex = index;

    partial void OnFeedRegionIndexChanged(int value)
    {
        if (value < 0 || value >= FeedRegionCodes.Length) return;
        _settings.SaveChange(s => s.FeedRegion = FeedRegionCodes[value]);
        _feeds?.NotifyRegionSettingChanged();
    }

    [ObservableProperty] private bool _largeAddressAwareOn;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LargeAddressAwareEnabled))] private bool _largeAddressAwareAvailable;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LargeAddressAwareEnabled))] private bool _largeAddressAwareBusy;
    [ObservableProperty] private string _largeAddressAwareStatus = "";
    private bool _largeAddressAwareForTest;
    private int _largeAddressAwareReads;

    public bool LargeAddressAwareEnabled => LargeAddressAwareAvailable && !LargeAddressAwareBusy;

    internal LargeAddressAwareSwitch.Target? LargeAddressAwareTargetForTest { get; set; }
    internal Task LargeAddressAwareChange { get; private set; } = Task.CompletedTask;

    private LargeAddressAwareSwitch.Target LargeAddressAwareTarget() =>
        LargeAddressAwareTargetForTest ?? LargeAddressAwareSwitch.For(_settings.DetectedRegion, _session.LargeAddressAwareBlocked);

    public async Task ReadLargeAddressAwareAsync()
    {
        if (_largeAddressAwareForTest || LargeAddressAwareBusy) return;
        int mine = ++_largeAddressAwareReads;
        var now = await ReadLargeAddressAware();
        if (mine != _largeAddressAwareReads || _largeAddressAwareForTest || LargeAddressAwareBusy) return;
        LargeAddressAwareAvailable = now.Available;
        LargeAddressAwareStatus = now.Note ?? "";
    }

    private async Task<LargeAddressAwareSwitch.Reading> ReadLargeAddressAware()
    {
        var target = LargeAddressAwareTarget();
        var (on, clear) = (_settings.Current.LargeAddressAware, _settings.Current.LargeAddressAwareClearAtStart);
        try { return await Task.Run(() => LargeAddressAwareSwitch.Read(target, on, clear)); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read Large Address Aware");
            return new(true, null);
        }
    }

    internal void ShowLargeAddressAwareForTest(bool available, bool on, string status, bool busy = false)
    {
        _largeAddressAwareForTest = true;
        LargeAddressAwareBusy = busy;
        LargeAddressAwareAvailable = available;
        LargeAddressAwareOn = on;
        LargeAddressAwareStatus = status;
    }

    partial void OnLargeAddressAwareOnChanged(bool value)
    {
        if (_largeAddressAwareForTest) return;
        _settings.SaveChange(s =>
        {
            s.LargeAddressAware = value;
            s.LargeAddressAwareClearAtStart = false;
        });
        LargeAddressAwareChange = ChangeLargeAddressAwareAsync(value);
    }

    private async Task ChangeLargeAddressAwareAsync(bool on)
    {
        _largeAddressAwareReads++;
        LargeAddressAwareBusy = true;
        LargeAddressAwareStatus = LargeAddressAwareSwitch.WaitText;
        var target = LargeAddressAwareTarget();
        LargeAddressAwareSwitch.Outcome outcome;
        try { outcome = await Task.Run(() => LargeAddressAwareSwitch.Change(target, on, _holdPrestarts)); }
        catch (Exception ex)
        {
            Log.Error(ex, "Large Address Aware change failed");
            outcome = LargeAddressAwareSwitch.Outcome.NextStart;
        }
        if (outcome != LargeAddressAwareSwitch.Outcome.Done && !on) _settings.SaveChange(s => s.LargeAddressAwareClearAtStart = true);
        var after = await ReadLargeAddressAware();
        LargeAddressAwareBusy = false;
        LargeAddressAwareAvailable = after.Available;
        LargeAddressAwareStatus = after.Available && outcome == LargeAddressAwareSwitch.Outcome.Denied
            ? LargeAddressAwareSwitch.DeniedText : after.Note ?? "";
    }

    private void OnPhaseChanged()
    {
        if (ShownTab == SettingsTab.Backups) Backups.RefreshRestoreNote();
        var blocked = _session.LargeAddressAwareBlocked();
        if (blocked == _blockedWas) return;
        _blockedWas = blocked;
        _ = ReadLargeAddressAwareAsync();
    }

    private string? _blockedWas;

    public void Dispose()
    {
        _blockTimer?.Stop();
        _session.PhaseChanged -= OnPhaseChanged;
        _session.RunChanged -= MultiRunChanged;
    }

    public void MultiRunChanged()
    {
        Multi.RunChanged();
        Hooks.NotifyProfilesChanged();
        if (ShownTab == SettingsTab.Backups) Backups.RefreshRestoreNote();
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowProfileSwitcher), nameof(ShowProfileTools))] private SettingsTab _shownTab;

    private System.Windows.Threading.DispatcherTimer? _blockTimer;

    internal void BlockTick() => Backups.RefreshRestoreNote();

    private void WatchBlocks(bool on)
    {
        if (on)
        {
            _blockTimer ??= new(System.Windows.Threading.DispatcherPriority.Background, System.Windows.Threading.Dispatcher.CurrentDispatcher)
            {
                Interval = TimeSpan.FromSeconds(3),
            };
            _blockTimer.Tick -= OnBlockTick;
            _blockTimer.Tick += OnBlockTick;
            _blockTimer.Start();
        }
        else _blockTimer?.Stop();
    }

    private bool _blockTicking;

    private async void OnBlockTick(object? sender, EventArgs e)
    {
        if (_blockTicking) return;
        _blockTicking = true;
        try { await Backups.RefreshRestoreNoteAsync(); }
        finally { _blockTicking = false; }
    }

    internal bool WatchingBlocks => _blockTimer is { IsEnabled: true };

    public void ShowTab(SettingsTab tab)
    {
        ShownTab = tab;
        WatchBlocks(tab == SettingsTab.Backups);
        if (tab == SettingsTab.Game && !ShowingGame()) PickGameProfile();
        Refresh(tab);
        if (tab == SettingsTab.WindowerAshita) Hooks.ReloadCards();
        if (tab == SettingsTab.AutoLogin) CharacterLogin.Load();
    }

    public void WindowActivated() => Refresh(ShownTab);

    private void Refresh(SettingsTab tab)
    {
        switch (tab)
        {
            case SettingsTab.General:
                OpenViewerFolder.Refresh();
                OpenFfxiFolder.Refresh();
                break;
            case SettingsTab.Game:
                _ = ReadLargeAddressAwareAsync();
                Game.OpenScreenshotFolder.Refresh();
                break;
            case SettingsTab.WindowerAshita:
                Hooks.OpenAshitaFolder.Refresh();
                Hooks.OpenWindowerFolder.Refresh();
                break;
            case SettingsTab.Backups:
                _ = Backups.Reload();
                break;
        }
    }

    public bool ShowProfileSwitcher =>
        ShownTab is SettingsTab.WindowerAshita or SettingsTab.Game or SettingsTab.AddonsPlugins && (Hooks.IsWindower || Hooks.IsAshita)
        && !(ShownTab == SettingsTab.AddonsPlugins && Addons is { SameListForEveryProfile: true });
    public bool ShowProfileTools => ShownTab == SettingsTab.WindowerAshita && (Hooks.IsWindower || Hooks.IsAshita);

    private bool? AskSaveGame(string? which) => _dialogs.Choose("Unsaved game settings",
        $"You've changed some game settings{(which is null ? "" : " for " + which)} but haven't saved them yet.", "CONTINUE",
        "Save them first", "Don't save them") switch { 0 => true, 1 => false, _ => null };

    internal bool ConfirmClose()
    {
        var pick = _dialogs.Choose("Unsaved game settings", "You've changed some game settings but haven't saved them yet.", "CLOSE",
            "Save the changes, then close", "Close without saving");
        if (pick is null) return false;
        if (pick == 0)
        {
            Game.SaveCommand.Execute(null);
            return !Game.HasUnsavedChanges;
        }
        return true;
    }

    private IGameSettingsStore GameStoreForSelection()
    {
        var registry = new RegistryGameStore(_settings.DetectedRegion, _gameKey);
        var s = _settings.Current;
        if (Hooks.IsWindower && Hooks.WindowerDir is { } w)
            return Hooks.WindowerProfile is { } p ? new WindowerGameStore(w, p.Name, registry, p.Display)
                 : new WindowerGameStore(w, PlayTarget.For(s, null, ThirdPartyHook.Windower).Profile, registry);
        if (Hooks.IsAshita && Hooks.AshitaDir is { } a
            && (Hooks.AshitaBootConfig?.Name ?? PlayTarget.For(s, null, ThirdPartyHook.Ashita).Profile) is { Length: > 0 } boot)
            return new AshitaGameStore(a, boot, registry, Hooks.AshitaBootConfig?.Display);
        return registry;
    }

    private void SyncGame()
    {
        if (!_gameReady) return;
        var want = GameStoreForSelection();
        if (want.Key == Game.Store.Key && Game.Available || Game.HasUnsavedChanges) return;
        Game.UseStore(want);
    }

    private bool GameFollows() => ShownTab != SettingsTab.Game || ShowingGame();

    public bool ShowingGame()
    {
        var want = GameStoreForSelection();
        if (want.Key == Game.Store.Key && Game.Available) return true;
        if (Game.HasUnsavedChanges)
        {
            var answer = AskSaveGame(Game.HasProfile ? $"the {Game.ProfileKind} {Game.ProfileShown}" : null);
            if (answer is null) return false;
            if (answer == true)
            {
                Game.SaveCommand.Execute(null);
                if (Game.HasUnsavedChanges) return false;
            }
        }
        Game.UseStore(want);
        return true;
    }

    public void PickGameProfile()
    {
        if (Game.Store.ProfileName is not { } name) return;
        if (Hooks.IsWindower && Game.Store.Platform == ThirdPartyHook.Windower)
            Hooks.WindowerProfile = HookSettings.Item(Hooks.WindowerProfiles, name) ?? Hooks.WindowerProfile;
        else if (Hooks.IsAshita && Game.Store.Platform == ThirdPartyHook.Ashita)
            Hooks.AshitaBootConfig = HookSettings.Item(Hooks.AshitaBootConfigs, name) ?? Hooks.AshitaBootConfig;
    }

    private void RetargetGameAfterEdit(bool ok, string? renamedFrom)
    {
        var gameProfile = Game.Store.ProfileName;
        if (!ok || gameProfile is null || Game.Store.Platform != (Hooks.IsWindower ? ThirdPartyHook.Windower : ThirdPartyHook.Ashita)) return;
        if (renamedFrom is not null && gameProfile == renamedFrom) Game.Retarget(GameStoreForSelection());
        else if (!Hooks.StillThere(gameProfile)) Game.UseStore(GameStoreForSelection());
    }

    private void AshitaRenamed()
    {
        Multi.Load();
        if (Game.Store.Platform == ThirdPartyHook.Ashita && Game.Store.ProfileName == Hooks.AshitaBootConfig?.Name)
            Game.Retarget(GameStoreForSelection());
    }
}
