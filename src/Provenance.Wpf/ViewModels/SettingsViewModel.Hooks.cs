using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Provenance.Common.Game.Ashita;
using Provenance.Common.Game.Windower;
using Provenance.Common.PolFormat;
using Provenance.Wpf.Services;
using Serilog;
using BootConfig = Provenance.Common.Game.Ashita.AshitaBootConfig;

namespace Provenance.Wpf.ViewModels;

public sealed record HookProfileItem(string Name, string Display)
{
    public override string ToString() => Display;
}

public sealed record ConsoleKeyChoice(WindowerKey Key, string Display)
{
    public override string ToString() => Display;
}

// The Windower/Ashita page: the platform, its folder, the profiles and each profile's own settings. Like General,
// every change saves straight away
public partial class SettingsViewModel
{
    public string[] ThirdPartyHookChoices { get; } = { "None", "Ashita v4", "Windower 4" };

    [ObservableProperty] private int _thirdPartyHookIndex;
    [ObservableProperty] private string _ashitaPath = string.Empty;
    [ObservableProperty] private string _windowerPath = string.Empty;
    [ObservableProperty] private HookProfileItem? _ashitaBootConfig;
    [ObservableProperty] private HookProfileItem? _windowerProfile;
    [ObservableProperty] private string _thirdPartyHookStatus = string.Empty;
    [ObservableProperty] private bool _thirdPartyHookStatusIsError;
    [ObservableProperty] private string _profileStatus = string.Empty;
    [ObservableProperty] private bool _profileStatusIsError;
    public ObservableCollection<HookProfileItem> AshitaBootConfigs { get; } = new();
    public ObservableCollection<HookProfileItem> WindowerProfiles { get; } = new();
    public bool IsAshita => ThirdPartyHookIndex == 1;
    public bool IsWindower => ThirdPartyHookIndex == 2;

    // the window fills these in, a test answers them itself
    internal Func<string, string, string, Func<string, string?>, string?>? AskName { get; set; }
    internal Func<string, string, string, bool, bool>? Confirm { get; set; }
    internal Func<string?, bool?>? AskSaveGame { get; set; }
    internal Func<string, bool>? AskRestart { get; set; }
    internal Action? Restart { get; set; }
    // a pick that's turned down goes back once the dropdown's finished with it
    internal Action<Action> Later { get; set; } = a => a();

