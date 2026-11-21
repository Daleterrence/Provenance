/* kept in step with Provenance.Common/Hook/HookContract.cs, the app parses the LOG_ lines out of the hook's log */
#ifndef HOOK_CONTRACT_H
#define HOOK_CONTRACT_H

#define ENV_IPC_DIR "POL_IPC_DIR"
#define ENV_CRED_KEYFILE "POL_CRED_KEYFILE"
#define ENV_HOLD "POL_HOLD"
#define ENV_OWNER "POL_OWNER"
#define ENV_SCREENSAVER "POL_SCREENSAVER"
#define ENV_PRESTART "POL_PRESTART"
#define ENV_LEAN "POL_LEAN"
#define ENV_LOGIN_COMING "POL_LOGIN_COMING"
#define ENV_CRED_POL "POL_CRED_POL"
#define ENV_CRED_SEA "POL_CRED_SEA"
#define ENV_CRED_OTP "POL_CRED_OTP"
#define ENV_CRED_ONESHOT "POL_CRED_ONESHOT"
#define ENV_CRED_HANDLE "POL_CRED_HANDLE"
#define ENV_CRED_PLAY "POL_CRED_PLAY"
#define ENV_CRED_CHAT "POL_CRED_CHAT"
#define ENV_STATUS "POL_STATUS"
#define ENV_HOME "POL_HOME"
#define ENV_HANDLE "POL_HANDLE"
#define ENV_ACCEPT_LICENCE "POL_ACCEPT_LICENCE"
#define ENV_CHARACTER "POL_CHARACTER"
#define ENV_CHARACTER_WORLD "POL_CHARACTER_WORLD"
#define ENV_CHARACTER_SLOT "POL_CHARACTER_SLOT"
#define ENV_LAUNCH_ID "PROVENANCE_LAUNCH_ID"

#define AUTH_REFUSED_EXIT 3
#define NO_HANDLE_EXIT 4
#define HANDLE_REFUSED_EXIT 5
#define TURNED_AWAY_EXIT 7
#define STOPPED_EXIT 8
#define LIST_LOAD_EXIT 10
#define DETAILS_UNREADABLE_EXIT 11
#define IPC_DIR_EXIT 12
#define MUTEX_EXIT 13       /* no secret or HMAC means no safe mutex name, so end pol.exe rather than use the stock ones */
#define GAME_START_EXIT 15
#define NO_ENV_EXIT 16
#define NOT_HOOKED_EXIT 17
#define ADDRESSES_EXIT 18

/* a request file is pol_hook.<pid>.<name>, written by the app, and the hook renames it to .taking while it reads it */
#define REQ_SEND "send"
#define REQ_STATUS "status"
#define REQ_ACK "ack"
#define REQ_READ "read"
#define REQ_DELETE_FILES "deletefiles"
#define REQ_COMMENT "comment"
#define REQ_PIC "pic"
#define REQ_FRIEND "friend"
#define REQ_ANSWER "answer"
#define REQ_SEARCH "search"
#define REQ_PROFILE "profile"
#define REQ_GROUP "group"
#define REQ_MYPROFILE "myprofile"
#define REQ_SAVEPROFILE "saveprofile"
#define REQ_RENAME "rename"
#define REQ_CHARACTER "character"
#define REQ_FFXIPROFILE "ffxiprofile"
#define REQ_MYFFXIPROFILE "myffxiprofile"
#define REQ_CHATZONES "chatzones"
#define REQ_CHATROOMS "chatrooms"
#define REQ_CHATJOIN "chatjoin"
#define REQ_CHATSEND "chatsend"
#define REQ_CHATLEAVE "chatleave"
#define REQ_CHATCREATE "chatcreate"
#define REQ_CHATMASTER "chatmaster"
#define REQ_HANDLE "handle"
#define REQ_GROUPCHATJOIN "groupchatjoin"
#define REQ_GROUPCHATSEND "groupchatsend"
#define REQ_GROUPCHATLEAVE "groupchatleave"
#define REQ_FULL "full"

#define IPC_LOG "log"
#define IPC_SOCIAL "social.json"
#define IPC_STORE "store.json"
#define IPC_CHATROOM "chatroom.json"
#define IPC_GROUPCHAT "groupchat.json"
#define IPC_MYPROFILE "myprofile.json"
#define IPC_ENDED "ended"
#define IPC_GO "go"
#define IPC_LOBBY "lobby"
#define IPC_STOP "stop"
#define IPC_TAKING ".taking"
#define IPC_NAME "pol_hook.%u.%s"
#define IPC_PID_FILE "provenance.%s.pid"
#define IPC_QUIET "provenance.quiet"
#define IPC_NO_FRIEND_LINES "provenance.nofriendlines"
#define IPC_NO_PLAYING_LINES "provenance.noplayinglines"
#define IPC_CHARACTERS "provenance.characters"
#define IPC_PLAY_EVENT "provenance_play.%u"

#define LOG_FIRST_LINE "pol_hook: highest user address"
#define LOG_VIEWER_LANG "viewer_init: lang="
#define LOG_REAL_AUTH "login: logging in"
#define LOG_AUTH_STATE "login: state="
#define LOG_AUTH_OK "login: logged in"
#define LOG_PROFILE "profile: "
#define LOG_CONTENT_IDS "content ids: "
#define LOG_HELD "hold: PlayOnline session held"
#define LOG_STOP_REQUESTED "login: stop requested"
#define LOG_DETAILS_UNREADABLE "login: saved details unreadable"
#define LOG_AUTH_FAILED "login: refused"
#define LOG_REASON "reason="
#define LOG_ACCOUNT_BLOCKED "login: account turned away: "
#define LOG_LOAD_FAILED "login: couldn't load "
#define LOG_NO_SESSION "hold: no logged-in session"
#define LOG_HANDLE_WANTED "login: waiting for handle name"
#define LOG_NO_HANDLE "no PlayOnline handle"
#define LOG_HANDLE_REJECTED "handle name rejected"
#define LOG_HANDLE_CLAIM_FAILED "handle claim failed"
#define LOG_HANDLE_SAVE_FAILED "handle save failed"
#define LOG_CRASH "crash: "
#define LOG_PLAY_STARTED "hold: Play signalled"
#define LOG_PLAY_UNREADABLE "hold: Play entry unreadable"
#define LOG_PLAY_REFUSED "hold: Play refused:"
#define LOG_PRESTART_READY "prestart: ready"
#define LOG_WATCHER_RUNNING "pol.exe running (pid "
#define LOG_WATCHER_ATTACHED "attached to pol.exe"

#endif
