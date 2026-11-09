using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Install;
using Serilog;
using Provenance.Common.Game;
using Provenance.Common.Hook;
using Provenance.Common.Patcher;
using Provenance.Common.PolFormat;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Shared;

namespace Provenance.Wpf.ViewModels.Settings;

// The official FFXI Config tool's options, saved to the registry, or to the Windower profile or Ashita boot
// configuration the store's for. Changes wait for Save, and only the changed values get written
public partial class GameSettingsViewModel : ObservableObject
{
    private readonly IDialogs _dialogs;
    private readonly PolRegion? _region;
    private readonly Func<bool> _gameRunning;
    private IGameSettingsStore _store;
    private FfxiSettings _original = FfxiSettings.Defaults();
    // what the page read back to when it showed _original, which a clamped value or a nearest render size can move off it
    private FfxiSettings _shown = FfxiSettings.Defaults();
    private bool _applying, _forTest, _loaded;
    // 0037/0038 as they'll be saved. A loaded value stays exact until the slider or the resolution moves
    private int _uiWidth, _uiHeight;

    public GameSettingsViewModel(IDialogs dialogs, PolRegion? region, IGameSettingsStore? store = null, Func<bool>? gameRunning = null)
    {
        _gameRunning = gameRunning ?? PolUpdates.GameRunning;
        _dialogs = dialogs;
        _region = region;
        _store = store ?? new RegistryGameStore(region);
        foreach (var m in DisplayModes()) Resolutions.Add(m);
        Load();
    }

    public bool Available => _forTest || _loaded;
    public bool Editable => Available && !_store.ReadOnly;

    public string ReadOnlyNote => _store.ReadOnly
        ? HookFailures.AshitaExampleFile(ProfileShown)
        : "";

    public IGameSettingsStore Store => _store;
    public bool HasProfile => _store.Platform is not null;
    public string ProfileKind => _store.Platform switch
    {
        ThirdPartyHook.Windower => "Windower profile",
        ThirdPartyHook.Ashita => "Ashita boot configuration",
        _ => "",
    };
    public string ProfileHeading => HasProfile ? $"Game settings for the {ProfileKind}" : "";
    public string ProfileShown => _store.ProfileDisplay ?? "";
    public ThirdPartyHook? Platform => _store.Platform;

    // the game never reads 0044, it only picks which sizes the official tool lists, so a profile has no use for it
    public bool KeepAspectShown => !HasProfile;

    // the Config Tool line only holds with no Windower or Ashita, they keep some of these in their own files
    public string IntroText => HasProfile
        ? "Only the settings you change are saved."
        : "These are the same settings as in the FINAL FANTASY XI Config Tool. Only the settings you change are saved, and Windower or Ashita can override some of them while you play.";

    public void UseStore(IGameSettingsStore store)
    {
        _store = store;
        StoreChanged();
        Load();
    }

    // the same profile under its new name, so nothing on screen changes
    public void Retarget(IGameSettingsStore store)
    {
        _store = store;
        StoreChanged();
    }

    private void StoreChanged()
    {
        OnPropertyChanged(nameof(Store));
        OnPropertyChanged(nameof(HasProfile));
        OnPropertyChanged(nameof(ProfileKind));
        OnPropertyChanged(nameof(ProfileShown));
        OnPropertyChanged(nameof(Platform));
        OnPropertyChanged(nameof(ProfileHeading));
        OnPropertyChanged(nameof(IntroText));
        OnPropertyChanged(nameof(KeepAspectShown));
        OnPropertyChanged(nameof(Editable));
        OnPropertyChanged(nameof(ReadOnlyNote));
    }

    public string[] WindowModes { get; } = { "Full screen", "Windowed", "Borderless window" };
    public string[] Quality3 { get; } = { "Standard", "Medium", "High quality" };
    public string[] Quality2 { get; } = { "Standard", "High quality" };
    public string[] VegetationLevels { get; } = { "Off", "Normal", "Smooth" };
    public ObservableCollection<string> Resolutions { get; } = new();
    public ObservableCollection<string> RenderSizes { get; } = new();

