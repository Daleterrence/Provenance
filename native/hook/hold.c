static int hold_db_free(void)
{
    return GetTickCount64() - g_s.db_last >= 3000 && !(requests_busy() & JB_DB);
}

/* the server's handle, else the first in use (app.dll 0x10028a76). Provenance's last one (POL_HANDLE) goes before
   the first in use, since so far the server's never given my sessions one back */
static int hold_login_handle(BYTE *b, const char **why)
{
    pol_handle rec;
    char want[24], k[17];
    int slot = pc_current_handle(b), i;
    DWORD n = GetEnvironmentVariableA(ENV_HANDLE, want, sizeof want);
    *why = "the server's";
    if (slot >= 0) return slot;
    for (i = 0; n > 0 && n < sizeof want && i < 64; i++) {
        if (!handle_get(b, i, &rec)) continue;
        peers_hid_key(handle_hid(&rec), k);
        if (!lstrcmpiA(k, want)) { *why = "the one last active in Provenance"; return i; }
    }
    *why = "the first in use";
    for (i = 0; i < 64; i++)
        if (handle_get(b, i, &rec)) return i;
    return -1;
}
/* the hold counts on g_my_slot being a handle in use, and login_handle_bootstrap left the account one, so none by now
   signs out */
static void hold_handle(BYTE *b)
{
    char sv[8];
    const char *why;
    int want = 0;
    g_my_slot = hold_login_handle(b, &why);
    if (g_my_slot < 0) {
        base_log("hold: " LOG_NO_HANDLE " - signing out (exit %d)\r\n", NO_HANDLE_EXIT);
        login_viewer_sign_out(b);
        base_end_pol(NO_HANDLE_EXIT);
    }
    if (g_lean) return;
    if (GetEnvironmentVariableA(ENV_STATUS, sv, sizeof sv) > 0 &&
        (sv[0] == '0' || sv[0] == '1' || sv[0] == '4'))
        want = sv[0] - '0';
    base_log("presence: going online on handle slot %d, %s (current handle %d)\r\n", g_my_slot, why,
       pc_current_handle(b));
    login_status_start(b, g_my_slot, want, 1000);
}

static void hold_db_reads_abandon(BYTE *b)
{
    if (g_prof.step != 0) profiles_profile_abandon(b);
    if (g_ch_read.j.step != 0) profiles_character_abandon(b);
    if (g_fp_read.j.step != 0) profiles_ffxiprofile_abandon(b);
    if (g_search.step != 0) profiles_search_abandon(b);
    if (g_xi.step != 0) my_profile_xi_abandon(b);
}

static int hold_writes_pending(void)
{
    return (requests_busy() & JB_WRITE) != 0;
}
static int hold_writes_waiting(void)
{
    return (requests_busy() & JB_WAIT) != 0;
}
static void hold_drain_turn(BYTE *b, int mail)
{
    pc_pump_once(b);
    login_status_pump(b);
    if (mail) mail_inbox_pump(b);
    mail_send_pump(b);
    friends_edit_pump(b, 0);
    my_profile_pic_pump(b, 0);
    groups_pump(b, 0);
    mail_send_answer_pump(b, 0);
    my_profile_pump(b, 0);
    my_profile_xi_pump(b, 0);
    handles_pump(b, 0);
    Sleep(16);
}

/* the viewer sets the idle timeout from option.bin's GOPN1_AO at login and FFXI only turns the timer on,
   so session init's 0 would never fire. Left off till then */
