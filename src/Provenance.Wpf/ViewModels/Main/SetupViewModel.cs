using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Game.Ashita;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Game.Windower;
using Provenance.Common.Security;
using Provenance.Common.Settings;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Settings;
using Provenance.Wpf.ViewModels.Shared;
using Serilog;

namespace Provenance.Wpf.ViewModels.Main;

// the one question a new install gets asked: Windower, Ashita or neither. Nothing's saved till a button's pressed
public partial class SetupViewModel : ObservableObject
{
    internal const string LookingText = "Looking for Windower and Ashita...";
    internal const string NotFoundText = "Not found. Press Browse to choose its folder.";

    private readonly IDialogs _dialogs;
    private readonly ISettingsService _settings;
    private bool _looked;

    public SetupViewModel(IDialogs dialogs, ISettingsService settings)
    {
        _dialogs = dialogs;
        _settings = settings;
    }

    // true when the answer changed the platform
    internal event Action<bool>? Finished;

    public bool Pending => _settings.Current.SetupPending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowerText), nameof(AshitaText))]
    private bool _looking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowerText))]
    [NotifyCanExecuteChangedFor(nameof(UseWindowerCommand))]
    private string? _windowerFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AshitaText))]
    [NotifyCanExecuteChangedFor(nameof(UseAshitaCommand))]
    private string? _ashitaFolder;

    public string WindowerText => WindowerFolder ?? (_looked ? NotFoundText : "");
    public string AshitaText => AshitaFolder ?? (_looked ? NotFoundText : "");
    public StatusLine Status { get; } = new();

    internal Task? Looked { get; private set; }

    internal Task Look() => Looked ??= LookAsync();

    private async Task LookAsync()
    {
        Looking = true;
        Status.Show(LookingText);
        FoundHooks found;
        try { found = await Task.Run(ThirdPartyHookFinder.Find); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Looking for Windower and Ashita failed");
            found = new([], []);
        }
        // a folder browsed to meanwhile stays, and one already in settings goes ahead of anything found
        var s = _settings.Current;
        var (windowerOk, ashitaOk) = await Task.Run(() => (WindowerCheck(s.WindowerPath) is null, AshitaCheck(s.AshitaPath) is null));
        WindowerFolder ??= windowerOk ? s.WindowerPath : found.Windower.FirstOrDefault();
        AshitaFolder ??= ashitaOk ? s.AshitaPath : found.Ashita.FirstOrDefault();
        _looked = true;
        Looking = false;
        if (Status.Text == LookingText) Status.Clear();
    }

    // stands in for the folder checks
    internal Func<bool, string?, string?>? CheckFolder { get; set; }

    private string? WindowerCheck(string? dir) => CheckFolder is { } c ? c(true, dir) : WindowerProblem(dir);

    private string? AshitaCheck(string? dir) => CheckFolder is { } c ? c(false, dir) : AshitaProblem(dir);

    private static string? WindowerProblem(string? dir) =>
        HookFolder.Usable(dir) is not { } usable || !WindowerInstall.IsFolder(usable) ? HookFolder.DriveProblem(dir) ?? WindowerInstall.NotFoundText
        : FolderPins.Linked(usable) ? WindowerInstall.LinkedText : null;

    private static string? AshitaProblem(string? dir) =>
        HookFolder.Usable(dir) is { } usable && AshitaInstall.IsFolder(usable) ? null : HookFolder.DriveProblem(dir) ?? AshitaInstall.NotFoundText;

    // a share's checked off the UI thread, a local folder on the spot
    internal static Task<string?> ProblemAsync(Func<string?, string?> problemWith, string? dir) =>
        HookFolder.OnNetwork(dir) ? Task.Run(() => problemWith(dir)) : Task.FromResult(problemWith(dir));

    [RelayCommand]
    private async Task BrowseWindower()
    {
        if (await Browsed(WindowerInstall.BrowseTitle, WindowerCheck) is { } dir) WindowerFolder = dir;
    }

    [RelayCommand]
    private async Task BrowseAshita()
    {
        if (await Browsed(AshitaInstall.BrowseTitle, AshitaCheck) is { } dir) AshitaFolder = dir;
    }

    private async Task<string?> Browsed(string title, Func<string?, string?> problemWith)
    {
        if (_dialogs.PickFolder(title) is not { } dir) return null;
        if (HookFolder.OnNetwork(dir)) Status.Show(HookFolder.CheckingText);
        if (await ProblemAsync(problemWith, dir) is { } problem)
        {
            Status.Fail(problem);
            return null;
        }
        if (Status.IsError || Status.Text == HookFolder.CheckingText) Status.Clear();
        return dir;
    }

    private bool CanUseWindower() => WindowerFolder is not null;

    [RelayCommand(CanExecute = nameof(CanUseWindower))]
    private Task UseWindower() => Use(ThirdPartyHook.Windower, WindowerFolder, WindowerCheck);

    private bool CanUseAshita() => AshitaFolder is not null;

    [RelayCommand(CanExecute = nameof(CanUseAshita))]
    private Task UseAshita() => Use(ThirdPartyHook.Ashita, AshitaFolder, AshitaCheck);

    [RelayCommand]
    private void UseNeither() => Finish(s => s.ThirdPartyHookPlatform = ThirdPartyHook.None);

    // the folder's checked once more, it can have gone since it was found
    private async Task Use(ThirdPartyHook platform, string? dir, Func<string?, string?> problemWith)
    {
        if (dir is null) return;
        if (HookFolder.OnNetwork(dir)) Status.Show(HookFolder.CheckingText);
        if (await ProblemAsync(problemWith, dir) is { } problem)
        {
            Status.Fail(problem);
            return;
        }
        Finish(s =>
        {
            s.ThirdPartyHookPlatform = platform;
            if (platform == ThirdPartyHook.Windower) s.WindowerPath = dir;
            else s.AshitaPath = dir;
        });
    }

    // the profile and boot configuration stay as they are, blank already means Windower's unnamed profile and the
    // last boot configuration Ashita ran with
    private void Finish(Action<LauncherSettings> pick)
    {
        var before = _settings.Current.ThirdPartyHookPlatform;
        _settings.SaveChange(s =>
        {
            pick(s);
            s.SetupPending = false;
        });
        Status.Clear();
        OnPropertyChanged(nameof(Pending));
        Finished?.Invoke(_settings.Current.ThirdPartyHookPlatform != before);
    }

    // answered somewhere else, like the Settings page
    internal void Done()
    {
        if (!Pending) return;
        _settings.SaveChange(s => s.SetupPending = false);
        OnPropertyChanged(nameof(Pending));
        Finished?.Invoke(false);
    }
}