    [ObservableProperty] private int _windowModeIndex;
    [ObservableProperty] private string _resolution = "";
    [ObservableProperty] private string _renderSize = "";
    [ObservableProperty] private double _uiScaleMax = 1;
    [ObservableProperty] private double _uiScale = 1;
    [ObservableProperty] private double _brightness;
    [ObservableProperty] private bool _openingMovie;
    [ObservableProperty] private int _textures;
    [ObservableProperty] private int _maps2D;
    [ObservableProperty] private int _fonts;
    [ObservableProperty] private int _vegetation;
    [ObservableProperty] private bool _drawDistance;
    [ObservableProperty] private bool _enhancedShading;
    [ObservableProperty] private bool _keepAspect;
    [ObservableProperty] private bool _sound;
    [ObservableProperty] private double _soundEffects;
    [ObservableProperty] private bool _backgroundSound;
    [ObservableProperty] private string _screenshotFolder = "";
    [ObservableProperty] private bool _fullResScreenshots;
    [ObservableProperty] private bool _legacySettings;
    [ObservableProperty] private bool _hardwareMouse;
    [ObservableProperty] private bool _simpleCharSelect;
    [ObservableProperty] private bool _graphicsFix;
    [ObservableProperty] private bool _padEnabled;
    [ObservableProperty] private bool _padForceFeedback;
    [ObservableProperty] private bool _padSlider;
    [ObservableProperty] private bool _padHat;
    [ObservableProperty] private bool _padBackground;
    [ObservableProperty] private bool _padXInput;
    public StatusLine Status { get; } = new();

    public string BrightnessText => Brightness.ToString("+0.0;-0.0;0.0");
    partial void OnBrightnessChanged(double value) => OnPropertyChanged(nameof(BrightnessText));
    partial void OnKeepAspectChanged(bool value) => RebuildRenderSizes(RenderSize);

    // the scale stays put and the menu resolution follows the new size (the official tool leaves it stale)
    partial void OnResolutionChanged(string value)
    {
        RebuildRenderSizes(RenderSize);
        var (w, h) = Parse(value, 800, 600);
        UiScaleMax = FfxiSettings.MaxMenuScale(w, h);
        if (UiScale > UiScaleMax) UiScale = UiScaleMax;
        if (!_applying) UiFromScale();
    }

    partial void OnUiScaleChanged(double value)
    {
        if (!_applying) UiFromScale();
    }

    // moving back to where a loaded value sat gives that exact pair again, not one worked out a pixel off
    private void UiFromScale()
    {
        var (w, h) = Parse(Resolution, 800, 600);
        var scale = FfxiSettings.SnapMenuScale(UiScale);
        var o = _original;
        (_uiWidth, _uiHeight) = (w, h) == (o.Width, o.Height) && scale == FfxiSettings.MenuScale(o.Height, o.UiWidth, o.UiHeight)
            ? (o.UiWidth, o.UiHeight)
            : FfxiSettings.MenuResolution(w, h, scale);
        OnPropertyChanged(nameof(UiScaleText));
        OnPropertyChanged(nameof(UiSizeText));
    }

    // the official tool puts these back to 0 whenever Legacy settings is off (the hardware mouse too, but the game
    // reads that one whatever Legacy settings says)
    partial void OnLegacySettingsChanged(bool value)
    {
        if (value || _applying) return;
        SimpleCharSelect = false;
        GraphicsFix = false;
    }

    public string UiScaleText => FfxiSettings.SnapMenuScale(UiScale).ToString("0.0#") + "x";

    public string UiSizeText
    {
        get
        {
            var (w, h) = Parse(Resolution, 800, 600);
            return _uiWidth <= 0 || _uiHeight <= 0
                ? $"The menus are drawn at the full game resolution, {Fmt(w, h)}."
                : $"The menus are drawn at {Fmt(_uiWidth, _uiHeight)}, then scaled to fit.";
        }
    }

