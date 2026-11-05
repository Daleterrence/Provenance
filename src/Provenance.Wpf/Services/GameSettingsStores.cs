using System.IO;
using Microsoft.Win32;
using Provenance.Common;
using Provenance.Common.Game;
using Provenance.Common.Game.Ashita;
using Provenance.Common.Game.Windower;
using Provenance.Common.IO;
using Provenance.Common.PolFormat;

namespace Provenance.Wpf.Services;

public readonly record struct GameSaveResult(int Changed, bool Wrote);

public interface IGameSettingsStore
{
    // the same Key means the same place to save, so a switch back doesn't reload for nothing
    string Key { get; }
    string? Platform { get; }
    string? ProfileName { get; }
    string? ProfileDisplay { get; }
    bool SameShapeRenderSizes { get; }
    // shown, but never saved to
    bool ReadOnly { get; }

    // throws when there's nothing to load, Shown with the reason
    FfxiSettings Load();
    GameSaveResult Save(FfxiSettings settings, FfxiSettings original);
}

public static class GameFields
{
    public const string Resolution = "Resolution", WindowMode = "WindowMode", UiSize = "UiSize", Brightness = "Brightness",
        RenderSize = "RenderSize", KeepAspect = "KeepAspect", Textures = "Textures", Fonts = "Fonts", Maps = "Maps",
        Vegetation = "Vegetation", DistantTextures = "DistantTextures", EnhancedShading = "EnhancedShading", Sound = "Sound",
        SoundEffects = "SoundEffects", BackgroundSound = "BackgroundSound", Pad = "Pad", PadForceFeedback = "PadForceFeedback",
        PadSlider = "PadSlider", PadHat = "PadHat", PadBackground = "PadBackground", PadXInput = "PadXInput",
        OpeningMovie = "OpeningMovie", FullResScreenshots = "FullResScreenshots", ScreenshotFolder = "ScreenshotFolder",
        LegacySettings = "LegacySettings", HardwareMouse = "HardwareMouse", SimpleCharSelect = "SimpleCharSelect",
        GraphicsFix = "GraphicsFix";

    private sealed record Field(Func<FfxiSettings, FfxiSettings, bool> Same, Action<FfxiSettings, FfxiSettings> Copy);

    private static Field Int(Func<FfxiSettings, int> get, Action<FfxiSettings, int> set) =>
        new((a, b) => get(a) == get(b), (from, to) => set(to, get(from)));

    private static Field PadBit(int i) =>
        new((a, b) => Bit(a, i) == Bit(b, i), (from, to) => { to.PadMode = Padded(to.PadMode); to.PadMode[i] = Bit(from, i); });

    private static int Bit(FfxiSettings s, int i) => i < s.PadMode.Length ? s.PadMode[i] : 0;

    private static int[] Padded(int[] pad) => pad.Length >= 6 ? (int[])pad.Clone() : [.. pad, .. new int[6 - pad.Length]];