    // the Settings tab that's showing, which the window keeps up to date
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowProfileSwitcher), nameof(ShowProfileTools))] private int _shownTab;

    // Windower/Ashita, Game and Addons and plugins are the tabs that change a profile, though there's nothing to pick
    // on Addons and plugins while every profile gets the same list
    public bool ShowProfileSwitcher => ShownTab is 1 or 2 or 3 && (IsWindower || IsAshita) && !(ShownTab == 3 && Addons is { SameListForEveryProfile: true });
    public bool ShowProfileTools => ShownTab == 1;
    public string ProfileSwitcherLabel => IsAshita ? "Editing Ashita boot configuration:" : "Editing Windower profile:";
    internal Func<string, string, WindowerUpdater> NewWindowerUpdater { get; set; } = WindowerUpdates.NewUpdater;
    internal Task WindowerWork { get; private set; } = Task.CompletedTask;

    // only the folder in the box, never one found somewhere else, since profiles get changed in it
    internal string? WindowerDir => ThirdPartyHookService.IsWindowerFolder(WindowerPath) ? WindowerPath : null;
    // settings.xml's writer refuses one too, so nothing here offers to change it
    private bool WindowerLinked => WindowerDir is { } d && ThirdPartyHookService.IsLinkedFolder(d);
    internal string? AshitaDir => ThirdPartyHookService.IsAshitaFolder(AshitaPath) ? AshitaPath : null;

    private void LoadThirdPartyHook()
    {
        _loadingThirdPartyHook = true;
        var s = _settings.Current;
        if (HookProfiles.MapOldCopies(s)) _settings.Save(s);
        ThirdPartyHookIndex = s.ThirdPartyHookPlatform switch { ThirdPartyHookService.Ashita => 1, ThirdPartyHookService.Windower => 2, _ => 0 };
        AshitaPath = ThirdPartyHookService.DetectAshita(s.AshitaPath) ?? s.AshitaPath;
        WindowerPath = ThirdPartyHookService.DetectWindower(s.WindowerPath) ?? s.WindowerPath;
        _loadingThirdPartyHook = false;
        // the boot config Ashita really ran with last (from its logs), so the game keeps its window and plugins
        RefreshThirdPartyHookLists(s.WindowerProfile,
            AshitaBootFiles.List(AshitaDir).Contains(s.AshitaBootConfig) ? s.AshitaBootConfig : ThirdPartyHookService.LastUsedAshitaBootConfig(AshitaDir));
        UpdateThirdPartyHookStatus();
        LoadCards();
    }

    private static HookProfileItem? Item(IEnumerable<HookProfileItem> items, string? name) =>
        name is null ? null : items.FirstOrDefault(i => i.Name == name) ?? items.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    // a profile really called Default keeps the name, and the unnamed one says it has none
    internal static string WindowerDisplay(string name, IReadOnlyCollection<string> all) =>
        name.Length > 0 ? name : all.Any(n => n.Equals("Default", StringComparison.OrdinalIgnoreCase)) ? "Default (no name)" : "Default";

    // the lists change under a ComboBox, which drops its pick on the way, so the pick's put back by name
    private void RefreshThirdPartyHookLists(string? windowerKeep = null, string? ashitaKeep = null)
    {
        windowerKeep ??= WindowerProfile?.Name ?? _settings.Current.WindowerProfile;
        ashitaKeep ??= AshitaBootConfig?.Name ?? _settings.Current.AshitaBootConfig;
        bool was = _loadingThirdPartyHook;
        _loadingThirdPartyHook = true;
        try
        {
            AshitaBootConfigs.Clear();
            foreach (var (file, shown) in AshitaBootFiles.Names(AshitaDir)) AshitaBootConfigs.Add(new HookProfileItem(file, shown));
            WindowerProfiles.Clear();
            var names = ThirdPartyHookService.WindowerProfiles(WindowerDir);
            foreach (var n in names) WindowerProfiles.Add(new HookProfileItem(n, WindowerDisplay(n, names)));
            AshitaBootConfig = Item(AshitaBootConfigs, ashitaKeep)
                               ?? AshitaBootConfigs.FirstOrDefault(c => c.Name.Equals("default.ini", StringComparison.OrdinalIgnoreCase))
                               ?? AshitaBootConfigs.FirstOrDefault();
            WindowerProfile = Item(WindowerProfiles, windowerKeep) ?? WindowerProfiles.FirstOrDefault();
        }
        finally { _loadingThirdPartyHook = was; }
        RefreshMultiChoices();
        OnProfilesChanged();
    }

    private void UpdateThirdPartyHookStatus()
    {
        (ThirdPartyHookStatus, ThirdPartyHookStatusIsError) = ThirdPartyHookIndex switch
        {
            1 when AshitaDir is null => ("Couldn't find Ashita v4 in that folder.", true),
            1 => ("The game starts through Ashita with the boot configuration picked at the top.", false),
            2 when WindowerDir is null => ("Couldn't find Windower 4 in that folder.", true),
            2 when ThirdPartyHookService.IsLinkedFolder(WindowerDir) => (ThirdPartyHookService.LinkedWindowerText, true),
            2 => ("The game starts through Windower with the profile picked at the top.", false),
            _ => ("The game starts directly, without Windower or Ashita.", false),
        };
        OnPropertyChanged(nameof(IsAshita));
        OnPropertyChanged(nameof(IsWindower));
        OnPropertyChanged(nameof(ShowProfileSwitcher));
        OnPropertyChanged(nameof(ProfileSwitcherLabel));
        OnProfilesChanged();
    }

    private void SaveThirdPartyHook()
    {
        if (_loadingThirdPartyHook) return;
        var s = _settings.Current;
        s.ThirdPartyHookPlatform = ThirdPartyHookIndex switch { 1 => ThirdPartyHookService.Ashita, 2 => ThirdPartyHookService.Windower, _ => ThirdPartyHookService.None };
        s.AshitaPath = AshitaPath;
        s.WindowerPath = WindowerPath;
        // nothing shown (no folder yet) keeps what was picked before
        if (AshitaBootConfig is { } a) s.AshitaBootConfig = a.Name;
        if (WindowerProfile is { } w) s.WindowerProfile = w.Name;
        _settings.Save(s);
        UpdateThirdPartyHookStatus();
        SyncGame();
    }

    // set once the player's agreed to a restart for a platform change, the window closes and the app restarts after
    public bool RestartWanted { get; private set; }

    public static string RestartText(int from, int to) =>
        (from is 1 or 2 ? $"You're already logged in, so {PlatformName(from)} is still loaded." : "You're already logged in without Windower or Ashita.")
        + (to is 1 or 2 ? $" Provenance needs to restart to switch to {PlatformName(to)}." : $" Provenance needs to restart to stop using {PlatformName(from)}.");

    private static string PlatformName(int index) => index switch { 1 => "Ashita", 2 => "Windower", _ => "no Windower or Ashita" };

    // after the first load, a switch between Windower and Ashita clears each account's stored profile. Logged in, the
    // old one's already loaded, so it only switches with a restart and a no puts it back unsaved
    partial void OnThirdPartyHookIndexChanged(int oldValue, int newValue)
    {
        bool restart = !_loadingThirdPartyHook && _loggedIn?.Invoke() == true;
        if (restart && AskRestart?.Invoke(RestartText(oldValue, newValue)) != true)
        {
            Later(() => PutPlatformBack(oldValue));
            return;
        }
        SaveThirdPartyHook();
        RefreshMultiChoices(platformChanged: !_loadingThirdPartyHook);
        if (_loadingThirdPartyHook) return;
        // a pol.exe pre-started for a plain login would hold off Windower's updates, and it's no use to either of them
        if (newValue is 1 or 2) _stopPrestart?.Invoke();
        LoadCards();
        if (!restart) return;
        RestartWanted = true;
        Restart?.Invoke();
    }

    private void PutPlatformBack(int index)
    {
        _loadingThirdPartyHook = true;
        try { ThirdPartyHookIndex = index; }
        finally { _loadingThirdPartyHook = false; }
    }

    partial void OnAshitaBootConfigChanged(HookProfileItem? oldValue, HookProfileItem? newValue)
    {
        if (_loadingThirdPartyHook) return;
        if (!GameFollows())
        {
            Later(() => AshitaBootConfig = oldValue);
            return;
        }
        SaveThirdPartyHook();
        LoadAshitaCard();
    }

    partial void OnWindowerProfileChanged(HookProfileItem? oldValue, HookProfileItem? newValue)
    {
        if (_loadingThirdPartyHook) return;
        if (!GameFollows())
        {
            Later(() => WindowerProfile = oldValue);
            return;
        }
        SaveThirdPartyHook();
        LoadWindowerCard();
    }

    // on the Game tab the page moves with the pick, so changes it hasn't saved get asked about first. False when the
    // player backs out, and the pick goes back
    private bool GameFollows() => ShownTab != 2 || ShowingGame(AskSaveGame);

    // backing out of that on the way into the Game tab leaves the page on its own profile, so the pick follows it there
    public void PickGameProfile()
    {
        if (Game.Store.ProfileName is not { } name) return;
        if (IsWindower && Game.Store.Platform == ThirdPartyHookService.Windower) WindowerProfile = Item(WindowerProfiles, name) ?? WindowerProfile;
        else if (IsAshita && Game.Store.Platform == ThirdPartyHookService.Ashita) AshitaBootConfig = Item(AshitaBootConfigs, name) ?? AshitaBootConfig;
    }

    partial void OnAshitaPathChanged(string value)
    {
        if (_loadingThirdPartyHook) return;
        RefreshThirdPartyHookLists();
        SaveThirdPartyHook();
        LoadAshitaCard();
    }

    partial void OnWindowerPathChanged(string value)
    {
        if (_loadingThirdPartyHook) return;
        RefreshThirdPartyHookLists();
        SaveThirdPartyHook();
        LoadWindowerCard();
    }

    [RelayCommand]
    private void BrowseAshita() { if (PickFolder("Choose your Ashita v4 folder") is { } d) AshitaPath = d; }

    [RelayCommand]
    private void BrowseWindower() { if (PickFolder("Choose your Windower 4 folder") is { } d) WindowerPath = d; }

    internal static string? PickFolder(string title)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    private void Say(string text, bool error = false)
    {
        ProfileStatus = text;
        ProfileStatusIsError = error;
    }

    // with no list to pick from (settings.xml unreadable, say) it's still the chosen profile's, so its problem shows
    private IGameSettingsStore GameStoreForSelection()
    {
        var registry = new RegistryGameStore(_settings.DetectedRegion, _gameKey);
        var s = _settings.Current;
        if (IsWindower && WindowerDir is { } w)
            return WindowerProfile is { } p ? new WindowerGameStore(w, p.Name, registry, p.Display)
                 : new WindowerGameStore(w, WindowerSettingsFile.OldCopySource(s.WindowerProfile), registry);
        if (IsAshita && AshitaDir is { } a && (AshitaBootConfig?.Name ?? s.AshitaBootConfig) is { Length: > 0 } boot)
            return new AshitaGameStore(a, boot, registry, AshitaBootConfig?.Display);
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

    public bool ShowingGame(Func<string?, bool?>? saveFirst)
    {
        var want = GameStoreForSelection();
        if (want.Key == Game.Store.Key && Game.Available) return true;
        if (Game.HasUnsavedChanges)
        {
            var answer = saveFirst?.Invoke(Game.HasProfile ? $"the {Game.ProfileKind} {Game.ProfileShown}" : null);
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

    public bool CanManageProfiles => !MultiRunGoing && (IsWindower ? WindowerDir is not null && !WindowerLinked && WindowerSettingsProblem is null : IsAshita && AshitaDir is not null);
    public bool CanCopyProfile => CanManageProfiles && (IsWindower ? WindowerProfile is not null : AshitaBootConfig is not null);
    // the unnamed Windower profile and Ashita's own examples stay as they are
    public bool CanChangeProfile => CanManageProfiles && (IsWindower
        ? WindowerProfile is { Name.Length: > 0 }
        : AshitaBootConfig is { } a && !AshitaBootFiles.IsShippedExample(a.Name));

    private void OnProfilesChanged()
    {
        OnPropertyChanged(nameof(CanManageProfiles));
        OnPropertyChanged(nameof(CanCopyProfile));
        OnPropertyChanged(nameof(CanChangeProfile));
        OnPropertyChanged(nameof(WindowerCardEnabled));
        OnPropertyChanged(nameof(WindowerBranchEnabled));
        OnPropertyChanged(nameof(CanStartNewWindowerSettings));
    }

    private int PlayOnlineLanguage => _settings.DetectedRegion switch { PolRegion.Japan => 1, PolRegion.Europe => 3, _ => 2 };

    private WindowerSettingsFile? ReadWindower(string dir)
    {
        try { return WindowerSettingsFile.Load(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not read Windower's settings.xml");
            Say("Provenance couldn't read Windower's settings.xml. Try again in a moment.", error: true);
            return null;
        }
    }

    [RelayCommand]
    private void AddProfile()
    {
        if (!CanManageProfiles) return;
        if (IsWindower && WindowerDir is { } w && ReadWindower(w) is { } file)
        {
            var name = AskName?.Invoke("Add a Windower profile", "What should the new profile be called?", file.NewProfileName(), n => file.NameProblem(n));
            if (name is not null) ChangeProfiles(() => HookProfiles.AddWindower(_settings, w, name), "Provenance couldn't add the profile.");
        }
        else if (IsAshita && AshitaDir is { } a)
        {
            var name = AskName?.Invoke("Add a boot configuration", "What should the new boot configuration be called?", NewAshitaName(a), n => HookProfiles.AshitaNameProblem(a, n));
            if (name is not null) ChangeProfiles(() => HookProfiles.AddAshita(a, name, PlayOnlineLanguage), "Provenance couldn't add the boot configuration.");
        }
    }

    private static string NewAshitaName(string dir)
    {
        for (int i = 1; ; i++)
            if (HookProfiles.AshitaNameProblem(dir, $"profile{i}.ini") is null) return $"profile{i}.ini";
    }

    [RelayCommand]
    private void DuplicateProfile()
    {
        if (!CanCopyProfile) return;
        if (IsWindower && WindowerDir is { } w && WindowerProfile is { } p && ReadWindower(w) is { } file)
        {
            var start = file.NameProblem($"{p.Display} copy") is null ? $"{p.Display} copy" : file.NewProfileName();
            var name = AskName?.Invoke("Duplicate a Windower profile", $"What should the copy of {p.Display} be called?", start, n => file.NameProblem(n));
            if (name is not null) ChangeProfiles(() => HookProfiles.DuplicateWindower(_settings, w, p.Name, name), "Provenance couldn't copy the profile.");
        }
        else if (IsAshita && AshitaDir is { } a && AshitaBootConfig is { } b)
        {
            var stem = Path.GetFileNameWithoutExtension(b.Name);
            var start = HookProfiles.AshitaNameProblem(a, $"{stem}-copy.ini") is null ? $"{stem}-copy.ini" : NewAshitaName(a);
            var name = AskName?.Invoke("Duplicate a boot configuration", $"What should the copy of {b.Display} be called?", start, n => HookProfiles.AshitaNameProblem(a, n));
            if (name is not null) ChangeProfiles(() => HookProfiles.DuplicateAshita(a, b.Name, name), "Provenance couldn't copy the boot configuration.");
        }
    }

    [RelayCommand]
    private void RenameProfile()
    {
        if (!CanChangeProfile) return;
        if (IsWindower && WindowerDir is { } w && WindowerProfile is { } p && ReadWindower(w) is { } file)
        {
            var same = file.Profiles.FirstOrDefault(x => x.Name == p.Name);
            var name = AskName?.Invoke("Rename a Windower profile", $"What should {p.Display} be called?", p.Name, n => file.NameProblem(n, same));
            if (name is null || name == p.Name) return;
            ChangeProfiles(() => HookProfiles.RenameWindower(_settings, w, p.Name, name), "Provenance couldn't rename the profile.", renamedFrom: p.Name);
        }
        else if (IsAshita && AshitaDir is { } a && AshitaBootConfig is { } b)
        {
            // it's the file that's renamed, a name of its own stays put
            var ask = b.Display == b.Name ? $"What should {b.Name} be called?" : $"What should the file for {b.Display} be called?";
            var name = AskName?.Invoke("Rename a boot configuration", ask, b.Name, n => HookProfiles.AshitaNameProblem(a, n, renaming: b.Name));
            if (name is null || HookProfiles.AshitaFileName(name) == b.Name) return;
            ChangeProfiles(() => HookProfiles.RenameAshita(_settings, a, b.Name, name), "Provenance couldn't rename the boot configuration.", renamedFrom: b.Name);
        }
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (!CanChangeProfile) return;
        if (IsWindower && WindowerDir is { } w && WindowerProfile is { } p)
        {
            if (Confirm?.Invoke("Delete profile", $"Delete the Windower profile {p.Display}? Its settings are removed from Windower's settings.xml, and this can't be undone.", "DELETE", true) != true) return;
            ChangeProfiles(() => { HookProfiles.DeleteWindower(_settings, w, p.Name); return ""; }, "Provenance couldn't delete the profile.");
        }
        else if (IsAshita && AshitaDir is { } a && AshitaBootConfig is { } b)
        {
            var file = b.Display == b.Name ? "The file is" : $"Its file, {b.Name}, is";
            if (Confirm?.Invoke("Delete boot configuration", $"Delete the boot configuration {b.Display}? {file} removed from Ashita's config\\boot folder, and this can't be undone.", "DELETE", true) != true) return;
            ChangeProfiles(() => { HookProfiles.DeleteAshita(_settings, a, b.Name); return null; }, "Provenance couldn't delete the boot configuration.");
        }
    }

    private void ChangeProfiles(Func<string?> change, string failed, string? renamedFrom = null)
    {
        string? pick = null;
        bool ok = false;
        try
        {
            pick = change();
            ok = true;
            Say("");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Profile change failed");
            Say(ErrorText.Of(ex, failed), error: true);
        }
        var gamePlatform = Game.Store.Platform;
        var gameProfile = Game.Store.ProfileName;
        if (IsWindower) RefreshThirdPartyHookLists(windowerKeep: ok ? pick : null);
        else RefreshThirdPartyHookLists(ashitaKeep: ok ? pick : null);
        // the Game page keeps a renamed profile's unsaved changes, a deleted one's go with it
        if (ok && gameProfile is not null && gamePlatform == (IsWindower ? ThirdPartyHookService.Windower : ThirdPartyHookService.Ashita))
        {
            if (renamedFrom is not null && gameProfile == renamedFrom) Game.Retarget(GameStoreForSelection());
            else if (!StillThere(gameProfile)) Game.UseStore(GameStoreForSelection());
        }
        SaveThirdPartyHook();
        LoadMulti();
        LoadCards();
    }

    private bool StillThere(string name) => IsWindower ? Item(WindowerProfiles, name) is not null : Item(AshitaBootConfigs, name) is not null;

    public IReadOnlyList<ConsoleKeyChoice> ConsoleKeys { get; } = WindowerKeyNames.Pickable();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ConsoleKeyText))] private ConsoleKeyChoice? _windowerConsoleKey;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ConsoleKeyText))] private bool _waitingForConsoleKey;
    [ObservableProperty] private bool _windowerAllowWinKey;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(WindowerBranchName))] private bool _windowerDev;
    [ObservableProperty] private string _windowerVersionText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(WindowerBranchEnabled))] private bool _switchingBranch;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanManageProfiles), nameof(CanCopyProfile), nameof(CanChangeProfile), nameof(WindowerCardEnabled), nameof(WindowerBranchEnabled))]
    private string? _windowerSettingsProblem;
    private bool _loadingCard;

    public bool WindowerCardEnabled => WindowerDir is not null && !WindowerLinked && WindowerSettingsProblem is null && WindowerProfile is not null;
    public bool WindowerBranchEnabled => WindowerDir is not null && !WindowerLinked && WindowerSettingsProblem is null && !SwitchingBranch;
    public string WindowerBranchName => BranchName(Branch);

    private void LoadCards()
    {
        LoadWindowerCard();
        LoadAshitaCard();
    }

    // the Addons tab can repoint the boot configuration's script meanwhile
    public void ShowingHooks() => LoadCards();

    private void LoadWindowerCard()
    {
        _loadingCard = true;
        try
        {
            WindowerSettingsProblem = null;
            WaitingForConsoleKey = false;
            if (!IsWindower || WindowerDir is not { } dir) return;
            WindowerSettingsFile? file = null;
            try { file = WindowerSettingsFile.Load(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "Could not read Windower's settings.xml");
                WindowerSettingsProblem = "Provenance couldn't read Windower's settings.xml. Close anything that has it open, then open Settings again.";
            }
            if (file?.LoadError is { } why)
            {
                Log.Warning("Windower's settings.xml couldn't be read: {Why}", why);
                WindowerSettingsProblem = file.Unreadable("start a new one here. The old one's kept next to it.");
            }
            var p = file is { LoadError: null } && WindowerProfile is { } item ? Find(file, item.Name) : null;
            WindowerConsoleKey = Choice(p?.ConsoleKey ?? WindowerKeys.Insert);
            WindowerAllowWinKey = p?.AllowWinKey ?? false;
            // mid-switch the branch is the switch's, it reloads the card once it's done
            if (!SwitchingBranch)
            {
                WindowerDev = file?.Dev == true;
                RefreshWindowerVersion();
            }
        }
        finally { _loadingCard = false; }
        OnProfilesChanged();
    }

    private static WindowerProfile? Find(WindowerSettingsFile file, string name) =>
        file.Profiles.FirstOrDefault(x => x.Name == name) ?? file.Find(name) ?? (name.Length == 0 ? file.Unnamed : null);

    private ConsoleKeyChoice Choice(WindowerKey key) =>
        ConsoleKeys.FirstOrDefault(c => c.Key.ScanCode == key.ScanCode) ?? new ConsoleKeyChoice(key, WindowerKeyNames.Display(key));

    private string Branch => WindowerDev ? "dev" : "stable";
    private static string BranchName(string branch) => branch == "dev" ? "Development" : "Stable";

    private void RefreshWindowerVersion()
    {
        if (WindowerDir is not { } dir) { WindowerVersionText = ""; return; }
        if (!File.Exists(Path.Combine(dir, "Hook.dll")))
        {
            WindowerVersionText = "Hook.dll isn't installed yet. Provenance gets it the next time you log in.";
            return;
        }
        try { WindowerVersionText = $"Hook.dll {NewWindowerUpdater(dir, Branch).BranchInfo().HookFile} is installed."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not read Windower's versions");
            WindowerVersionText = "";
        }
    }

    partial void OnWindowerConsoleKeyChanged(ConsoleKeyChoice? value)
    {
        if (!_loadingCard && value is not null) SaveWindowerProfile(p => p.ConsoleKey = value.Key);
    }

    public string ConsoleKeyText => WaitingForConsoleKey ? "Press Key Now, Esc to Cancel" : $"{(WindowerConsoleKey ?? Choice(WindowerKeys.Insert)).Display} (Click to Edit)";

    [RelayCommand]
    private void EditConsoleKey() => WaitingForConsoleKey = !WaitingForConsoleKey && WindowerCardEnabled;

    public void CancelConsoleKey() => WaitingForConsoleKey = false;

    // a key in DirectInput's numbering. Esc gives up, one Windower can't use as its console key is ignored and the
    // wait goes on, and any other becomes the key and saves straight away
    public void PressConsoleKey(int code)
    {
        if (!WaitingForConsoleKey) return;
        if (code == EscapeCode)
        {
            WaitingForConsoleKey = false;
            return;
        }
        if (ConsoleKeys.FirstOrDefault(c => c.Key.ScanCode == code) is not { } pick) return;
        WaitingForConsoleKey = false;
        WindowerConsoleKey = pick;
    }

    private const int EscapeCode = 1;

    partial void OnWindowerAllowWinKeyChanged(bool value)
    {
        if (!_loadingCard) SaveWindowerProfile(p => p.AllowWinKey = value);
    }

    private void SaveWindowerProfile(Action<WindowerProfile> change)
    {
        if (WindowerDir is not { } dir || WindowerProfile is not { } item) return;
        try
        {
            WindowerSettingsFile.Update(dir, _settings.Current.WindowerAddonLists, (f, _) =>
                change(Find(f, item.Name) ?? (item.Name.Length == 0 ? f.GetOrAddUnnamed()
                    : throw Provenance.Common.Shown.Mark(new InvalidOperationException($"The profile {item.Display} isn't in Windower's settings.xml any more.")))));
            _settings.Save(_settings.Current);
            Say("");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not save a Windower profile setting");
            Say(ErrorText.Of(ex, "Provenance couldn't save that to Windower's settings.xml."), error: true);
        }
    }

    [RelayCommand]
    private void FlipWindowerBranch()
    {
        if (WindowerBranchEnabled) WindowerDev = !WindowerDev;
    }

    partial void OnWindowerDevChanged(bool value)
    {
        if (!_loadingCard) WindowerWork = SwitchBranchAsync(value ? "dev" : "stable");
    }

    private void PutBranchBack()
    {
        _loadingCard = true;
        try { WindowerDev = WindowerDir is { } dir && ReadWindower(dir) is { Dev: true }; }
        finally { _loadingCard = false; }
    }

    // only saved here, the next login's update fetches it. The flag makes that update ask for the new branch's
    // manifest in full rather than trust the old one's date
    private async Task SwitchBranchAsync(string branch)
    {
        if (WindowerDir is not { } dir) return;
        Say("");
        SwitchingBranch = true;
        try
        {
            if (!await NewWindowerUpdater(dir, branch).PrepareBranchSwitchAsync())
            {
                Say("Provenance couldn't switch Windower's branch. Try again in a moment.", error: true);
                PutBranchBack();
                return;
            }
            WindowerSettingsFile.Update(dir, _settings.Current.WindowerAddonLists, (f, _) => f.Dev = branch == "dev");
            _settings.Save(_settings.Current);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Windower branch switch failed");
            Say(ErrorText.Of(ex, "Provenance couldn't switch Windower's branch. Try again in a moment."), error: true);
            PutBranchBack();
        }
        finally
        {
            SwitchingBranch = false;
            LoadWindowerCard();
        }
    }

    public bool CanStartNewWindowerSettings => WindowerDir is not null && !WindowerLinked;

    [RelayCommand]
    private void StartNewWindowerSettings()
    {
        if (WindowerDir is not { } dir || WindowerLinked) return;
        if (Confirm?.Invoke("Start a new settings file",
                "Windower's settings.xml can't be read. Provenance will keep it next to the new one, and the new one starts with just the Default profile.",
                "START A NEW ONE", false) != true) return;
        try
        {
            var file = WindowerSettingsFile.Load(dir);
            var moved = file.MoveBadFileAside(dir);
            file.Save(dir);
            Say(moved is null ? "" : $"Windower's old settings.xml is kept as {Path.GetFileName(moved)}.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not set Windower's settings.xml aside");
            Say(ErrorText.Of(ex, "Provenance couldn't set Windower's settings.xml aside."), error: true);
        }
        RefreshThirdPartyHookLists();
        SaveThirdPartyHook();
        LoadWindowerCard();
    }

    public ObservableCollection<string> AshitaScripts { get; } = new();
    [ObservableProperty] private string _ashitaName = "";
    [ObservableProperty] private string? _ashitaScript;
    [ObservableProperty] private string _ashitaScriptArgs = "";
    [ObservableProperty] private bool _ashitaCardEnabled;
    [ObservableProperty] private string _ashitaCardNote = "";
    private const string NoScript = "None";

    private string? AshitaFile => AshitaDir is { } dir && AshitaBootConfig is { } item ? Path.Combine(AshitaBootFiles.Folder(dir), item.Name) : null;

    private void LoadAshitaCard()
    {
        _loadingCard = true;
        try
        {
            AshitaScripts.Clear();
            AshitaCardNote = "";
            AshitaCardEnabled = false;
            AshitaName = "";
            AshitaScript = null;
            AshitaScriptArgs = "";
            if (!IsAshita || AshitaFile is not { } path) return;
            AshitaScripts.Add(NoScript);
            foreach (var s in Scripts(AshitaDir!)) AshitaScripts.Add(s);
            BootConfig cfg;
            try { cfg = BootConfig.Load(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Log.Warning(ex, "Could not read the boot configuration");
                AshitaCardNote = "Provenance couldn't read this boot configuration.";
                return;
            }
            AshitaName = cfg.Name ?? "";
            var script = string.IsNullOrWhiteSpace(cfg.Script) ? NoScript : cfg.Script!;
            if (!AshitaScripts.Contains(script)) AshitaScripts.Add(script);
            AshitaScript = script;
            AshitaScriptArgs = cfg.ScriptArgs ?? "";
            bool example = AshitaBootFiles.IsShippedExample(AshitaBootConfig!.Name);
            AshitaCardEnabled = !example;
            if (example) AshitaCardNote = "This is one of Ashita's example files, which Provenance doesn't change. Duplicate it to make one of your own.";
        }
        finally { _loadingCard = false; }
        OnProfilesChanged();
    }

    private static IEnumerable<string> Scripts(string dir)
    {
        try
        {
            var folder = Path.Combine(dir, "scripts");
            return Directory.Exists(folder)
                ? Directory.GetFiles(folder, "*.txt").Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    // the lists, the Game page and the accounts' picks all show the new name
    partial void OnAshitaNameChanged(string value)
    {
        if (_loadingCard || !SaveAshita(c => c.Name = value.Trim())) return;
        RefreshThirdPartyHookLists();
        LoadMulti();
        if (Game.Store.Platform == ThirdPartyHookService.Ashita && Game.Store.ProfileName == AshitaBootConfig?.Name) Game.Retarget(GameStoreForSelection());
    }

    partial void OnAshitaScriptChanged(string? value) { if (!_loadingCard && value is not null) SaveAshita(c => c.Script = value == NoScript ? "" : value); }
    partial void OnAshitaScriptArgsChanged(string value) { if (!_loadingCard) SaveAshita(c => c.ScriptArgs = value.Trim()); }

    private bool SaveAshita(Action<BootConfig> change)
    {
        if (!AshitaCardEnabled || AshitaFile is not { } path) return false;
        try
        {
            var cfg = BootConfig.Load(path);
            change(cfg);
            if (cfg.Ini.Changed) cfg.Save(path);
            Say("");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not save the boot configuration");
            Say(ErrorText.Of(ex, "Provenance couldn't save that to the boot configuration."), error: true);
            LoadAshitaCard();
            return false;
        }
    }
}

// Windower's console keys as people know them, from the names settings.xml uses
public static class WindowerKeyNames
{
    private static readonly Dictionary<string, string> Named = new()
    {
        ["PageUp"] = "Page Up", ["PageDown"] = "Page Down", ["PrintScreen"] = "Print Screen", ["ScrollLock"] = "Scroll Lock",
        ["Break"] = "Pause", ["Up"] = "Up arrow", ["Down"] = "Down arrow", ["Left"] = "Left arrow", ["Right"] = "Right arrow",
        ["NumLock"] = "Num Lock", ["NumPadEnter"] = "Numpad Enter", ["NumPadPlus"] = "Numpad +", ["NumPadMinus"] = "Numpad -",
        ["NumPadMultiply"] = "Numpad *", ["NumPadDivide"] = "Numpad /", ["NumPadPeriod"] = "Numpad .", ["CapsLock"] = "Caps Lock",
        ["Grave"] = "`", ["Hyphen"] = "-", ["Equals"] = "=", ["LeftBracket"] = "[", ["RightBracket"] = "]", ["Backslash"] = "\\",
        ["Semicolon"] = ";", ["Apostrophe"] = "'", ["Comma"] = ",", ["Period"] = ".", ["Slash"] = "/",
        ["PlayPause"] = "Play/Pause", ["NextTrack"] = "Next track", ["VolumeUp"] = "Volume up", ["VolumeDown"] = "Volume down",
        ["MediaSelect"] = "Media select", ["WebBack"] = "Browser back", ["WebForward"] = "Browser forward",
        ["WebStop"] = "Browser stop", ["WebRefresh"] = "Browser refresh", ["WebHome"] = "Browser home",
        ["WebSearch"] = "Browser search", ["WebBookmarks"] = "Browser favourites", ["NoConvert"] = "No convert",
        // the one key code both names share, saved as Circumflex
        ["Circumflex"] = "Previous track",
    };

    public static string Display(WindowerKey key)
    {
        if (Named.TryGetValue(key.Name, out var name)) return name;
        if (key.Name.StartsWith("NumPad", StringComparison.Ordinal)) return "Numpad " + key.Name[6..];
        if (key.Name.StartsWith("Number", StringComparison.Ordinal)) return key.Name[6..];
        return key.Name;
    }

    public static IReadOnlyList<ConsoleKeyChoice> Pickable() =>
        WindowerKeys.All.Where(WindowerKeys.Pickable).Select(WindowerKeys.Canonical).DistinctBy(k => k.ScanCode)
                    .Select(k => new ConsoleKeyChoice(k, Display(k))).ToList();

    // DirectInput's number for a key, which is what Windower's are: the scan code, plus 0x80 for an extended one.
    // Windows hands Pause and Num Lock over the wrong way round, and Print Screen only ever comes as a key up
    public static int DirectInputCode(int virtualKey, int scanCode, bool extended) => virtualKey switch
    {
        0x13 => 197,
        0x90 => 69,
        0x2C => 183,
        _ => (scanCode & 0x7F) | (extended ? 0x80 : 0),
    };
}
