using System.Globalization;
using System.Xml.Linq;

namespace Provenance.Common.Game.Windower;

public enum WindowerRegion { Auto = -1, JP = 0, US = 1, EU = 2 }

public enum WindowerWindowMode { BorderlessWindow, Window, Fullscreen }

public sealed record WindowerKey(string Name, int ScanCode, string InternalName);

public static class WindowerKeys
{
    // settings.xml name, DirectInput scan code, the name the hook wants
    private static readonly (string Name, int Code, string Internal)[] Table =
    [
        ("Escape", 1, "escape"), ("Enter", 28, "enter"), ("Insert", 210, "insert"), ("Delete", 211, "delete"),
        ("Home", 199, "home"), ("End", 207, "end"), ("PageUp", 201, "pageup"), ("PageDown", 209, "pagedown"),
        ("LeftControl", 29, "lctrl"), ("LeftAlt", 56, "lalt"), ("LeftShift", 42, "lshift"), ("RightControl", 157, "rctrl"),
        ("RightAlt", 184, "ralt"), ("RightShift", 54, "rshift"), ("LeftWindows", 219, "lwindows"),
        ("RightWindows", 220, "rwindows"), ("AppMenu", 221, "apps"),
        ("F1", 59, "f1"), ("F2", 60, "f2"), ("F3", 61, "f3"), ("F4", 62, "f4"), ("F5", 63, "f5"), ("F6", 64, "f6"),
        ("F7", 65, "f7"), ("F8", 66, "f8"), ("F9", 67, "f9"), ("F10", 68, "f10"), ("F11", 87, "f11"), ("F12", 88, "f12"),
        ("F13", 100, "f13"), ("F14", 101, "f14"), ("F15", 102, "f15"),
        ("PrintScreen", 183, "sysrq"), ("ScrollLock", 70, "scrolllock"), ("Break", 197, "pause"),
        ("Up", 200, "up"), ("Down", 208, "down"), ("Left", 203, "left"), ("Right", 205, "right"), ("NumLock", 69, "numlock"),
        ("NumPad0", 82, "numpad0"), ("NumPad1", 79, "numpad1"), ("NumPad2", 80, "numpad2"), ("NumPad3", 81, "numpad3"),
        ("NumPad4", 75, "numpad4"), ("NumPad5", 76, "numpad5"), ("NumPad6", 77, "numpad6"), ("NumPad7", 71, "numpad7"),
        ("NumPad8", 72, "numpad8"), ("NumPad9", 73, "numpad9"), ("NumPadEnter", 156, "numpadenter"),
        ("NumPadPlus", 78, "numpad+"), ("NumPadMinus", 74, "numpad-"), ("NumPadMultiply", 55, "numpad*"),
        ("NumPadDivide", 181, "numpad/"), ("NumPadPeriod", 83, "numpad."),
        ("Tab", 15, "tab"), ("Backspace", 14, "backspace"), ("Space", 57, "space"),
        ("A", 30, "a"), ("B", 48, "b"), ("C", 46, "c"), ("D", 32, "d"), ("E", 18, "e"), ("F", 33, "f"), ("G", 34, "g"),
        ("H", 35, "h"), ("I", 23, "i"), ("J", 36, "j"), ("K", 37, "k"), ("L", 38, "l"), ("M", 50, "m"), ("N", 49, "n"),
        ("O", 24, "o"), ("P", 25, "p"), ("Q", 16, "q"), ("R", 19, "r"), ("S", 31, "s"), ("T", 20, "t"), ("U", 22, "u"),
        ("V", 47, "v"), ("W", 17, "w"), ("X", 45, "x"), ("Y", 21, "y"), ("Z", 44, "z"),
        ("Grave", 41, "`"), ("Number1", 2, "1"), ("Number2", 3, "2"), ("Number3", 4, "3"), ("Number4", 5, "4"),
        ("Number5", 6, "5"), ("Number6", 7, "6"), ("Number7", 8, "7"), ("Number8", 9, "8"), ("Number9", 10, "9"),
        ("Number0", 11, "0"), ("Hyphen", 12, "-"), ("Equals", 13, "="), ("LeftBracket", 26, "["), ("RightBracket", 27, "]"),
        ("Backslash", 43, "\\"), ("Semicolon", 39, ";"), ("Apostrophe", 40, "'"), ("Comma", 51, ","), ("Period", 52, "."),
        ("Slash", 53, "/"), ("CapsLock", 58, "capslock"),
        ("PlayPause", 162, "mmplaypause"), ("Stop", 164, "mmstop"), ("NextTrack", 153, "mmnexttrack"),
        ("PreviousTrack", 144, "mmprevtrack"), ("VolumeUp", 176, "mmvolup"), ("VolumeDown", 174, "mmvoldown"),
        ("Mute", 160, "mute"), ("MediaSelect", 237, "mmselect"), ("Power", 222, "power"), ("Sleep", 223, "sleep"),
        ("Wake", 227, "wake"), ("WebBack", 234, "webback"), ("WebForward", 233, "webforward"), ("WebStop", 232, "webstop"),
        ("WebRefresh", 231, "webrefresh"), ("WebHome", 178, "webhome"), ("WebSearch", 229, "websearch"),
        ("WebBookmarks", 230, "webfav"), ("Mail", 236, "webfav"), ("Computer", 235, "mycomputer"),
        ("Calculator", 161, "calculator"), ("Yen", 125, "yen"), ("Kana", 112, "kana"), ("Kanji", 148, "kanji"),
        ("Convert", 121, "convert"), ("NoConvert", 123, "noconvert"), ("Circumflex", 144, "circumflex"),
    ];

