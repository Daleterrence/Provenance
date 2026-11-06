using System.Globalization;

namespace Provenance.Common.Game.Ashita;

public readonly record struct AshitaPolPlugin(string Name, bool On);

public sealed class AshitaBootConfig(AshitaIniFile ini)
{
    private const string Launcher = "ashita.launcher", Boot = "ashita.boot", PolPluginSection = "ashita.polplugins", Registry = "ffxi.registry";

    // 0044 and 0045 aren't among the values Ashita fakes, so keep aspect and legacy settings stay registry only
    internal static readonly IReadOnlyList<string> GameValues =
    [
        "0000", "0001", "0002", "0003", "0004", "0007", "0011", "0017", "0018", "0019", "0021", "0022", "0023", "0028",
        "0029", "0034", "0035", "0036", "0037", "0038", "0040", "0042", "0043", "padmode000",
    ];

    private static readonly string[] TextValues = ["0042", "padexsin000", "padguid000", "padmode000", "padsin000"];

    public AshitaIniFile Ini { get; } = ini;

    public static AshitaBootConfig Load(string path) => new(AshitaIniFile.Load(path));

    public void Save(string path) => Ini.Save(path);

    public IReadOnlyList<string> Warnings
    {
        get
        {
            var w = Ini.Warnings.ToList();
            var blank = TextValues.Where(n => string.IsNullOrEmpty(Ini.Get(Registry, n))).ToList();
            if (blank.Count > 0)
                w.Add($"[ffxi.registry] doesn't set {string.Join(", ", blank)}, so Ashita gives the game empty values for "
                      + "them instead of your Windows settings. Saving this profile's game settings fills them in.");
            return w;
        }
    }

    public string? Name { get => Ini.Get(Launcher, "name"); set => SetText(Launcher, "name", value); }
    public string? Script { get => Ini.Get(Boot, "script"); set => SetText(Boot, "script", value); }
    public string? ScriptArgs { get => Ini.Get(Boot, "args"); set => SetText(Boot, "args", value); }

    // Ashita.dll only loads one whose value is exactly 1
    public IReadOnlyList<AshitaPolPlugin> PolPlugins =>
        Ini.Entries(PolPluginSection).Where(e => !e.Key.StartsWith('#'))
           .DistinctBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
           .Select(e => new AshitaPolPlugin(e.Key, Ini.Get(PolPluginSection, e.Key) == "1")).ToList();

