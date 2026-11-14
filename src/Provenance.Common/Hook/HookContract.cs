namespace Provenance.Common.Hook;

// pol.exe's exit codes with the hook's names for them (native/hook/hook_contract.h), 0 is a clean end
public enum HookExit
{
    Refused = 3,                // AUTH_REFUSED_EXIT
    NoHandle = 4,               // NO_HANDLE_EXIT
    HandleRefused = 5,          // HANDLE_REFUSED_EXIT
    TurnedAway = 7,             // TURNED_AWAY_EXIT
    Stopped = 8,                // STOPPED_EXIT
    ListLoadFailed = 10,        // LIST_LOAD_EXIT
    DetailsUnreadable = 11,     // DETAILS_UNREADABLE_EXIT
    BadIpcFolder = 12,          // IPC_DIR_EXIT
    UnsafeStart = 13,           // MUTEX_EXIT
    GameStartFailed = 15,       // GAME_START_EXIT
    NotStartedByProvenance = 16, // NO_ENV_EXIT
    NotHooked = 17,             // NOT_HOOKED_EXIT
    AddressesNotFound = 18,     // ADDRESSES_EXIT
}

// the same names as native/hook/hook_contract.h, a change goes in both
public static class HookContract
{
    public static class Env
    {
        public const string IpcDir = "POL_IPC_DIR";
        public const string CredKeyfile = "POL_CRED_KEYFILE";
        public const string Hold = "POL_HOLD";
        public const string Owner = "POL_OWNER";
        public const string Screensaver = "POL_SCREENSAVER";
        public const string Prestart = "POL_PRESTART";
        public const string Lean = "POL_LEAN";
        public const string CredPol = "POL_CRED_POL";
        public const string CredSea = "POL_CRED_SEA";
        public const string CredOtp = "POL_CRED_OTP";
        public const string CredOneshot = "POL_CRED_ONESHOT";
        public const string CredHandle = "POL_CRED_HANDLE";
        public const string CredPlay = "POL_CRED_PLAY";
        public const string CredChat = "POL_CRED_CHAT";
        public const string Status = "POL_STATUS";
        public const string Home = "POL_HOME";
        public const string Handle = "POL_HANDLE";
        public const string LaunchId = "PROVENANCE_LAUNCH_ID";
        public const string Launch = "PROVENANCE_LAUNCH";

        // the hook reads anything starting with either, so a launch clears them out of the user's own environment
        public const string PolPrefix = "POL_";
        public const string ProvenancePrefix = "PROVENANCE_";

        // a pre-started pol.exe's go file carries these, the hook drops any other key
        public static readonly string[] GoFile =
            [CredPol, CredSea, CredOneshot, CredOtp, CredHandle, CredPlay, CredChat, Status, Home, Handle, Lean];
    }

    // a request file is pol_hook.<pid>.<name>, written by the app, and the hook renames it to .taking while it reads it
    public static class Requests
    {
        public const string Send = "send";
        public const string Status = "status";
        public const string Ack = "ack";
        public const string Comment = "comment";
        public const string Pic = "pic";
        public const string Friend = "friend";
        public const string Answer = "answer";
        public const string Search = "search";
        public const string Profile = "profile";
        public const string Group = "group";
        public const string MyProfile = "myprofile";
        public const string SaveProfile = "saveprofile";
        public const string Rename = "rename";
        public const string Character = "character";
        public const string FfxiProfile = "ffxiprofile";
        public const string MyFfxiProfile = "myffxiprofile";
        public const string ChatZones = "chatzones";
        public const string ChatRooms = "chatrooms";
        public const string ChatJoin = "chatjoin";
        public const string ChatSend = "chatsend";
        public const string ChatLeave = "chatleave";
        public const string ChatCreate = "chatcreate";
        public const string ChatMaster = "chatmaster";
        public const string Handle = "handle";
        public const string GroupChatJoin = "groupchatjoin";
        public const string GroupChatSend = "groupchatsend";
        public const string GroupChatLeave = "groupchatleave";
        public const string Full = "full";

        public static readonly string[] All =
        [
            Send, Status, Ack, Comment, Pic, Friend, Answer, Search, Profile, Group, MyProfile, SaveProfile, Rename,
            Character, FfxiProfile, MyFfxiProfile, ChatZones, ChatRooms, ChatJoin, ChatSend, ChatLeave, ChatCreate, ChatMaster,
            Handle, GroupChatJoin, GroupChatSend, GroupChatLeave, Full,
        ];
    }

    public static class Files
    {
        public const string Log = "log";
        public const string Social = "social.json";
        public const string Store = "store.json";
        public const string ChatRoom = "chatroom.json";
        public const string GroupChat = "groupchat.json";
        public const string MyProfile = "myprofile.json";
        public const string Ended = "ended";
        public const string Go = "go";
        public const string Stop = "stop";
        // the app's own, the hook never opens it
        public const string Prestart = "prestart";
        public const string Taking = ".taking";
        // the format of every file's name, pid then name, and of the pid file a Windower or Ashita launch gets (launch id)
        public const string Name = "pol_hook.{0}.{1}";
        public const string PidFile = "provenance.{0}.pid";
        // the hold makes it and the app sets it after writing the Play entry, so Play doesn't wait for the hold's next look
        public const string PlayEvent = "provenance_play.{0}";
    }

    // pieces of the hook's log lines (and the watcher's output) that the app looks for, so none of them get reworded
    public static class LogLines
    {
        public const string FirstLine = "pol_hook: highest user address";
        public const string ViewerLang = "viewer_init: lang=";
        public const string RealAuth = "login: logging in";
        public const string AuthState = "login: state=";
        public const string AuthOk = "login: logged in";
        public const string Profile = "profile: ";
        public const string ContentIds = "content ids: ";
        public const string Held = "hold: PlayOnline session held";
        public const string StopRequested = "login: stop requested";
        public const string DetailsUnreadable = "login: saved details unreadable";
        public const string AuthFailed = "login: refused";
        public const string Reason = "reason=";
        public const string AccountBlocked = "login: account turned away: ";
        public const string LoadFailed = "login: couldn't load ";
        public const string NoSession = "hold: no logged-in session";
        public const string HandleWanted = "login: waiting for handle name";
        public const string NoHandle = "no PlayOnline handle";
        public const string HandleRejected = "handle name rejected";
        public const string HandleClaimFailed = "handle claim failed";
        public const string HandleSaveFailed = "handle save failed";
        public const string Crash = "crash: ";
        public const string PlayStarted = "hold: Play signalled";
        public const string PlayUnreadable = "hold: Play entry unreadable";
        public const string PlayRefused = "hold: Play refused:";
        public const string PrestartReady = "prestart: ready";
        public const string WatcherRunning = "pol.exe running (pid ";
        public const string WatcherAttached = "attached to pol.exe";
    }
}
