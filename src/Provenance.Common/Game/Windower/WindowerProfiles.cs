using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Settings;

namespace Provenance.Common.Game.Windower;

// a settings.xml read before, so a name box checking every keystroke doesn't read the file each time
public sealed class WindowerProfiles(ISettingsService settings, string dir, WindowerSettingsFile? snapshot = null) : ILaunchProfiles
{
    private WindowerSettingsFile Current => snapshot ?? WindowerSettingsFile.Load(dir);

    public IReadOnlyList<string> List() => Current.Profiles.Select(p => p.Name).ToList();

    public string? NameProblem(string name, string? renaming = null)
    {
        var file = Current;
        return file.NameProblem(name, renaming is null ? null : file.Profiles.FirstOrDefault(p => p.Name == renaming));
    }

    public string Add(string name)
    {
        string added = "";
        WindowerSettingsFile.Update(dir, settings.Current.WindowerAddonLists, (f, l) => added = f.Add(name, l).Name);
        settings.Save(settings.Current);
        return added;
    }

    public string Duplicate(string source, string name)
    {
        string made = "";
        WindowerSettingsFile.Update(dir, settings.Current.WindowerAddonLists, (f, l) => made = f.Duplicate(source, name, l).Name);
        settings.Save(settings.Current);
        return made;
    }

    public string Rename(string name, string newName)
    {
        string renamed = "";
        WindowerSettingsFile.Update(dir, settings.Current.WindowerAddonLists, (f, l) => renamed = f.Rename(name, newName, l).Name);
        LaunchProfiles.Repoint(settings, ThirdPartyHook.Windower, name, renamed);
        settings.Save(settings.Current);
        return renamed;
    }

    public void Delete(string name)
    {
        WindowerSettingsFile.Update(dir, settings.Current.WindowerAddonLists, (f, l) => f.Delete(name, l));
        LaunchProfiles.Repoint(settings, ThirdPartyHook.Windower, name, null);
        settings.Save(settings.Current);
    }
}