    // it ends up as polplugins\<name>.dll
    public void SetPolPlugin(string name, bool on)
    {
        if (name.Length == 0 || name[0] == '.' || name.Contains("..") || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
            throw Shown.Mark(new ArgumentException($"Ashita can't load a PlayOnline plugin called \"{name}\"."));
        Ini.Set(PolPluginSection, name, on ? "1" : "0");
    }

    private static int InRange(string name, int v) => name switch
    {
        "0000" => Math.Clamp(v, 0, 6),
        "0011" or "0018" or "0036" => Math.Clamp(v, 0, 2),
        "0019" => Math.Clamp(v, 0, 1),
        "0029" => Math.Clamp(v, 12, 20),
        "0034" => FfxiSettings.KnownWindowMode(v),
        _ => v,
    };

    private static float InRange(float brightness) => float.IsFinite(brightness) ? Math.Clamp(brightness, -1f, 1f) : 0f;

    public FfxiSettings ReadGame(FfxiSettings windows)
    {
        var s = windows.Clone();
        void ReadInt(string name, Action<int> apply)
        {
            var v = Ini.Get(Registry, name);
            if (v is not null && ToInt(v) != -1) apply(InRange(name, ToInt(v)));
        }
        // the registry's when it starts -1, but "-1,..." is a pad value like any other. An empty one shows the registry's
        // too, Warnings flags it
        void ReadText(string name, Action<string> apply)
        {
            var v = Ini.Get(Registry, name);
            if (!string.IsNullOrEmpty(v) && (!v.StartsWith("-1", StringComparison.Ordinal) || v.StartsWith("-1,", StringComparison.Ordinal))) apply(v);
        }
        // a mip level 0-6 here, not FfxiSettings' on/off
        ReadInt("0000", v => s.DrawDistanceMip = v);
        ReadInt("0001", v => s.Width = v);
        ReadInt("0002", v => s.Height = v);
        ReadInt("0003", v => s.BgWidth = v);
        ReadInt("0004", v => s.BgHeight = v);
        ReadInt("0007", v => s.Sound = v);
        ReadInt("0011", v => s.Vegetation = v);
        ReadInt("0017", v => s.EnhancedShading = v);
        ReadInt("0018", v => s.Textures = v);
        ReadInt("0019", v => s.Maps2D = v);
        ReadInt("0021", v => s.HardwareMouse = v);
        ReadInt("0022", v => s.OpeningMovie = v);
        ReadInt("0023", v => s.SimpleCharSelect = v);
        ReadInt("0028", v => s.Brightness = InRange(BitConverter.Int32BitsToSingle(v)));
        ReadInt("0029", v => s.SoundEffects = v);
        ReadInt("0034", v => s.WindowMode = v);
        ReadInt("0035", v => s.BackgroundSound = v);
        ReadInt("0036", v => s.Fonts = v);
        ReadInt("0037", v => s.UiWidth = v);
        ReadInt("0038", v => s.UiHeight = v);
        ReadInt("0040", v => s.GraphicsFix = v);
        ReadInt("0043", v => s.FullResScreenshots = v);
        ReadText("0042", v => s.ScreenshotFolder = v);
        ReadText("padmode000", v => s.PadMode = Pad(v, windows.PadMode));
        return s;
    }

    private static int[] Pad(string v, int[] fallback)
    {
        var parts = v.Split(',');
        var pad = new int[6];
        for (int i = 0; i < pad.Length; i++)
            pad[i] = i < parts.Length && int.TryParse(parts[i].Trim(), out var x) ? x : i < fallback.Length ? fallback[i] : 0;
        return pad;
    }

    // every value written out, and the pad values Provenance doesn't edit get -1 so the game never gets them blank
    public void WriteGame(FfxiSettings s)
    {
        var shots = string.IsNullOrEmpty(s.ScreenshotFolder) ? "-1" : s.ScreenshotFolder;
        if (Ini.ValueProblem(Registry, "0042", shots) is { } problem) throw Shown.Mark(new ArgumentException(problem));
        void Write(string name, int v) => SetInt(Registry, name, InRange(name, v));
        Write("0000", s.DrawDistanceMip);
        Write("0001", s.Width);
        Write("0002", s.Height);
        Write("0003", s.BgWidth);
        Write("0004", s.BgHeight);
        Write("0007", s.Sound);
        Write("0011", s.Vegetation);
        Write("0017", s.EnhancedShading);
        Write("0018", s.Textures);
        Write("0019", s.Maps2D);
        Write("0021", s.HardwareMouse);
        Write("0022", s.OpeningMovie);
        Write("0023", s.SimpleCharSelect);
        // the float's bits as a signed int. They're never -1 (that's a NaN), so it can't read as unset
        Write("0028", BitConverter.SingleToInt32Bits(InRange(s.Brightness)));
        Write("0029", s.SoundEffects);
        // an odd number already in the file stays unless the window mode itself changed
        var mode = FfxiSettings.KnownWindowMode(s.WindowMode);
        if (Ini.Get(Registry, "0034") is not { } was || ToInt(was) == -1 || FfxiSettings.KnownWindowMode(ToInt(was)) != mode)
            Write("0034", mode);
        Write("0035", s.BackgroundSound);
        Write("0036", s.Fonts);
        Write("0037", s.UiWidth);
        Write("0038", s.UiHeight);
        Write("0040", s.GraphicsFix);
        SetText(Registry, "0042", shots);
        Write("0043", s.FullResScreenshots);
        foreach (var n in TextValues.Skip(1))
            if (n == "padmode000") SetText(Registry, n, s.PadMode is { Length: > 0 } pad ? string.Join(",", pad) : "-1");
            else if (string.IsNullOrEmpty(Ini.Get(Registry, n))) SetText(Registry, n, "-1");
    }

    private void SetText(string section, string key, string? value)
    {
        if (value is null) Ini.Remove(section, key);
        else Ini.Set(section, key, value);
    }

    // a value that already reads as this number keeps its own text, so nothing changes that doesn't have to
    private void SetInt(string section, string key, int value)
    {
        var old = Ini.Get(section, key);
        SetText(section, key, old is not null && ToInt(old) == value ? old : value.ToString(CultureInfo.InvariantCulture));
    }

    // strtoll(v, 0, 0) cut to 32 bits like Ashita.dll, so 010 is 8 (because of course it is) and "" is 0
    internal static int ToInt(string v) => unchecked((int)Number(v));

    private static long Number(string v)
    {
        int i = 0;
        while (i < v.Length && v[i] is ' ' or '\t' or '\r' or '\n' or '\v' or '\f') i++;
        bool negative = false;
        if (i < v.Length && v[i] is '+' or '-') negative = v[i++] == '-';
        int radix = 10;
        if (i + 2 < v.Length && v[i] == '0' && v[i + 1] is 'x' or 'X' && char.IsAsciiHexDigit(v[i + 2]))
        {
            radix = 16;
            i += 2;
        }
        else if (i < v.Length && v[i] == '0') radix = 8;
        ulong n = 0;
        bool over = false;
        for (; i < v.Length; i++)
        {
            int d = char.IsAsciiDigit(v[i]) ? v[i] - '0' : char.IsAsciiLetter(v[i]) ? (v[i] | 0x20) - 'a' + 10 : 99;
            if (d >= radix) break;
            if (n > (ulong.MaxValue - (ulong)d) / (ulong)radix) over = true;
            else n = n * (ulong)radix + (ulong)d;
        }
        if (negative) return over || n > 1UL << 63 ? long.MinValue : unchecked((long)(0 - n));
        return over || n > long.MaxValue ? long.MaxValue : (long)n;
    }
}
