using System.Globalization;
using System.IO;
using Provenance.Common;
using Provenance.Common.Game.Ashita;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Game.Windower;
using Provenance.Common.Hook;
using Provenance.Common.Login;
using Provenance.Common.Security;
using Provenance.Wpf.Services;
using Serilog;

namespace Provenance.Wpf.ViewModels.Settings;

internal static class AddonsPageBuilder
{
    private const string LoadedBy = "already loaded by ";

    internal static AddonsScan? Scan(AddonsTarget target) => target switch
    {
        { Platform: ThirdPartyHook.Windower, Folder: { } dir } when !FolderPins.Linked(dir) => ScanWindower(dir),
        { Platform: ThirdPartyHook.Ashita, Folder: { } dir } when !string.IsNullOrWhiteSpace(target.Profile) && File.Exists(AddonSwitches.BootPath(dir, target.Profile)) => ScanAshita(dir),
        _ => null,
    };

    private static WindowerScan ScanWindower(string dir)
    {
        var addonsXml = WindowerAddons.AddonsXml(dir);
        return new WindowerScan(WindowerPlugins.Scan(dir), WindowerAddons.Scan(dir, addonsXml, readmes: true), WindowerAddonTree.Load(WindowerAddonTree.CachePath(dir)), addonsXml);
    }

    private static AshitaScan ScanAshita(string dir)
    {
        var docs = AshitaDocs.Scan(dir);
        return new AshitaScan(AshitaAddons.Scan(dir, docs), AshitaPlugins.Scan(dir, docs));
    }

    internal static AddonsPage Build(AddonsTarget target, IReadOnlyDictionary<string, WindowerAddonList> lists, WindowerAddonList? extras, AddonsScan? scan,
                                     AutoLoginReach reach) => target switch
    {
        { Platform: ThirdPartyHook.Windower, Folder: null } =>
            AddonsPage.Empty("Provenance couldn't find Windower. Choose its folder under Windower/Ashita first."),
        { Platform: ThirdPartyHook.Windower, Folder: { } dir } when FolderPins.Linked(dir) => AddonsPage.Empty(WindowerInstall.LinkedText),
        { Platform: ThirdPartyHook.Windower, Folder: { } dir } => BuildWindower(dir, target.Profile, lists, extras, scan as WindowerScan, reach),
        { Platform: ThirdPartyHook.Ashita, Folder: null } =>
            AddonsPage.Empty("Provenance couldn't find Ashita. Choose its folder under Windower/Ashita first."),
        { Platform: ThirdPartyHook.Ashita, Folder: { } dir } => BuildAshita(dir, target.Profile, scan as AshitaScan, reach),
        _ => AddonsPage.Empty("Choose Windower or Ashita and its folder under Windower/Ashita first."),
    };

