using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Game.Windower;

namespace Provenance.Common.Settings;

public sealed class LauncherSettings
{
    public List<Account> Profiles { get; set; } = [new Account { Id = "default", DisplayName = "Default" }];

    public string ActiveProfileId { get; set; } = "default";

    // on from a start with no settings.json till the Windower/Ashita question's answered
    public bool SetupPending { get; set; }

    public static LauncherSettings FirstStart() => new() { Profiles = [], ActiveProfileId = "", SetupPending = true };

    // shift at start-up or --noautologin skips it, and a rejected login turns it off
    public bool AutoLogin { get; set; }

    public bool ExitAfterGameExits { get; set; } = true;

    // "auto" follows the installed game, otherwise "na", "eu" or "jp"
    public string FeedRegion { get; set; } = "auto";

    public bool StartGameImmediately { get; set; }

    public bool KeepSignedInInTray { get; set; }

    public List<MultiAccountEntry> MultiAccounts { get; set; } = [];

    // how wide the conversation pane in the Friends window was last dragged to, 0 for the default
    public double MessagesPaneWidth { get; set; }

    public bool AddonSectionsFolded { get; set; }

    public string Theme { get; set; } = "dark";

    public bool MinimizeToTray { get; set; }

    public bool TrayTipShown { get; set; }

    // one notice switch and one taskbar flash switch for each kind of news the friends side shows. A friend coming
    // online never flashes, so it has no flash
    public bool NotifyFriendsOnline { get; set; } = true;
    public bool NotifyMessages { get; set; } = true;
    public bool FlashMessages { get; set; } = true;
    public bool NotifyFriendRequests { get; set; } = true;
    public bool FlashFriendRequests { get; set; } = true;
    public bool NotifyGroupInvites { get; set; } = true;
    public bool FlashGroupInvites { get; set; } = true;
    public bool NotifyAnswers { get; set; } = true;
    public bool FlashAnswers { get; set; } = true;
    // a busy group chat with its window hidden or behind
    public bool GroupChatNotice { get; set; } = true;
    public bool GroupChatFlash { get; set; } = true;

    // the time next to messages, group chat lines and chat room lines
    public bool ShowTimestamps { get; set; } = true;

    // 0 online, 1 away, 2 busy, 4 appear offline
    public int PlayOnlineStatus { get; set; }

    public ThirdPartyHook ThirdPartyHookPlatform { get; set; } = ThirdPartyHook.None;
    public string AshitaPath { get; set; } = "";
    public string AshitaBootConfig { get; set; } = "";
    public string WindowerPath { get; set; } = "";
    public string WindowerProfile { get; set; } = "";
    // by Windower profile name, and no entry means the shared list
    public Dictionary<string, WindowerAddonList> WindowerAddonLists { get; set; } = [];
    // the shared list's addons and plugins that Windower.exe doesn't offer, so they stay out of settings.xml
    public WindowerAddonList? WindowerSharedExtras { get; set; }

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

    public int? AutoDisconnectMinutes { get; set; }

    // the last read with no pol.exe running, since a running one has it switched off
    public bool? ScreenSaverWasOn { get; set; }

    public bool BackupGameSettings { get; set; }

    // each extra window's last place and size, by window name
    public Dictionary<string, WindowPlace> WindowPlaces { get; set; } = [];

    // "" is %APPDATA%\Provenance\Backups
    public string BackupFolder { get; set; } = "";
}

public sealed class WindowPlace
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}
