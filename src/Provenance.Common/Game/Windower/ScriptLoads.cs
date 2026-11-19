using Provenance.Common.IO;
using Provenance.Common.Security;

namespace Provenance.Common.Game.Windower;

internal static class ScriptLoads
{
    private const int MaxRead = 1 << 20, MaxDepth = 8, MaxScripts = 64;
    public const string InitCommand = "the profile's init command";
    private const string InitTxt = "your init.txt";

    public static (Dictionary<string, string> Plugins, Dictionary<string, string> Addons) Read(string windowerDir, string? initCommand)
    {
        var plugins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var addons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Scan(WindowerHookBlock.InitCommandAsSent(initCommand), InitCommand, plugins, addons);
        Run(Path.Combine(windowerDir, "scripts"), "init.txt", 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase), plugins, addons);
        return (plugins, addons);
    }

    public static bool LoadsAddon(string script)
    {
        var addons = new Dictionary<string, string>();
        Scan(script, "", new Dictionary<string, string>(), addons);
        return addons.Count > 0;
    }

    private static void Run(string scriptsDir, string name, int depth, HashSet<string> read, Dictionary<string, string> plugins, Dictionary<string, string> addons)
    {
        if (depth > MaxDepth || read.Count >= MaxScripts || Find(scriptsDir, name) is not { } path || !read.Add(path)) return;
        if (TextFiles.ReadHead(path, MaxRead) is not { } text) return;
        var runs = new List<string>();
        Scan(text, InitTxt, plugins, addons, runs);
        foreach (var next in runs.Distinct(StringComparer.OrdinalIgnoreCase)) Run(scriptsDir, next, depth + 1, read, plugins, addons);
    }

    // Hook.dll opens scripts\<name> as written, and only when that won't open tries it with .txt on the end
    private static string? Find(string scriptsDir, string name)
    {
        if (PathInside.Collapse(scriptsDir, name) is not { } path) return null;
        return File.Exists(path) ? path : File.Exists(path + ".txt") ? path + ".txt" : null;
    }

    internal static void Scan(string script, string where, Dictionary<string, string> plugins, Dictionary<string, string> addons, List<string>? runs = null)
    {
        foreach (var whole in script.Split('\n'))
        {
            var line = whole;
            // the hook cuts a script's line at the first // before it splits it at ;
            if (runs is not null && line.IndexOf("//", StringComparison.Ordinal) is >= 0 and var cut) line = line[..cut];
            else if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            foreach (var command in line.Split(';'))
            {
                var words = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length < 2) continue;
                string Name(int i) => words[i].Trim('"', '\'');
                var verb = words[0].ToLowerInvariant();
                if (runs is null ? words[0] == "plugin_load" : verb is "load" or "plugin_load") plugins.TryAdd(Name(1), where);
                else if (verb == "lua" && words.Length >= 3 && words[1].ToLowerInvariant() is "load" or "l") addons.TryAdd(Name(2), where);
                else if (runs is not null && words is [("exec" or "console_exec"), var name] && !name.Contains('"') && !name.Contains('\'')) runs.Add(name);
            }
        }
    }
}
