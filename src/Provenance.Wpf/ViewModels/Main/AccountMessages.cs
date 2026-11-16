using System.IO;
using System.Text.RegularExpressions;
using Provenance.Common;
using Serilog;

namespace Provenance.Wpf.ViewModels.Main;

// the files ChatStore and GroupChatLog keep for one account, and any copy of them that was set aside unreadable
internal static partial class AccountMessages
{
    [GeneratedRegex(@"^(dat|deleted\.dat|groups\.dat)(\.\d{14}\.unreadable|\.[0-9a-f]{32}\.tmp)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Kept();

    internal static string? Folder => AppPaths.UserData is { } userData ? Path.Combine(userData, "messages") : null;

    // best effort, a file that won't go is logged and left
    internal static int Delete(string profileId, string? folder = null)
    {
        folder ??= Folder;
        int gone = 0;
        if (profileId.Length == 0 || folder is null || !Directory.Exists(folder)) return gone;
        var prefix = profileId + ".";
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, prefix + "*"))
            {
                var name = Path.GetFileName(file);
                if (!Kept().IsMatch(name[prefix.Length..])) continue;
                try
                {
                    File.Delete(file);
                    gone++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warning(ex, "Could not delete the saved messages file {Name} of account {Id}", name, profileId);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not look through the saved messages of account {Id}", profileId);
        }
        return gone;
    }
}