    private static readonly Dictionary<string, Field> Table = new()
    {
        [Resolution] = new((a, b) => a.Width == b.Width && a.Height == b.Height, (f, t) => (t.Width, t.Height) = (f.Width, f.Height)),
        [WindowMode] = Int(s => s.WindowMode, (s, v) => s.WindowMode = v),
        [UiSize] = new((a, b) => a.UiWidth == b.UiWidth && a.UiHeight == b.UiHeight, (f, t) => (t.UiWidth, t.UiHeight) = (f.UiWidth, f.UiHeight)),
        // the page works in tenths, so a value from elsewhere between two of them isn't a change till it's moved
        [Brightness] = new((a, b) => MathF.Round(a.Brightness, 1) == MathF.Round(b.Brightness, 1), (f, t) => t.Brightness = f.Brightness),
        [RenderSize] = new((a, b) => a.BgWidth == b.BgWidth && a.BgHeight == b.BgHeight, (f, t) => (t.BgWidth, t.BgHeight) = (f.BgWidth, f.BgHeight)),
        [KeepAspect] = Int(s => s.KeepAspect, (s, v) => s.KeepAspect = v),
        [Textures] = Int(s => s.Textures, (s, v) => s.Textures = v),
        [Fonts] = Int(s => s.Fonts, (s, v) => s.Fonts = v),
        [Maps] = Int(s => s.Maps2D, (s, v) => s.Maps2D = v),
        [Vegetation] = Int(s => s.Vegetation, (s, v) => s.Vegetation = v),
        [DistantTextures] = Int(s => s.DrawDistanceMip, (s, v) => s.DrawDistanceMip = v),
        [EnhancedShading] = Int(s => s.EnhancedShading, (s, v) => s.EnhancedShading = v),
        [Sound] = Int(s => s.Sound, (s, v) => s.Sound = v),
        [SoundEffects] = Int(s => s.SoundEffects, (s, v) => s.SoundEffects = v),
        [BackgroundSound] = Int(s => s.BackgroundSound, (s, v) => s.BackgroundSound = v),
        [Pad] = PadBit(0),
        [PadForceFeedback] = PadBit(1),
        [PadSlider] = PadBit(2),
        [PadHat] = PadBit(3),
        [PadBackground] = PadBit(4),
        [PadXInput] = PadBit(5),
        [OpeningMovie] = Int(s => s.OpeningMovie, (s, v) => s.OpeningMovie = v),
        [FullResScreenshots] = Int(s => s.FullResScreenshots, (s, v) => s.FullResScreenshots = v),
        [ScreenshotFolder] = new((a, b) => a.ScreenshotFolder == b.ScreenshotFolder, (f, t) => t.ScreenshotFolder = f.ScreenshotFolder),
        [LegacySettings] = Int(s => s.LegacySettings, (s, v) => s.LegacySettings = v),
        [HardwareMouse] = Int(s => s.HardwareMouse, (s, v) => s.HardwareMouse = v),
        [SimpleCharSelect] = Int(s => s.SimpleCharSelect, (s, v) => s.SimpleCharSelect = v),
        [GraphicsFix] = Int(s => s.GraphicsFix, (s, v) => s.GraphicsFix = v),
    };

    // what Windower writes into the registry from a profile at every launch
    public static readonly IReadOnlySet<string> Windower = new HashSet<string>
    {
        Resolution, WindowMode, UiSize, Brightness, RenderSize, DistantTextures, EnhancedShading, Sound, BackgroundSound,
        HardwareMouse, PadBackground,
    };

    // Ashita fakes everything but these two, so they stay Windows' own
    public static readonly IReadOnlySet<string> Ashita = Table.Keys.Except([KeepAspect, LegacySettings]).ToHashSet();

    public static List<string> Changed(FfxiSettings edited, FfxiSettings original) =>
        Table.Where(kv => !kv.Value.Same(edited, original)).Select(kv => kv.Key).ToList();

    public static FfxiSettings With(FfxiSettings into, FfxiSettings from, IEnumerable<string> fields)
    {
        var s = into.Clone();
        foreach (var f in fields) Table[f].Copy(from, s);
        return s;
    }
}

public sealed class RegistryGameStore(PolRegion? region, Func<bool, RegistryKey?>? openKey = null) : IGameSettingsStore
{
    public string Key => $"registry|{region}";
    public string? Platform => null;
    public string? ProfileName => null;
    public string? ProfileDisplay => null;
    public bool SameShapeRenderSizes => false;
    public bool ReadOnly => false;

    private RegistryKey? Open(bool write) => openKey is not null ? openKey(write) : region is { } r ? FfxiConfig.Open(r, write) : null;

    // the game makes its settings on its first start
    public FfxiSettings Load()
    {
        using var k = Open(false);
        if (k is null)
            throw Shown.Mark(new InvalidOperationException(region is { } r && PolPaths.TryGetInstallFolder(r, PolPaths.FfxiProduct) != null
                ? "FINAL FANTASY XI is installed, but it has no saved settings yet. Open the FINAL FANTASY XI Config Tool once as an administrator so it saves them, then open these settings again."
                : "FINAL FANTASY XI isn't installed on this PC, so there are no game settings to change."));
        return FfxiConfig.LoadFrom(k);
    }

    public FfxiSettings LoadWindows()
    {
        using var k = Open(false);
        return k is null ? FfxiSettings.Defaults() : FfxiConfig.LoadFrom(k);
    }

