namespace Provenance.Common.Game.Windower;

public sealed record WindowerLaunchLists(IReadOnlyList<string> RequiredPlugins, IReadOnlyList<string> Plugins,
                                         IReadOnlyList<string> Addons, IReadOnlyList<string> Skipped)
{
    public IReadOnlyList<string> InitCommandAddons { get; init; } = [];
}

// a profile's addons and plugins, kept in Provenance's own settings by Windower profile name. UseGlobal (and no entry
// at all) means the shared list, settings.xml's <autoload> plus Provenance's own extras
public sealed class WindowerAddonList
{
    public bool UseGlobal { get; set; } = true;
    public List<string> Addons { get; set; } = [];
    public List<string> Plugins { get; set; } = [];

    public WindowerAddonList Copy() => new() { UseGlobal = UseGlobal, Addons = Clean(Addons), Plugins = Clean(Plugins) };

    private static List<string> Clean(List<string>? names) => names?.Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? [];

    public static WindowerAddonList Global(WindowerSettingsFile settings, WindowerAddonList? extras = null) =>
        new() { UseGlobal = true, Addons = Merge(settings.Addons, extras?.Addons), Plugins = Merge(settings.Plugins, extras?.Plugins) };

    private static List<string> Merge(List<string> names, List<string>? extras) =>
        [.. names, .. Clean(extras).Where(n => !names.Contains(n, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase)];

    // a profile renamed outside Provenance has no entry any more, so it's back on the global list. settings.json can
    // hold a null for any of it, so nothing here trusts it not to
    public static WindowerAddonList Effective(WindowerSettingsFile settings, IReadOnlyDictionary<string, WindowerAddonList>? lists, string profileName,
                                              WindowerAddonList? extras = null) =>
        lists is not null && lists.TryGetValue(profileName, out var own) && own is { UseGlobal: false } ? own.Copy() : Global(settings, extras);

    // the new name never inherits whatever an old profile of that name left behind
    public static void Renamed(IDictionary<string, WindowerAddonList> lists, string oldName, string newName)
    {
        if (oldName == newName) return;
        lists.Remove(newName);
        if (lists.Remove(oldName, out var own) && own is not null) lists[newName] = own;
    }

    public static void Duplicated(IDictionary<string, WindowerAddonList> lists, string source, string copy)
    {
        lists.Remove(copy);
        if (lists.TryGetValue(source, out var own) && own is not null) lists[copy] = own.Copy();
    }

    // a file that didn't load reads as having no profiles, which would wipe every list
    public static void Prune(IDictionary<string, WindowerAddonList> lists, WindowerSettingsFile settings)
    {
        if (!settings.CanSave) return;
        var names = settings.Profiles.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var key in lists.Where(kv => kv.Value is null || !names.Contains(kv.Key)).Select(kv => kv.Key).ToList())
            lists.Remove(key);
    }

    // the shared list and a profile's own go out the same way: whatever's installed and loadable, less anything
    // init.txt loads itself
    public static WindowerLaunchLists ForLaunch(string windowerDir, WindowerProfile profile, WindowerAddonList effective,
                                                IReadOnlyList<WindowerPluginInfo>? plugins = null, IReadOnlyList<WindowerAddonInfo>? addons = null)
    {
        plugins ??= WindowerPlugins.Scan(windowerDir);
        var skipped = new List<string>();
        var required = plugins.Where(p => p.InManifest && p.Required).Select(p => p.Name).ToList();
        var wantedPlugins = Clean(effective.Plugins).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var wantedAddons = Clean(effective.Addons).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var sendPlugins = new List<string>();
        foreach (var n in wantedPlugins)
        {
            var p = plugins.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (p is { Required: true }) continue;
            if (p is { Installed: true, IsWindowerPlugin: true }) sendPlugins.Add(p.Name);
            else skipped.Add(p is { Installed: true } ? $"{n} (not a Windower plugin)" : $"{n} (not installed)");
        }
        sendPlugins = plugins.Select(p => p.Name).Where(sendPlugins.Contains).ToList();

        addons ??= WindowerAddons.Scan(windowerDir);
        var sendAddons = new List<string>();
        foreach (var n in wantedAddons)
            if (addons.FirstOrDefault(a => a.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) is { } a) sendAddons.Add(a.Name);
            else skipped.Add($"{n} (addon not installed)");

        // required plugins always go, the addons need LuaCore before init.txt runs. Nothing shows init's own load
        // LuaCore runs, so its addons go in too (a second load just says "already loaded")
        var loads = ScriptLoads.Read(windowerDir, profile.InitCommand);
        string? Already(string name, Dictionary<string, string> loaded) =>
            loaded.TryGetValue(name, out var where) && where != ScriptLoads.InitCommand ? $"{name} (already loaded by {where})" : null;
        foreach (var n in sendPlugins.ToList())
            if (Already(n, loads.Plugins) is { } why) { sendPlugins.Remove(n); skipped.Add(why); }
        foreach (var n in sendAddons.ToList())
            if (Already(n, loads.Addons) is { } why) { sendAddons.Remove(n); skipped.Add(why); }
        var fromInit = addons.Where(a => loads.Addons.GetValueOrDefault(a.Name) == ScriptLoads.InitCommand).Select(a => a.Name).ToList();
        sendAddons.AddRange(fromInit.Where(n => !sendAddons.Contains(n, StringComparer.OrdinalIgnoreCase)));
        return new WindowerLaunchLists(required, sendPlugins, sendAddons, skipped) { InitCommandAddons = fromInit };
    }
}

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
