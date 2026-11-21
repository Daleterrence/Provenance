using Provenance.Common.Hook;
using Provenance.Common.Settings;

namespace Provenance.Common.Login;

public static class LobbyPicks
{
    public static bool Waiting(ISettingsService settings) => settings.Current.Profiles.Any(BySlot);

    // the hook leaves a note when it picks by slot: the character's name, then its world
    public static bool Take(ISettingsService settings, Func<IEnumerable<(int Pid, string ProfileId)>>? games = null)
    {
        if (!Waiting(settings)) return false;
        bool any = false;
        foreach (var (pid, profileId) in (games ?? Running)())
        {
            if (settings.Current.Profiles.FirstOrDefault(a => a.Id == profileId) is not { } account || !BySlot(account)) continue;
            if (HookFiles.ReadJson(pid, HookContract.Files.Picked, 64, text => text) is not { } note) continue;
            var lines = note.Split('\n');
            if (AutoLogin.PickFor(account, lines[0], lines.Length > 1 ? lines[1] : null) is not { } pick) continue;
            settings.SaveChange(s =>
            {
                if (s.Profiles.FirstOrDefault(a => a.Id == profileId) is { } saved)
                    (saved.AutoLoginCharacter, saved.AutoLoginWorld, saved.AutoLoginSlot) = (pick.Name, pick.World, null);
            });
            HookFiles.Delete(pid, HookContract.Files.Picked);
            any = true;
        }
        return any;
    }

    private static bool BySlot(Account a) => a.AutoLoginCharacter is null && AutoLogin.IsSlot(a.AutoLoginSlot);

    private static IEnumerable<(int Pid, string ProfileId)> Running() =>
        SessionStore.SessionFilePaths().Select(SessionStore.TryReadRecord)
            .Where(f => f is { Played: true, ProfileId: not null } && f.Game.IsRunning())
            .Select(f => (f!.Pid, f.ProfileId!));
}