    public GameSaveResult Save(FfxiSettings settings, FfxiSettings original)
    {
        using var k = Open(true) ?? throw Shown.Mark(new InvalidOperationException("Provenance couldn't open the FINAL FANTASY XI settings."));
        int n = FfxiConfig.SaveTo(k, settings, original);
        return new(n, n > 0);
    }
}

// a Windower profile's own values go to settings.xml, and Windower writes them into the registry at every launch. The
// rest are the registry's, shared by every profile
public sealed class WindowerGameStore(string windowerDir, string profileName, RegistryGameStore registry, string? display = null) : IGameSettingsStore
{
    public string Key => $"windower|{Path.GetFullPath(windowerDir)}|{profileName}|{registry.Key}";
    public string? Platform => ThirdPartyHookService.Windower;
    public string? ProfileName => profileName;
    public string? ProfileDisplay => display ?? (profileName.Length == 0 ? "Default" : profileName);
    public bool SameShapeRenderSizes => true;
    public bool ReadOnly => false;

    private WindowerProfile Profile(WindowerSettingsFile file) =>
        file.Find(profileName) ?? (profileName.Length == 0 ? file.Unnamed ?? new WindowerProfile() : null)
        ?? throw Shown.Mark(new InvalidOperationException($"The Windower profile {profileName} isn't in Windower's settings any more. Choose another one under Windower/Ashita."));

    // a link is refused here rather than only when Save gets to it
    public FfxiSettings Load()
    {
        if (FolderPins.Linked(windowerDir)) throw Shown.Mark(new IOException(ThirdPartyHookService.LinkedWindowerText));
        if (LinkCheck.IsRedirect(WindowerSettingsFile.PathIn(windowerDir)))
            throw Shown.Mark(new IOException("Windower's settings.xml is a link to somewhere else, so Provenance won't change it."));
        var windows = registry.Load();
        var file = WindowerSettingsFile.Load(windowerDir);
        if (file.LoadError is not null)
            throw Shown.Mark(new InvalidDataException("Windower's settings.xml couldn't be read, so these settings can't be changed until it's fixed or a new one is started under Windower/Ashita."));
        return Read(Profile(file), windows);
    }

    // exactly what Windower writes at launch, on top of what the registry has for everything else
    public static FfxiSettings Read(WindowerProfile p, FfxiSettings windows)
    {
        var s = windows.Clone();
        foreach (var v in WindowerGameRegistry.Values(p, p.Width, p.Height))
        {
            int n = v.Value is int i ? i : 0;
            // FFXI's own registry value names, mapped the same as FfxiConfig's
            switch (v.Name)
            {
                case "0000": s.DrawDistanceMip = n; break;
                case "0001": s.Width = n; break;
                case "0002": s.Height = n; break;
                case "0003": s.BgWidth = n; break;
                case "0004": s.BgHeight = n; break;
                case "0007": s.Sound = n; break;
                case "0017": s.EnhancedShading = n; break;
                case "0021": s.HardwareMouse = n; break;
                case "0028": s.Brightness = BitConverter.Int32BitsToSingle(n); break;
                case "0034": s.WindowMode = n; break;
                case "0035": s.BackgroundSound = n; break;
                case "0037": s.UiWidth = n; break;
                case "0038": s.UiHeight = n; break;
            }
        }
        s.PadMode = s.PadMode.Length >= 6 ? (int[])s.PadMode.Clone() : [.. s.PadMode, .. new int[6 - s.PadMode.Length]];
        s.PadMode[4] = p.AlwaysEnableGamepad ? 1 : 0;
        return s;
    }

    public GameSaveResult Save(FfxiSettings settings, FfxiSettings original)
    {
        var changed = GameFields.Changed(settings, original);
        var own = changed.Where(GameFields.Windower.Contains).ToList();
        // the 3D resolution on screen stays put, which is a new scale of a new resolution
        if (own.Contains(GameFields.Resolution) && !own.Contains(GameFields.RenderSize)) own.Add(GameFields.RenderSize);
        if (own.Count > 0) WindowerSettingsFile.Update(windowerDir, f => Write(Profile(f), settings, own));
        var shared = changed.Where(f => !GameFields.Windower.Contains(f)).ToList();
        int written = 0;
        // against the registry's own values, the page's are the profile's and would go in with them
        if (shared.Count > 0)
        {
            var windows = registry.LoadWindows();
            written = registry.Save(GameFields.With(windows, settings, shared), windows).Changed;
        }
        return new(own.Count + written, own.Count + written > 0);
    }