    private void Load()
    {
        FfxiSettings? loaded = null;
        string? problem = null;
        try { loaded = _store.Load(); }
        catch (Exception ex)
        {
            if (!ErrorText.Ours(ex)) Log.Warning(ex, "Could not load the game settings");
            problem = ErrorText.Of(ex, "Provenance couldn't read the game settings.");
        }
        _loaded = loaded is not null;
        _original = loaded ?? FfxiSettings.Defaults();
        ShowOriginal();
        OnPropertyChanged(nameof(Available));
        OnPropertyChanged(nameof(Editable));
        Status.Show(problem ?? (HasProfile ? $"These are the current game settings for this {ProfileKind}." : "These are your current game settings."));
    }

    // made-up settings in place of this PC's, and Save never writes them
    internal void ShowForTest(FfxiSettings s)
    {
        _forTest = true;
        _original = s.Clone();
        ShowOriginal();
        OnPropertyChanged(nameof(Available));
        OnPropertyChanged(nameof(Editable));
        Status.Show("These are your current game settings.");
    }

    public bool HasUnsavedChanges => Editable && GameFields.Changed(Collect(), _shown).Count > 0;

    private void ShowOriginal()
    {
        Apply(_original);
        _shown = Collect();
    }

    private void Apply(FfxiSettings s)
    {
        _applying = true;
        try
        {
            WindowModeIndex = s.WindowMode switch { 0 => 0, 3 => 2, _ => 1 };
            var res = Fmt(s.Width, s.Height);
            if (!Resolutions.Contains(res)) Resolutions.Insert(0, res);
            Resolution = res;
            KeepAspect = s.KeepAspect == 1;
            RebuildRenderSizes(Fmt(s.BgWidth, s.BgHeight));
            (_uiWidth, _uiHeight) = (s.UiWidth, s.UiHeight);
            // a value from elsewhere can sit past either end. The slider rests at that end and the pair stays as it is
            UiScaleMax = FfxiSettings.MaxMenuScale(s.Width, s.Height);
            UiScale = Math.Clamp(FfxiSettings.MenuScale(s.Height, s.UiWidth, s.UiHeight), 1, UiScaleMax);
            OnPropertyChanged(nameof(UiScaleText));
            OnPropertyChanged(nameof(UiSizeText));
            Brightness = Math.Round(s.Brightness, 1);
            OpeningMovie = s.OpeningMovie == 1;
            Textures = Math.Clamp(s.Textures, 0, 2);
            Maps2D = Math.Clamp(s.Maps2D, 0, 1);
            Fonts = Math.Clamp(s.Fonts, 0, 2);
            Vegetation = Math.Clamp(s.Vegetation, 0, 2);
            DrawDistance = s.DrawDistanceMip == 0;
            EnhancedShading = s.EnhancedShading == 1;
            Sound = s.Sound == 1;
            SoundEffects = Math.Clamp(s.SoundEffects, 12, 20);
            BackgroundSound = s.BackgroundSound == 1;
            ScreenshotFolder = s.ScreenshotFolder;
            FullResScreenshots = s.FullResScreenshots == 1;
            LegacySettings = s.LegacySettings == 1;
            HardwareMouse = s.HardwareMouse == 1;
            SimpleCharSelect = s.SimpleCharSelect == 1;
            GraphicsFix = s.GraphicsFix == 1;
            PadEnabled = s.PadMode[0] == 1; PadForceFeedback = s.PadMode[1] == 1; PadSlider = s.PadMode[2] == 1;
            PadHat = s.PadMode[3] == 1; PadBackground = s.PadMode[4] == 1; PadXInput = s.PadMode[5] == 1;
        }
        finally { _applying = false; }
    }

