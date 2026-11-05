using System.IO;
using Provenance.Common.Game.Ashita;
using Provenance.Common.Game.Windower;

namespace Provenance.Wpf.Services;

public static class HookProfiles
{
    private static bool Same(string? a, string b) => a is not null && a.Trim().Equals(b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool On(LauncherSettings s, string platform) =>
        (s.ThirdPartyHookPlatform ?? "").Trim().Equals(platform, StringComparison.OrdinalIgnoreCase);

    public static string AddWindower(ISettingsService settings, string dir, string name)
    {
        string added = "";
        WindowerSettingsFile.Update(dir, settings.Current.WindowerAddonLists, (f, l) => added = f.Add(name, l).Name);
        settings.Save(settings.Current);
        return added;
    }

    public static string DuplicateWindower(ISettingsService settings, string dir, string source, string name)
    {
        string made = "";
        WindowerSettingsFile.Update(dir, settings.Current.WindowerAddonLists, (f, l) => made = f.Duplicate(source, name, l).Name);
        settings.Save(settings.Current);
        return made;
    }

    public static string RenameWindower(ISettingsService settings, string dir, string name, string newName)
    {
        string renamed = "";
        var s = settings.Current;
        WindowerSettingsFile.Update(dir, s.WindowerAddonLists, (f, l) => renamed = f.Rename(name, newName, l).Name);
        // a launch finds a profile by its name in any case, so these do too
        if (Same(s.WindowerProfile, name)) s.WindowerProfile = renamed;
        if (On(s, ThirdPartyHook.Windower))
            foreach (var e in s.MultiAccounts.Where(e => Same(e.ThirdPartyHookProfile, name))) e.ThirdPartyHookProfile = renamed;
        settings.Save(s);
        return renamed;
    }

    // anything that used it goes back to the default, an account to whatever the Windower/Ashita page says
    public static void DeleteWindower(ISettingsService settings, string dir, string name)
    {
        var s = settings.Current;
        WindowerSettingsFile.Update(dir, s.WindowerAddonLists, (f, l) => f.Delete(name, l));
        if (Same(s.WindowerProfile, name)) s.WindowerProfile = "";
        if (On(s, ThirdPartyHook.Windower))
            foreach (var e in s.MultiAccounts.Where(e => Same(e.ThirdPartyHookProfile, name))) e.ThirdPartyHookProfile = null;
        settings.Save(s);
    }

    // "mychar" is taken to mean mychar.ini
    public static string AshitaFileName(string name)
    {
        var n = name.Trim();
        return n.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) ? n : n + ".ini";
    }

    public static string? AshitaNameProblem(string dir, string name, string? renaming = null)
    {
        var file = AshitaFileName(name);
        if (AshitaBootFiles.NameProblem(file, dir) is { } problem) return problem;
        if (File.Exists(Path.Combine(AshitaBootFiles.Folder(dir), file)) && !file.Equals(renaming, StringComparison.OrdinalIgnoreCase))
            return $"There's already a boot configuration called {file}.";
        return null;
    }

    public static string AddAshita(string dir, string name, int playOnline) =>
        Path.GetFileName(AshitaBootFiles.Create(dir, AshitaFileName(name), fromExample: true, playOnline));

    public static string DuplicateAshita(string dir, string source, string name) =>
        Path.GetFileName(AshitaBootFiles.Duplicate(dir, source, AshitaFileName(name)));

    public static string RenameAshita(ISettingsService settings, string dir, string name, string newName)
    {
        var renamed = Path.GetFileName(AshitaBootFiles.Rename(dir, name, AshitaFileName(newName)));
        var s = settings.Current;
        if (s.AshitaBootConfig.Equals(name, StringComparison.OrdinalIgnoreCase)) s.AshitaBootConfig = renamed;
        if (On(s, ThirdPartyHook.Ashita))
            foreach (var e in s.MultiAccounts.Where(e => name.Equals(e.ThirdPartyHookProfile, StringComparison.OrdinalIgnoreCase)))
                e.ThirdPartyHookProfile = renamed;
        settings.Save(s);
        return renamed;
    }

    public static void DeleteAshita(ISettingsService settings, string dir, string name)
    {
        AshitaBootFiles.Delete(dir, name);
        var s = settings.Current;
        if (s.AshitaBootConfig.Equals(name, StringComparison.OrdinalIgnoreCase)) s.AshitaBootConfig = "";
        if (On(s, ThirdPartyHook.Ashita))
            foreach (var e in s.MultiAccounts.Where(e => name.Equals(e.ThirdPartyHookProfile, StringComparison.OrdinalIgnoreCase)))
                e.ThirdPartyHookProfile = null;
        settings.Save(s);
    }

    // a stored name of one of the old "(Provenance)" copies means its source. True when anything changed
    public static bool MapOldCopies(LauncherSettings s)
    {
        bool changed = false;
        if (WindowerSettingsFile.IsOldCopy(s.WindowerProfile))
        {
            s.WindowerProfile = WindowerSettingsFile.OldCopySource(s.WindowerProfile);
            changed = true;
        }
        if (!On(s, ThirdPartyHook.Windower)) return changed;
        foreach (var e in s.MultiAccounts.Where(e => WindowerSettingsFile.IsOldCopy(e.ThirdPartyHookProfile)))
        {
            e.ThirdPartyHookProfile = WindowerSettingsFile.OldCopySource(e.ThirdPartyHookProfile);
            changed = true;
        }
        return changed;
    }
}
