using System.Text.Json.Serialization;
using Provenance.Common.Game.Windower;
using Provenance.Common.PolFormat;

namespace Provenance.Wpf.Services;

// %APPDATA%\Provenance\settings.json
public interface ISettingsService
{
    LauncherSettings Current { get; }

    // from the registry, not a setting. A PC only ever has one working install
    PolRegion? DetectedRegion { get; }

    // after an install or a located one, which can be another region than the one found before
    void RedetectRegion() { }

    void Save(LauncherSettings settings);

    // only the first failed save of a run raises it, so a full disk doesn't pop a dialog on every change
    event Action<string>? SaveFailed;

    string? LoadProblem { get; }

    // settings.json as it is now, another copy may have saved since
    LauncherSettings? ReadSaved() => null;

    // the lock Save and ReadSaved take, held across both (re-entrant on this thread)
    IDisposable? HoldLock() => null;
}

public sealed class LauncherSettings
{
    public List<Profile> Profiles { get; set; } = [new Profile { Id = "default", DisplayName = "Default" }];

    public string ActiveProfileId { get; set; } = "default";

    // shift at start-up or --noautologin skips it, and a rejected sign-in turns it off
    public bool AutoLogin { get; set; }

    public bool ExitAfterGameExits { get; set; } = true;

    // "auto" follows the installed game, otherwise "na", "eu" or "jp"
    public string FeedRegion { get; set; } = "auto";

    public bool StartGameImmediately { get; set; }

    public bool KeepSignedInInTray { get; set; } = true;

    public List<MultiAccountEntry> MultiAccounts { get; set; } = [];

    public bool SocialSplit { get; set; }

    public bool MinimizeToTray { get; set; }

    // 0 = never, otherwise 10-60 mins like POL's own sign-out timer
    public int TraySignOutMinutes { get; set; }

    public bool TrayTipShown { get; set; }

    public bool NotifyFriendsOnline { get; set; } = true;

    // 0 online, 1 away, 2 busy, 4 appear offline
    public int PlayOnlineStatus { get; set; }

    public string ThirdPartyHookPlatform { get; set; } = "none";
    public string AshitaPath { get; set; } = "";
    public string AshitaBootConfig { get; set; } = "";
    public string WindowerPath { get; set; } = "";
    // "" is Windower's default profile
    public string WindowerProfile { get; set; } = "";
    // by Windower profile name, and no entry means settings.xml's own list
    public Dictionary<string, WindowerAddonList> WindowerAddonLists { get; set; } = [];

    public bool AcceptedMemberAgreement { get; set; }

    public bool MultiContinueOnError { get; set; } = true;

    public bool MultiCloseWhenFinished { get; set; } = true;

    // SE's patches can put a plain pol.exe back, so the bit goes on again before every start
    public bool LargeAddressAware { get; set; } = true;

    // turning it off couldn't clear pol.exe there and then, so the next start does it, once
    public bool LargeAddressAwareClearAtStart { get; set; }

    // the highest home slot handed out so far, so a removed account's number isn't given to the next one
    public int LastHomeSlot { get; set; }

    // POL's own, which the game uses too. Null leaves the accounts' as they are, till they're picked in Settings
    // (or set off by a new install)
    public bool? ChatLanguageFilter { get; set; }

    // -1 is off
    public int? AutoDisconnectMinutes { get; set; }
}

public sealed class MultiAccountEntry
{
    public string ProfileId { get; set; } = "";
    public bool Enabled { get; set; }
    // null uses the Windower/Ashita tab's setting
    public string? ThirdPartyHookProfile { get; set; }
    // why a run turned it off. Never retried by itself, three refusals lock the account
    public string? Problem { get; set; }
    public bool CredentialsRefused { get; set; }
}

// Id is the Credential Manager key so it can't change. DisplayName is the handle, the POL and SE IDs stay off screen
public sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string DisplayName { get; set; } = "New account";

    public string? HandleName { get; set; }

    // profile picture, 0 = none. The JSON name has to stay, saved accounts find their pics by it
    [JsonPropertyName("FaceId")]
    public int ProfilePictureId { get; set; }

    // the code itself is asked for at every sign-in and never stored
    public bool UseOtp { get; set; }

    // "" (typed in each time), "bitwarden", "1password" or "keepassxc"
    public string OtpSource { get; set; } = "";

    // item name or id, or the entry URL for KeePassXC. Not a secret
    public string OtpItem { get; set; } = "";

    // the viewer folder's pub\homeNN this account's POL files go in, 0 till it's given one
    public int HomeSlot { get; set; }

    // the 1Password account that holds the item, "" lets op pick
    public string OtpAccount { get; set; } = "";
}
