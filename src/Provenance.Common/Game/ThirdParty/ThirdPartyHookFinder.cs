using Microsoft.Win32;
using Provenance.Common.Game.Ashita;
using Provenance.Common.Game.Windower;
using Provenance.Common.IO;
using Provenance.Common.Security;
using Serilog;

namespace Provenance.Common.Game.ThirdParty;

// each list best first
public sealed record FoundHooks(IReadOnlyList<string> Windower, IReadOnlyList<string> Ashita);

// looks a lot wider than Detect does, which is fine for a one-off but not for something Play runs
public static class ThirdPartyHookFinder
{
    internal sealed record Sources(Func<IEnumerable<string>> ShortcutTargets, Func<IEnumerable<string>> UsualFolders,
                                   Func<IEnumerable<string>> RunPrograms, Func<string, bool> OnFixedDrive,
                                   Func<string, bool> IsWindower, Func<string, bool> IsAshita);

    internal static readonly Sources Real = new(ShortcutTargets, UsualFolders, RunPrograms, PathSafety.OnLocalFixedDrive,
                                                 WindowerHere, AshitaInstall.IsFolder);

    internal static Func<FoundHooks>? FindForTest;

    public static FoundHooks Find() => FindForTest is { } fake ? fake() : Find(Real);

    internal static FoundHooks Find(Sources from)
    {
        var folders = Read("shortcuts", () => from.ShortcutTargets().Select(Path.GetDirectoryName))
            .Concat(Read("usual folders", from.UsualFolders))
            .Concat(Read("run list", () => from.RunPrograms().Select(RunFolder)));
        var windower = new List<string>();
        var ashita = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            // the drive check comes first, a dead network path can hang anything that looks inside it
            if (Whole(folder) is not { } dir || !seen.Add(dir) || !Is(from.OnFixedDrive, dir)) continue;
            if (Is(from.IsWindower, dir)) windower.Add(dir);
            if (Is(from.IsAshita, dir)) ashita.Add(dir);
        }
        Log.Information("Looked for Windower and Ashita: {Windower} and {Ashita} folder(s) found", windower.Count, ashita.Count);
        return new(windower, ashita);
    }

    private static List<string?> Read(string what, Func<IEnumerable<string?>> source)
    {
        try { return source().ToList(); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read the {Source} while looking for Windower and Ashita", what);
            return [];
        }
    }

    private static bool Is(Func<string, bool> check, string dir)
    {
        try { return check(dir); }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not look in {Folder} for Windower or Ashita", dir);
            return false;
        }
    }

    private static string? Whole(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathFullyQualified(dir)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return null; }
    }

    // a launch refuses a Windower folder that's reached through a link, so there's no point offering one
    private static bool WindowerHere(string dir) => WindowerInstall.IsSetUp(dir) && !FolderPins.Linked(dir);

    private static IEnumerable<string> ShortcutTargets()
    {
        var places = new (string Folder, bool Subfolders)[]
        {
            (AppPaths.Special(Environment.SpecialFolder.DesktopDirectory), false),
            (AppPaths.Special(Environment.SpecialFolder.CommonDesktopDirectory), false),
            (AppPaths.Special(Environment.SpecialFolder.StartMenu), true),
            (AppPaths.Special(Environment.SpecialFolder.CommonStartMenu), true),
            (Path.Combine(AppPaths.Special(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"), false),
        };
        return places.SelectMany(p => Read("shortcuts in " + p.Folder, () => TargetsUnder(p.Folder, p.Subfolders))).OfType<string>()
            .SelectMany(t => InEitherProgramFiles(t, AppPaths.ProgramFilesX86, ProgramFiles64));
    }

    private static string? ProgramFiles64 => Environment.GetEnvironmentVariable("ProgramW6432");

    // both of AppPaths' are the x86 folder in a 32-bit app, ProgramW6432 is the only way to the other one
    internal static IEnumerable<string> ProgramFilesRoots() =>
        new[] { AppPaths.ProgramFilesX86, AppPaths.ProgramFiles, ProgramFiles64 }.OfType<string>().Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    // Windows hands a 32-bit app the x86 Program Files for a shortcut into the 64-bit one, so a target that isn't there
    // gets tried in both
    internal static IEnumerable<string> InEitherProgramFiles(string target, string x86, string? x64)
    {
        yield return target;
        if (x86.Length == 0 || string.IsNullOrEmpty(x64)) yield break;
        var under = Path.TrimEndingDirectorySeparator(x86) + Path.DirectorySeparatorChar;
        if (target.StartsWith(under, StringComparison.OrdinalIgnoreCase) && !File.Exists(target))
            yield return Path.Combine(x64, target[under.Length..]);
    }

    internal static IEnumerable<string> TargetsUnder(string folder, bool subfolders)
    {
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)) return [];
        var walk = new EnumerationOptions { RecurseSubdirectories = subfolders, MaxRecursionDepth = 4, IgnoreInaccessible = true };
        return Directory.EnumerateFiles(folder, "*.lnk", walk).Select(Shortcut.TargetOf).OfType<string>();
    }

    private static IEnumerable<string> UsualFolders() =>
        UsualFolders(DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed).Select(d => d.Name),
                     Path.GetPathRoot(AppPaths.Windows) ?? @"C:\", ProgramFilesRoots());

    internal static IEnumerable<string> UsualFolders(IEnumerable<string> driveRoots, string windowsDrive, IEnumerable<string> programFiles)
    {
        foreach (var root in driveRoots.OrderBy(r => !r.Equals(windowsDrive, StringComparison.OrdinalIgnoreCase)))
        {
            yield return Path.Combine(root, "Windower");
            yield return Path.Combine(root, "Ashita");
        }
        var roots = programFiles.ToList();
        foreach (var names in new[] { WindowerInstall.ProgramFilesNames, AshitaInstall.ProgramFilesNames })
            foreach (var root in roots)
                foreach (var name in names) yield return Path.Combine(root, name);
    }

    private static readonly string[] RunEndings = [".FriendlyAppName", ".ApplicationCompany"];
    private static readonly string[] RunNames = ["Windower.exe", "Ashita-cli.exe"];

    // Windows keeps a value for each program this user has run, named by the exe's path with one of RunEndings after it
    private static IEnumerable<string> RunPrograms()
    {
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = user.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache");
        return key?.GetValueNames() ?? [];
    }

    private static string? RunFolder(string name)
    {
        var path = RunEndings.FirstOrDefault(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) is { } ending ? name[..^ending.Length] : name;
        return RunNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase) ? Path.GetDirectoryName(path) : null;
    }
}