static int hold_idle_timeout_for_game(BYTE *b)
{
    BYTE *rec = NULL;
    int r = 0, mins = -1;
    ULONGLONG until;
    if (!pc_has(b, SV_OPTION_LOAD_START) || !pc_has(b, SV_OPTION_LOAD_DONE) || !pc_has(b, SV_OPTION_LOAD_END) ||
        !pc_has(b, SV_OPTION_RECORD) || !pc_has(b, SV_SET_IDLE_TIMEOUT))
        return -1;
    pc_option_load_start(b);
    until = GetTickCount64() + 5000;
    while ((r = pc_option_load_done(b)) == 0 && GetTickCount64() < until) Sleep(32);
    if (r > 0) rec = pc_option_record(b, "GOPN1_AO");
    if (rec) mins = *(int *)(rec + 0x18);
    pc_option_load_end(b);
    if (mins < 1 || mins > 60) {
        base_log("hold: auto-disconnect off in FFXI (%s)\r\n", r <= 0 ? "no option.bin" : rec ? "set off" : "not in option.bin");
        return -1;
    }
    pc_set_idle_timeout(b, mins * 60000);
    base_log("hold: auto-disconnect after %d min in FFXI\r\n", mins);
    return mins;
}

/* one turn of the requests and jobs. Nothing new starts mid-switch, it'd go under whichever handle polcore has by then,
   and chat waits it out completely */
static void hold_turn(BYTE *b, int ctl)
{
    char sv[8];
    int wctl, want, was = handles_switching();
    handles_pump(b, ctl);
    /* every turn, the one it finishes on too, so nothing written while it ran is taken after under the new handle */
    if (was || handles_switching()) handles_writes_refuse();
    wctl = ctl && !handles_switching();
    mail_send_pump(b);
    friends_edit_pump(b, wctl);
    profiles_search_pump(b, wctl);
    profiles_profile_pump(b, wctl);
    profiles_character_pump(b, wctl);
    profiles_ffxiprofile_pump(b, wctl);
    groups_pump(b, wctl);
    my_profile_pump(b, wctl);
    my_profile_xi_pump(b, wctl);
    mail_send_answer_pump(b, wctl);
    my_profile_pic_pump(b, wctl);
    if (!g_lean && !handles_switching()) chat_room_pump(b, wctl);
    if (!g_lean && !handles_switching()) group_chat_pump(b, wctl);
    if (!g_lean && wctl && g_s.status_job < 0 && requests_take(REQ_STATUS, sv, sizeof sv) >= 0) {
        want = sv[0] - '0';
        /* the viewer's OK maps busy (2) to online and 3 to appear offline */
        if (want == 2) want = 0;
        if (want == 3) want = 4;
        /* a go-online still waiting for a slot keeps its location, -2 keeps the rest */
        if (want == 0 || want == 1 || want == 4) login_status_start(b, g_my_slot, want, g_s.status_want >= 0 ? g_s.status_loc : -2);
    }
}

/* Play: only writes hold it, ms at most. An inbox read or delete is cancelled after the wait: a cancel stalls job polls
   10s, which FFXI's start-up sits out */
static void hold_play_drain(BYTE *b, const char *spath, DWORD ms)
{
    ULONGLONG until = GetTickCount64() + ms;
    g_s.draining = 1;
    if (!g_lean) mail_inbox_ack_request();
    hold_db_reads_abandon(b);
    my_profile_abandon(b);
    chat_room_end(b);
    group_chat_end(b, "left", 1);
    while (hold_writes_pending() && GetTickCount64() < until)
        hold_drain_turn(b, !g_lean);
    for (int i = 0; i < FETCH_LANES; i++) if (g_s.mail_lanes[i].job >= 0) pc_cancel_job(b, g_s.mail_lanes[i].job);
    if (g_s.del_job >= 0) pc_cancel_job(b, g_s.del_job);
    for (int i = 0; i < g_s.send_queue_n; i++) if (g_sq[i].job >= 0) pc_cancel_job(b, g_sq[i].job);
    if (g_s.status_job >= 0) pc_cancel_job(b, g_s.status_job);
    handles_abandon(b);
    g_in_hold = 0;
    if (g_s.edit_job >= 0) pc_cancel_job(b, g_s.edit_job);
    if (g_s.pic_want >= 0) my_profile_pic_cancel(b);
    if (g_s.grp_job >= 0) pc_cancel_job(b, g_s.grp_job);
    /* FFXI installs its own message handlers, so polcore gets its defaults back */
    if (pc_has(b, SV_RESET_PUSH_CALLBACKS)) pc_reset_push_callbacks(b);
    /* the viewer always has notices on when a game starts, without them FFXI only sees new messages at its next lobby
       login. A local flag, nothing goes on the wire */
    if (g_lean && pc_has(b, SV_MESSAGE_NOTICES)) pc_message_notices(b, 1);
    hold_idle_timeout_for_game(b);
    DeleteFileA(spath);   /* the snapshot has friends' messages in it */
    message_store_drop();
}