    public static void Write(WindowerProfile p, FfxiSettings s, IEnumerable<string> fields)
    {
        foreach (var f in fields)
            switch (f)
            {
                case GameFields.Resolution: (p.Width, p.Height) = (s.Width, s.Height); break;
                case GameFields.WindowMode:
                    p.WindowMode = s.WindowMode switch { 0 => WindowerWindowMode.Fullscreen, 1 => WindowerWindowMode.Window, _ => WindowerWindowMode.BorderlessWindow };
                    break;
                case GameFields.UiSize: p.UIScale = (float)FfxiSettings.MenuScale(s.Height, s.UiWidth, s.UiHeight); break;
                case GameFields.Brightness: p.Gamma = MathF.Round(s.Brightness, 1); break;
                // the list only offers sizes the same shape as the game's, the one thing a single scale can hold
                case GameFields.RenderSize: p.SuperSampling = s.Width > 0 ? (float)s.BgWidth / s.Width : 1f; break;
                case GameFields.DistantTextures: p.MipMapLevel = Math.Max(0, s.DrawDistanceMip); break;
                case GameFields.EnhancedShading: p.BumpMapping = s.EnhancedShading == 1; break;
                case GameFields.Sound: p.Sound = s.Sound == 1; break;
                case GameFields.BackgroundSound: p.AlwaysPlaySound = s.BackgroundSound == 1; break;
                case GameFields.HardwareMouse: p.HardwareMouse = s.HardwareMouse == 1; break;
                case GameFields.PadBackground: p.AlwaysEnableGamepad = s.PadMode.Length > 4 && s.PadMode[4] == 1; break;
            }
    }
}

// an Ashita boot configuration's [ffxi.registry], which Ashita hands the game in place of the registry's values
public sealed class AshitaGameStore(string ashitaDir, string bootName, RegistryGameStore registry, string? display = null) : IGameSettingsStore
{
    public string Key => $"ashita|{Path.GetFullPath(ashitaDir)}|{bootName}|{registry.Key}";
    public string? Platform => ThirdPartyHookService.Ashita;
    public string? ProfileName => bootName;
    public string? ProfileDisplay => display ?? bootName;
    public bool SameShapeRenderSizes => false;
    public bool ReadOnly => AshitaBootFiles.IsShippedExample(bootName);

    private string PathOf()
    {
        if (!ThirdPartyHookLauncher.IsBootFileName(bootName))
            throw Shown.Mark(new InvalidOperationException("Choose an Ashita boot configuration under Windower/Ashita first."));
        var path = Path.Combine(AshitaBootFiles.Folder(ashitaDir), bootName);
        if (!File.Exists(path))
            throw Shown.Mark(new FileNotFoundException($"The Ashita boot configuration {bootName} isn't in Ashita's config\\boot folder any more. Choose another one under Windower/Ashita."));
        return path;
    }

    // what the file leaves to Windows shows the registry's value
    public FfxiSettings Load()
    {
        var windows = registry.Load();
        var cfg = AshitaBootConfig.Load(PathOf());
        return cfg.ReadGame(windows);
    }

    // every value goes in, so nothing's left to a registry the game may never see (sandbox empties it)
    public GameSaveResult Save(FfxiSettings settings, FfxiSettings original)
    {
        var changed = GameFields.Changed(settings, original);
        var path = PathOf();
        var cfg = AshitaBootConfig.Load(path);
        cfg.WriteGame(settings);
        bool wrote = cfg.Ini.Changed;
        if (wrote) cfg.Save(path);
        int shared = 0;
        var registryOnly = changed.Where(f => !GameFields.Ashita.Contains(f)).ToList();
        if (registryOnly.Count > 0)
        {
            var windows = registry.LoadWindows();
            shared = registry.Save(GameFields.With(windows, settings, registryOnly), windows).Changed;
        }
        return new(changed.Count, wrote || shared > 0);
    }
}
