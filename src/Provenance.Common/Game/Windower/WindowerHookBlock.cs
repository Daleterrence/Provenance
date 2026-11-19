using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Provenance.Common.PolFormat;

namespace Provenance.Common.Game.Windower;

// the 42180 bytes Hook.dll copies out of the WindowerMMFSettingsHandler mapping, offsets as Windower.exe 4.7.9.3 puts them
public sealed record WindowerHookBlock(byte[] Bytes, string InitCommand, string InitPlugins, string InitAddons,
                                       WindowerSizes Sizes, IReadOnlyList<string> Warnings)
{
    public const int Size = 0xA4C4;
    public const string MappingName = "WindowerMMFSettingsHandler";
    public const string EventName = "windower.signal-injection";

    internal static readonly (int At, int Chars) ProfileName = (0x0000, 256), BranchField = (0x0200, 32),
        LauncherVersionField = (0x0240, 16), HookVersionField = (0x0260, 16), PlayOnlinePathField = (0x0280, 4096),
        FfxiPathField = (0x2280, 4096), WindowerPathField = (0x4280, 4096), ConsoleKeyField = (0x6280, 16),
        InitCommandField = (0x62C4, 4096), InitPluginsField = (0x82C4, 256), InitAddonsField = (0x84C4, 4096);

    internal const int RegionAt = 0x62A0, WidthAt = 0x62A4, HeightAt = 0x62A8, UIWidthAt = 0x62AC, UIHeightAt = 0x62B0,
        XAt = 0x62B4, YAt = 0x62B8, FullscreenAt = 0x62BC, BorderlessAt = 0x62BD, GamepadAt = 0x62BE, WinKeyAt = 0x62BF,
        AccountLimitAt = 0x62C0, FastLoginAt = 0x62C1, BackgroundAt = 0x62C2;

    private const string Unsupported = "echo Command not supported in initialization: ";
    private const string LoadLuaCore = "load LuaCore";

    public static WindowerHookBlock Build(WindowerHookInput input)
    {
        var p = input.Profile ?? throw new ArgumentNullException(nameof(input));
        if (input.Region is not { } region)
            throw new ArgumentException("The hook needs a real region, not Auto.", nameof(input));
        var warnings = new List<string>();
        var sizes = WindowerSizes.For(p.UIScale, input.Width, input.Height);
        var initCommand = InitCommandAsSent(p.InitCommand, warnings);
        var initPlugins = JoinNames(input.RequiredPlugins.Concat(input.Plugins), InitPluginsField.Chars - 1, "plugins", warnings);
        var addons = input.Addons.OrderBy(a => a.ToUpperInvariant(), NlsSort.UserDefault);
        var initAddons = JoinNames(addons, InitAddonsField.Chars - 1, "addons", warnings);

        var b = new byte[Size];
        Text(b, ProfileName, p.Name, "the profile name", warnings);
        Text(b, BranchField, input.Branch, "the branch", warnings);
        Text(b, LauncherVersionField, input.LauncherVersion, "the launcher version", warnings);
        Text(b, HookVersionField, input.HookVersion, "the hook version", warnings);
        Text(b, PlayOnlinePathField, FormatDir(input.PlayOnlinePath), "the POL folder", warnings);
        Text(b, FfxiPathField, FormatDir(input.FfxiPath), "the FFXI folder", warnings);
        Text(b, WindowerPathField, FormatDir(input.WindowerPath), "the Windower folder", warnings);
        Text(b, ConsoleKeyField, p.ConsoleKey.InternalName, "the console key", warnings);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(RegionAt), (uint)PolEnv.AreaIndex(region));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(WidthAt), sizes.Width);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(HeightAt), sizes.Height);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(UIWidthAt), sizes.UIWidth);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(UIHeightAt), sizes.UIHeight);
        b[FullscreenAt] = Flag(p.WindowMode == WindowerWindowMode.Fullscreen);
        b[BorderlessAt] = Flag(p.WindowMode == WindowerWindowMode.BorderlessWindow);
        b[GamepadAt] = Flag(p.AlwaysEnableGamepad);
        b[WinKeyAt] = Flag(p.AllowWinKey);
        b[AccountLimitAt] = Flag(p.IncreaseAccountLimit);
        b[FastLoginAt] = Flag(p.EnableFastLogin);
        b[BackgroundAt] = Flag(p.PreventBackgroundThrottling);
        Text(b, InitCommandField, initCommand, "the init command", warnings);
        Text(b, InitPluginsField, initPlugins, "the plugin list", warnings);
        Text(b, InitAddonsField, initAddons, "the addon list", warnings);
        return new WindowerHookBlock(b, initCommand, initPlugins, initAddons, sizes, warnings);
    }

    private static byte Flag(bool on) => on ? (byte)1 : (byte)0;

    // the marshaller's ByValTStr rule: anything longer than the field loses its tail, and the last WCHAR is always NUL
    private static void Text(byte[] b, (int At, int Chars) field, string? value, string what, List<string> warnings)
    {
        var s = value ?? "";
        if (s.Length > field.Chars - 1)
        {
            warnings.Add($"{char.ToUpperInvariant(what[0])}{what[1..]} is longer than the hook takes ({field.Chars - 1} characters), so it was cut short.");
            s = s[..(field.Chars - 1)];
        }
        MemoryMarshal.AsBytes(s.AsSpan()).CopyTo(b.AsSpan(field.At));
    }

    public static string FilterInitCommand(string? initCommand)
    {
        if (initCommand is null) return "";
        return string.Join(";", initCommand.Split(';').Select(piece =>
        {
            var t = piece.Trim();
            return t.StartsWith("load ", StringComparison.Ordinal) || t.StartsWith("exec ", StringComparison.Ordinal)
                ? Unsupported + piece : piece;
        }));
    }

    // Windower cuts it mid-command, so this stops at the last whole one
    internal static string InitCommandAsSent(string? initCommand, List<string>? warnings = null)
    {
        var command = FilterInitCommand(initCommand);
        if (ScriptLoads.LoadsAddon(command)) command = LoadLuaCore + ";" + command;
        int max = InitCommandField.Chars - 1;
        if (command.Length <= max) return command;
        int cut = command.LastIndexOf(';', max);
        warnings?.Add($"The init command is longer than the hook takes ({max} characters), so the last commands in it were left out.");
        return cut < 0 ? "" : command[..cut];
    }

    private static string JoinNames(IEnumerable<string> names, int max, string what, List<string> warnings)
    {
        var kept = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dropped = new List<string>();
        int length = 0;
        foreach (var raw in names)
        {
            var n = raw?.Trim() ?? "";
            if (n.Length == 0 || !seen.Add(n)) continue;
            if (n.Contains(';'))
            {
                warnings.Add($"\"{n}\" has a ; in its name, so it can't go in the {what} list.");
                continue;
            }
            int next = length + (kept.Count > 0 ? 1 : 0) + n.Length;
            if (dropped.Count > 0 || next > max) { dropped.Add(n); continue; }
            kept.Add(n);
            length = next;
        }
        if (dropped.Count > 0)
            warnings.Add($"The hook takes {max} characters of {what} at most, so these weren't loaded: {string.Join(", ", dropped)}.");
        return string.Join(";", kept);
    }

    public static string FormatDir(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("A folder is needed.", nameof(path));
        path = path.Replace('/', '\\');
        if (path[^1] != '\\') path += "\\";
        return char.ToUpperInvariant(path[0]) + path[1..];
    }

    internal static string LauncherVersion(string windowerDir, WindowerScanCache? cache = null) =>
        ReadVersion(Path.Combine(windowerDir, "Windower.exe"), cache)?.ToString()
        ?? (WindowerManifest.LoadCached(windowerDir)?.LauncherVersion is { } v && v != new Version() ? v.ToString() : null)
        ?? "0.0.0.0";

    internal static string HookVersion(string windowerDir, WindowerScanCache? cache = null) =>
        ReadVersion(Path.Combine(windowerDir, "Hook.dll"), cache)?.ToString() ?? "0.0.0.0";

    private static Version? ReadVersion(string file, WindowerScanCache? cache) => cache is null ? PeFile.ReadFileVersion(file) : cache.Pe(file)?.Version;
}
