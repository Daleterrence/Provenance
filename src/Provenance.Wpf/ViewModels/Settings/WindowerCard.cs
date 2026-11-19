using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common;
using Provenance.Common.Game.Windower;
using Provenance.Common.Hook;
using Provenance.Common.Settings;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Shared;
using Serilog;

namespace Provenance.Wpf.ViewModels.Settings;

public partial class WindowerCard : ObservableObject
{
    private readonly IProfileHost _hook;
    private readonly ISettingsService _settings;
    private readonly IDialogs _dialogs;
    private readonly Func<string, string, WindowerUpdater> _newUpdater;
    private readonly CardWork _work = new();
    private bool _loadingCard;
    private int _loads;

    public WindowerCard(IProfileHost hook, ISettingsService settings, IDialogs dialogs, Func<string, string, WindowerUpdater>? newUpdater = null)
    {
        _newUpdater = newUpdater ?? ((dir, branch) => new WindowerUpdater(dir, branch));
        _hook = hook;
        _settings = settings;
        _dialogs = dialogs;
        hook.WindowerReload += Load;
        hook.ProfilesChanged += () =>
        {
            OnPropertyChanged(nameof(CardEnabled));
            OnPropertyChanged(nameof(WindowerBranchEnabled));
            OnPropertyChanged(nameof(CanStartNewWindowerSettings));
        };
    }

    internal Task WindowerWork { get; private set; } = Task.CompletedTask;

    internal CardWork Work => _work;

