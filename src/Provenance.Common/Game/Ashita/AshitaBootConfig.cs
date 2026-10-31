using System.Globalization;

namespace Provenance.Common.Game.Ashita;

public readonly record struct AshitaPolPlugin(string Name, bool On);

// Blank is the text values the file leaves out or empty, which Ashita hands the game as empty strings
public sealed record AshitaGameValues(FfxiSettings Settings, IReadOnlySet<string> Own, IReadOnlySet<string> Blank);

public sealed class AshitaBootConfig(AshitaIniFile ini)
{
    private const string Launcher = "ashita.launcher", Boot = "ashita.boot", Language = "ashita.language",
                         PolPluginSection = "ashita.polplugins", Misc = "ashita.misc", Registry = "ffxi.registry";

    // 0044 and 0045 aren't among the values Ashita fakes, so keep aspect and legacy settings stay registry only
    public static readonly IReadOnlyList<string> GameValues =
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
    public bool? AutoClose { get => Bool(Launcher, "autoclose"); set => SetBool(Launcher, "autoclose", value); }

    public string? LaunchFile { get => Ini.Get(Boot, "file"); set => SetText(Boot, "file", value); }
    public string? Command { get => Ini.Get(Boot, "command"); set => SetText(Boot, "command", value); }
    public string? GameModule { get => Ini.Get(Boot, "gamemodule"); set => SetText(Boot, "gamemodule", value); }
    public string? Script { get => Ini.Get(Boot, "script"); set => SetText(Boot, "script", value); }
    public string? ScriptArgs { get => Ini.Get(Boot, "args"); set => SetText(Boot, "args", value); }

    // 0 and 2 English, 1 Japanese, 3 European
    public int? PlayOnlineLanguage { get => Int(Language, "playonline"); set => SetInt(Language, "playonline", value); }
    public int? AshitaLanguage { get => Int(Language, "ashita"); set => SetInt(Language, "ashita", value); }

    public bool? AddonsSilent { get => Bool(Misc, "addons.silent"); set => SetBool(Misc, "addons.silent", value); }
    public bool? AliasesSilent { get => Bool(Misc, "aliases.silent"); set => SetBool(Misc, "aliases.silent", value); }
    public bool? PluginsSilent { get => Bool(Misc, "plugins.silent"); set => SetBool(Misc, "plugins.silent", value); }

    // Ashita.dll only loads one whose value is exactly 1
    public IReadOnlyList<AshitaPolPlugin> PolPlugins =>
        Ini.Entries(PolPluginSection).Where(e => !e.Key.StartsWith('#'))
           .DistinctBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
           .Select(e => new AshitaPolPlugin(e.Key, Ini.Get(PolPluginSection, e.Key) == "1")).ToList();

    public void SetPolPlugin(string name, bool on)
    {
        CheckPolPlugin(name);
        Ini.Set(PolPluginSection, name, on ? "1" : "0");
    }

    public bool RemovePolPlugin(string name)
    {
        CheckPolPlugin(name);
        return Ini.Remove(PolPluginSection, name) > 0;
    }

    // it ends up as polplugins\<name>.dll
    private static void CheckPolPlugin(string name)
    {
        if (name.Length == 0 || name[0] == '.' || name.Contains("..") || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
            throw Shown.Mark(new ArgumentException($"\"{name}\" isn't a polplugin name Ashita can load."));
    }

    private static int InRange(string name, int v) => name switch
    {
        "0000" => Math.Clamp(v, 0, 6),
        "0011" or "0018" or "0036" => Math.Clamp(v, 0, 2),
        "0019" => Math.Clamp(v, 0, 1),
        "0029" => Math.Clamp(v, 12, 20),
        "0034" => Math.Clamp(v, 0, 3),
        _ => v,
    };

    private static float InRange(float brightness) => float.IsFinite(brightness) ? Math.Clamp(brightness, -1f, 1f) : 0f;

    public AshitaGameValues ReadGame(FfxiSettings windows)
    {
        var s = windows.Clone();
        var own = new HashSet<string>();
        var blank = new HashSet<string>();
        void ReadInt(string name, Action<int> apply)
        {
            var v = Ini.Get(Registry, name);
            if (v is null || ToInt(v) == -1) return;
            apply(InRange(name, ToInt(v)));
            own.Add(name);
        }
        // the registry's when it starts -1, but "-1,..." is a pad value like any other
        void ReadText(string name, Action<string>? apply)
        {
            var v = Ini.Get(Registry, name);
            if (string.IsNullOrEmpty(v)) blank.Add(name);
            else if (!v.StartsWith("-1", StringComparison.Ordinal) || v.StartsWith("-1,", StringComparison.Ordinal))
            {
                apply?.Invoke(v);
                own.Add(name);
            }
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
        foreach (var n in TextValues.Except(["0042", "padmode000"])) ReadText(n, null);
        return new AshitaGameValues(s, own, blank);
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
        Write("0034", s.WindowMode);
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

    private int? Int(string section, string key) => Ini.Get(section, key) is { } v ? ToInt(v) : null;

    private bool? Bool(string section, string key) => Ini.Get(section, key) is { } v ? ToBool(v) : null;

    // a value that already reads as this number keeps its own text, so nothing changes that doesn't have to
    private void SetInt(string section, string key, int? value)
    {
        var old = Ini.Get(section, key);
        SetText(section, key, value is not { } n ? null : old is not null && ToInt(old) == n ? old : n.ToString(CultureInfo.InvariantCulture));
    }

    private void SetBool(string section, string key, bool? value)
    {
        var old = Ini.Get(section, key);
        SetText(section, key, value is not { } b ? null : old is not null && ToBool(old) == b ? old : b ? "1" : "0");
    }

    // strtoll(v, 0, 0) cut to 32 bits like Ashita.dll, so 010 is 8 (because of course it is) and "" is 0
    public static int ToInt(string v) => unchecked((int)Number(v, true));

    public static bool ToBool(string v) =>
        v.Equals("true", StringComparison.OrdinalIgnoreCase)
        || (!v.Equals("false", StringComparison.OrdinalIgnoreCase) && Math.Clamp(Number(v, false), int.MinValue, int.MaxValue) > 0);

    private static long Number(string v, bool anyBase)
    {
        int i = 0;
        while (i < v.Length && v[i] is ' ' or '\t' or '\r' or '\n' or '\v' or '\f') i++;
        bool negative = false;
        if (i < v.Length && v[i] is '+' or '-') negative = v[i++] == '-';
        int radix = 10;
        if (anyBase && i + 2 < v.Length && v[i] == '0' && v[i + 1] is 'x' or 'X' && char.IsAsciiHexDigit(v[i + 2]))
        {
            radix = 16;
            i += 2;
        }
        else if (anyBase && i < v.Length && v[i] == '0') radix = 8;
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