    private static AddonsPage BuildWindower(string dir, string profileName, IReadOnlyDictionary<string, WindowerAddonList> lists, WindowerAddonList? extras,
                                      WindowerScan? scan, AutoLoginReach reach)
    {
        scan ??= (WindowerScan)Scan(new AddonsTarget(ThirdPartyHook.Windower, dir, profileName))!;
        var page = new AddonsPage { Platform = ThirdPartyHook.Windower };
        WindowerSettingsFile file;
        bool locked = false;
        try { file = WindowerSettingsFile.Load(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(e, "Could not read Windower's settings.xml");
            file = WindowerSettingsFile.Empty();
            locked = true;
            page.Problem = "Provenance couldn't read Windower's settings.xml, so its list can't be changed right now. Close anything that has it open, then come back to this page.";
        }
        if (file.LoadError is not null)
        {
            locked = true;
            page.Problem = AddonSwitches.Unreadable(file);
        }
        var profile = file.Find(profileName);
        page.Key = profile?.Name ?? profileName;
        page.Display = WindowerInstall.DisplayName(page.Key, file.Profiles.Select(p => p.Name).ToList());
        page.Branch = file.Branch;
        page.Intro = "Turn addons and plugins on or off. Changes save straight away.";
        var effective = WindowerAddonList.Effective(file, lists, page.Key, extras);
        page.UseGlobal = effective.UseGlobal;
        page.ListLocked = locked;
        profile ??= new WindowerProfile();
        bool frozen = locked && effective.UseGlobal;

        bool Listed(List<string> names, string name) => names.Contains(name, StringComparer.OrdinalIgnoreCase);
        var plugins = scan.Plugins.Where(p => !p.Required && (!p.Hidden || p.Installed || Listed(effective.Plugins, p.Listing.Key))).ToList();
        var missingPlugins = Missing(effective.Plugins, scan.Plugins.Select(p => p.Listing.Key));
        var pluginNames = plugins.Select(p => p.Listing.Key).Concat(missingPlugins).ToList();
        var installed = scan.Addons.Select(a => a.Listing.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var notInstalled = (scan.Tree?.Addons ?? []).Where(t => !installed.Contains(t.Name)).ToList();
        var missingAddons = Missing(effective.Addons, scan.Addons.Select(a => a.Listing.Key).Concat(notInstalled.Select(t => t.Name)));
        var addonNames = scan.Addons.Select(a => a.Listing.Key).Concat(notInstalled.Select(t => t.Name)).Concat(missingAddons).ToList();

        var loadedPlugins = Reasons(dir, profile, scan, AddonKind.Plugin, pluginNames);
        var loadedAddons = Reasons(dir, profile, scan, AddonKind.Addon, addonNames);

        RowData Row(AddonKind kind, AddonListing listing, bool isInstalled, bool installable, bool windowerPlugin = true, bool official = false,
                    AddonBadge? problem = null)
        {
            var key = listing.Key;
            var on = Listed(kind == AddonKind.Addon ? effective.Addons : effective.Plugins, key);
            var badges = new List<AddonBadge>();
            bool canTurnOn = true, hasSwitch = true;
            if (!isInstalled) badges.Add(installable ? Badges.Install : Badges.NotInWindower);
            else if (!windowerPlugin)
            {
                badges.Add(Badges.NotAPlugin);
                canTurnOn = false;
            }
            else if (problem is not null)
            {
                badges.Add(problem);
                canTurnOn = false;
            }
            bool replaced = kind == AddonKind.Addon && Replaced(key, AutoLogin.WindowerAddon, reach, badges);
            var loaded = kind == AddonKind.Addon ? loadedAddons : loadedPlugins;
            AddonGroup? group = null;
            if (loaded.TryGetValue(key, out var why) && why.StartsWith(LoadedBy, StringComparison.Ordinal))
            {
                group = why.EndsWith("init.txt", StringComparison.Ordinal) ? Groups.InitTxt : Groups.InitCommand;
                hasSwitch = false;
            }
            return new RowData(kind, listing)
            {
                On = on && !replaced, HasSwitch = hasSwitch, CanChange = !frozen && !replaced && (canTurnOn || on), CanInstall = !isInstalled && installable,
                Badges = badges, Group = group, Team = official ? "Windower" : null, ReplacedByAutoLogin = replaced,
            };
        }

        var repo = (scan.Tree?.Addons ?? []).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        AddonListing Offered(WindowerAddon t) => scan.AddonsXml.TryGetValue(t.Name, out var x)
            ? new AddonListing(t.Name, t.Name, null, x.Author, x.Description, null) : AddonListing.Named(t.Name);
        AddonListing Linked(AddonListing l) =>
            scan.AddonsXml.ContainsKey(l.Key) && scan.Tree?.Find(l.Key) is { } t && WindowerAddonTree.ReadmeLink(t, file.Branch) is { } link
                ? l with { Docs = new AddonDocs(AddonDocsKind.Link, link) } : l;
        page.Plugins = plugins.Select(p => Row(AddonKind.Plugin, p.Listing, p.Installed, p.InManifest, p.IsWindowerPlugin, official: p.InManifest, problem: WindowerProblem(p)))
            .Concat(missingPlugins.Select(n => Row(AddonKind.Plugin, AddonListing.Named(n), false, false)))
            .OrderBy(r => r.Listing.Name, StringComparer.OrdinalIgnoreCase).ToList();
        page.Addons = scan.Addons.Select(a => Row(AddonKind.Addon, Linked(a.Listing), true, false, official: repo.Contains(a.Listing.Key)))
            .Concat(notInstalled.Select(t => Row(AddonKind.Addon, Linked(Offered(t)), false, true, official: true)))
            .Concat(missingAddons.Select(n => Row(AddonKind.Addon, AddonListing.Named(n), false, false)))
            .OrderBy(r => r.Listing.Name, StringComparer.OrdinalIgnoreCase).ToList();
        page.NoAddons = $"No addons found in {Path.Combine(dir, "addons")}.";
        page.NoPlugins = $"No plugins found in {Path.Combine(dir, "plugins")}.";
        return page;
    }

    private static List<string> Missing(IEnumerable<string> listed, IEnumerable<string> known)
    {
        var seen = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
        return listed.Where(n => !string.IsNullOrWhiteSpace(n) && seen.Add(n)).ToList();
    }

    private static Dictionary<string, string> Reasons(string dir, WindowerProfile profile, WindowerScan scan, AddonKind kind, List<string> names)
    {
        var list = kind == AddonKind.Addon ? new WindowerAddonList { Addons = names } : new WindowerAddonList { Plugins = names };
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var launch = WindowerAddonList.ForLaunch(dir, profile, list, scan.Plugins, scan.Addons);
        foreach (var s in launch.Skipped)
        {
            int at = s.LastIndexOf(" (", StringComparison.Ordinal);
            if (at > 0 && s.EndsWith(')')) reasons.TryAdd(s[..at], s[(at + 2)..^1]);
        }
        foreach (var n in kind == AddonKind.Addon ? launch.InitCommandAddons : launch.InitCommandPlugins) reasons.TryAdd(n, LoadedBy + "the profile's init command");
        return reasons;
    }

    private static AddonsPage BuildAshita(string dir, string bootName, AshitaScan? scan, AutoLoginReach reach)
    {
        if (string.IsNullOrWhiteSpace(bootName)) return AddonsPage.Empty("Choose a boot configuration under Windower/Ashita first.");
        var path = AddonSwitches.BootPath(dir, bootName);
        if (!File.Exists(path))
            return AddonsPage.Empty($"The boot configuration {bootName} isn't in Ashita's config\\boot folder any more. Choose another one under Windower/Ashita.");
        scan ??= ScanAshita(dir);
        var page = new AddonsPage
        {
            Platform = ThirdPartyHook.Ashita, Key = bootName, Display = AshitaBootFiles.ShownName(dir, bootName),
            Intro = "Turn addons and plugins on or off for this boot configuration. Changes save straight away.",
        };
        AshitaBootConfig boot;
        try { boot = AshitaBootConfig.Load(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(e, "Could not read the Ashita boot configuration {Boot}", bootName);
            return AddonsPage.Empty($"Provenance couldn't read the boot configuration {bootName}. If it's open in another program, close it and come back to this page.");
        }
        bool readOnly = AshitaBootFiles.IsShippedExample(bootName);
        if (readOnly)
            page.Problem = HookFailures.AshitaExampleFile(page.Display);
        AshitaScript script;
        try
        {
            var opened = AddonSwitches.OpenScript(dir, boot.Script);
            if (opened is null) page.Problem ??= $"The script {boot.Script} isn't in Ashita's scripts folder, so Provenance can't read what it loads.";
            script = opened ?? AshitaScript.FromBytes([]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(e, "Could not read the Ashita script {Script}", boot.Script);
            script = AshitaScript.FromBytes([]);
            readOnly = true;
            page.Problem = ErrorText.Of(e, $"Provenance couldn't read the script {boot.Script}.");
        }

        RowData Row(AddonKind kind, AddonListing listing, AshitaLoad state, bool installed, AddonBadge? problem = null)
        {
            var key = listing.Key;
            var badges = new List<AddonBadge>();
            if (!installed) badges.Add(Badges.NotInAshita);
            if (problem is not null) badges.Add(problem);
            else if (installed && !AddonSwitches.Loadable(kind, key)) badges.Add(problem = Badges.BadName);
            bool replaced = kind == AddonKind.Addon && Replaced(key, AutoLogin.AshitaAddon, reach, badges);
            bool on = state == AshitaLoad.On, canTurnOn = installed && problem is null, byScript = state == AshitaLoad.ByScript;
            return new RowData(kind, listing)
            {
                On = on && !replaced, HasSwitch = !byScript, CanChange = !readOnly && !replaced && (canTurnOn || on), Badges = badges,
                Group = byScript ? Groups.ByScript : null, Team = AshitaTeam(kind, key), ReplacedByAutoLogin = replaced,
            };
        }

        List<RowData> Rows(AddonKind kind, IEnumerable<(AddonListing Listing, AddonBadge? Problem)> found, IEnumerable<string> loaded,
                           Func<string, AshitaLoad> state)
        {
            var list = found.ToList();
            return list.Select(f => Row(kind, f.Listing, state(f.Listing.Key), true, f.Problem))
                .Concat(Missing(loaded, list.Select(f => f.Listing.Key)).Select(n => Row(kind, AddonListing.Named(n), state(n), false)))
                .OrderBy(r => r.Listing.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        page.Addons = Rows(AddonKind.Addon,
            scan.Addons.Select(a => (a.Listing, a.NotForAshitaV4 ? Badges.NotForV4 : (AddonBadge?)null)),
            script.Addons.Select(a => a.Name), script.Addon);
        page.Plugins = Rows(AddonKind.Plugin,
            scan.Plugins.Where(p => p.Kind == AshitaPluginKind.Plugin)
                .Select(p => (p.Listing, AshitaProblem(p))),
            script.Plugins.Select(p => p.Name), script.Plugin);

        var pol = boot.PolPlugins;
        AshitaLoad PolState(string n) => pol.Any(p => Same(p.Name, n) && p.On) ? AshitaLoad.On : AshitaLoad.Off;
        var polFound = scan.Plugins.Where(p => p.Kind == AshitaPluginKind.PolPlugin).ToList();
        var polRows = polFound.Select(p => AshitaPolPlugins.IsTakeover(p.Listing.Key)
            ? new RowData(AddonKind.PolPlugin, p.Listing with { Description = AshitaPolPlugins.IncompatibleReason(p.Listing.Key) })
                { CanChange = false, Incompatible = true, Badges = [Badges.Incompatible], Team = AshitaTeam(AddonKind.PolPlugin, p.Listing.Key) }
            : Row(AddonKind.PolPlugin, p.Listing, PolState(p.Listing.Key), true, AshitaProblem(p))).ToList();
        var turnedOn = pol.Where(p => p.On && !AshitaPolPlugins.IsTakeover(p.Name)).Select(p => p.Name);
        polRows.AddRange(Missing(turnedOn, polFound.Select(p => p.Listing.Key))
            .Select(n => Row(AddonKind.PolPlugin, AddonListing.Named(n), AshitaLoad.On, false)));
        page.PolPlugins = polRows.OrderBy(r => r.Incompatible).ThenBy(r => r.Listing.Name, StringComparer.OrdinalIgnoreCase).ToList();

        page.NoAddons = $"No addons found in {Path.Combine(dir, "addons")}.";
        page.NoPlugins = $"No plugins found in {Path.Combine(dir, "plugins")}.";
        page.NoPolPlugins = $"No PlayOnline plugins found in {Path.Combine(dir, "polplugins")}.";
        return page;
    }

    private static string? AshitaTeam(AddonKind kind, string name) => kind switch
    {
        AddonKind.Addon => AshitaShipped.IsAddon(name),
        AddonKind.Plugin => AshitaShipped.IsPlugin(name),
        _ => AshitaShipped.IsPolPlugin(name),
    } ? "Ashita" : null;

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // the game leaves the addon out for those accounts, so it's only locked when that's every one of them
    private static bool Replaced(string key, string addon, AutoLoginReach reach, List<AddonBadge> badges)
    {
        if (reach == AutoLoginReach.Nobody || !Same(key, addon)) return false;
        badges.Add(reach == AutoLoginReach.EveryAccount ? Badges.ReplacedForEveryone : Badges.ReplacedForSome);
        return reach == AutoLoginReach.EveryAccount;
    }

    private static AddonBadge? WindowerProblem(WindowerPluginInfo p) =>
        p is { Outdated: true, InterfaceVersion: { } v } ? Badges.Outdated(WindowerPlugins.InterfaceName(v)) : p.NeedsNewerWindower ? Badges.OtherWindower : null;

    // Ashita shows interface versions to two places itself (4.20, 4.30)
    private static AddonBadge? AshitaProblem(AshitaPlugin p) =>
        p is { Outdated: true, InterfaceVersion: { } v } ? Badges.Outdated("Ashita " + v.ToString("0.00", CultureInfo.InvariantCulture))
        : p.ForOtherAshita ? Badges.OtherAshita : null;

    private static class Badges
    {
        public static readonly AddonBadge Install = new("Not installed", "Press Install to add it to your Windower folder.");
        public static readonly AddonBadge NotInWindower = new("Not installed", "It's turned on, but it isn't in your Windower folder.");
        public static readonly AddonBadge NotInAshita = new("Not installed", "It's named in this boot configuration, but it isn't in your Ashita folder.");
        public static readonly AddonBadge NotAPlugin = new("Not a Windower plugin",
            "This file in Windower's plugins folder isn't a Windower 4 plugin, so it can't be turned on.", Warn: true);
        public static AddonBadge Outdated(string madeFor) =>
            new("Outdated", $"This plugin was made for {madeFor}. It needs updating before it will work again.", Warn: true);
        public static readonly AddonBadge OtherWindower = new("Built for a different Windower",
            "This plugin was built for a different version of Windower, so Windower won't load it.", Warn: true);
        public static readonly AddonBadge NotForV4 = new("Not for Ashita v4",
            "This addon was written for Windower or an older Ashita, so it won't run in Ashita v4.", Warn: true);
        public static readonly AddonBadge OtherAshita = new("Built for a different Ashita",
            "This plugin was built for a different version of Ashita, so Ashita won't load it.", Warn: true);
        public static readonly AddonBadge Incompatible = new(AshitaInstall.IncompatibleText,
            "Provenance keeps this off whenever it starts the game. Your Ashita settings aren't changed.", Warn: true);
        public static readonly AddonBadge ReplacedForEveryone = new("Replaced by Auto-login",
            "Provenance's Auto-login does this addon's job, so it doesn't load and is disabled. To use it instead, turn off Accept the licence agreement "
            + $"and choose {AutoLoginSettings.NoneText} for every account under Auto-login.");
        public static readonly AddonBadge ReplacedForSome = new("Replaced by Auto-login",
            "Provenance's Auto-login does this addon's job, so it doesn't load for an account with a character chosen under Auto-login.");
        public static readonly AddonBadge BadName = new("Name Ashita can't load",
            "Ashita can't load a name like this one from a script or boot configuration. Rename it to plain letters and numbers to use it.", Warn: true);
    }

    internal static class Groups
    {
        public static readonly AddonGroup InitTxt = new("Loaded by your init.txt",
            "Windower's scripts\\init.txt loads these itself, or runs a script that does, so they load no matter what the list says. "
            + "To turn one on or off here instead, take its line out of the script that loads it.");
        public static readonly AddonGroup InitCommand = new("Loaded by this profile's init command",
            "This profile's init command loads these itself, so they load no matter what the list says.");
        public static readonly AddonGroup ByScript = new("Set by your script",
            "Your Ashita script loads or unloads these in a way Provenance can't safely change, so they're left as they are.");
        public static readonly IReadOnlyList<AddonGroup> All = [InitTxt, InitCommand, ByScript];
    }
}
