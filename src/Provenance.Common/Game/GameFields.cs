namespace Provenance.Common.Game;

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
