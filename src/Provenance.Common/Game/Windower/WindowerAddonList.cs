namespace Provenance.Common.Game.Windower;

public sealed record WindowerLaunchLists(IReadOnlyList<string> RequiredPlugins, IReadOnlyList<string> Plugins,
                                         IReadOnlyList<string> Addons, IReadOnlyList<string> Skipped)
{
    public IReadOnlyList<string> InitCommandAddons { get; init; } = [];
    public IReadOnlyList<string> InitCommandPlugins { get; init; } = [];
}

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

    public static WindowerAddonList Effective(WindowerSettingsFile settings, IReadOnlyDictionary<string, WindowerAddonList>? lists, string profileName,
                                              WindowerAddonList? extras = null) =>
        lists is not null && lists.TryGetValue(profileName, out var own) && own is { UseGlobal: false } ? own.Copy() : Global(settings, extras);

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

    public static void Prune(IDictionary<string, WindowerAddonList> lists, WindowerSettingsFile settings)
    {
        if (!settings.CanSave) return;
        var names = settings.Profiles.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var key in lists.Where(kv => kv.Value is null || !names.Contains(kv.Key)).Select(kv => kv.Key).ToList())
            lists.Remove(key);
    }

    public static WindowerLaunchLists ForLaunch(string windowerDir, WindowerProfile profile, WindowerAddonList effective,
                                                IReadOnlyList<WindowerPluginInfo>? plugins = null, IReadOnlyList<WindowerAddonInfo>? addons = null)
    {
        plugins ??= WindowerPlugins.Scan(windowerDir);
        var skipped = new List<string>();
        var required = plugins.Where(p => p.InManifest && p.Required).Select(p => p.Listing.Key).ToList();
        var wantedPlugins = Clean(effective.Plugins).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var wantedAddons = Clean(effective.Addons).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var sendPlugins = new List<string>();
        foreach (var n in wantedPlugins)
        {
            var p = plugins.FirstOrDefault(x => x.Listing.Key.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (p is { Required: true }) continue;
            if (p is { Installed: true, IsWindowerPlugin: true }) sendPlugins.Add(p.Listing.Key);
            else skipped.Add(p is { Installed: true } ? $"{n} (not a Windower plugin)" : $"{n} (not installed)");
        }
        sendPlugins = plugins.Select(p => p.Listing.Key).Where(sendPlugins.Contains).ToList();

        addons ??= WindowerAddons.Scan(windowerDir);
        var sendAddons = new List<string>();
        foreach (var n in wantedAddons)
            if (addons.FirstOrDefault(a => a.Listing.Key.Equals(n, StringComparison.OrdinalIgnoreCase)) is { } a) sendAddons.Add(a.Listing.Key);
            else skipped.Add($"{n} (addon not installed)");

        // required plugins always go, the addons need LuaCore before init.txt runs
        var loads = ScriptLoads.Read(windowerDir, profile.InitCommand);
        string? Already(string name, Dictionary<string, string> loaded) =>
            loaded.TryGetValue(name, out var where) && where != ScriptLoads.InitCommand ? $"{name} (already loaded by {where})" : null;
        foreach (var n in sendPlugins.ToList())
            if (Already(n, loads.Plugins) is { } why) { sendPlugins.Remove(n); skipped.Add(why); }
        foreach (var n in sendAddons.ToList())
            if (Already(n, loads.Addons) is { } why) { sendAddons.Remove(n); skipped.Add(why); }
        var fromInit = addons.Where(a => loads.Addons.GetValueOrDefault(a.Listing.Key) == ScriptLoads.InitCommand).Select(a => a.Listing.Key).ToList();
        sendAddons.AddRange(fromInit.Where(n => !sendAddons.Contains(n, StringComparer.OrdinalIgnoreCase)));
        var pluginsFromInit = plugins.Where(p => p is { Installed: true, IsWindowerPlugin: true, Required: false }
                                                 && loads.Plugins.GetValueOrDefault(p.Listing.Key) == ScriptLoads.InitCommand).Select(p => p.Listing.Key).ToList();
        sendPlugins = plugins.Select(p => p.Listing.Key).Where(n => sendPlugins.Contains(n) || pluginsFromInit.Contains(n)).ToList();
        return new WindowerLaunchLists(required, sendPlugins, sendAddons, skipped) { InitCommandAddons = fromInit, InitCommandPlugins = pluginsFromInit };
    }
}
