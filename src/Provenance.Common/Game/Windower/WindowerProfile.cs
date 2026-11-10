using System.Globalization;
using System.Xml.Linq;
using Provenance.Common.PolFormat;

namespace Provenance.Common.Game.Windower;

public enum WindowerWindowMode { BorderlessWindow, Window, Fullscreen }

public sealed class WindowerProfile
{
    public const string DefaultExecutable = "pol.exe";

    public string Name { get; internal set; } = "";
    public PolRegion? Region { get; set; }
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

    private static readonly (PolRegion Region, string Name)[] RegionNames =
        [(PolRegion.Japan, "JP"), (PolRegion.UnitedStates, "US"), (PolRegion.Europe, "EU")];

    public static string RegionName(PolRegion region) => RegionNames.First(r => r.Region == region).Name;

    private static PolRegion? RegionFrom(string? name) =>
        RegionNames.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Name: not null } r ? r.Region : null;

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
            Region = RegionFrom(S("region")),
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
        if (Region is { } region) e.Add(new XElement("region", RegionName(region)));
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