    private FfxiSettings Collect()
    {
        var s = _original.Clone();
        s.WindowMode = WindowModeIndex switch { 0 => 0, 2 => 3, _ => 1 };
        (s.Width, s.Height) = Parse(Resolution, s.Width, s.Height);
        (s.BgWidth, s.BgHeight) = Parse(RenderSize, s.BgWidth, s.BgHeight);
        (s.UiWidth, s.UiHeight) = (_uiWidth, _uiHeight);
        s.Brightness = (float)Math.Round(Brightness, 1);
        s.OpeningMovie = B(OpeningMovie); s.Textures = Textures; s.Maps2D = Maps2D; s.Fonts = Fonts;
        // off keeps a mip level Windower or Ashita had above 1
        s.Vegetation = Vegetation; s.DrawDistanceMip = DrawDistance ? 0 : Math.Max(1, _original.DrawDistanceMip); s.EnhancedShading = B(EnhancedShading);
        s.KeepAspect = KeepAspectShown ? B(KeepAspect) : _original.KeepAspect; s.Sound = B(Sound); s.SoundEffects = (int)Math.Round(SoundEffects);
        s.BackgroundSound = B(BackgroundSound); s.ScreenshotFolder = ScreenshotFolder.Trim();
        s.FullResScreenshots = B(FullResScreenshots); s.LegacySettings = B(LegacySettings);
        s.HardwareMouse = B(HardwareMouse); s.SimpleCharSelect = B(SimpleCharSelect); s.GraphicsFix = B(GraphicsFix);
        s.PadMode = PadModeShown();
        return s;
    }

    private int[] PadModeShown() => [B(PadEnabled), B(PadForceFeedback), B(PadSlider), B(PadHat), B(PadBackground), B(PadXInput)];

