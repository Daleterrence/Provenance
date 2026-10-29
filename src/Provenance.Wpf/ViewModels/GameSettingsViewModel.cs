using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Provenance.Common.Game;
using Provenance.Common.PolFormat;

namespace Provenance.Wpf.ViewModels;

// The official FFXI Config tool's options, on the same registry values. Changes wait for Save, and only the changed
// values get written
public partial class GameSettingsViewModel : ObservableObject
{
    private readonly PolRegion? _region;
    private FfxiSettings _original = FfxiSettings.Defaults();
    private bool _applying, _edited, _forTest;
    // 0037/0038 as they'll be saved. A loaded value stays exact until the slider or the resolution moves
    private int _uiWidth, _uiHeight;

    public GameSettingsViewModel(PolRegion? region)
    {
        _region = region;
        foreach (var m in DisplayModes()) Resolutions.Add(m);
        Load();
    }

    public bool Available => _forTest || (_region is { } r && FfxiConfig.Exists(r));

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
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;
    partial void OnStatusChanged(string value) => StatusIsError = false;

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

    // the official tool puts these back to 0 whenever Legacy settings is off
    partial void OnLegacySettingsChanged(bool value)
    {
        if (value || _applying) return;
        HardwareMouse = false;
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
        _original = _region is { } r && FfxiConfig.Exists(r) ? FfxiConfig.Load(r) : FfxiSettings.Defaults();
        Apply(_original);
        // the game makes its settings on its first start
        Status = Available ? "These are your current game settings."
               : _region is { } reg && PolPaths.TryGetInstallFolder(reg, PolPaths.FfxiProduct) != null
                   ? "FINAL FANTASY XI is installed, but it has no saved settings yet. Open the FINAL FANTASY XI Config tool once as an administrator so it saves them, then open these settings again."
                   : "FINAL FANTASY XI isn't installed on this PC, so there are no game settings to change.";
    }

    // made-up settings in place of this PC's, and Save never writes them
    internal void ShowForTest(FfxiSettings s)
    {
        _forTest = true;
        _original = s.Clone();
        Apply(_original);
        OnPropertyChanged(nameof(Available));
        Status = "These are your current game settings.";
    }

    public bool HasUnsavedChanges => Available && _edited;

    // any property change that isn't loading values (or a read-out) counts as an edit
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_applying && e.PropertyName is not (nameof(Status) or nameof(StatusIsError) or nameof(BrightnessText) or nameof(Available)
                or nameof(HasUnsavedChanges) or nameof(UiScaleText) or nameof(UiSizeText) or nameof(UiScaleMax)))
            _edited = true;
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
        _edited = false;
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
        s.Vegetation = Vegetation; s.DrawDistanceMip = DrawDistance ? 0 : 1; s.EnhancedShading = B(EnhancedShading);
        s.KeepAspect = B(KeepAspect); s.Sound = B(Sound); s.SoundEffects = (int)Math.Round(SoundEffects);
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
        if (_region is not { } r) { Status = "Provenance couldn't find a PlayOnline install, so there's nowhere to save these settings."; return; }
        if (!FfxiConfig.Exists(r)) { Status = "FINAL FANTASY XI isn't installed on this PC, so there are no settings to save."; return; }
        try
        {
            int n = FfxiConfig.Save(r, Collect(), _original);
            _original = FfxiConfig.Load(r);
            Apply(_original);
            bool running = Process.GetProcessesByName("pol").Length > 0;
            Status = n == 0 ? "You haven't changed anything, so there's nothing to save." :
                     "Your changes are saved." + (running ? " If the game is running, it'll use them the next time it starts." : "");
            Log.Information("Saved {Count} FFXI settings values", n);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Error(ex, "FFXI settings save refused");
            Status = "Your changes couldn't be saved because Windows didn't let Provenance change the FINAL FANTASY XI settings.";
            StatusIsError = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "FFXI settings save failed");
            Status = "Provenance couldn't write the FINAL FANTASY XI settings, so your changes weren't saved.";
            StatusIsError = true;
        }
    }

    [RelayCommand]
    private void Revert() { Apply(_original); Status = "Your unsaved changes have been undone."; }

    [RelayCommand]
    private void Defaults()
    {
        // the official Default button leaves the gamepad alone
        var d = FfxiSettings.Defaults();
        d.PadMode = PadModeShown();
        Apply(d);
        _edited = true;
        Status = "The default settings are shown. Press Save to keep them.";
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
                RenderSize = PickOrAdd(KeepAspect ? RenderSizes.FirstOrDefault() ?? Fmt(w, h) : Fmt(512, 512));
                Textures = 0; Maps2D = 0; Fonts = 0; Vegetation = 1; DrawDistance = false; EnhancedShading = false; break;
        }
        Status = $"The {which} preset is shown. Press Save to keep it.";
    }

    [RelayCommand]
    private void BrowseScreenshots()
    {
        if (SettingsViewModel.PickFolder("Choose a folder for the game's screenshots and settings") is { } d) ScreenshotFolder = d;
    }

    [RelayCommand]
    private void OpenPadConfig()
    {
        var ffxi = _region is { } r ? PolPaths.TryGetInstallFolder(r, PolPaths.FfxiProduct) : null;
        var tools = _region switch { PolRegion.Europe => "ToolsEU", PolRegion.Japan => "Tools", _ => "ToolsUS" };
        var exe = ffxi is null ? null : Path.Combine(ffxi, tools, "FFXiPadConfig.exe");
        if (exe is null || !File.Exists(exe)) { Status = "Provenance couldn't find the FINAL FANTASY XI gamepad tool in the game's folder."; StatusIsError = true; return; }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
        Status = "The gamepad tool is open. When you've finished with it, close these settings and open them again to see its changes.";
    }

    private void RebuildRenderSizes(string keep)
    {
        var (w, h) = Parse(Resolution, 800, 600);
        RenderSizes.Clear();
        var list = new List<string> { Fmt(w, h), Fmt(w * 2, h * 2) };
        if (!KeepAspect) foreach (var p in new[] { 512, 1024, 2048, 4096 }) list.Add(Fmt(p, p));
        foreach (var m in Resolutions) { var (mw, mh) = Parse(m, 0, 0); if (!KeepAspect || mw * h == mh * w) list.Add(m); }
        foreach (var x in list.Distinct()) RenderSizes.Add(x);
        RenderSize = PickOrAdd(string.IsNullOrEmpty(keep) ? Fmt(w, h) : keep);
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
        foreach (var (w, h) in FfxiConfig.PrimaryDisplayModes())
            if (w >= 800 && h >= 600 && w <= 3840)
                seen.Add((w, h));
        return seen.OrderByDescending(m => m.Item1 * m.Item2).Select(m => Fmt(m.Item1, m.Item2));
    }
}
