using Provenance.Common.Game.Ashita;
using Provenance.Common.Game.Windower;
using Provenance.Common.Install;
using Provenance.Common.Settings;

namespace Provenance.Common.Game.ThirdParty;

public sealed record PlayTarget(ThirdPartyHook Platform, string? Dir, string Profile, bool FromAccount)
{
    public static PlayTarget For(LauncherSettings s, string? accountProfile) => For(s, accountProfile, s.ThirdPartyHookPlatform);

    public static PlayTarget For(LauncherSettings s, string? accountProfile, ThirdPartyHook platform)
    {
        switch (platform)
        {
            case ThirdPartyHook.Windower:
                return new(platform, WindowerInstall.Detect(s.WindowerPath), (accountProfile ?? s.WindowerProfile ?? "").Trim(), accountProfile is not null);
            case ThirdPartyHook.Ashita:
                var dir = AshitaInstall.Detect(s.AshitaPath);
                bool fromAccount = accountProfile is { Length: > 0 };
                var boot = fromAccount ? accountProfile
                         : string.IsNullOrWhiteSpace(s.AshitaBootConfig) ? AshitaInstall.LastUsedBootConfig(dir) : s.AshitaBootConfig;
                return new(platform, dir, boot ?? "", fromAccount);
            default:
                return new(ThirdPartyHook.None, null, "", false);
        }
    }

    public string? Picked(IEnumerable<string> names) => names.FirstOrDefault(Is);

    public bool Is(string name) => name.Equals(Profile, StringComparison.OrdinalIgnoreCase);
}
