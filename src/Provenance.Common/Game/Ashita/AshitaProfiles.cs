using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Settings;

namespace Provenance.Common.Game.Ashita;

public sealed class AshitaProfiles(ISettingsService settings, string dir, int playOnline) : ILaunchProfiles
{
    // "<name>" is taken to mean <name>.ini
    public static string FileName(string name)
    {
        var n = name.Trim();
        return n.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) ? n : n + ".ini";
    }

    public IReadOnlyList<string> List() => AshitaBootFiles.List(dir);

    public string? NameProblem(string name, string? renaming = null)
    {
        var file = FileName(name);
        if (AshitaBootFiles.NameProblem(file, dir) is { } problem) return problem;
        if (File.Exists(Path.Combine(AshitaBootFiles.Folder(dir), file)) && !file.Equals(renaming, StringComparison.OrdinalIgnoreCase))
            return $"There's already a boot configuration called {file}.";
        return null;
    }

    public string NewName()
    {
        for (int i = 1; i <= 999; i++)
            if (NameProblem($"profile{i}.ini") is null) return $"profile{i}.ini";
        return "profile1.ini";
    }

    public string CopyName(string name, string display)
    {
        var copy = $"{Path.GetFileNameWithoutExtension(name)}-copy.ini";
        return NameProblem(copy) is null ? copy : NewName();
    }

    public string Canonical(string typed) => FileName(typed);

    public string Add(string name) =>
        Path.GetFileName(AshitaBootFiles.Create(dir, FileName(name), fromExample: true, playOnline));

    // a file really called " x.ini" wins, the trimmed name only stands in for padding that isn't in the file's name
    private string FileOf(string name) =>
        File.Exists(Path.Combine(AshitaBootFiles.Folder(dir), name)) ? name : name.Trim();

    public string Duplicate(string source, string name) =>
        Path.GetFileName(AshitaBootFiles.Duplicate(dir, FileOf(source), FileName(name)));

    public string Rename(string name, string newName)
    {
        var renamed = Path.GetFileName(AshitaBootFiles.Rename(dir, FileOf(name), FileName(newName)));
        Repoint(name, renamed);
        return renamed;
    }

    public void Delete(string name)
    {
        AshitaBootFiles.Delete(dir, FileOf(name));
        Repoint(name, null);
    }

    private void Repoint(string old, string? replacement) =>
        LaunchProfiles.Repoint(settings, ThirdPartyHook.Ashita, old, replacement, s =>
        {
            if (LaunchProfiles.Same(s.AshitaBootConfig, old)) s.AshitaBootConfig = replacement ?? "";
        });
}
