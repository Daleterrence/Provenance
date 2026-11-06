static int db_free(void)
{
    return GetTickCount64() - g_db_last >= 3000 && !g_srch_step && !g_prof_step && !g_ch_step && !g_fp_step &&
           !g_cr_db && !g_me_step && !g_xi_step && g_pic_want < 0;
}

/* the server's handle, else the first in use (app.dll 0x10028a76). Provenance's last one (POL_HANDLE) goes before
   the first in use, since so far the server's never given my sessions one back */
static int login_handle(BYTE *b, const char **why)
{
    BYTE rec[0x28];
    char want[24], k[17];
    int slot = ((svc_v)SVC(0x2f8))(), i;
    DWORD n = GetEnvironmentVariableA(ENV_HANDLE, want, sizeof want);
    *why = "the server's";
    if (slot >= 0) return slot;
    for (i = 0; n > 0 && n < sizeof want && i < 64; i++) {
        if (get_handle(b, i, rec) != 0 || !(rec[0] & 1)) continue;
        handle_key(rec, k);
        if (!lstrcmpiA(k, want)) { *why = "the one last active in Provenance"; return i; }
    }
    *why = "the first in use";
    for (i = 0; i < 64; i++)
        if (get_handle(b, i, rec) == 0 && (rec[0] & 1)) return i;
    return -1;
}
/* the hold counts on g_my_slot being a handle in use, and handle_bootstrap left the account one, so none by now
   signs out */
static void hold_handle(BYTE *b)
{
    char sv[8];
    const char *why;
    int want = 0;
    g_my_slot = login_handle(b, &why);
    if (g_my_slot < 0) {
        lg("hold: " LOG_NO_HANDLE " - signing out (exit %d)\r\n", NO_HANDLE_EXIT);
        viewer_sign_out(b);
        end_pol(NO_HANDLE_EXIT);
    }
    if (g_lean) return;
    if (GetEnvironmentVariableA(ENV_STATUS, sv, sizeof sv) > 0 &&
        (sv[0] == '0' || sv[0] == '1' || sv[0] == '4'))
        want = sv[0] - '0';
    lg("presence: going online on handle slot %d, %s (current handle %d)\r\n", g_my_slot, why,
       ((svc_v)SVC(0x2f8))());
    status_start(b, g_my_slot, want, 1000);
}

static void db_reads_abandon(BYTE *b)
{
    if (g_prof_step != 0) profile_abandon(b);
    if (g_ch_step != 0) character_abandon(b);
    if (g_fp_step != 0) ffxiprofile_abandon(b);
    if (g_srch_step != 0) search_abandon(b);
}

static int writes_pending(void)
{
    return g_sq_n || g_status_job >= 0 || g_edit_job >= 0 || g_edit_upload_want ||
           g_comment_wanted || g_pic_want >= 0 || g_grp_job >= 0 || g_answer_kind || g_me_step >= 3 ||
           g_hop;
}
/* writes that are due but haven't got a job slot yet, or an answer's message still to come */
static int writes_waiting(void)
{
    return send_built() || g_answer_kind || g_edit_upload_want || g_comment_wanted || g_me_step == 3 ||
           g_hwant;
}
static void drain_turn(BYTE *b, int mail)
{
    POLCON_STEP(b);
    POLCON_IDLE(b);
    status_pump(b);
    if (mail) mail_pump(b);
    mail_send_pump(b);
    friend_edit_pump(b, 0);
    pic_pump(b, 0);
    group_pump(b, 0);
    answer_pump(b, 0);
    me_pump(b, 0);
    xi_pump(b, 0);
    handle_pump(b, 0);
    Sleep(16);
}

/* the viewer sets polcore's idle timeout from option.bin's GOPN1_AO at login, and FFXI only switches the timer on, so
   session init's 0 would never fire. It stays off till FFXI turns it on */
static int idle_timeout_for_game(BYTE *b)
{
    BYTE *rec = NULL;
    int r = 0, mins = -1;
    ULONGLONG until;
    if (!SVC(0xe2c) || !SVC(0xe30) || !SVC(0xe3c) || !SVC(0xe40) || !SVC(0x11d4)) return -1;
    ((int (__cdecl *)(void))SVC(0xe2c))();
    until = GetTickCount64() + 5000;
    while ((r = ((int (__cdecl *)(void))SVC(0xe30))()) == 0 && GetTickCount64() < until) Sleep(32);
    if (r > 0) rec = ((BYTE *(__cdecl *)(const char *))SVC(0xe40))("GOPN1_AO");
    if (rec) mins = *(int *)(rec + 0x18);
    ((void (__cdecl *)(void))SVC(0xe3c))();
    if (mins < 1 || mins > 60) {
        lg("hold: auto-disconnect off in FFXI (%s)\r\n", r <= 0 ? "no option.bin" : rec ? "set off" : "not in option.bin");
        return -1;
    }
    ((void (__cdecl *)(int))SVC(0x11d4))(mins * 60000);
    lg("hold: auto-disconnect after %d min in FFXI\r\n", mins);
    return mins;
}

