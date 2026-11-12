using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common;
using Provenance.Common.Game;
using Provenance.Common.Game.Ashita;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Game.Windower;
using Provenance.Common.PolFormat;
using Provenance.Common.Security;
using Provenance.Common.Settings;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Shared;
using Serilog;

namespace Provenance.Wpf.ViewModels.Settings;

// Like General, every change saves straight away. The cards, the accounts and the Game page follow each one
// through the events
public partial class HookSettings : ObservableObject, IProfileHost
{
    private readonly IDialogs _dialogs;
    private readonly UiThread _ui = new();
    private readonly ISettingsService _settings;
    private readonly SettingsContext _context;
    private readonly Action? _stopPrestart;
    private readonly Func<bool> _gameFollows;
    private bool _loading;

    internal HookSettings(IDialogs dialogs, ISettingsService settings, SettingsContext context, Action? stopPrestart,
                          Func<bool> gameFollows)
    {
        _dialogs = dialogs;
        _settings = settings;
        _context = context;
        _stopPrestart = stopPrestart;
        _gameFollows = gameFollows;
    }

    internal event Action? Saved;
    internal event Action? ListsRefreshed;
    // true when the player switched platform, false for the first load
    internal event Action<bool>? PlatformSwitched;
    public event Action? WindowerReload, AshitaReload;
    internal event Action? AccountsReload;
    public event Action? ProfilesChanged;
    // whether the change worked, and the profile's old name after a rename
    internal event Action<bool, string?>? ProfileEdited;
    internal event Action? CloseRequested;

    public IReadOnlyList<Choice<ThirdPartyHook>> ThirdPartyHookChoices { get; } =
        [new(ThirdPartyHook.None, "None"), new(ThirdPartyHook.Ashita, "Ashita v4"), new(ThirdPartyHook.Windower, "Windower 4")];

