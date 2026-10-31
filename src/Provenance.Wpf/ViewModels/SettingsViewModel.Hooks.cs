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

public sealed partial class PolPluginRow : ObservableObject
{
    public required string Name { get; init; }
    public bool Available { get; init; } = true;
    public string? Note { get; init; }
    [ObservableProperty] private bool _on;
    internal Action<PolPluginRow>? Changed;

    partial void OnOnChanged(bool value) => Changed?.Invoke(this);
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
    internal Func<string, string, WindowerUpdater> NewWindowerUpdater { get; set; } = WindowerUpdates.NewUpdater;
    internal Func<bool> WindowerBusy { get; set; } = WindowerUpdater.AnyRunning;
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
            foreach (var c in AshitaBootFiles.List(AshitaDir)) AshitaBootConfigs.Add(new HookProfileItem(c, c));
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
            1 => ("The game starts through Ashita with the boot configuration below.", false),
            2 when WindowerDir is null => ("Couldn't find Windower 4 in that folder.", true),
            2 when ThirdPartyHookService.IsLinkedFolder(WindowerDir) => (ThirdPartyHookService.LinkedWindowerText, true),
            2 => ("The game starts through Windower with the profile below.", false),
            _ => ("The game starts directly, without Windower or Ashita.", false),
        };
        OnPropertyChanged(nameof(IsAshita));
        OnPropertyChanged(nameof(IsWindower));
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

    // after the first load, a switch between Windower and Ashita clears each account's stored profile
    partial void OnThirdPartyHookIndexChanged(int value)
    {
        SaveThirdPartyHook();
        RefreshMultiChoices(platformChanged: !_loadingThirdPartyHook);
        if (_loadingThirdPartyHook) return;
        // a pol.exe pre-started for a plain login would hold off Windower's updates, and it's no use to either of them
        if (value is 1 or 2) _stopPrestart?.Invoke();
        LoadCards();
    }

    partial void OnAshitaBootConfigChanged(HookProfileItem? value)
    {
        if (_loadingThirdPartyHook) return;
        SaveThirdPartyHook();
        LoadAshitaCard();
    }

    partial void OnWindowerProfileChanged(HookProfileItem? value)
    {
        if (_loadingThirdPartyHook) return;
        SaveThirdPartyHook();
        LoadWindowerCard();
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
            return new AshitaGameStore(a, boot, registry);
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

    public void ShowingGame(Func<string?, bool?>? saveFirst)
    {
        var want = GameStoreForSelection();
        if (want.Key == Game.Store.Key && Game.Available) return;
        if (Game.HasUnsavedChanges)
        {
            var answer = saveFirst?.Invoke(Game.HasProfile ? $"the {Game.ProfileKind} {Game.ProfileShown}" : null);
            if (answer is null) return;
            if (answer == true)
            {
                Game.SaveCommand.Execute(null);
                if (Game.HasUnsavedChanges) return;   // the save failed, its error's on the page
            }
        }
        Game.UseStore(want);
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
        OnPropertyChanged(nameof(WindowerUpdatesEnabled));
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
            var name = AskName?.Invoke("Duplicate a boot configuration", $"What should the copy of {b.Name} be called?", start, n => HookProfiles.AshitaNameProblem(a, n));
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
            var name = AskName?.Invoke("Rename a boot configuration", $"What should {b.Name} be called?", b.Name, n => HookProfiles.AshitaNameProblem(a, n, renaming: b.Name));
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
            if (Confirm?.Invoke("Delete boot configuration", $"Delete the boot configuration {b.Name}? The file is removed from Ashita's config\\boot folder, and this can't be undone.", "DELETE", true) != true) return;
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
    public string[] WindowerBranches { get; } = { "Stable", "Development" };
    [ObservableProperty] private ConsoleKeyChoice? _windowerConsoleKey;
    [ObservableProperty] private bool _windowerAllowWinKey;
    [ObservableProperty] private int _windowerBranchIndex;
    [ObservableProperty] private string _windowerVersionText = "";
    [ObservableProperty] private string _windowerUpdateText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(WindowerUpdatesEnabled))] private bool _windowerUpdating;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanManageProfiles), nameof(CanCopyProfile), nameof(CanChangeProfile), nameof(WindowerCardEnabled), nameof(WindowerUpdatesEnabled))]
    private string? _windowerSettingsProblem;
    private bool _loadingCard;

    public bool WindowerCardEnabled => WindowerDir is not null && !WindowerLinked && WindowerSettingsProblem is null && WindowerProfile is not null;
    public bool WindowerUpdatesEnabled => WindowerDir is not null && !WindowerLinked && WindowerSettingsProblem is null && !WindowerUpdating;

    private void LoadCards()
    {
        LoadWindowerCard();
        LoadAshitaCard();
    }

    private void LoadWindowerCard()
    {
        _loadingCard = true;
        try
        {
            WindowerSettingsProblem = null;
            if (!IsWindower || WindowerDir is not { } dir) return;
            WindowerSettingsFile? file = null;
            try { file = WindowerSettingsFile.Load(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "Could not read Windower's settings.xml");
                WindowerSettingsProblem = "Provenance couldn't read Windower's settings.xml. Close anything that has it open, then open Settings again.";
            }
            if (file?.LoadError is { } why)
                WindowerSettingsProblem = $"Windower's settings.xml couldn't be read ({why}). Fix the file, or start a new one here. The old one's kept next to it.";
            var p = file is { LoadError: null } && WindowerProfile is { } item ? Find(file, item.Name) : null;
            WindowerConsoleKey = Choice(p?.ConsoleKey ?? WindowerKeys.Insert);
            WindowerAllowWinKey = p?.AllowWinKey ?? false;
            WindowerBranchIndex = file?.Dev == true ? 1 : 0;
            RefreshWindowerVersion();
            ShowLastUpdate();
        }
        finally { _loadingCard = false; }
        OnProfilesChanged();
    }

    private static WindowerProfile? Find(WindowerSettingsFile file, string name) =>
        file.Profiles.FirstOrDefault(x => x.Name == name) ?? file.Find(name) ?? (name.Length == 0 ? file.Unnamed : null);

    private ConsoleKeyChoice Choice(WindowerKey key) =>
        ConsoleKeys.FirstOrDefault(c => c.Key.ScanCode == key.ScanCode) ?? new ConsoleKeyChoice(key, WindowerKeyNames.Display(key));

    private string Branch => WindowerBranchIndex == 1 ? "dev" : "stable";
    private static string BranchName(string branch) => branch == "dev" ? "Development" : "Stable";

    private void RefreshWindowerVersion()
    {
        if (WindowerDir is not { } dir) { WindowerVersionText = ""; return; }
        var name = BranchName(Branch);
        if (!File.Exists(Path.Combine(dir, "Hook.dll")))
        {
            WindowerVersionText = "Hook.dll isn't installed yet. Check for updates to get it.";
            return;
        }
        WindowerBranchInfo info;
        try { info = NewWindowerUpdater(dir, Branch).BranchInfo(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not read Windower's versions");
            WindowerVersionText = "";
            return;
        }
        WindowerVersionText = info.State switch
        {
            WindowerBranchState.Matches => $"Hook.dll {info.HookFile} is installed, the latest {name} version.",
            WindowerBranchState.UpdatePending when info.HookManifest == info.HookFile =>
                $"Hook.dll {info.HookFile} is installed, the latest {name} version. Windower.exe has an update waiting, which the next check gets.",
            WindowerBranchState.UpdatePending => $"Hook.dll {info.HookFile} is installed. The {name} version is {info.HookManifest}, which the next check gets.",
            _ => $"Hook.dll {info.HookFile} is installed. Check for updates to compare it with the {name} version.",
        };
    }

    private void ShowLastUpdate() =>
        WindowerUpdateText = WindowerUpdates.For(WindowerDir) is { } last ? $"Last checked at {last.At:HH:mm}. {last.Text}" : "";

    partial void OnWindowerConsoleKeyChanged(ConsoleKeyChoice? value)
    {
        if (!_loadingCard && value is not null) SaveWindowerProfile(p => p.ConsoleKey = value.Key);
    }

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

    partial void OnWindowerBranchIndexChanged(int value)
    {
        if (!_loadingCard) WindowerWork = SwitchBranchAsync(value == 1 ? "dev" : "stable");
    }

    private void PutBranchBack()
    {
        _loadingCard = true;
        try { WindowerBranchIndex = WindowerDir is { } dir && ReadWindower(dir) is { Dev: true } ? 1 : 0; }
        finally { _loadingCard = false; }
    }

    // Windower's own files get swapped out, so nothing that uses them can be running
    private async Task SwitchBranchAsync(string branch)
    {
        if (WindowerDir is not { } dir) return;
        if (WindowerBusy())
        {
            Say("Close FINAL FANTASY XI, PlayOnline and Windower before you switch Windower's branch.", error: true);
            PutBranchBack();
            return;
        }
        Say("");
        WindowerUpdating = true;
        WindowerUpdateText = $"Switching Windower to the {BranchName(branch)} version...";
        try
        {
            var updater = NewWindowerUpdater(dir, branch);
            if (!await updater.PrepareBranchSwitchAsync())
            {
                Say("Provenance couldn't switch Windower's branch. Try again in a moment.", error: true);
                PutBranchBack();
                return;
            }
            WindowerSettingsFile.Update(dir, _settings.Current.WindowerAddonLists, (f, _) => f.Dev = branch == "dev");
            _settings.Save(_settings.Current);
            var result = await Task.Run(() => updater.UpdateAsync());
            WindowerUpdates.Record(dir, WindowerUpdates.Describe(result));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Windower branch switch failed");
            Say(ErrorText.Of(ex, "Provenance couldn't switch Windower's branch. Try again in a moment."), error: true);
            PutBranchBack();
        }
        finally
        {
            WindowerUpdating = false;
            RefreshWindowerVersion();
            ShowLastUpdate();
        }
    }

    [RelayCommand]
    private Task CheckWindowerUpdates() => WindowerWork = CheckWindowerUpdatesAsync();

    private async Task CheckWindowerUpdatesAsync()
    {
        if (WindowerDir is not { } dir || WindowerUpdating) return;
        var branch = ReadWindower(dir)?.Branch ?? Branch;
        WindowerUpdating = true;
        WindowerUpdateText = "Checking for Windower updates...";
        try
        {
            var result = await Task.Run(() => NewWindowerUpdater(dir, branch).UpdateAsync());
            foreach (var e in result.Errors) Log.Warning("Windower update: {Error}", e);
            WindowerUpdates.Record(dir, WindowerUpdates.Describe(result));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Windower update failed");
            WindowerUpdates.Record(dir, "Windower's update stopped part way.");
        }
        finally
        {
            WindowerUpdating = false;
            RefreshWindowerVersion();
            ShowLastUpdate();
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
    public ObservableCollection<PolPluginRow> AshitaPolPlugins { get; } = new();
    public string[] AshitaLanguages { get; } = { "Japanese", "English (North America)", "English (Europe)" };
    [ObservableProperty] private string _ashitaName = "";
    [ObservableProperty] private string? _ashitaScript;
    [ObservableProperty] private string _ashitaScriptArgs = "";
    [ObservableProperty] private int _ashitaLanguageIndex = 1;
    [ObservableProperty] private bool _ashitaSandboxOn;
    [ObservableProperty] private bool _ashitaCardEnabled;
    [ObservableProperty] private string _ashitaCardNote = "";
    private const string NoScript = "None";

    private string? AshitaFile => AshitaDir is { } dir && AshitaBootConfig is { } item ? Path.Combine(AshitaBootFiles.Folder(dir), item.Name) : null;

    private void LoadAshitaCard()
    {
        _loadingCard = true;
        try
        {
            AshitaPolPlugins.Clear();
            AshitaScripts.Clear();
            AshitaCardNote = "";
            AshitaCardEnabled = false;
            AshitaName = "";
            AshitaScript = null;
            AshitaScriptArgs = "";
            AshitaLanguageIndex = 1;
            AshitaSandboxOn = false;
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
            AshitaLanguageIndex = cfg.PlayOnlineLanguage switch { 1 => 0, 3 => 2, _ => 1 };
            foreach (var pp in cfg.PolPlugins)
            {
                bool takeover = AshitaBootFiles.IsTakeover(pp.Name);
                var row = new PolPluginRow { Name = pp.Name, Available = !takeover, Note = takeover ? "Not available with Provenance" : null, On = pp.On && !takeover };
                row.Changed = SavePolPlugin;
                AshitaPolPlugins.Add(row);
            }
            AshitaSandboxOn = SandboxOn();
            bool example = AshitaBootFiles.IsShippedExample(AshitaBootConfig!.Name);
            AshitaCardEnabled = !example;
            if (example) AshitaCardNote = "This is one of Ashita's example files, which Provenance doesn't change. Duplicate it to make one of your own.";
        }
        finally { _loadingCard = false; }
        OnProfilesChanged();
    }

    private bool SandboxOn() => AshitaPolPlugins.Any(p => p.Name.Equals("sandbox", StringComparison.OrdinalIgnoreCase) && p.On);

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

    partial void OnAshitaNameChanged(string value) { if (!_loadingCard) SaveAshita(c => c.Name = value.Trim()); }
    partial void OnAshitaScriptChanged(string? value) { if (!_loadingCard && value is not null) SaveAshita(c => c.Script = value == NoScript ? "" : value); }
    partial void OnAshitaScriptArgsChanged(string value) { if (!_loadingCard) SaveAshita(c => c.ScriptArgs = value.Trim()); }

    // 0 and 2 are both English to Ashita, so English is only written back when it's picked
    partial void OnAshitaLanguageIndexChanged(int value)
    {
        if (!_loadingCard) SaveAshita(c => c.PlayOnlineLanguage = value switch { 0 => 1, 2 => 3, _ => 2 });
    }

    private void SavePolPlugin(PolPluginRow row)
    {
        if (_loadingCard || !row.Available) return;
        SaveAshita(c => c.SetPolPlugin(row.Name, row.On));
        AshitaSandboxOn = SandboxOn();
    }

    private void SaveAshita(Action<BootConfig> change)
    {
        if (!AshitaCardEnabled || AshitaFile is not { } path) return;
        try
        {
            var cfg = BootConfig.Load(path);
            change(cfg);
            if (cfg.Ini.Changed) cfg.Save(path);
            Say("");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not save the boot configuration");
            Say(ErrorText.Of(ex, "Provenance couldn't save that to the boot configuration."), error: true);
            LoadAshitaCard();
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
}
