using System.IO;

namespace Provenance.Wpf.ViewModels.Settings;

// what the Game page and the setup card make of a Windower or Ashita folder the player typed or browsed to
internal static class HookFolder
{
    internal static string? Usable(string? path) => string.IsNullOrWhiteSpace(path) ? null : path;

    // the line for a folder whose drive isn't there at all, which "couldn't find it in that folder" doesn't explain
    internal static string? DriveProblem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 2 || path[1] != ':' || !char.IsAsciiLetter(path[0])) return null;
        return Directory.Exists(path[..2] + "\\") ? null
            : $"Drive {char.ToUpperInvariant(path[0])}: isn't available to Provenance. If it's a SUBST drive, choose the folder by its real path instead.";
    }
}
