using Provenance.Common.Game.Ashita;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Game.Windower;
using Provenance.Common.Settings;

namespace Provenance.Common.Login;

public sealed record CommandLineTarget(Account Account, ThirdPartyHook Platform, string? Profile);

public static class CommandLineLaunch
{
    public sealed record Installed(IReadOnlyList<(string Name, string Shown)> Choices);

    public const string NeedsPlatform = "Logging in from the command line needs Windower or Ashita. Add --windower or --ashita to the command.";

    public static string NoAccount(string name) =>
        $"There's no saved account with the handle name {name}. Log in with it in Provenance once, then try again.";

    public static string NoProfile(ThirdPartyHook platform, string name) => platform == ThirdPartyHook.Ashita
        ? $"Ashita has no boot configuration called {name}."
        : $"Windower has no profile called {name}.";

    public static string NotSetUp(ThirdPartyHook platform) =>
        $"{(platform == ThirdPartyHook.Ashita ? "Ashita" : "Windower")} isn't set up in Provenance yet. Choose its folder in Settings, under Windower/Ashita.";

    public static CommandLineTarget? Resolve(LauncherSettings s, PlayRequest r, out string? problem) => Resolve(s, r, out problem, Look);

    internal static CommandLineTarget? Resolve(LauncherSettings s, PlayRequest r, out string? problem, Func<ThirdPartyHook, LauncherSettings, Installed?> look)
    {
        problem = null;
        if (AccountNames.Find(s.Profiles, r.Handle) is not { } account)
        {
            problem = NoAccount(r.Handle);
            return null;
        }
        var platform = r.Platform ?? s.ThirdPartyHookPlatform;
        if (platform is not (ThirdPartyHook.Windower or ThirdPartyHook.Ashita))
        {
            problem = NeedsPlatform;
            return null;
        }
        if (look(platform, s) is not { } installed)
        {
            problem = NotSetUp(platform);
            return null;
        }
        if (r.Profile is null) return new(account, platform, null);
        var picked = installed.Choices.FirstOrDefault(c => c.Name.Equals(r.Profile, StringComparison.OrdinalIgnoreCase)
                                                           || c.Shown.Equals(r.Profile, StringComparison.OrdinalIgnoreCase));
        if (picked.Name is null)
        {
            problem = NoProfile(platform, r.Profile);
            return null;
        }
        return new(account, platform, picked.Name);
    }

    private static Installed? Look(ThirdPartyHook platform, LauncherSettings s)
    {
        var target = PlayTarget.For(s, null, platform);
        if (target.Dir is null) return null;
        if (platform == ThirdPartyHook.Ashita)
            return new(AshitaBootFiles.Names(target.Dir).Select(n => (n.File, n.Shown)).ToList());
        var names = WindowerInstall.Profiles(target.Dir);
        return new(names.Select(n => (n, WindowerInstall.DisplayName(n, names))).ToList());
    }
}