    public static IReadOnlyList<WindowerKey> All { get; } = Table.Select(t => new WindowerKey(t.Name, t.Code, t.Internal)).ToList();

    public static WindowerKey Insert { get; } = All.First(k => k.Name == "Insert");

    // PreviousTrack and Circumflex share 144 in Windower's enum, and .NET Framework names 144 Circumflex, so that's
    // what gets saved and sent (with its own internal name)
    private static readonly WindowerKey Circumflex = All.First(k => k.Name == "Circumflex");

    public static WindowerKey? Find(string? name) =>
        name is null ? null : All.FirstOrDefault(k => k.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } k ? Canonical(k) : null;

    public static WindowerKey Canonical(WindowerKey key) => key.ScanCode == 144 ? Circumflex : key;

    public static bool Pickable(WindowerKey key) =>
        key.Name is not ("LeftShift" or "RightShift" or "LeftControl" or "RightControl" or "LeftAlt" or "RightAlt"
            or "LeftWindows" or "RightWindows" or "AppMenu");
}

public sealed class WindowerProfile
{
    public const string DefaultExecutable = "pol.exe";

    public string Name { get; internal set; } = "";
    public WindowerRegion Region { get; set; } = WindowerRegion.Auto;
    public WindowerKey ConsoleKey { get; set => field = WindowerKeys.Canonical(value ?? WindowerKeys.Insert); } = WindowerKeys.Insert;
    public string? InitCommand { get; set; }
    public WindowerWindowMode WindowMode { get; set; } = WindowerWindowMode.BorderlessWindow;
    public int Width { get; set; }
    public int Height { get; set; }
    public int MipMapLevel { get; set; }
    public float Gamma { get; set; }
    public float UIScale { get; set; } = 1f;
    public bool HardwareMouse { get; set; } = true;
    public float SuperSampling { get => field <= 0f ? 1f : field; set; } = 1f;
    public bool BumpMapping { get; set; }
    public bool Sound { get; set; } = true;
    public bool AlwaysPlaySound { get; set; } = true;
    public bool AlwaysEnableGamepad { get; set; } = true;
    public bool AllowWinKey { get; set; }
    public bool IncreaseAccountLimit { get; set; }
    public bool EnableFastLogin { get; set; }
    public bool PreventBackgroundThrottling { get; set; }
    public string Args { get; set => field = value ?? ""; } = "";
    public string Executable { get; set => field = value ?? DefaultExecutable; } = DefaultExecutable;