/* one turn of the requests and jobs. Nothing new starts mid-switch, it'd go under whichever handle polcore has by then,
   and chat waits it out completely */
static void hold_turn(BYTE *b, int ctl)
{
    char sv[8];
    int wctl, want, was = handle_switching();
    handle_pump(b, ctl);
    /* every turn, the one it finishes on too, so nothing written while it ran is taken after under the new handle */
    if (was || handle_switching()) writes_refuse(b);
    wctl = ctl && !handle_switching();
    mail_send_pump(b);
    friend_edit_pump(b, wctl);
    search_pump(b, wctl);
    profile_pump(b, wctl);
    character_pump(b, wctl);
    ffxiprofile_pump(b, wctl);
    group_pump(b, wctl);
    me_pump(b, wctl);
    xi_pump(b, wctl);
    answer_pump(b, wctl);
    pic_pump(b, wctl);
    if (!g_lean && !handle_switching()) chat_pump(b, wctl);
    if (!g_lean && !handle_switching()) gc_pump(b, wctl);
    if (!g_lean && wctl && g_status_job < 0 && take_request(REQ_STATUS, sv, sizeof sv) >= 0) {
        want = sv[0] - '0';
        /* the viewer's OK maps busy (2) to online and 3 to appear offline */
        if (want == 2) want = 0;
        if (want == 3) want = 4;
        /* a go-online still waiting for a slot keeps its location, -2 keeps the rest */
        if (want == 0 || want == 1 || want == 4) status_start(b, g_my_slot, want, g_status_want >= 0 ? g_status_loc : -2);
    }
}

/* Play: only writes hold it, ms at most. An inbox read or delete is cancelled after the wait: a cancel stalls job polls
   10 s, which FFXI's start-up sits out */
static void play_drain(BYTE *b, const char *spath, DWORD ms)
{
    ULONGLONG until = GetTickCount64() + ms;
    g_draining = 1;
    if (!g_lean) mail_ack_request();
    db_reads_abandon(b);
    me_abandon(b);
    chat_end(b);
    gc_end(b, "left", 1);
    while (writes_pending() && GetTickCount64() < until)
        drain_turn(b, !g_lean);
    for (int i = 0; i < FETCH_LANES; i++) if (g_rd[i].job >= 0) CANCEL_JOB(g_rd[i].job);
    if (g_del_job >= 0) CANCEL_JOB(g_del_job);
    for (int i = 0; i < g_sq_n; i++) if (g_sq[i].job >= 0) CANCEL_JOB(g_sq[i].job);
    if (g_status_job >= 0) CANCEL_JOB(g_status_job);
    handle_abandon(b);
    g_in_hold = 0;
    if (g_edit_job >= 0) CANCEL_JOB(g_edit_job);
    if (g_pic_want >= 0) profile_pic_cancel(b);
    if (g_grp_job >= 0) CANCEL_JOB(g_grp_job);
    /* FFXI installs its own message handlers, so polcore gets its defaults back */
    if (SVC(0xda0)) ((void (__cdecl *)(void))SVC(0xda0))();
    /* the viewer always has notices on when a game starts, without them FFXI only sees new messages at its next lobby
       login. A local flag, nothing goes on the wire */
    if (g_lean && SVC(0x1020)) ((svc_i)SVC(0x1020))(1);
    idle_timeout_for_game(b);
    DeleteFileA(spath);   /* the snapshot has friends' messages in it */
    store_drop();
}

/* writes under way get 5 s to finish, then the viewer's sign-out */
#define SIGN_OUT_DRAIN_MS 5000
static void hold_sign_out(BYTE *b, const char *spath)
{
    ULONGLONG until = GetTickCount64() + SIGN_OUT_DRAIN_MS;
    DeleteFileA(spath);
    store_drop();
    g_draining = 1;
    if (!g_lean) mail_ack_request();
    me_abandon(b);
    while (writes_pending() && GetTickCount64() < until)
        drain_turn(b, !g_lean);
    viewer_sign_out(b);
    lg("hold: signed out\r\n");
    end_pol(0);
}

/* read only: polcore stamps conn+0x39d4 on every send, on a quiet hold just the PONG to the ~125 s PING. The session
   drops as -250 400 s on, so this logs the server going quiet */
#define LINK_LOG_MS 600000
static DWORD g_link_sent;
static ULONGLONG g_link_sent_at, g_link_logged_at;
static int g_link_sends;
static void link_watch(BYTE *b)
{
    int s = *(int *)(b + 0x99250);
    BYTE *at;
    DWORD sent;
    if (s < 0 || s > 15) return;
    at = b + 0x3e58a0 + s * 0x3a00 + 0x39d4;
    if (IsBadReadPtr(at, 4) || !(sent = *(DWORD *)at) || sent == g_link_sent) return;
    g_link_sent = sent;
    g_link_sent_at = GetTickCount64();
    if (!g_link_logged_at) {
        g_link_logged_at = g_link_sent_at;
        return;
    }
    g_link_sends++;
    if (g_link_sent_at - g_link_logged_at < LINK_LOG_MS) return;
    lg("link: polcore sent something %d time(s) since the last of these lines (on a quiet hold that's the PONG)\r\n",
       g_link_sends);
    g_link_logged_at = g_link_sent_at;
    g_link_sends = 0;
}