/* writes under way get 5s to finish, then the viewer's sign-out */
#define SIGN_OUT_DRAIN_MS 5000
static void hold_sign_out(BYTE *b, const char *spath)
{
    ULONGLONG until = GetTickCount64() + SIGN_OUT_DRAIN_MS;
    DeleteFileA(spath);
    message_store_drop();
    g_s.draining = 1;
    if (!g_lean) mail_inbox_ack_request();
    my_profile_abandon(b);
    while (hold_writes_pending() && GetTickCount64() < until)
        hold_drain_turn(b, !g_lean);
    login_viewer_sign_out(b);
    base_log("hold: signed out\r\n");
    base_end_pol(0);
}

/* read only: polcore stamps conn+0x39d4 on every send (on a quiet hold, the PONG to the ~125s PING).
   The session drops as -250 at 400s */
#define LINK_LOG_MS 600000
static DWORD g_link_sent;
static ULONGLONG g_link_sent_at, g_link_logged_at;
static int g_link_sends;
static void hold_link_watch(BYTE *b)
{
    int s = *pc_link_index(b);
    DWORD *at, sent;
    if (s < 0 || s > 15) return;
    at = pc_link_last_send(b, s);
    if (IsBadReadPtr(at, 4) || !(sent = *at) || sent == g_link_sent) return;
    g_link_sent = sent;
    g_link_sent_at = GetTickCount64();
    if (!g_link_logged_at) {
        g_link_logged_at = g_link_sent_at;
        return;
    }
    g_link_sends++;
    if (g_link_sent_at - g_link_logged_at < LINK_LOG_MS) return;
    base_log("link: polcore sent something %d time(s) since the last of these lines (on a quiet hold that's the PONG)\r\n",
       g_link_sends);
    g_link_logged_at = g_link_sent_at;
    g_link_sends = 0;
}

/* held until Play, a stop, Provenance ending or pol.exe ending. It's an IRC link so it never stops pumping, and never
   sends more than the viewer would. */