    // anything Windower doesn't know about, kept so a save here doesn't lose it (Windower's own save still will)
    public List<XElement> UnknownElements { get; private set; } = [];
    public List<XAttribute> UnknownAttributes { get; private set; } = [];

    public WindowerProfile Clone()
    {
        var c = (WindowerProfile)MemberwiseClone();
        c.UnknownElements = UnknownElements.Select(e => new XElement(e)).ToList();
        c.UnknownAttributes = UnknownAttributes.Select(a => new XAttribute(a)).ToList();
        return c;
    }

    private static readonly HashSet<string> Known =
    [
        "region", "consolekey", "initcommand", "windowmode", "width", "height", "mipmaplevel", "gamma", "uiscale",
        "hardwaremouse", "supersampling", "bumpmapping", "sound", "alwaysplaysound", "alwaysenablegamepad", "allowwinkey",
        "increaseaccountlimit", "enablefastlogin", "preventbackgroundthrottling", "args", "executable",
    ];

    internal static WindowerProfile FromXml(XElement e)
    {
        string? S(string n) => (string?)e.Element(n);
        var p = new WindowerProfile
        {
            Name = ((string?)e.Attribute("name") ?? "").Trim(),
            Region = Parse.Enum(S("region"), WindowerRegion.Auto),
            ConsoleKey = WindowerKeys.Find(S("consolekey")) ?? WindowerKeys.Insert,
            InitCommand = S("initcommand"),
            WindowMode = Parse.Enum(S("windowmode"), WindowerWindowMode.BorderlessWindow),
            Width = Parse.Int(S("width"), 0),
            Height = Parse.Int(S("height"), 0),
            MipMapLevel = Parse.Int(S("mipmaplevel"), 0),
            Gamma = Parse.Float(S("gamma"), 0f),
            UIScale = Parse.Float(S("uiscale"), 1f),
            HardwareMouse = Parse.Bool(S("hardwaremouse"), true),
            SuperSampling = Parse.Float(S("supersampling"), 1f),
            BumpMapping = Parse.Bool(S("bumpmapping"), false),
            Sound = Parse.Bool(S("sound"), true),
            AlwaysPlaySound = Parse.Bool(S("alwaysplaysound"), true),
            AlwaysEnableGamepad = Parse.Bool(S("alwaysenablegamepad"), true),
            AllowWinKey = Parse.Bool(S("allowwinkey"), false),
            IncreaseAccountLimit = Parse.Bool(S("increaseaccountlimit"), false),
            EnableFastLogin = Parse.Bool(S("enablefastlogin"), false),
            PreventBackgroundThrottling = Parse.Bool(S("preventbackgroundthrottling"), false),
            Executable = S("executable") ?? DefaultExecutable,
            Args = S("args") ?? "",
        };
        foreach (var child in e.Elements())
            if (!Known.Contains(child.Name.LocalName) || child.Name.Namespace != XNamespace.None)
                p.UnknownElements.Add(new XElement(child));
        foreach (var a in e.Attributes())
            if (a.Name != "name" && !a.IsNamespaceDeclaration) p.UnknownAttributes.Add(new XAttribute(a));
        return p;
    }

