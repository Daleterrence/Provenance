using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Provenance.Common.Feeds;
using Provenance.Common.Game;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Install;
using Provenance.Common.Security;
using Provenance.Common.Settings;
using Serilog;
using Provenance.Common.PolFormat;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Shared;

namespace Provenance.Wpf.ViewModels.Settings;

// Every change saves straight away. The install region's detected, not picked, but news can come from any region's site
public partial class SettingsViewModel : ObservableObject
{
    private static readonly string[] FeedRegionCodes = ["auto", "na", "eu", "jp"];

    private readonly ISettingsService _settings;
    private readonly FeedService? _feeds;
    private readonly Func<bool>? _multiRunning, _loggedIn;
    private readonly Func<string?>? _largeAddressAwareBlocked;
    private readonly Action? _holdPrestarts, _releasePrestarts, _stopPrestart;
    private readonly Func<bool, RegistryKey?>? _gameKey;
    private bool _loadingThirdPartyHook, _gameReady;

    public SettingsViewModel(ISettingsService settings, FeedService? feeds = null, Func<bool>? multiRunning = null,
                             Action? holdPrestarts = null, Action? releasePrestarts = null, Func<string?>? largeAddressAwareBlocked = null,
                             Func<bool, RegistryKey?>? gameKey = null, Action? stopPrestart = null, Func<bool>? loggedIn = null,
                             HandlesViewModel? handles = null)
    {
        _loggedIn = loggedIn;
        Handles = handles ?? new HandlesViewModel(() => null);
        _gameKey = gameKey;
        _stopPrestart = stopPrestart;
        _settings = settings;
        _multiRunning = multiRunning;
        _largeAddressAwareBlocked = largeAddressAwareBlocked;
        _holdPrestarts = holdPrestarts;
        _releasePrestarts = releasePrestarts;
        _feeds = feeds ?? AppFeedService();
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
        _backupGameSettings = settings.Current.BackupGameSettings;
        _backupFolder = settings.Current.BackupFolder;
        LoadThirdPartyHook();
        LoadMulti();
        Game = new GameSettingsViewModel(region, GameStoreForSelection());
        _gameReady = true;
        Addons = new AddonsPluginsViewModel(settings, this);
        Addons.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AddonsPluginsViewModel.SameListForEveryProfile)) OnPropertyChanged(nameof(ShowProfileSwitcher));
        };
    }

    public GameSettingsViewModel Game { get; }
    public AddonsPluginsViewModel Addons { get; }
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

    private void Save(Action<LauncherSettings> change)
    {
        change(_settings.Current);
        _settings.Save(_settings.Current);
    }

    [ObservableProperty] private bool _exitAfterGameExits;
    [ObservableProperty] private bool _keepSignedInInTray;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _notifyFriendsOnline;
    [ObservableProperty] private bool _groupChatNotice;
    [ObservableProperty] private bool _groupChatFlash;
    [ObservableProperty] private bool _groupChatTimes;
    [ObservableProperty] private int _feedRegionIndex;

    partial void OnExitAfterGameExitsChanged(bool value) => Save(s => s.ExitAfterGameExits = value);
    partial void OnKeepSignedInInTrayChanged(bool value) => Save(s => s.KeepSignedInInTray = value);
    partial void OnMinimizeToTrayChanged(bool value) => Save(s => s.MinimizeToTray = value);
    partial void OnNotifyFriendsOnlineChanged(bool value) => Save(s => s.NotifyFriendsOnline = value);
    partial void OnGroupChatNoticeChanged(bool value) => Save(s => s.GroupChatNotice = value);
    partial void OnGroupChatFlashChanged(bool value) => Save(s => s.GroupChatFlash = value);
    partial void OnGroupChatTimesChanged(bool value) => Save(s => s.GroupChatTimes = value);

    [ObservableProperty] private bool _chatLanguageFilter;
    [ObservableProperty] private int _autoDisconnectIndex;

    // either one set here pins the other at what's on screen, since both get written from then on
    partial void OnChatLanguageFilterChanged(bool value) => Save(s =>
    {
        s.ChatLanguageFilter = value;
        s.AutoDisconnectMinutes ??= PolOptions.AutoDisconnect(s);
    });

    partial void OnAutoDisconnectIndexChanged(int value)
    {
        if (value < 0 || value >= PolOptions.AutoDisconnectChoices.Length) return;
        Save(s =>
        {
            s.AutoDisconnectMinutes = PolOptions.AutoDisconnectChoices[value];
            s.ChatLanguageFilter ??= PolOptions.ChatFilter(s);
        });
    }

    [ObservableProperty] private int _themeIndex;

    partial void OnThemeIndexChanged(int value)
    {
        if (value < 0 || value >= AppTheme.Names.Length) return;
        Save(s => s.Theme = AppTheme.Names[value]);
        AppTheme.Apply(AppTheme.Names[value]);
    }

    partial void OnFeedRegionIndexChanged(int value)
    {
        if (value < 0 || value >= FeedRegionCodes.Length) return;
        Save(s => s.FeedRegion = FeedRegionCodes[value]);
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
        LargeAddressAwareTargetForTest ?? LargeAddressAwareSwitch.For(_settings.DetectedRegion, _largeAddressAwareBlocked);

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
        Save(s =>
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
        try { outcome = await Task.Run(() => LargeAddressAwareSwitch.Change(target, on, _holdPrestarts, _releasePrestarts)); }
        catch (Exception ex)
        {
            Log.Error(ex, "Large Address Aware change failed");
            outcome = LargeAddressAwareSwitch.Outcome.NextStart;
        }
        // an on gets applied at every start anyway, an off only once (a refused one too, Windows may allow it later)
        if (outcome != LargeAddressAwareSwitch.Outcome.Done && !on) Save(s => s.LargeAddressAwareClearAtStart = true);
        var after = await ReadLargeAddressAware();
        LargeAddressAwareBusy = false;
        LargeAddressAwareAvailable = after.Available;
        LargeAddressAwareStatus = after.Available && outcome == LargeAddressAwareSwitch.Outcome.Denied
            ? LargeAddressAwareSwitch.DeniedText : after.Note ?? "";
    }

    private static FeedService? AppFeedService()
    {
        try { return (System.Windows.Application.Current as App)?.Services.GetService(typeof(FeedService)) as FeedService; }
        catch (InvalidOperationException) { return null; }   // host not started (design time)
    }

    // Multi-account page. The first profile choice is none, which an account that's turned on can't start with
    public string ProfileDefaultChoice => IsAshita ? "Select Ashita boot configuration..." : "Select Windower profile...";
    public ObservableCollection<MultiAccountRow> MultiAccountRows { get; } = new();
    public ObservableCollection<string> MultiProfileChoices { get; } = new();
    public bool MultiHasThirdPartyHook => Hook != ThirdPartyHook.None;
    public string MultiProfileLabel => IsAshita ? "Ashita boot configuration" : "Windower profile";

    // read-only while a run goes, it saves refused and blocked accounts as they happen and a save from here
    // mustn't undo that
    public bool MultiRunGoing => _multiRunning?.Invoke() ?? false;
    public bool MultiEditable => MultiHasThirdPartyHook && !MultiRunGoing;

    [ObservableProperty] private bool _multiContinueOnError;
    [ObservableProperty] private bool _multiCloseWhenFinished;

    partial void OnMultiContinueOnErrorChanged(bool value) => SaveUnlessLoading(s => s.MultiContinueOnError = value);
    partial void OnMultiCloseWhenFinishedChanged(bool value) =>
        SaveUnlessLoading(s => s.MultiCloseWhenFinished = value);
    private void SaveUnlessLoading(Action<LauncherSettings> change) { if (!_loadingMulti) Save(change); }

    public void MultiRunChanged()
    {
        LoadMulti();
        OnPropertyChanged(nameof(MultiRunGoing));
        OnPropertyChanged(nameof(MultiEditable));
        OnProfilesChanged();
    }

    private bool _loadingMulti;

    // each account's stored Enabled as last loaded or saved. One that's gone off since got turned off by a run
    private Dictionary<string, bool> _multiEnabledAsKnown = new();

    private void RememberMulti(IEnumerable<MultiAccountEntry> stored) =>
        _multiEnabledAsKnown = stored.GroupBy(e => e.ProfileId).ToDictionary(g => g.Key, g => g.First().Enabled);

    private void LoadMulti()
    {
        _loadingMulti = true;
        MultiAccountRows.Clear();
        var s = _settings.Current;
        RememberMulti(s.MultiAccounts);
        MultiContinueOnError = s.MultiContinueOnError;
        MultiCloseWhenFinished = s.MultiCloseWhenFinished;
        var byId = s.Profiles.ToDictionary(p => p.Id);
        var ids = s.MultiAccounts.Select(e => e.ProfileId).Where(byId.ContainsKey).Distinct()
                   .Concat(s.Profiles.Select(p => p.Id)).Distinct().ToList();
        foreach (var id in ids)
        {
            var entry = s.MultiAccounts.FirstOrDefault(e => e.ProfileId == id);
            var p = byId[id];
            var row = new MultiAccountRow
            {
                ProfileId = id,
                Name = string.IsNullOrWhiteSpace(p.HandleName) ? p.DisplayName : p.HandleName!,
                Enabled = entry?.Enabled ?? false,
                Problem = entry?.Problem,
                CredentialsRefused = entry?.CredentialsRefused ?? false,
                ThirdPartyHookProfile = ToChoice(entry?.ThirdPartyHookProfile),
            };
            row.Changed = () => { SaveMulti(); NoteUnpicked(); };
            MultiAccountRows.Add(row);
        }
        _loadingMulti = false;
        RefreshMultiChoices();
    }

    private ObservableCollection<HookProfileItem>? ChoiceItems => Hook switch { ThirdPartyHook.Ashita => AshitaBootConfigs, ThirdPartyHook.Windower => WindowerProfiles, _ => null };

    private string ToChoice(string? stored) =>
        stored is null ? ProfileDefaultChoice
        : ChoiceItems is { } items ? Item(items, stored)?.Display ?? stored
        : stored;

    private string? FromChoice(string choice) =>
        choice == ProfileDefaultChoice ? null
        : ChoiceItems?.FirstOrDefault(i => i.Display == choice)?.Name ?? choice;

    // a Windower profile means nothing to Ashita and vice versa, so a platform switch clears every stored one
    private void RefreshMultiChoices(bool platformChanged = false)
    {
        _loadingMulti = true;
        var keep = MultiAccountRows.Select(r => r.ThirdPartyHookProfile).ToList();
        MultiProfileChoices.Clear();
        MultiProfileChoices.Add(ProfileDefaultChoice);
        foreach (var c in ChoiceItems ?? Enumerable.Empty<HookProfileItem>())
            MultiProfileChoices.Add(c.Display);
        for (int i = 0; i < MultiAccountRows.Count; i++)
            MultiAccountRows[i].ThirdPartyHookProfile = !platformChanged && MultiProfileChoices.Contains(keep[i]) ? keep[i] : ProfileDefaultChoice;
        _loadingMulti = false;
        if (platformChanged) ClearMultiProfiles();
        OnPropertyChanged(nameof(ProfileDefaultChoice));
        OnPropertyChanged(nameof(MultiHasThirdPartyHook));
        OnPropertyChanged(nameof(MultiEditable));
        OnPropertyChanged(nameof(MultiProfileLabel));
        NoteUnpicked();
    }

    private void NoteUnpicked()
    {
        foreach (var r in MultiAccountRows)
            r.ProfileNeeded = MultiHasThirdPartyHook && r.Enabled && r.ThirdPartyHookProfile == ProfileDefaultChoice
                ? $"Choose the {MultiProfileLabel} {r.Name} starts with." : null;
    }

    // safe mid-run too: the run read its profiles at the start, and only the profile changes
    private void ClearMultiProfiles()
    {
        var withProfile = _settings.Current.MultiAccounts.Where(e => e.ThirdPartyHookProfile is not null).ToList();
        if (withProfile.Count == 0) return;
        foreach (var e in withProfile) e.ThirdPartyHookProfile = null;
        _settings.Save(_settings.Current);
    }

    private void SaveMulti()
    {
        if (_loadingMulti || MultiRunGoing) return;
        // read the stored list again, a run may have saved a failure since the rows were loaded
        var stored = _settings.Current.MultiAccounts;
        var saved = new List<MultiAccountEntry>();
        _loadingMulti = true;   // the rows brought in line below mustn't save again
        try
        {
            foreach (var r in MultiAccountRows)
            {
                var entry = new MultiAccountEntry { ProfileId = r.ProfileId, Enabled = r.Enabled, ThirdPartyHookProfile = FromChoice(r.ThirdPartyHookProfile), Problem = r.Problem };
                // a refused password, or one a run turned off since the load: the stored state wins, so no edit
                // here turns it back on by mistake
                if (stored.FirstOrDefault(e => e.ProfileId == r.ProfileId) is { } now &&
                    (now.CredentialsRefused || (!now.Enabled && _multiEnabledAsKnown.GetValueOrDefault(r.ProfileId))))
                {
                    entry.Enabled = false;
                    entry.Problem = now.Problem;
                    entry.CredentialsRefused = now.CredentialsRefused;
                    r.Enabled = false;
                    r.Problem = now.Problem;
                    r.CredentialsRefused = now.CredentialsRefused;
                }
                saved.Add(entry);
            }
        }
        finally { _loadingMulti = false; }
        _settings.Current.MultiAccounts = saved;
        _settings.Save(_settings.Current);
        RememberMulti(saved);
    }

    [RelayCommand] private void MoveMultiUp(MultiAccountRow? row) => MoveMulti(row, -1);
    [RelayCommand] private void MoveMultiDown(MultiAccountRow? row) => MoveMulti(row, +1);

    private void MoveMulti(MultiAccountRow? row, int by)
    {
        if (row is null) return;
        int i = MultiAccountRows.IndexOf(row), j = i + by;
        if (i < 0 || j < 0 || j >= MultiAccountRows.Count) return;
        MultiAccountRows.Move(i, j);
        SaveMulti();
    }
}