/* held until Play, a stop, Provenance ending or pol.exe ending. It's an IRC link so it never stops pumping, and never
   sends more than the viewer would. */
static void pol_hold(BYTE *b)
{
    static ULONGLONG lastCtl;
    ULONGLONG lastSnap = 0;
    char spath[MAX_PATH + 64], path[MAX_PATH + 64], num[16];
    int ctl, idle, reason, why;
    HANDLE ef;
    DWORD wn;
    if (!b) return;
    if (*(unsigned *)(b + 0x99408) != 30) {
        lg(LOG_NO_SESSION " (auth state %u) - exiting, FFXI NOT started\r\n", *(unsigned *)(b + 0x99408));
        end_pol(AUTH_REFUSED_EXIT);
    }
    ipc_file(spath, IPC_SOCIAL);
    /* a login that finished after its Provenance went never gets held */
    if (owner_gone()) {
        lg("hold: Provenance ended during the login - signing out and exiting\r\n");
        hold_sign_out(b, spath);
    }
    lg(LOG_HELD "; waiting for Play (POL_CRED_PLAY)\r\n");
    if (g_lean) lg("hold: lean session (no social)\r\n");
    if (!g_lean && SVC(0xd9c)) {
        void *cb[6];
        push_callbacks(b, cb);
        ((void (__cdecl *)(void **))SVC(0xd9c))(cb);
        lg("mail: push callbacks registered (svc 0xd9c)\r\n");
    }
    hold_handle(b);
    if (!g_lean) store_import(b);
    g_in_hold = 1;
    snapshot_social(b);
    for (;;) {
        ctl = GetTickCount64() - lastCtl >= 150;
        if (ctl) lastCtl = GetTickCount64();
        POLCON_STEP(b);
        /* a dropped session (another login of this POL ID, KILL, a ban push, the server hanging up) ends the hold,
           so Play never boots FFXI on a dead one */
        idle = POLCON_IDLE(b);
        reason = *(int *)(b + 0x99254);
        if (idle < 0 || reason < 0 || *(int *)(b + 0x9924c) == 0) {
            why = idle < 0 ? idle : reason < 0 ? reason : *(int *)(b + 0x9aa14);
            if (g_link_sent_at)
                lg("hold: SESSION LOST reason=%d - signed out (last answered the server %lu s before)\r\n", why,
                   (DWORD)((GetTickCount64() - g_link_sent_at) / 1000));
            else lg("hold: SESSION LOST reason=%d - signed out\r\n", why);
            DeleteFileA(spath);
            store_drop();
            ipc_file(path, IPC_ENDED);
            ef = CreateFileA(path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
            if (ef != INVALID_HANDLE_VALUE) {
                wsprintfA(num, "%d", why);
                WriteFile(ef, num, (DWORD)lstrlenA(num), &wn, NULL);
                CloseHandle(ef);
            }
            end_pol(0);
        }
        if (ctl) link_watch(b);
        if (ctl && SVC(0x13e4) && ((svc_v)SVC(0x13e4))()) {
            DeleteFileA(spath);
            store_drop();
            account_blocked(b, "jail");
        }
        status_pump(b);
        if (g_status_job < 0 && g_status_want >= 0) status_start(b, g_my_slot, g_status_want, g_status_loc);
        /* the new-mail flag (svc 0x1460, set by a push), cleared with 0x1464(0) like the viewer (app.dll 0x1019fef3) */
        if (!g_lean && SVC(0x1460) && ((svc_v)SVC(0x1460))()) {
            ((void (__cdecl *)(int))SVC(0x1464))(0);
            lg("mail: new-mail flag set - listing the inbox\r\n");
            g_mail_refresh = 1;
        }
        if (ctl && !g_lean) mail_ack_request();
        if (!g_lean) mail_pump(b);
        hold_turn(b, ctl);
        if (ctl && play_signalled()) {
            if (!g_ffxi_service) {
                static int said;
                if (!said) lg(LOG_PLAY_REFUSED "no-content-id\r\n");
                said = 1;
            } else {
                lg(LOG_PLAY_STARTED " - finishing PlayOnline writes, then booting FFXI\r\n");
                play_drain(b, spath, 15000);
                /* the game keeps going whatever happens to Provenance */
                if (g_owner) CloseHandle(g_owner);
                g_owner = NULL;
                lg("hold: booting FFXI\r\n");
                return;
            }
        }
        /* only an admin can write in Provenance's folder, so a stop file there really is Provenance asking */
        ipc_file(path, IPC_STOP);
        if (ctl && GetFileAttributesA(path) != INVALID_FILE_ATTRIBUTES) {
            lg("hold: stop file seen - signing out and exiting\r\n");
            DeleteFileA(path);
            hold_sign_out(b, spath);
        }
        if (ctl && owner_gone()) {
            lg("hold: Provenance ended - signing out and exiting\r\n");
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