    internal XElement ToXml()
    {
        var e = new XElement("profile", new XAttribute("name", Name));
        void Add(bool when, string name, string value) { if (when) e.Add(new XElement(name, value)); }
        Add(Region != WindowerRegion.Auto, "region", Region.ToString());
        Add(true, "consolekey", ConsoleKey.Name);
        Add(InitCommand is not null, "initcommand", InitCommand!);
        Add(WindowMode != WindowerWindowMode.BorderlessWindow, "windowmode", WindowMode.ToString());
        bool sized = Width > 0 && Height > 0;
        Add(sized, "width", Width.ToString(CultureInfo.InvariantCulture));
        Add(sized, "height", Height.ToString(CultureInfo.InvariantCulture));
        Add(MipMapLevel > 0, "mipmaplevel", MipMapLevel.ToString(CultureInfo.InvariantCulture));
        // written as Windower compares them, NaN included
        Add(Gamma != 0f, "gamma", FormatFloat(Gamma));
        Add(!(UIScale <= 0f), "uiscale", FormatFloat(UIScale));
        Add(!HardwareMouse, "hardwaremouse", "false");
        Add(SuperSampling != 1f, "supersampling", FormatFloat(SuperSampling));
        Add(BumpMapping, "bumpmapping", "true");
        Add(!Sound, "sound", "false");
        Add(!AlwaysPlaySound, "alwaysplaysound", "false");
        Add(!AlwaysEnableGamepad, "alwaysenablegamepad", "false");
        Add(AllowWinKey, "allowwinkey", "true");
        Add(IncreaseAccountLimit, "increaseaccountlimit", "true");
        Add(EnableFastLogin, "enablefastlogin", "true");
        Add(PreventBackgroundThrottling, "preventbackgroundthrottling", "true");
        Add(Args.Length > 0, "args", Args);
        Add(Executable != DefaultExecutable, "executable", Executable);
        foreach (var a in UnknownAttributes) e.Add(new XAttribute(a));
        foreach (var u in UnknownElements) e.Add(new XElement(u));
        return e;
    }

    // .NET Framework's float.ToString(): 7 significant digits, rounded half up where .NET now goes to even
    // (2371148.5 is 2371149 there, 2371148 here), and -0 comes out as 0
    public static string FormatFloat(float v)
    {
        if (v == 0f) return "0";
        if (!float.IsFinite(v)) return v.ToString(CultureInfo.InvariantCulture);
        var e = ((double)v).ToString("E30", CultureInfo.InvariantCulture);
        bool negative = e[0] == '-';
        var digits = e.Substring(negative ? 1 : 0, 32).Remove(1, 1);
        int exponent = int.Parse(e.AsSpan(e.IndexOf('E') + 1), CultureInfo.InvariantCulture);
        long seven = long.Parse(digits.AsSpan(0, 7), CultureInfo.InvariantCulture) + (digits[7] >= '5' ? 1 : 0);
        if (seven == 10_000_000) { seven = 1_000_000; exponent++; }
        var s = seven.ToString(CultureInfo.InvariantCulture);
        var rounded = double.Parse($"{(negative ? "-" : "")}{s[0]}.{s[1..]}E{exponent}", CultureInfo.InvariantCulture);
        return rounded.ToString("G7", CultureInfo.InvariantCulture);
    }

    internal static class Parse
    {
        public static T Enum<T>(string? s, T def) where T : struct, System.Enum =>
            System.Enum.GetNames<T>().FirstOrDefault(n => n.Equals(s, StringComparison.OrdinalIgnoreCase)) is { } name
                ? System.Enum.Parse<T>(name) : def;

        public static int Int(string? s, int def) => int.TryParse(s, out var v) ? v : def;

        public static bool Bool(string? s, bool def) => bool.TryParse(s, out var v) ? v : def;

        // .NET Framework parsed a float as a double and refused anything out of range, where .NET now gives infinity.
        // The spelt-out symbols are the only way in for those
        public static float Float(string? s, float def)
        {
            if (s is null) return def;
            var t = s.Trim();
            var inv = NumberFormatInfo.InvariantInfo;
            if (t == inv.PositiveInfinitySymbol) return float.PositiveInfinity;
            if (t == inv.NegativeInfinitySymbol) return float.NegativeInfinity;
            if (t == inv.NaNSymbol) return float.NaN;
            if (!double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d)) return def;
            var f = (float)d;
            return float.IsInfinity(f) ? def : f;
        }
    }
}