    [RelayCommand]
    private void Save()
    {
        if (_forTest) return;
        if (!HasProfile && _region is null) { Status.Show("Provenance couldn't find a PlayOnline install, so there's nowhere to save these settings."); return; }
        if (!_loaded) { Status.Show("FINAL FANTASY XI isn't installed on this PC, so there are no settings to save."); return; }
        if (_store.ReadOnly) { Status.Fail(ReadOnlyNote); return; }
        try
        {
            var saved = _store.Save(Collect(), _original);
            _original = _store.Load();
            ShowOriginal();
            bool running = _gameRunning();
            Status.Show(!saved.Wrote ? "You haven't changed anything, so there's nothing to save." :
                        "Your changes are saved." + (running ? " If the game is running, it'll use them the next time it starts." : ""));
            Log.Information("Saved {Count} FFXI settings values ({Store})", saved.Changed, _store.Key);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Error(ex, "FFXI settings save refused");
            Status.Fail("Your changes couldn't be saved because Windows didn't let Provenance change the FINAL FANTASY XI settings.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "FFXI settings save failed");
            Status.Fail(ErrorText.Of(ex, "Provenance couldn't write the FINAL FANTASY XI settings, so your changes weren't saved."));
        }
    }

    [RelayCommand]
    private void Revert() { ShowOriginal(); Status.Show("Your unsaved changes have been undone."); }

    [RelayCommand]
    private void Defaults()
    {
        // the official Default button leaves the gamepad alone
        var d = FfxiSettings.Defaults();
        d.PadMode = PadModeShown();
        Apply(d);
        Status.Show("The default settings are shown. Press Save to keep them.");
    }

    // the official tool's presets, which only touch the graphics values
    [RelayCommand]
    private void Preset(string? which)
    {
        var (w, h) = Parse(Resolution, 800, 600);
        switch (which)
        {
            case "Ultra":
                RenderSize = PickOrAdd(Fmt(w * 2, h * 2)); Textures = 2; Fonts = 2; Vegetation = 2;
                EnhancedShading = true; DrawDistance = true; Maps2D = 1; break;
            case "High":
                RenderSize = PickOrAdd(Fmt(w, h)); Textures = 1; Fonts = 1; Vegetation = 1;
                EnhancedShading = true; DrawDistance = true; Maps2D = 1; break;
            default:
                RenderSize = PickOrAdd(SameShapeOnly ? RenderSizes.FirstOrDefault() ?? Fmt(w, h) : Fmt(512, 512));
                Textures = 0; Maps2D = 0; Fonts = 0; Vegetation = 1; DrawDistance = false; EnhancedShading = false; break;
        }
        Status.Show($"The {which} preset is shown. Press Save to keep it.");
    }

    [RelayCommand]
    private void BrowseScreenshots()
    {
        if (_dialogs.PickFolder("Choose a folder for the game's screenshots and settings") is { } d) ScreenshotFolder = d;
    }

    [RelayCommand]
    private void OpenPadConfig()
    {
        var ffxi = _region is { } r ? PolPaths.TryGetInstallFolder(r, GameProduct.Game) : null;
        var tools = _region switch { PolRegion.Europe => "ToolsEU", PolRegion.Japan => "Tools", _ => "ToolsUS" };
        var exe = ffxi is null ? null : Path.Combine(ffxi, tools, "FFXiPadConfig.exe");
        if (exe is null || !File.Exists(exe)) { Status.Fail("Provenance couldn't find the FINAL FANTASY XI gamepad tool in the game's folder."); return; }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
        Status.Show("The gamepad tool is open. When you've finished with it, close these settings and open them again to see its changes.");
    }

    // Windower keeps the 3D resolution as one scale of the game's, so only that shape can be saved there
    private bool SameShapeOnly => (KeepAspectShown && KeepAspect) || _store.SameShapeRenderSizes;

    private void RebuildRenderSizes(string keep)
    {
        var (w, h) = Parse(Resolution, 800, 600);
        RenderSizes.Clear();
        var list = new List<string> { Fmt(w, h), Fmt(w * 2, h * 2) };
        if (!SameShapeOnly) foreach (var p in new[] { 512, 1024, 2048, 4096 }) list.Add(Fmt(p, p));
        foreach (var m in Resolutions) { var (mw, mh) = Parse(m, 0, 0); if (!SameShapeOnly || mw * h == mh * w) list.Add(m); }
        foreach (var x in list.Distinct()) RenderSizes.Add(x);
        var pick = string.IsNullOrEmpty(keep) ? Fmt(w, h) : keep;
        // a scale's rounding can leave it a pixel off the shape, which is fine. Another resolution's shape takes the
        // nearest one that fits
        var (kw, kh) = Parse(pick, w, h);
        if (_store.SameShapeRenderSizes && Math.Abs((long)kw * h - (long)kh * w) > Math.Max(w, h))
            pick = RenderSizes.OrderBy(r => Math.Abs(Parse(r, 0, 0).Item1 - kw)).First();
        RenderSize = PickOrAdd(pick);
    }

    private string PickOrAdd(string v)
    {
        if (!RenderSizes.Contains(v)) RenderSizes.Add(v);
        return v;
    }

    private static string Fmt(int w, int h) => $"{w} x {h}";
    private static int B(bool v) => v ? 1 : 0;
    private static (int, int) Parse(string? s, int dw, int dh)
    {
        var p = s?.Split('x', StringSplitOptions.TrimEntries) ?? [];
        return p.Length == 2 && int.TryParse(p[0], out var a) && int.TryParse(p[1], out var b) ? (a, b) : (dw, dh);
    }

    // the primary screen's modes, largest first, capped at 3840 wide like the official tool
    private static IEnumerable<string> DisplayModes()
    {
        var seen = new HashSet<(int, int)>();
        foreach (var (w, h) in Display.Modes())
            if (w >= 800 && h >= 600 && w <= 3840)
                seen.Add((w, h));
        return seen.OrderByDescending(m => m.Item1 * m.Item2).Select(m => Fmt(m.Item1, m.Item2));
    }
}
