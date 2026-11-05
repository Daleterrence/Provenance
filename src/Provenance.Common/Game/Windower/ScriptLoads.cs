namespace Provenance.Common.Game.Windower;

internal static class ScriptLoads
{
    private const int MaxRead = 1 << 20;
    public const string InitCommand = "the profile's init command";

    public static (Dictionary<string, string> Plugins, Dictionary<string, string> Addons) Read(string windowerDir, string? initCommand)
    {
        var plugins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var addons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // just what the hook gets: load and exec are echoes by then, and a cut-off tail never runs
        Scan(WindowerHookBlock.InitCommandAsSent(initCommand), InitCommand, plugins, addons);
        Scan(ReadInit(Path.Combine(windowerDir, "scripts", "init.txt")), "your init.txt", plugins, addons);
        return (plugins, addons);
    }

    private static string ReadInit(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[(int)Math.Min(fs.Length, MaxRead)];
            fs.ReadExactly(buf);
            // Notepad's BOM would stick to the first word otherwise
            var bom = buf.AsSpan().StartsWith((byte[])[0xEF, 0xBB, 0xBF]) ? 3 : 0;
            return System.Text.Encoding.UTF8.GetString(buf, bom, buf.Length - bom);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }

    public static bool LoadsAddon(string script)
    {
        var addons = new Dictionary<string, string>();
        Scan(script, "", new Dictionary<string, string>(), addons);
        return addons.Count > 0;
    }

    internal static void Scan(string script, string where, Dictionary<string, string> plugins, Dictionary<string, string> addons)
    {
        foreach (var line in script.Split('\n'))
        {
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            foreach (var command in line.Split(';'))
            {
                var words = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length < 2) continue;
                string Name(int i) => words[i].Trim('"', '\'');
                var verb = words[0].ToLowerInvariant();
                if (verb is "load" or "plugin_load") plugins.TryAdd(Name(1), where);
                else if (verb == "lua" && words.Length >= 3 && words[1].ToLowerInvariant() is "load" or "l") addons.TryAdd(Name(2), where);
            }
        }
    }
}
