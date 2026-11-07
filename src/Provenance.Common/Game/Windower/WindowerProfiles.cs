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

    public string NewName() => Current.NewProfileName();

    public string CopyName(string name, string display)
    {
        var copy = $"{display} copy";
        return NameProblem(copy) is null ? copy : NewName();
    }

    public string Canonical(string typed) => typed;

    public string Add(string name) => Edit((f, l) => f.Add(name, l).Name);

    public string Duplicate(string source, string name) => Edit((f, l) => f.Duplicate(source, name, l).Name);

    public string Rename(string name, string newName)
    {
        var renamed = "";
        return Edit((f, l) => renamed = f.Rename(name, newName, l).Name, () => Repoint(name, renamed));
    }

    public void Delete(string name) =>
        Edit((f, l) => { f.Delete(name, l); return ""; }, () => Repoint(name, null));

    // the settings only get saved once everything's been changed, so the save can't be missed
    private string Edit(Func<WindowerSettingsFile, IDictionary<string, WindowerAddonList>, string> change, Action? then = null)
    {
        var result = "";
        WindowerSettingsFile.Update(dir, settings.Current.WindowerAddonLists, (f, l) => result = change(f, l));
        then?.Invoke();
        settings.Save(settings.Current);
        return result;
    }

    private void Repoint(string old, string? replacement) =>
        LaunchProfiles.Repoint(settings, ThirdPartyHook.Windower, old, replacement, s =>
        {
            if (LaunchProfiles.Same(s.WindowerProfile, old)) s.WindowerProfile = replacement ?? "";
        });
}
