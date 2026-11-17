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

// Every change saves straight away. The install region's detected, not picked, but news can come from any region's site
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
        string Folder(GameProduct product) => (region is { } r ? PolPaths.TryGetInstallFolder(r, product) : null) ?? "Not found";
        ViewerFolder = Folder(GameProduct.Viewer);
        PolOptionsStatus = region is { } r && PolPaths.TryGetInstallFolder(r, GameProduct.Viewer) is { } viewer && FolderPins.Linked(viewer)
            ? PolOptions.LinkedText : "";
        FfxiFolder = Folder(GameProduct.Game);
        _exitAfterGameExits = settings.Current.ExitAfterGameExits;
        _keepSignedInInTray = settings.Current.KeepSignedInInTray;
        _minimizeToTray = settings.Current.MinimizeToTray;
        _notifyFriendsOnline = settings.Current.NotifyFriendsOnline;
        _groupChatNotice = settings.Current.GroupChatNotice;
        _groupChatFlash = settings.Current.GroupChatFlash;
        _groupChatTimes = settings.Current.GroupChatTimes;
        _themeIndex = Array.IndexOf(AppTheme.Names, AppTheme.Normalise(settings.Current.Theme));
        _largeAddressAwareOn = settings.Current.LargeAddressAware;
        _chatLanguageFilter = PolOptions.ChatFilter(settings.Current);
        _autoDisconnectIndex = Math.Max(0, Array.IndexOf(PolOptions.AutoDisconnectChoices, PolOptions.AutoDisconnect(settings.Current)));
        Backups = new BackupSettings(dialogs, settings, _context);
        Hooks.Load();
        Multi.Load();
        Game = new GameSettingsViewModel(dialogs, region, GameStoreForSelection(), _context.GameRunning);
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
    public GameSettingsViewModel Game { get; }
    public AddonsPluginsViewModel Addons { get; }
    public BackupSettings Backups { get; }
    public HandlesViewModel Handles { get; }
    public string RegionText { get; }
    public string ViewerFolder { get; }
    public string PolOptionsStatus { get; }
    public string FfxiFolder { get; }

    // the SemVer one (1.0.0-beta.1), not the numeric assembly version, minus the "+<commit>" the build adds
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
    [ObservableProperty] private bool _notifyFriendsOnline;
    [ObservableProperty] private bool _groupChatNotice;
    [ObservableProperty] private bool _groupChatFlash;
    [ObservableProperty] private bool _groupChatTimes;
    [ObservableProperty] private int _feedRegionIndex;

    partial void OnExitAfterGameExitsChanged(bool value) => _settings.SaveChange(s => s.ExitAfterGameExits = value);
    partial void OnKeepSignedInInTrayChanged(bool value) => _settings.SaveChange(s => s.KeepSignedInInTray = value);
    partial void OnMinimizeToTrayChanged(bool value) => _settings.SaveChange(s => s.MinimizeToTray = value);
    partial void OnNotifyFriendsOnlineChanged(bool value) => _settings.SaveChange(s => s.NotifyFriendsOnline = value);
    partial void OnGroupChatNoticeChanged(bool value) => _settings.SaveChange(s => s.GroupChatNotice = value);
    partial void OnGroupChatFlashChanged(bool value) => _settings.SaveChange(s => s.GroupChatFlash = value);
    partial void OnGroupChatTimesChanged(bool value) => _settings.SaveChange(s => s.GroupChatTimes = value);

    [ObservableProperty] private bool _chatLanguageFilter;
    [ObservableProperty] private int _autoDisconnectIndex;

    // either one set here pins the other at what's on screen, since both get written from then on
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
    }

    partial void OnFeedRegionIndexChanged(int value)
    {
        if (value < 0 || value >= FeedRegionCodes.Length) return;
        _settings.SaveChange(s => s.FeedRegion = FeedRegionCodes[value]);
        _feeds?.NotifyRegionSettingChanged();
    }

    // the saved setting. The line under it says when pol.exe hasn't caught up with it yet
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

    // hashes pol.exe and polcore.dll, so it's off the UI thread. A newer read or a change drops an older one
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
        // an on gets applied at every start anyway, an off only once (a refused one too, Windows may allow it later)
        if (outcome != LargeAddressAwareSwitch.Outcome.Done && !on) _settings.SaveChange(s => s.LargeAddressAwareClearAtStart = true);
        var after = await ReadLargeAddressAware();
        LargeAddressAwareBusy = false;
        LargeAddressAwareAvailable = after.Available;
        LargeAddressAwareStatus = after.Available && outcome == LargeAddressAwareSwitch.Outcome.Denied
            ? LargeAddressAwareSwitch.DeniedText : after.Note ?? "";
    }

    // the phases inside one login or play all read the same, so the files only get read again when the answer moves
    private void OnPhaseChanged()
    {
        var blocked = _session.LargeAddressAwareBlocked();
        if (blocked == _blockedWas) return;
        _blockedWas = blocked;
        _ = ReadLargeAddressAwareAsync();
    }

    private string? _blockedWas;

    public void Dispose()
    {
        _session.PhaseChanged -= OnPhaseChanged;
        _session.RunChanged -= MultiRunChanged;
    }

    public void MultiRunChanged()
    {
        Multi.RunChanged();
        Hooks.NotifyProfilesChanged();
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowProfileSwitcher), nameof(ShowProfileTools))] private SettingsTab _shownTab;

    public void ShowTab(SettingsTab tab)
    {
        ShownTab = tab;
        // a profile picked on another tab since the Game page was last shown
        if (tab == SettingsTab.Game && !ShowingGame()) PickGameProfile();
        if (tab == SettingsTab.General) _ = ReadLargeAddressAwareAsync();
        if (tab == SettingsTab.WindowerAshita) Hooks.ReloadCards();
    }

    // Windower can change pol.exe while the window's open, so it's read again on every show, switch back and activate
    public void WindowActivated()
    {
        if (ShownTab == SettingsTab.General) _ = ReadLargeAddressAwareAsync();
    }

    // Windower/Ashita, Game and Addons and plugins are the tabs that change a profile, though there's nothing to pick
    // on Addons and plugins while every profile gets the same list
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
            return !Game.HasUnsavedChanges;   // the save failed, its error's on the page
        }
        return true;
    }

    // with no list to pick from (settings.xml unreadable, say) it's still the chosen profile's, so its problem shows
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

    // changes made on the Game page for the profile before wait there, ShowingGame asks about them
    private void SyncGame()
    {
        if (!_gameReady) return;
        var want = GameStoreForSelection();
        // one that couldn't load gets another go, settings.xml may have been sorted since
        if (want.Key == Game.Store.Key && Game.Available || Game.HasUnsavedChanges) return;
        Game.UseStore(want);
    }

    // on the Game tab the page moves with the pick, so changes it hasn't saved get asked about first. False when the
    // player backs out, and the pick goes back
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
                if (Game.HasUnsavedChanges) return false;   // the save failed, its error's on the page
            }
        }
        Game.UseStore(want);
        return true;
    }

    // backing out of that on the way into the Game tab leaves the page on its own profile, so the pick follows it there
    public void PickGameProfile()
    {
        if (Game.Store.ProfileName is not { } name) return;
        if (Hooks.IsWindower && Game.Store.Platform == ThirdPartyHook.Windower)
            Hooks.WindowerProfile = HookSettings.Item(Hooks.WindowerProfiles, name) ?? Hooks.WindowerProfile;
        else if (Hooks.IsAshita && Game.Store.Platform == ThirdPartyHook.Ashita)
            Hooks.AshitaBootConfig = HookSettings.Item(Hooks.AshitaBootConfigs, name) ?? Hooks.AshitaBootConfig;
    }

    // the Game page keeps a renamed profile's unsaved changes, a deleted one's go with it
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