static void hold_run(BYTE *b)
{
    static ULONGLONG lastCtl;
    ULONGLONG lastSnap = 0;
    char spath[MAX_PATH + 64], path[MAX_PATH + 64], num[16];
    int ctl, idle, reason, why;
    HANDLE ef;
    DWORD wn;
    if (!b) return;
    if (*pc_auth_state(b) != AUTH_IN) {
        base_log(LOG_NO_SESSION " (auth state %u) - exiting, FFXI NOT started\r\n", *pc_auth_state(b));
        base_end_pol(AUTH_REFUSED_EXIT);
    }
    base_ipc_file(spath, IPC_SOCIAL);
    /* a login that finished after its Provenance went never gets held */
    if (base_owner_gone()) {
        base_log("hold: Provenance ended during the login - signing out and exiting\r\n");
        hold_sign_out(b, spath);
    }
    base_log(LOG_HELD "; waiting for Play (POL_CRED_PLAY)\r\n");
    if (g_lean) base_log("hold: lean session (no social)\r\n");
    if (!g_lean && pc_has(b, SV_SET_PUSH_CALLBACKS)) {
        void *cb[6];
        chat_frame_push_callbacks(b, cb);
        pc_set_push_callbacks(b, cb);
        base_log("mail: push callbacks registered (svc 0xd9c)\r\n");
    }
    hold_handle(b);
    if (!g_lean) message_store_import(b);
    g_in_hold = 1;
    snapshot_social(b);
    for (;;) {
        ctl = GetTickCount64() - lastCtl >= 150;
        if (ctl) lastCtl = GetTickCount64();
        /* a dropped session (another login of this POL ID, KILL, a ban push, the server hanging up) ends the hold,
           so Play never boots FFXI on a dead one */
        idle = pc_pump_once(b);
        reason = *pc_session_ready(b);
        if (idle < 0 || reason < 0 || *pc_session_alive(b) == 0) {
            why = idle < 0 ? idle : reason < 0 ? reason : (int)*pc_auth_error(b);
            if (g_link_sent_at)
                base_log("hold: SESSION LOST reason=%d - signed out (last answered the server %lus before)\r\n", why,
                   (DWORD)((GetTickCount64() - g_link_sent_at) / 1000));
            else base_log("hold: SESSION LOST reason=%d - signed out\r\n", why);
            DeleteFileA(spath);
            message_store_drop();
            base_ipc_file(path, IPC_ENDED);
            ef = CreateFileA(path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
            if (ef != INVALID_HANDLE_VALUE) {
                wsprintfA(num, "%d", why);
                WriteFile(ef, num, (DWORD)lstrlenA(num), &wn, NULL);
                CloseHandle(ef);
            }
            base_end_pol(0);
        }
        if (ctl) hold_link_watch(b);
        if (ctl && pc_jailed_or(b, 0)) {
            DeleteFileA(spath);
            message_store_drop();
            login_account_blocked(b, "jail");
        }
        login_status_pump(b);
        if (g_s.status_job < 0 && g_s.status_want >= 0)
            login_status_start(b, g_my_slot, g_s.status_want, g_s.status_loc);
        /* the new-mail flag (svc 0x1460, set by a push), cleared with 0x1464(0) like the viewer (app.dll 0x1019fef3) */
        if (!g_lean && pc_has(b, SV_NEW_MAIL_FLAG) && pc_new_mail_flag(b)) {
            pc_set_new_mail_flag(b, 0);
            base_log("mail: new-mail flag set - listing the inbox\r\n");
            g_s.mail_refresh = 1;
        }
        if (ctl && !g_lean) mail_inbox_ack_request();
        if (!g_lean) mail_inbox_pump(b);
        hold_turn(b, ctl);
        if (ctl && cred_play_signalled()) {
            if (!g_ffxi_service) {
                static int said;
                if (!said) base_log(LOG_PLAY_REFUSED "no-content-id\r\n");
                said = 1;
            } else {
                base_log(LOG_PLAY_STARTED " - finishing PlayOnline writes, then booting FFXI\r\n");
                hold_play_drain(b, spath, 15000);
                /* the game keeps going whatever happens to Provenance */
                if (g_owner) CloseHandle(g_owner);
                g_owner = NULL;
                base_log("hold: booting FFXI\r\n");
                return;
            }
        }
        /* only an admin can write in Provenance's folder, so a stop file there really is Provenance asking */
        base_ipc_file(path, IPC_STOP);
        if (ctl && GetFileAttributesA(path) != INVALID_FILE_ATTRIBUTES) {
            base_log("hold: stop file seen - signing out and exiting\r\n");
            DeleteFileA(path);
            hold_sign_out(b, spath);
        }
        if (ctl && base_owner_gone()) {
            base_log("hold: Provenance ended - signing out and exiting\r\n");
            hold_sign_out(b, spath);
        }
        if ((g_snap_dirty && GetTickCount64() - lastSnap >= 250) || GetTickCount64() - lastSnap > 2000) {
            g_snap_dirty = 0;
            snapshot_social(b);
            lastSnap = GetTickCount64();
        }
        Sleep(16);
    }
}
