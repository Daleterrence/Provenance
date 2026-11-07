/* what the app and the hook have to agree on, kept in step with Provenance.Common/Hook/HookContract.cs.
   The app parses the LOG_ lines below out of the hook's log, so don't reword one without changing the other side */
#ifndef HOOK_CONTRACT_H
#define HOOK_CONTRACT_H

/* wide copy of a string macro above, L_(ENV_IPC_DIR) */
#define L_(s) L_I(s)
#define L_I(s) L##s

/* set on pol.exe's start, and a pre-started one's go file can carry some of them later */
#define ENV_IPC_DIR "POL_IPC_DIR"
#define ENV_CRED_KEYFILE "POL_CRED_KEYFILE"
#define ENV_HOLD "POL_HOLD"
#define ENV_OWNER "POL_OWNER"
#define ENV_SCREENSAVER "POL_SCREENSAVER"
#define ENV_PRESTART "POL_PRESTART"
#define ENV_LEAN "POL_LEAN"
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
#define ENV_LAUNCH_ID "PROVENANCE_LAUNCH_ID"

/* pol.exe's exit codes, 0 is a clean end */
#define AUTH_REFUSED_EXIT 3
#define NO_HANDLE_EXIT 4
#define HANDLE_REFUSED_EXIT 5
#define TURNED_AWAY_EXIT 7
#define STOPPED_EXIT 8
#define LIST_LOAD_EXIT 10
#define DETAILS_UNREADABLE_EXIT 11
#define IPC_DIR_EXIT 12
#define MUTEX_EXIT 13       /* no secret or HMAC: end pol.exe, never fall back to the stock names */
#define GAME_START_EXIT 15
#define NO_ENV_EXIT 16      /* not started by Provenance, its environment isn't there */
#define NOT_HOOKED_EXIT 17  /* no detour, so it'd only ever get as far as the viewer's own menu */
#define ADDRESSES_EXIT 18   /* something it needs in pol.exe or polcore can't be found, nothing in it was touched */

/* a request file is pol_hook.<pid>.<name>, written by the app, and the hook renames it to .taking while it reads it */
#define REQ_SEND "send"
#define REQ_STATUS "status"
#define REQ_ACK "ack"
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
#define REQ_XIPROFILE "xiprofile"
#define REQ_CHARACTER "character"
#define REQ_FFXIPROFILE "ffxiprofile"
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

/* the other files in the same folder, and the names they and the requests go by */
#define IPC_LOG "log"
#define IPC_SOCIAL "social.json"
#define IPC_STORE "store.json"
#define IPC_CHATROOM "chatroom.json"
#define IPC_GROUPCHAT "groupchat.json"
#define IPC_MYPROFILE "myprofile.json"
#define IPC_ENDED "ended"
#define IPC_GO "go"
#define IPC_STOP "stop"
#define IPC_TAKING ".taking"
#define IPC_NAME "pol_hook.%u.%s"
#define IPC_PID_FILE "provenance.%s.pid"

/* log lines the app reads, each a piece of the line that starts it or is all of it */
#define LOG_FIRST_LINE "pol_hook: highest user address"
#define LOG_VIEWER_LANG "viewer_init: lang="
#define LOG_REAL_AUTH "phase_c: REAL auth"
#define LOG_AUTH_STATE "auth SM: state="
#define LOG_AUTH_OK "phase_c: AUTH OK"
#define LOG_PROFILE "profile: "
#define LOG_CONTENT_IDS "content ids: "
#define LOG_HELD "hold: POL-only session held"
#define LOG_STOP_REQUESTED "phase_c: stop requested"
#define LOG_DETAILS_UNREADABLE "phase_c: SAVED DETAILS UNREADABLE"
#define LOG_AUTH_FAILED "phase_c: AUTH FAILED"
#define LOG_REASON "reason="
#define LOG_ACCOUNT_BLOCKED "phase_c: ACCOUNT BLOCKED "
#define LOG_LOAD_FAILED "phase_c: LOAD FAILED "
#define LOG_NO_SESSION "hold: no signed-in session"
#define LOG_HANDLE_WANTED "phase_c: waiting for handle name"
#define LOG_NO_HANDLE "NO POL HANDLE"
#define LOG_HANDLE_REJECTED "handle name rejected"
#define LOG_HANDLE_CLAIM_FAILED "handle claim failed"
#define LOG_HANDLE_SAVE_FAILED "handle save failed"
#define LOG_CRASH "CRASH "
#define LOG_PLAY_STARTED "hold: Play signalled"
#define LOG_PLAY_UNREADABLE "hold: Play entry unreadable"
#define LOG_PLAY_REFUSED "hold: Play refused:"
#define LOG_PRESTART_READY "prestart: ready"
/* the watcher's, printed rather than logged */
#define LOG_WATCHER_RUNNING "pol.exe running (pid "
#define LOG_WATCHER_ATTACHED "attached to pol.exe"

#endif
