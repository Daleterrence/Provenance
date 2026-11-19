using Provenance.Common.Game.Ashita;
using Provenance.Common.Game.Windower;
using Provenance.Common.Settings;
using Serilog;

namespace Provenance.Common.Game.ThirdParty;

public static class AddonSwitches
{
    private const string SettingsNotSaved = "Provenance couldn't save your settings, so your changes will only last until Provenance closes.";

    public static void SetUseGlobal(ISettingsService settings, string dir, string key, bool value)
    {
        var lists = settings.Current.WindowerAddonLists ??= [];
        lists.TryGetValue(key, out var own);
        if (value)
        {
            if (own is null) return;
            own.UseGlobal = true;
        }
        else
        {
            // a settings.xml that didn't read is an empty list, and that'd stick as the profile's own
            var file = Readable(dir);
            if (own is null) lists[key] = own = WindowerAddonList.Global(file, settings.Current.WindowerSharedExtras);
            own.UseGlobal = false;
        }
        SaveSettings(settings);
    }

    public static void SwitchWindower(ISettingsService settings, string dir, string profile, AddonKind kind, string name, bool on, string? offered)
    {
        var lists = settings.Current.WindowerAddonLists ??= [];
        if (lists.TryGetValue(profile, out var own) && own is { UseGlobal: false })
        {
            Toggle(kind == AddonKind.Addon ? own.Addons : own.Plugins, name, on);
            SaveSettings(settings);
            return;
        }
        var file = Readable(dir);
        var extras = settings.Current.WindowerSharedExtras ??= new WindowerAddonList();
        var extraNames = kind == AddonKind.Addon ? extras.Addons ??= [] : extras.Plugins ??= [];
        if (on ? offered is not null : Has(kind == AddonKind.Addon ? file.Addons : file.Plugins, name))
            WindowerSettingsFile.Update(dir, f => Toggle(kind == AddonKind.Addon ? f.Addons : f.Plugins, offered ?? name, on));
        if (on ? offered is null : Has(extraNames, name))
        {
            Toggle(extraNames, name, on);
            SaveSettings(settings);
        }
    }

    public static string? SwitchAshita(string dir, string bootName, AddonKind kind, string key, string shownName, bool on)
    {
        var path = BootPath(dir, bootName);
        var boot = AshitaBootConfig.Load(path);
        if (kind == AddonKind.PolPlugin)
        {
            boot.SetPolPlugin(key, on);
            boot.Save(path);
            return null;
        }
        bool Change(AshitaScript s) => kind == AddonKind.Addon ? s.SetAddon(key, on) : s.SetPlugin(key, on);
        var name = boot.Script;
        var current = OpenScript(dir, name);
        if (!Change(current ?? AshitaScript.FromBytes([])))
            throw Shown.Mark(new InvalidOperationException(string.IsNullOrWhiteSpace(name)
                ? $"Provenance couldn't change {shownName} for this boot configuration."
                : $"Provenance couldn't change {shownName} in the script {AshitaScript.FileName(name)}."));
        if (current is not null && !AshitaScript.NeedsCopy(dir, name, bootName, Scripts(dir, name)))
        {
            current.Save();
            return null;
        }
        var copyName = AshitaScript.CopyFor(dir, name, bootName);
        var copyPath = AshitaScript.PathFor(dir, copyName)!;
        try
        {
            var copy = AshitaScript.Open(dir, copyName)!;
            Change(copy);
            copy.Save();
            boot.Script = copyName;
            boot.Save(path);
        }
        catch
        {
            if (!PointsAt(path, copyName))
                foreach (var f in (string[])[copyPath, copyPath + ".bak"])
                    try { File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
        Log.Information("Gave the Ashita boot configuration {Boot} its own script {Script}", bootName, copyName);
        return string.IsNullOrWhiteSpace(name)
            ? $"This boot configuration now has its own script, {copyName}."
            : $"This boot configuration now has its own script, {copyName} (a copy of {AshitaScript.FileName(name)}), so {AshitaScript.FileName(name)} stays as it is.";
    }

    public static string BootPath(string dir, string bootName) => Path.Combine(AshitaBootFiles.Folder(dir), bootName);

    public static AshitaScript? OpenScript(string dir, string? name) =>
        string.IsNullOrWhiteSpace(name) ? AshitaScript.FromBytes([], Path.Combine(dir, "scripts")) : AshitaScript.Open(dir, name);

    public static bool Loadable(AddonKind kind, string name)
    {
        if (kind != AddonKind.PolPlugin) return AshitaScript.FromBytes([]).SetPlugin(name, true);
        try
        {
            new AshitaBootConfig(AshitaIniFile.FromText("")).SetPolPlugin(name, true);
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    public static string Unreadable(WindowerSettingsFile file)
    {
        Log.Warning("Windower's settings.xml couldn't be read: {Why}", file.LoadError);
        return file.Unreadable("choose Start a new settings file under Windower/Ashita to set it aside and start again. Until then its list can't be changed here.");
    }

    private static void SaveSettings(ISettingsService settings)
    {
        if (!settings.Save(settings.Current)) throw Shown.Mark(new IOException(SettingsNotSaved));
    }

    private static WindowerSettingsFile Readable(string dir)
    {
        var file = WindowerSettingsFile.Load(dir);
        return file.LoadError is not null ? throw Shown.Mark(new InvalidOperationException(Unreadable(file))) : file;
    }

    private static bool Has(List<string> names, string name) => names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    private static void Toggle(List<string> names, string name, bool on)
    {
        bool Same(string n) => string.Equals(n, name, StringComparison.OrdinalIgnoreCase);
        if (!on) names.RemoveAll(Same);
        else if (!names.Any(Same)) names.Add(name);
    }

    private static bool PointsAt(string bootPath, string script)
    {
        try { return string.Equals(AshitaBootConfig.Load(bootPath).Script, script, StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    // a boot config that can't be read counts as sharing the script, a needless copy is harmless and a missed one isn't
    private static List<(string Boot, string? Script)> Scripts(string dir, string? fallback) =>
        AshitaBootFiles.List(dir).Select(n =>
        {
            try { return (n, AshitaBootConfig.Load(BootPath(dir, n)).Script); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (n, fallback); }
        }).ToList();
}
