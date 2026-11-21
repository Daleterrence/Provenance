using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Game.Windower;

namespace Provenance.Common.Settings;

public sealed class LauncherSettings
{
    public List<Account> Profiles { get; set; } = [new Account { Id = "default", DisplayName = "Default" }];

    public string ActiveProfileId { get; set; } = "default";

    public bool SetupPending { get; set; }

    public static LauncherSettings FirstStart() => new() { Profiles = [], ActiveProfileId = "", SetupPending = true };

    public bool AutoLogin { get; set; }

    public bool ExitAfterGameExits { get; set; } = true;

    public string FeedRegion { get; set; } = "auto";

    public bool StartGameImmediately { get; set; }

    public bool KeepSignedInInTray { get; set; }

    public List<MultiAccountEntry> MultiAccounts { get; set; } = [];

    public double MessagesPaneWidth { get; set; }

    public bool AddonSectionsFolded { get; set; }

    public string Theme { get; set; } = "dark";

    public bool MinimizeToTray { get; set; }

    public bool TrayTipShown { get; set; }

    public bool NotifyFriendsOnline { get; set; } = true;
    public bool NotifyMessages { get; set; } = true;
    public bool FlashMessages { get; set; } = true;
    public bool NotifyFriendRequests { get; set; } = true;
    public bool FlashFriendRequests { get; set; } = true;
    public bool NotifyGroupInvites { get; set; } = true;
    public bool FlashGroupInvites { get; set; } = true;
    public bool NotifyAnswers { get; set; } = true;
    public bool FlashAnswers { get; set; } = true;
    public bool GroupChatNotice { get; set; } = true;
    public bool GroupChatFlash { get; set; } = true;
    public bool GameMessageSound { get; set; } = true;
    public bool GameFriendLines { get; set; } = true;

    public bool ShowTimestamps { get; set; } = true;

    // 0 online, 1 away, 2 busy, 4 appear offline
    public int PlayOnlineStatus { get; set; }

    public ThirdPartyHook ThirdPartyHookPlatform { get; set; } = ThirdPartyHook.None;
    public string AshitaPath { get; set; } = "";
    public string AshitaBootConfig { get; set; } = "";
    public string WindowerPath { get; set; } = "";
    public string WindowerProfile { get; set; } = "";
    public Dictionary<string, WindowerAddonList> WindowerAddonLists { get; set; } = [];
    public WindowerAddonList? WindowerSharedExtras { get; set; }

    public bool AcceptedMemberAgreement { get; set; }

    public bool MultiContinueOnError { get; set; } = true;

    public bool MultiCloseWhenFinished { get; set; } = true;

    public bool MultiStartAtOpen { get; set; }

    public bool AcceptLicenceAgreement { get; set; } = true;

    // SE's patches can put a plain pol.exe back, so the bit goes on again before every start
    public bool LargeAddressAware { get; set; } = true;

    public bool LargeAddressAwareClearAtStart { get; set; }

    public int LastHomeSlot { get; set; }

    public bool? ChatLanguageFilter { get; set; }

    public int? AutoDisconnectMinutes { get; set; }

    public bool? ScreenSaverWasOn { get; set; }

    public bool BackupGameSettings { get; set; }

    public Dictionary<string, WindowPlace> WindowPlaces { get; set; } = [];

    public string BackupFolder { get; set; } = "";

    public Dictionary<string, int> PasswordRefusals { get; set; } = [];
}

public sealed class WindowPlace
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}
