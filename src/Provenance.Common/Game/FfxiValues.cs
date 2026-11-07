namespace Provenance.Common.Game;

public abstract class FfxiValue
{
    public required string Name { get; init; }
    public bool Ashita { get; init; }
    public bool Windower { get; init; }
}

// a DWORD behind a field of FfxiSettings
public class IntValue : FfxiValue
{
    public required Func<FfxiSettings, int> Get { get; init; }
    public required Action<FfxiSettings, int> Set { get; init; }
    public Func<int, int> Range { get; init; } = v => v;
    public Func<int, int>? AshitaRange { get; init; }
    // set on the first of a pair, the two always go in together
    public string? Partner { get; init; }
    // a stored number outside Range reads as Range's answer
    public bool ReadsInRange { get; init; }
    // and stays as it is on a save that doesn't change the value
    public bool KeepsStored { get; init; }

    public int AshitaClamp(int v) => (AshitaRange ?? Range)(v);
}

// a float's bits in a DWORD
public sealed class BrightnessValue : IntValue;

// no setting behind it, the tool's default is all it gets
public sealed class FixedValue : FfxiValue
{
    public required int Value { get; init; }
}

public sealed class TextValue : FfxiValue
{
    public required Func<FfxiSettings, string> Get { get; init; }
    public required Action<FfxiSettings, string> Set { get; init; }
}

// padmode000, six numbers in a string
public sealed class PadValue : FfxiValue;

// not ours to edit, Ashita gives the game a blank for it
public sealed class OpaqueValue : FfxiValue;

// FFXI's numbered registry values, once. 0001/0002, 0003/0004 and 0037/0038 are pairs, 0028 is float bits. 0030
// (3D LCD) and 0041 are legacy-tool only, the rest of the Fixed ones have no UI. All get the tool's defaults
// (0031 is 0.006f). 0044 and 0045 aren't among the values Ashita fakes, so they stay registry only
public static class FfxiValues
{
    private static Func<int, int> Clamp(int min, int max) => v => Math.Clamp(v, min, max);

    private static IntValue Int(string name, Func<FfxiSettings, int> get, Action<FfxiSettings, int> set, Func<int, int>? range = null,
        bool windower = false, bool ashita = true, string? partner = null, Func<int, int>? ashitaRange = null,
        bool readsInRange = false, bool keepsStored = false) =>
        new()
        {
            Name = name, Get = get, Set = set, Range = range ?? (v => v), AshitaRange = ashitaRange, Ashita = ashita, Windower = windower,
            Partner = partner, ReadsInRange = readsInRange, KeepsStored = keepsStored,
        };

    private static FixedValue Fixed(string name, int value) => new() { Name = name, Value = value };

    public static readonly IReadOnlyList<FfxiValue> All =
    [
        // a mip level 0-6 in Ashita, not FfxiSettings' on/off
        Int("0000", s => s.DrawDistanceMip, (s, v) => s.DrawDistanceMip = v, windower: true, ashitaRange: Clamp(0, 6)),
        Int("0001", s => s.Width, (s, v) => s.Width = v, windower: true, partner: "0002"),
        Int("0002", s => s.Height, (s, v) => s.Height = v, windower: true),
        Int("0003", s => s.BgWidth, (s, v) => s.BgWidth = v, windower: true, partner: "0004"),
        Int("0004", s => s.BgHeight, (s, v) => s.BgHeight = v, windower: true),
        Int("0007", s => s.Sound, (s, v) => s.Sound = v, windower: true),
        Int("0011", s => s.Vegetation, (s, v) => s.Vegetation = v, Clamp(0, 2)),
        Int("0017", s => s.EnhancedShading, (s, v) => s.EnhancedShading = v, windower: true),
        Int("0018", s => s.Textures, (s, v) => s.Textures = v, Clamp(0, 2)),
        Int("0019", s => s.Maps2D, (s, v) => s.Maps2D = v, Clamp(0, 1)),
        Fixed("0020", 1),
        Int("0021", s => s.HardwareMouse, (s, v) => s.HardwareMouse = v, windower: true),
        Int("0022", s => s.OpeningMovie, (s, v) => s.OpeningMovie = v),
        Int("0023", s => s.SimpleCharSelect, (s, v) => s.SimpleCharSelect = v),
        Fixed("0024", 0),
        new BrightnessValue
        {
            Name = "0028", Ashita = true, Windower = true,
            Get = s => BitConverter.SingleToInt32Bits(s.Brightness), Set = (s, v) => s.Brightness = BitConverter.Int32BitsToSingle(v),
        },
        Int("0029", s => s.SoundEffects, (s, v) => s.SoundEffects = v, Clamp(12, 20)),
        Fixed("0030", 0),
        Fixed("0031", 0x3BC49BA6),
        Fixed("0032", 0),
        Fixed("0033", 0),
        Int("0034", s => s.WindowMode, (s, v) => s.WindowMode = v, FfxiSettings.KnownWindowMode, windower: true,
            readsInRange: true, keepsStored: true),
        Int("0035", s => s.BackgroundSound, (s, v) => s.BackgroundSound = v, windower: true),
        Int("0036", s => s.Fonts, (s, v) => s.Fonts = v, Clamp(0, 2)),
        Int("0037", s => s.UiWidth, (s, v) => s.UiWidth = v, windower: true, partner: "0038"),
        Int("0038", s => s.UiHeight, (s, v) => s.UiHeight = v, windower: true),
        Fixed("0039", 1),
        Int("0040", s => s.GraphicsFix, (s, v) => s.GraphicsFix = v),
        Fixed("0041", 0),
        new TextValue { Name = "0042", Get = s => s.ScreenshotFolder, Set = (s, v) => s.ScreenshotFolder = v, Ashita = true },
        Int("0043", s => s.FullResScreenshots, (s, v) => s.FullResScreenshots = v),
        Int("0044", s => s.KeepAspect, (s, v) => s.KeepAspect = v, ashita: false),
        Int("0045", s => s.LegacySettings, (s, v) => s.LegacySettings = v, ashita: false),
        new OpaqueValue { Name = "padexsin000", Ashita = true },
        new OpaqueValue { Name = "padguid000", Ashita = true },
        new PadValue { Name = "padmode000", Ashita = true, Windower = true },
        new OpaqueValue { Name = "padsin000", Ashita = true },
    ];

    public static FfxiValue Find(string name) => All.First(v => v.Name == name);

    // the second of a pair is the one another value names as its partner
    public static bool IsSecond(IntValue v) => All.OfType<IntValue>().Any(i => i.Partner == v.Name);

    public static IntValue? Partner(IntValue v) => v.Partner is { } p ? (IntValue)Find(p) : null;
}