    [ObservableProperty] private ThirdPartyHook _hook;
    [ObservableProperty] private string _ashitaPath = string.Empty;
    [ObservableProperty] private string _windowerPath = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SelectedProfile))] private HookProfileItem? _ashitaBootConfig;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SelectedProfile))] private HookProfileItem? _windowerProfile;
    public StatusLine ThirdPartyHookStatus { get; } = new();
    public StatusLine ProfileStatus { get; } = new();
    public ObservableCollection<HookProfileItem> AshitaBootConfigs { get; } = new();
    public ObservableCollection<HookProfileItem> WindowerProfiles { get; } = new();
    // the title bar's one switcher shows whichever platform's list is the current one
    public ObservableCollection<HookProfileItem> SwitcherProfiles => IsAshita ? AshitaBootConfigs : WindowerProfiles;
    public HookProfileItem? SelectedProfile
    {
        get => IsAshita ? AshitaBootConfig : WindowerProfile;
        // swapping the list in the box clears its selection and says so, which isn't a pick
        set
        {
            if (value is null) return;
            if (IsAshita) AshitaBootConfig = value;
            else WindowerProfile = value;
        }
    }
    public string ProfileNoun => IsAshita ? "boot configuration" : "profile";
    public string ProfileSwitcherName => IsAshita ? "Ashita boot configuration" : "Windower profile";
    public string ProfileSwitcherLabel => IsAshita ? "Editing Ashita boot configuration:" : "Editing Windower profile:";
    public bool IsAshita => Hook == ThirdPartyHook.Ashita;
    public bool IsWindower => Hook == ThirdPartyHook.Windower;

    // the same folder and profile this tab is changing. With nothing in its list (settings.xml won't read, say) it's
    // still the one that was picked
    internal AddonsTarget PickedAddonsTarget() => Hook switch
    {
        ThirdPartyHook.Ashita => new(ThirdPartyHook.Ashita, AshitaDir, AshitaBootConfig?.Name ?? _settings.Current.AshitaBootConfig ?? ""),
        ThirdPartyHook.Windower => new(ThirdPartyHook.Windower, WindowerDir,
            WindowerProfile?.Name ?? PlayTarget.For(_settings.Current, null, ThirdPartyHook.Windower).Profile),
        _ => new(ThirdPartyHook.None, null, ""),
    };

    private string? AskName(string title, string prompt, string start, Func<string, string?> check) =>
        _dialogs.Ask(title, prompt, start, WindowerSettingsFile.MaxNameLength, validate: check);

    // a pick that's turned down goes back once the dropdown's finished with it
    private void Later(Action a)
    {
        if (_ui.Available) _ui.Post(a);
        else a();
    }

    // only the folder in the box, never one found somewhere else, since profiles get changed in it
    public string? WindowerDir => WindowerInstall.IsFolder(WindowerPath) ? WindowerPath : null;
    // settings.xml's writer refuses one too, so nothing here offers to change it
    public bool WindowerLinked => WindowerDir is { } d && FolderPins.Linked(d);
    public string? AshitaDir => AshitaInstall.IsFolder(AshitaPath) ? AshitaPath : null;

    internal void Load()
    {
        _loading = true;
        var s = _settings.Current;
        Hook = s.ThirdPartyHookPlatform;
        AshitaPath = AshitaInstall.Detect(s.AshitaPath) ?? s.AshitaPath;
        WindowerPath = WindowerInstall.Detect(s.WindowerPath) ?? s.WindowerPath;
        _loading = false;
        // the boot config Ashita really ran with last (from its logs), so the game keeps its window and plugins
        var windower = PlayTarget.For(s, null, ThirdPartyHook.Windower);
        var ashita = PlayTarget.For(s, null, ThirdPartyHook.Ashita);
        RefreshThirdPartyHookLists(windower.Profile, ashita.Profile);
        // a pick that's gone from its folder isn't swapped for another here, Play would fail on it too
        _loading = true;
        if (ashita.Profile.Length > 0 && AshitaBootConfig is not null && !ashita.Is(AshitaBootConfig.Name)) AshitaBootConfig = null;
        if (windower.Profile.Length > 0 && WindowerProfile is not null && !windower.Is(WindowerProfile.Name)) WindowerProfile = null;
        _loading = false;
        UpdateThirdPartyHookStatus();
        ReloadCards();
    }

    internal static HookProfileItem? Item(IEnumerable<HookProfileItem> items, string? name) =>
        name is null ? null : items.FirstOrDefault(i => i.Name == name) ?? items.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    internal bool StillThere(string name) => IsWindower ? Item(WindowerProfiles, name) is not null : Item(AshitaBootConfigs, name) is not null;

    // the cards load again, the Addons tab can repoint the boot configuration's script meanwhile
    internal void ReloadCards()
    {
        WindowerReload?.Invoke();
        AshitaReload?.Invoke();
    }

    // the lists change under a ComboBox, which drops its pick on the way, so the pick's put back by name
    public void RefreshThirdPartyHookLists(string? windowerKeep = null, string? ashitaKeep = null)
    {
        windowerKeep ??= WindowerProfile?.Name ?? _settings.Current.WindowerProfile;
        ashitaKeep ??= AshitaBootConfig?.Name ?? _settings.Current.AshitaBootConfig;
        bool was = _loading;
        _loading = true;
        try
        {
            AshitaBootConfigs.Clear();
            foreach (var (file, shown) in AshitaBootFiles.Names(AshitaDir)) AshitaBootConfigs.Add(new HookProfileItem(file, shown));
            WindowerProfiles.Clear();
            var names = WindowerInstall.Profiles(WindowerDir);
            foreach (var n in names) WindowerProfiles.Add(new HookProfileItem(n, WindowerInstall.DisplayName(n, names)));
            AshitaBootConfig = Item(AshitaBootConfigs, ashitaKeep)
                               ?? AshitaBootConfigs.FirstOrDefault(c => c.Name.Equals("default.ini", StringComparison.OrdinalIgnoreCase))
                               ?? AshitaBootConfigs.FirstOrDefault();
            WindowerProfile = Item(WindowerProfiles, windowerKeep) ?? WindowerProfiles.FirstOrDefault();
        }
        finally { _loading = was; }
        ListsRefreshed?.Invoke();
        NotifyProfilesChanged();
    }

    private void UpdateThirdPartyHookStatus()
    {
        var (text, isError) = Hook switch
        {
            ThirdPartyHook.Ashita when AshitaDir is null => (AshitaInstall.NotFoundText, true),
            ThirdPartyHook.Ashita => ("The game starts through Ashita with the boot configuration picked at the top.", false),
            ThirdPartyHook.Windower when WindowerDir is null => (WindowerInstall.NotFoundText, true),
            ThirdPartyHook.Windower when FolderPins.Linked(WindowerDir) => (WindowerInstall.LinkedText, true),
            ThirdPartyHook.Windower => ("The game starts through Windower with the profile picked at the top.", false),
            _ => ("The game starts directly, without Windower or Ashita.", false),
        };
        ThirdPartyHookStatus.Set(text, isError);
        OnPropertyChanged(nameof(IsAshita));
        OnPropertyChanged(nameof(IsWindower));
        OnPropertyChanged(nameof(ProfileSwitcherLabel));
        OnPropertyChanged(nameof(ProfileSwitcherName));
        OnPropertyChanged(nameof(ProfileNoun));
        OnPropertyChanged(nameof(SwitcherProfiles));
        OnPropertyChanged(nameof(SelectedProfile));
        NotifyProfilesChanged();
    }

    public void SaveThirdPartyHook()
    {
        if (_loading) return;
        var (hook, ashita, windower, boot, profile) = (Hook, AshitaPath, WindowerPath, AshitaBootConfig?.Name, WindowerProfile?.Name);
        _settings.SaveChange(s =>
        {
            s.ThirdPartyHookPlatform = hook;
            s.AshitaPath = ashita;
            s.WindowerPath = windower;
            // nothing shown (no folder yet) keeps what was picked before
            if (boot is not null) s.AshitaBootConfig = boot;
            if (profile is not null) s.WindowerProfile = profile;
        });
        UpdateThirdPartyHookStatus();
        Saved?.Invoke();
    }

    // set once the player's agreed to a restart for a platform change, the window closes and the app restarts after
    public bool RestartWanted { get; private set; }

    public static string RestartText(ThirdPartyHook from, ThirdPartyHook to) =>
        (from != ThirdPartyHook.None ? $"You're already logged in, so {PlatformName(from)} is still loaded." : "You're already logged in without Windower or Ashita.")
        + (to != ThirdPartyHook.None ? $" Provenance needs to restart to switch to {PlatformName(to)}." : $" Provenance needs to restart to stop using {PlatformName(from)}.");

    private static string PlatformName(ThirdPartyHook hook) => hook switch
    {
        ThirdPartyHook.Ashita => "Ashita",
        ThirdPartyHook.Windower => "Windower",
        _ => "no Windower or Ashita",
    };

    // after the first load, switching between Windower and Ashita clears each account's stored profile. Logged in, the
    // old one's loaded, so it takes a restart, and a no puts it back unsaved
    partial void OnHookChanged(ThirdPartyHook oldValue, ThirdPartyHook newValue)
    {
        bool restart = !_loading && _context.LoggedIn();
        if (restart && !_dialogs.Confirm("Restart Provenance?", RestartText(oldValue, newValue), "RESTART"))
        {
            Later(() => PutPlatformBack(oldValue));
            return;
        }
        SaveThirdPartyHook();
        PlatformSwitched?.Invoke(!_loading);
        if (_loading) return;
        // a pol.exe pre-started for a plain login would hold off Windower's updates, and it's no use to either of them
        if (newValue != ThirdPartyHook.None) _stopPrestart?.Invoke();
        ReloadCards();
        if (!restart) return;
        RestartWanted = true;
        Later(() => CloseRequested?.Invoke());
    }

    private void PutPlatformBack(ThirdPartyHook hook)
    {
        _loading = true;
        try { Hook = hook; }
        finally { _loading = false; }
    }

    partial void OnAshitaBootConfigChanged(HookProfileItem? oldValue, HookProfileItem? newValue)
    {
        if (_loading) return;
        if (!_gameFollows())
        {
            Later(() => AshitaBootConfig = oldValue);
            return;
        }
        SaveThirdPartyHook();
        AshitaReload?.Invoke();
    }

    partial void OnWindowerProfileChanged(HookProfileItem? oldValue, HookProfileItem? newValue)
    {
        if (_loading) return;
        if (!_gameFollows())
        {
            Later(() => WindowerProfile = oldValue);
            return;
        }
        SaveThirdPartyHook();
        WindowerReload?.Invoke();
    }

    partial void OnAshitaPathChanged(string value)
    {
        if (_loading) return;
        RefreshThirdPartyHookLists();
        SaveThirdPartyHook();
        AshitaReload?.Invoke();
    }

    partial void OnWindowerPathChanged(string value)
    {
        if (_loading) return;
        RefreshThirdPartyHookLists();
        SaveThirdPartyHook();
        WindowerReload?.Invoke();
    }

    [RelayCommand]
    private void BrowseAshita() { if (_dialogs.PickFolder(AshitaInstall.BrowseTitle) is { } d) AshitaPath = d; }

    [RelayCommand]
    private void BrowseWindower() { if (_dialogs.PickFolder(WindowerInstall.BrowseTitle) is { } d) WindowerPath = d; }

    // the Windower card keeps this up to date
    public string? WindowerProblem { get; set; }

    public bool CanManageProfiles => !_context.MultiRunning()
        && (IsWindower ? WindowerDir is not null && !WindowerLinked && WindowerProblem is null : IsAshita && AshitaDir is not null);
    public bool CanCopyProfile => CanManageProfiles && (IsWindower ? WindowerProfile is not null : AshitaBootConfig is not null);
    // the unnamed Windower profile and Ashita's own examples stay as they are
    public bool CanChangeProfile => CanManageProfiles && (IsWindower
        ? WindowerProfile is { Name.Length: > 0 }
        : AshitaBootConfig is { } a && !AshitaBootFiles.IsShippedExample(a.Name));

    public void NotifyProfilesChanged()
    {
        OnPropertyChanged(nameof(CanManageProfiles));
        OnPropertyChanged(nameof(CanCopyProfile));
        OnPropertyChanged(nameof(CanChangeProfile));
        ProfilesChanged?.Invoke();
    }

    private int PlayOnlineLanguage => _settings.DetectedRegion switch { PolRegion.Japan => 1, PolRegion.Europe => 3, _ => 2 };

    public WindowerSettingsFile? ReadWindower(string dir)
    {
        try { return WindowerSettingsFile.Load(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not read Windower's settings.xml");
            ProfileStatus.Fail("Provenance couldn't read Windower's settings.xml. Try again in a moment.");
            return null;
        }
    }

    // the current platform's side of the profile commands. A delete doesn't need Windower's settings.xml read, and
    // KeepAfterDelete is what the list picks once one's gone (Windower's unnamed profile if there is one)
    private sealed record ProfileSet(ILaunchProfiles Profiles, string Label, HookProfileItem? Selected, string? KeepAfterDelete);

    private ProfileSet? CurrentProfiles(bool needsSelected, bool readWindower = true)
    {
        if (IsWindower && WindowerDir is { } w)
        {
            if (needsSelected && WindowerProfile is null) return null;
            WindowerSettingsFile? file = null;
            if (readWindower && (file = ReadWindower(w)) is null) return null;
            return new(new WindowerProfiles(_settings, w, file), "a Windower profile", WindowerProfile, "");
        }
        if (IsAshita && AshitaDir is { } a)
        {
            if (needsSelected && AshitaBootConfig is null) return null;
            return new(new AshitaProfiles(_settings, a, PlayOnlineLanguage), "a boot configuration", AshitaBootConfig, null);
        }
        return null;
    }

    [RelayCommand]
    private void AddProfile()
    {
        if (!CanManageProfiles || CurrentProfiles(needsSelected: false) is not { } set) return;
        var name = AskName($"Add {set.Label}", $"What should the new {ProfileNoun} be called?", set.Profiles.NewName(), n => set.Profiles.NameProblem(n));
        if (name is not null) ChangeProfiles(() => set.Profiles.Add(name), $"Provenance couldn't add the {ProfileNoun}.");
    }

    [RelayCommand]
    private void DuplicateProfile()
    {
        if (!CanCopyProfile || CurrentProfiles(needsSelected: true) is not { Selected: { } p } set) return;
        var name = AskName($"Duplicate {set.Label}", $"What should the copy of {p.Display} be called?", set.Profiles.CopyName(p.Name, p.Display),
                           n => set.Profiles.NameProblem(n));
        if (name is not null) ChangeProfiles(() => set.Profiles.Duplicate(p.Name, name), $"Provenance couldn't copy the {ProfileNoun}.");
    }

    [RelayCommand]
    private void RenameProfile()
    {
        if (!CanChangeProfile || CurrentProfiles(needsSelected: true) is not { Selected: { } p } set) return;
        // Ashita renames the file, a name of its own stays put
        var ask = IsAshita && p.Display != p.Name ? $"What should the file for {p.Display} be called?" : $"What should {p.Display} be called?";
        var name = AskName($"Rename {set.Label}", ask, p.Name, n => set.Profiles.NameProblem(n, renaming: p.Name));
        if (name is null || set.Profiles.Canonical(name) == p.Name) return;
        ChangeProfiles(() => set.Profiles.Rename(p.Name, name), $"Provenance couldn't rename the {ProfileNoun}.", renamedFrom: p.Name);
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (!CanChangeProfile || CurrentProfiles(needsSelected: true, readWindower: false) is not { Selected: { } p } set) return;
        var body = IsAshita
            ? $"Delete the boot configuration {p.Display}? {(p.Display == p.Name ? "The file is" : $"Its file, {p.Name}, is")} removed from Ashita's config\\boot folder, and this can't be undone."
            : $"Delete the Windower profile {p.Display}? Its settings are removed from Windower's settings.xml, and this can't be undone.";
        if (!_dialogs.Confirm($"Delete {ProfileNoun}", body, "DELETE", ConfirmStyle.Destructive)) return;
        ChangeProfiles(() => { set.Profiles.Delete(p.Name); return set.KeepAfterDelete; }, $"Provenance couldn't delete the {ProfileNoun}.");
    }

    private void ChangeProfiles(Func<string?> change, string failed, string? renamedFrom = null)
    {
        string? pick = null;
        bool ok = false;
        try
        {
            pick = change();
            ok = true;
            ProfileStatus.Clear();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Profile change failed");
            ProfileStatus.Fail(ErrorText.Of(ex, failed));
        }
        if (IsWindower) RefreshThirdPartyHookLists(windowerKeep: ok ? pick : null);
        else RefreshThirdPartyHookLists(ashitaKeep: ok ? pick : null);
        ProfileEdited?.Invoke(ok, renamedFrom);
        SaveThirdPartyHook();
        AccountsReload?.Invoke();
        ReloadCards();
    }
}
