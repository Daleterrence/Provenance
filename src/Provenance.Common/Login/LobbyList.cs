using System.Globalization;
using Provenance.Common.Hook;
using Provenance.Common.IO;
using Provenance.Common.Settings;
using Serilog;

namespace Provenance.Common.Login;

public static class LobbyList
{
    private static readonly HashSet<int> Warned = [];

    // the hook writes the game's character list once it's seen it, and the account that game is logged in as takes it
    public static bool Take(ISettingsService settings, Func<IEnumerable<(int Pid, string ProfileId)>>? games = null)
    {
        if (!HookFiles.Any(HookContract.Files.Slots)) return false;
        bool any = false;
        foreach (var (pid, profileId) in (games ?? Running)())
        {
            if (!settings.Current.Profiles.Any(a => a.Id == profileId)) continue;
            try
            {
                var path = HookFiles.PathFor(pid, HookContract.Files.Slots);
                if (Read(path) is not { } text) continue;
                if (Parse(text) is { } list)
                    settings.SaveChange(s => any |= s.Profiles.FirstOrDefault(a => a.Id == profileId) is { } account && AutoLogin.Listed(account, list));
                // one the hook has written again since stays for the next look
                if (Read(path) == text) HookFiles.Delete(pid, HookContract.Files.Slots);
            }
            // on a timer in the window's thread, nothing can be let out of here
            catch (Exception e)
            {
                if (Warned.Add(pid)) Log.Warning("The game's character list couldn't be taken up ({Error}: {Message})", e.GetType().Name, e.Message);
            }
        }
        return any;
    }

    // null when it isn't there or the hook has it open. One far too long to be a list reads as nothing
    private static string? Read(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return !file.Exists ? null : file.Length > HookFiles.MaxBytes.Slots ? "" : SharedFile.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // a line a character: slot, name and world with tabs between. Anything that isn't that and the whole note is refused
    public static List<AccountCharacter>? Parse(string text)
    {
        if (!text.EndsWith('\n')) return null;
        var list = new List<AccountCharacter>();
        foreach (var line in text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = line.Split('\t');
            if (part.Length != 3 || !int.TryParse(part[0], NumberStyles.None, CultureInfo.InvariantCulture, out int slot) || !AutoLogin.IsSlot(slot)
                || list.Any(c => c.Slot == slot) || !AutoLogin.IsName(part[1])) return null;
            list.Add(new(part[1], AutoLogin.WorldNamed(part[2]), slot));
        }
        return list.Count > 0 ? list : null;
    }

    private static IEnumerable<(int Pid, string ProfileId)> Running() =>
        SessionStore.SessionFilePaths().Select(SessionStore.TryReadRecord)
            .Where(f => f is { Played: true, ProfileId: not null } && f.Game.IsRunning())
            .Select(f => (f!.Pid, f.ProfileId!));
}
