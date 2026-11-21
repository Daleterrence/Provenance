using System.Globalization;
using Provenance.Common.Hook;
using Provenance.Common.Settings;

namespace Provenance.Common.Login;

public static class LobbyList
{
    // the hook writes the game's character list once it's seen it, and the account that game is logged in as takes it
    public static bool Take(ISettingsService settings, Func<IEnumerable<(int Pid, string ProfileId)>>? games = null)
    {
        if (!HookFiles.Any(HookContract.Files.Slots)) return false;
        bool any = false;
        foreach (var (pid, profileId) in (games ?? Running)())
        {
            if (!settings.Current.Profiles.Any(a => a.Id == profileId)) continue;
            if (HookFiles.ReadJson(pid, HookContract.Files.Slots, HookFiles.MaxBytes.Slots, Parse) is not { } list) continue;
            HookFiles.Delete(pid, HookContract.Files.Slots);
            settings.SaveChange(s => any |= s.Profiles.FirstOrDefault(a => a.Id == profileId) is { } account && AutoLogin.Listed(account, list));
        }
        return any;
    }

    // a line a character: slot, name and world with tabs between. Anything that isn't that and the whole note is refused
    public static List<AccountCharacter>? Parse(string text)
    {
        if (!text.EndsWith('\n')) return null;
        var list = new List<AccountCharacter>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = line.Split('\t');
            if (part.Length != 3 || !int.TryParse(part[0], NumberStyles.None, CultureInfo.InvariantCulture, out int slot) || !AutoLogin.IsSlot(slot)
                || list.Any(c => c.Slot == slot)) return null;
            if (AutoLogin.IsName(part[1])) list.Add(new(part[1], AutoLogin.WorldNamed(part[2]), slot));
        }
        return list.Count > 0 ? list : null;
    }

    private static IEnumerable<(int Pid, string ProfileId)> Running() =>
        SessionStore.SessionFilePaths().Select(SessionStore.TryReadRecord)
            .Where(f => f is { Played: true, ProfileId: not null } && f.Game.IsRunning())
            .Select(f => (f!.Pid, f.ProfileId!));
}
