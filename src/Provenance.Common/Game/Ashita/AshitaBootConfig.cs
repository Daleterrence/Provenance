using System.Globalization;

namespace Provenance.Common.Game.Ashita;

public readonly record struct AshitaPolPlugin(string Name, bool On);

public sealed class AshitaBootConfig(AshitaIniFile ini)
{
    private const string Launcher = "ashita.launcher", Boot = "ashita.boot", PolPluginSection = "ashita.polplugins", Registry = "ffxi.registry";

    internal static readonly IReadOnlyList<string> GameValues =
        [.. FfxiValues.All.Where(v => v.Ashita && v is not OpaqueValue).Select(v => v.Name)];

    // the ones that come out blank in a file that doesn't set them
    private static readonly IReadOnlyList<string> TextValues =
        [.. FfxiValues.All.Where(v => v.Ashita && v is TextValue or PadValue or OpaqueValue).Select(v => v.Name)];

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

    private static float InRange(float brightness) => float.IsFinite(brightness) ? Math.Clamp(brightness, -1f, 1f) : 0f;

    public FfxiSettings ReadGame(FfxiSettings windows)
    {
        var s = windows.Clone();
        foreach (var v in FfxiValues.All.Where(v => v.Ashita))
        {
            var text = Ini.Get(Registry, v.Name);
            switch (v)
            {
                case BrightnessValue b when text is not null && ToInt(text) != -1:
                    b.Set(s, BitConverter.SingleToInt32Bits(InRange(BitConverter.Int32BitsToSingle(ToInt(text)))));
                    break;
                case IntValue i when text is not null && ToInt(text) != -1:
                    i.Set(s, i.AshitaClamp(ToInt(text)));
                    break;
                // the registry's when it starts -1, but "-1,..." is a pad value like any other. An empty one shows the
                // registry's too, Warnings flags it
                case TextValue or PadValue when !string.IsNullOrEmpty(text)
                    && (!text.StartsWith("-1", StringComparison.Ordinal) || text.StartsWith("-1,", StringComparison.Ordinal)):
                    if (v is TextValue t) t.Set(s, text);
                    else s.PadMode = Pad(text, windows.PadMode);
                    break;
            }
        }
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
        foreach (var v in FfxiValues.All.Where(v => v.Ashita))
            switch (v)
            {
                // the float's bits as a signed int. They're never -1 (that's a NaN), so it can't read as unset
                case BrightnessValue:
                    SetInt(Registry, v.Name, BitConverter.SingleToInt32Bits(InRange(s.Brightness)));
                    break;
                case IntValue i:
                    // an odd number already in the file stays unless the value itself changed
                    int now = i.AshitaClamp(i.Get(s));
                    if (i.KeepsStored && Ini.Get(Registry, i.Name) is { } was && ToInt(was) != -1 && i.Range(ToInt(was)) == now) break;
                    SetInt(Registry, i.Name, now);
                    break;
                case TextValue:
                    SetText(Registry, v.Name, shots);
                    break;
                case PadValue:
                    SetText(Registry, v.Name, s.PadMode is { Length: > 0 } pad ? string.Join(",", pad) : "-1");
                    break;
                case OpaqueValue when string.IsNullOrEmpty(Ini.Get(Registry, v.Name)):
                    SetText(Registry, v.Name, "-1");
                    break;
            }
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