    public IReadOnlyList<ConsoleKeyChoice> ConsoleKeys { get; } = WindowerKeyNames.Pickable();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ConsoleKeyText))] private ConsoleKeyChoice? _consoleKey;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ConsoleKeyText))] private bool _waitingForConsoleKey;
    [ObservableProperty] private bool _allowWinKey;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(WindowerBranchName))] private bool _windowerDev;
    [ObservableProperty] private string _versionText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(WindowerBranchEnabled))] private bool _switchingBranch;
    [ObservableProperty] private string? _windowerSettingsProblem;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CardEnabled), nameof(WindowerBranchEnabled), nameof(CanStartNewWindowerSettings))] private bool _reading;

    partial void OnWindowerSettingsProblemChanged(string? value)
    {
        _hook.WindowerProblem = value;
        _hook.NotifyProfilesChanged();
    }

    public bool CardEnabled => _hook.WindowerDir is not null && !_hook.WindowerLinked && WindowerSettingsProblem is null && _hook.WindowerProfile is not null && !Reading;
    public bool WindowerBranchEnabled => _hook.WindowerDir is not null && !_hook.WindowerLinked && WindowerSettingsProblem is null && !SwitchingBranch && !Reading;
    public string WindowerBranchName => BranchName(Branch);

    private void Load()
    {
        int mine = ++_loads;
        _loadingCard = true;
        try
        {
            WindowerSettingsProblem = null;
            _settingsUnparseable = false;
            WaitingForConsoleKey = false;
        }
        finally { _loadingCard = false; }
        if (_hook.IsWindower && _hook.WindowerDir is { } dir)
        {
            var name = _hook.WindowerProfile?.Name;
            bool switching = SwitchingBranch;
            Reading = true;
            _work.Run(() => ReadCard(dir, name, switching), r => { if (mine == _loads) Show(r, switching); });
        }
        else Reading = false;
        _hook.NotifyProfilesChanged();
    }

    private sealed record CardRead(WindowerSettingsFile? File, string? Problem, WindowerProfile? Profile, string? Version);

    private CardRead ReadCard(string dir, string? profile, bool switching)
    {
        WindowerSettingsFile? file = null;
        string? problem = null;
        try { file = WindowerSettingsFile.Load(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not read Windower's settings.xml");
            problem = "Provenance couldn't read Windower's settings.xml. Close anything that has it open, then open Settings again.";
        }
        var p = file is { LoadError: null } && profile is not null ? Find(file, profile) : null;
        return new CardRead(file, problem, p, switching ? null : HookVersion(dir, file?.Dev == true ? "dev" : "stable"));
    }

    private void Show(CardRead r, bool switching)
    {
        _loadingCard = true;
        try
        {
            WindowerSettingsProblem = r.Problem;
            if (r.File?.LoadError is { } why)
            {
                Log.Warning("Windower's settings.xml couldn't be read: {Why}", why);
                _settingsUnparseable = true;
                WindowerSettingsProblem = r.File.Unreadable("start a new one here. The old one's kept next to it.");
            }
            ConsoleKey = Choice(r.Profile?.ConsoleKey ?? WindowerKeys.Insert);
            AllowWinKey = r.Profile?.AllowWinKey ?? false;
            if (!switching)
            {
                WindowerDev = r.File?.Dev == true;
                VersionText = r.Version ?? "";
            }
        }
        finally
        {
            _loadingCard = false;
            Reading = false;
        }
        _hook.NotifyProfilesChanged();
    }

    private static WindowerProfile? Find(WindowerSettingsFile file, string name) =>
        file.Profiles.FirstOrDefault(x => x.Name == name) ?? file.Find(name) ?? (name.Length == 0 ? file.Unnamed : null);

    private ConsoleKeyChoice Choice(WindowerKey key) =>
        ConsoleKeys.FirstOrDefault(c => c.Key.ScanCode == key.ScanCode) ?? new ConsoleKeyChoice(key, WindowerKeyNames.Display(key));

    private string Branch => WindowerDev ? "dev" : "stable";
    private static string BranchName(string branch) => branch == "dev" ? "Development" : "Stable";

    private string HookVersion(string dir, string branch)
    {
        if (!File.Exists(Path.Combine(dir, "Hook.dll"))) return "Hook.dll isn't installed yet. Provenance gets it the next time you log in.";
        try { return $"Hook.dll {_newUpdater(dir, branch).BranchInfo().HookFile} is installed."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not read Windower's versions");
            return "";
        }
    }

    partial void OnConsoleKeyChanged(ConsoleKeyChoice? value)
    {
        if (!_loadingCard && value is not null) SaveWindowerProfile(p => p.ConsoleKey = value.Key);
    }

    public string ConsoleKeyText => WaitingForConsoleKey ? "PRESS KEY NOW, ESC TO CANCEL" : $"{(ConsoleKey ?? Choice(WindowerKeys.Insert)).Display.ToUpperInvariant()} (CLICK TO EDIT)";

    [RelayCommand]
    private void EditConsoleKey() => WaitingForConsoleKey = !WaitingForConsoleKey && CardEnabled;

    public void CancelConsoleKey() => WaitingForConsoleKey = false;

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
        ConsoleKey = pick;
    }

    private const int EscapeCode = 1;

    partial void OnAllowWinKeyChanged(bool value)
    {
        if (!_loadingCard) SaveWindowerProfile(p => p.AllowWinKey = value);
    }

    private void SaveWindowerProfile(Action<WindowerProfile> change)
    {
        if (_hook.WindowerDir is not { } dir || _hook.WindowerProfile is not { } item) return;
        var lists = _settings.Current.WindowerAddonLists;
        WindowerWork = _work.Run(() =>
        {
            try
            {
                WindowerSettingsFile.Update(dir, lists, (f, _) =>
                    change(Find(f, item.Name) ?? (item.Name.Length == 0 ? f.GetOrAddUnnamed()
                        : throw Provenance.Common.Shown.Mark(new InvalidOperationException($"The profile {item.Display} isn't in Windower's settings.xml any more.")))));
                _settings.Save(_settings.Current);
                return null;
            }
            catch (Exception ex) { return ex; }
        }, ex =>
        {
            if (ex is null)
            {
                _hook.ProfileStatus.Clear();
                return;
            }
            Log.Warning(ex, "Could not save a Windower profile setting");
            _hook.ProfileStatus.Fail(ErrorText.Of(ex, "Provenance couldn't save that to Windower's settings.xml."));
        });
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

    private async Task PutBranchBack(string dir)
    {
        var dev = await _work.Run(() => _hook.ReadWindower(dir) is { Dev: true });
        _loadingCard = true;
        try { WindowerDev = dev; }
        finally { _loadingCard = false; }
    }

    private async Task SwitchBranchAsync(string branch)
    {
        if (_hook.WindowerDir is not { } dir) return;
        _hook.ProfileStatus.Clear();
        SwitchingBranch = true;
        try
        {
            if (!await _newUpdater(dir, branch).PrepareBranchSwitchAsync())
            {
                _hook.ProfileStatus.Fail("Provenance couldn't switch Windower's branch. Try again in a moment.");
                await PutBranchBack(dir);
                return;
            }
            var lists = _settings.Current.WindowerAddonLists;
            await _work.Run(() =>
            {
                WindowerSettingsFile.Update(dir, lists, (f, _) => f.Dev = branch == "dev");
                _settings.Save(_settings.Current);
                return true;
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Windower branch switch failed");
            _hook.ProfileStatus.Fail(ErrorText.Of(ex, "Provenance couldn't switch Windower's branch. Try again in a moment."));
            await PutBranchBack(dir);
        }
        finally
        {
            SwitchingBranch = false;
            Load();
        }
    }

    private bool _settingsUnparseable;

    public bool CanStartNewWindowerSettings => _settingsUnparseable && _hook.WindowerDir is not null && !_hook.WindowerLinked;

    [RelayCommand]
    private void StartNewWindowerSettings()
    {
        if (_hook.WindowerDir is not { } dir || _hook.WindowerLinked) return;
        if (!_dialogs.Confirm("Start a new settings file",
                "Windower's settings.xml can't be read. Provenance will keep it next to the new one, and the new one starts with just the Default profile.",
                "START A NEW ONE")) return;
        WindowerWork = _work.Run(() =>
        {
            try
            {
                var file = WindowerSettingsFile.Load(dir);
                var moved = file.MoveBadFileAside(dir);
                file.Save(dir);
                return (Moved: moved, Failure: (Exception?)null);
            }
            catch (Exception ex) { return (Moved: null, Failure: ex); }
        }, r =>
        {
            if (r.Failure is { } ex)
            {
                Log.Warning(ex, "Could not set Windower's settings.xml aside");
                _hook.ProfileStatus.Fail(ErrorText.Of(ex, "Provenance couldn't set Windower's settings.xml aside."));
            }
            else _hook.ProfileStatus.Show(r.Moved is null ? "" : $"Windower's old settings.xml is kept as {Path.GetFileName(r.Moved)}.");
            _hook.RefreshThirdPartyHookLists();
            _hook.SaveThirdPartyHook();
            Load();
        });
    }
}
