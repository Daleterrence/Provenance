static int g_ffxi_service;
static int g_friends_loaded;
static int g_lean;
static unsigned g_notice, g_notice_days;
static int g_bad_password;

static char g_presence[32] = "starting";

/* Provenance's own numbering (POL_HOME), from 10 so the viewer's 01-08 are never touched, and never 84 */
static int login_home_slot_ok(int s) { return s >= 10 && s <= 99 && s != LEGACY_HOME; }
static int login_home_from_env(void)
{
    char v[8];
    int s;
    DWORD n = GetEnvironmentVariableA(ENV_HOME, v, sizeof v);
    if (n == 0 || n >= sizeof v || !requests_parse_num(v, 2, &s) || !login_home_slot_ok(s)) return -1;
    return s;
}
/* the account's own slot, or home84 when Provenance didn't send one (it's run out of slots) */
static void login_home_pick(BYTE *b)
{
    int slot = login_home_from_env();
    if (!g_viewer[0] || slot < 0) {
        base_log("home: no user slot from Provenance - staying in home%02d\r\n", g_home);
        return;
    }
    if (!viewer_home_set(b, slot)) return;
    base_log("home: user slot %d (pub\\home%02d)\r\n", slot, slot);
}

/* notices whose viewer screen only offers log out or a fix-it page (closed, suspended, banned...). The JP, US and EU
   blocks of app.dll's table block the same codes */
static int login_notice_blocks(unsigned st)
{
    static const unsigned char block[] = { 221, 226, 227, 228, 229, 230, 231, 233, 235, 237, 239,
                                           240, 241, 242, 243, 244, 245, 246, 247, 248, 251 };
    if (st >= 253 && st <= 255) return 1;   /* no table row: the viewer shows LM-01 (log out only) */
    for (int i = 0; i < (int)sizeof block; i++) if (block[i] == st) return 1;
    return 0;
}
/* LM-02's text has a day count. The viewer only asks svc 0xd24 for it on that row
   (polcore 0x10046100: days from now to the login result's date, today included) */
static void login_notice_read(BYTE *b)
{
    if (!pc_has(b, SV_NOTICE_STATUS)) return;
    g_notice = pc_notice_status(b);
    base_log("login: account notice status %u (LM-%02u)\r\n", g_notice, g_notice > 220 && g_notice <= 252 ? g_notice - 220 : 0);
    if (g_notice == 222 && pc_has(b, SV_NOTICE_DAYS)) {
        g_notice_days = pc_notice_days(b);
        base_log("login: LM-02 days left %u\r\n", g_notice_days);
    }
}

/* the viewer's log out (app.dll 0x101983fb). 0xcc4 runs once, it returns 0, not 7, for the hook's connection type */
static void login_viewer_sign_out(BYTE *b)
{
    if (pc_has(b, SV_SIGN_OUT_PREPARE)) pc_sign_out_prepare(b, 1);
    if (pc_has(b, SV_SIGN_OUT)) pc_sign_out(b);
    if (pc_has(b, SV_GCHAT_SIGN_OUT)) pc_gchat_sign_out(b);
    group_chat_signed_out();
}
NORETURN static void login_account_blocked(BYTE *b, const char *what)
{
    base_log(LOG_ACCOUNT_BLOCKED "%s - logged out, nothing started\r\n", what);
    login_viewer_sign_out(b);
    base_end_pol(TURNED_AWAY_EXIT);
}

/* presence only gets pushed once I'm in with a handle, the viewer's last login step is (-2,-1,1,-2,0,1000).
   Handle -1 = none */
static void login_status_start(BYTE *b, int handle, int status, int location)
{
    int chr = location >= 0 ? -1 : -2;   /* a location clears the game, like the viewer's (app.dll 0x10041342) */
    int t = pc_change_my_status(b, handle, chr, 1, status, 0, location);
    if (t == NO_SLOT) { g_s.status_want = status; g_s.status_loc = location; return; }
    g_s.status_want = -1;
    base_log("status: ChangeMyStatus(%d,%d,1,%d,0,%d) -> ticket %d\r\n", handle, chr, status, location, t);
    if (t >= 0) g_s.status_job = t;
    else lstrcpyA(g_presence, requests_failed(t));
}
static void login_status_pump(BYTE *b)
{
    if (g_s.status_job < 0) return;
    int r = pc_change_my_status_check(b, g_s.status_job);
    if (r == 0) return;
    g_s.status_job = -1;
    base_log("status: ChangeMyStatus done -> %d (current handle %d, my status %d)\r\n", r,
       pc_current_handle_or(b, -1), pc_my_status_or(b, -1));
    if (r == 1) lstrcpyA(g_presence, "online"); else lstrcpyA(g_presence, requests_failed(r));
    g_snap_dirty = 1;
}

/* the stop file is a file stat, so it's looked for on a beat and not every step */
#define STOP_BEAT_MS 16
static int login_stop_due(ULONGLONG *last)
{
    ULONGLONG now = GetTickCount64();
    if (*last && now - *last < STOP_BEAT_MS) return 0;
    *last = now;
    return base_stop_requested();
}

struct pending_load { const char *name; int (*load)(BYTE *); int (*check)(BYTE *, int); int ticket, res, write; };
/* read only. LOAD_MY_STATUS's answer, via the pointer at polcore.dll 0x10404df8 + job * 0x338: +0 handle slot, +6
   status + 1, at 0 polcore drops the slot */
static void login_status_reply_log(BYTE *b, int t)
{
    BYTE *r = t >= 0 && t <= 3 ? pc_status_answer(b, t) : NULL;
    if (!r || IsBadReadPtr(r, 8)) {
        base_log(LOG_PROFILE "my status from the server: unreadable (current handle %d)\r\n", pc_current_handle_or(b, -1));
        return;
    }
    base_log(LOG_PROFILE "my status from the server: handle %u, status byte %u (current handle %d, my status %d)\r\n",
       r[0], r[6], pc_current_handle_or(b, -1), pc_my_status_or(b, -1));
}
#define LOADS_IN_FLIGHT 4   /* polcore's job slots, the viewer's own peak */
#define LOADS_BURST 4       /* passes between sleeps */
/* every load's check, and the start of any that can go. How many are still waiting */
static int login_loads_pass(BYTE *b, struct pending_load *items, int n, ULONGLONG *started)
{
    int i, pending = 0, active;
    for (active = 0, i = 0; i < n; i++) if (started[i] && items[i].res == 0) active++;
    for (i = 0; i < n; i++) {
        int t;
        if (items[i].res != 0) continue;
        if (!started[i]) {
            if (active >= LOADS_IN_FLIGHT) { pending++; continue; }
            t = items[i].load(b);
            if (t >= 0) active++;
            if (t == NO_SLOT) { pending++; continue; }
            items[i].ticket = t;
            started[i] = GetTickCount64();
            base_log(LOG_PROFILE "%s load -> ticket %d\r\n", items[i].name, t);
            if (t < 0) {
                items[i].res = t;   /* never retried, the viewer asks once and logs out */
                continue;
            }
        }
        items[i].res = items[i].check(b, items[i].ticket);
        if (items[i].res == 0) { pending++; continue; }
        base_log(LOG_PROFILE "%s -> %d after %ums\r\n", items[i].name, items[i].res,
           (unsigned)(GetTickCount64() - started[i]));
        if (items[i].load == pc_status_load && items[i].res == 1) login_status_reply_log(b, items[i].ticket);
    }
    login_status_pump(b);
    return pending;
}
/* a running write (the handle save) ignores the stop file, the server may already have it */
static void login_loads_stop(BYTE *b, struct pending_load *items, int n, const ULONGLONG *started, ULONGLONG *beat)
{
    int i;
    for (i = 0; i < n; i++) if (items[i].write && started[i] && items[i].res == 0) return;
    if (!login_stop_due(beat)) return;
    base_log(LOG_STOP_REQUESTED " during profile load - logging out and exiting, the game wasn't started\r\n");
    for (i = 0; i < n; i++)
        if (items[i].res == 0 && started[i]) pc_cancel_job(b, items[i].ticket);
    {
        char spath[MAX_PATH];
        base_ipc_file(spath, IPC_SOCIAL);
        DeleteFileA(spath);   /* a lean hold's snapshot, nothing there before the hold */
        message_store_drop();
    }
    login_viewer_sign_out(b);
    base_end_pol(STOPPED_EXIT);
}
static void login_run_loads(BYTE *b, struct pending_load *items, int n)
{
    ULONGLONG started[8] = {0}, beat = 0;
    int i, pass, done = 0;
    if (n > 8) n = 8;
    for (i = 0; i < n; i++) { items[i].ticket = -1; items[i].res = 0; }
    while (!done) {
        for (pass = 0; pass < LOADS_BURST; pass++) {
            if (!login_loads_pass(b, items, n, started)) { done = 1; break; }
            if (pass == 0) login_loads_stop(b, items, n, started, &beat);
            pc_pump_once(b);
        }
        if (!done) Sleep(1);
    }
    for (i = 0; i < n; i++)
        if (items[i].res != 1)
            base_log(LOG_PROFILE "%s -> %d (FAILED)\r\n", items[i].name, items[i].res);
}

static int login_count_handles(BYTE *b)
{
    pol_handle rec;
    int n = 0;
    for (int i = 0; i < 64; i++)
        if (handle_get(b, i, &rec)) n++;
    return n;
}

static int login_wait_handle_name(BYTE *b, char *name, int cap)
{
    WCHAR tbuf[256];
    char user[64], ans[256];
    int warned = 0;
    const WCHAR *t = cred_target(ENV_CRED_HANDLE, tbuf, 256);
    if (!t) return -2;
    base_log(LOG_HANDLE_WANTED " from Provenance\r\n");   /* Provenance watches for this line */
    /* waits rather than exits, as exiting skips the QUIT and a stale session blocks the next login for a while */
    for (int ticks = 0; ticks < 10 * 60 * 5; ticks++) {
        int rc = cred_read(t, user, sizeof user, ans, sizeof ans);
        if (rc == PC_OK) {
            if (strcmp(user, "handle") != 0) {
                SecureZeroMemory(ans, sizeof ans);
            } else {
                lstrcpynA(name, ans, cap);
                SecureZeroMemory(ans, sizeof ans);
                if (cred_launch_target(t)) CredDeleteW(t, CRED_TYPE_GENERIC, 0);
                return name[0] ? 1 : -1;
            }
        } else if (rc != PC_NOT_FOUND && !warned) {
            /* any program of the user can plant an unreadable entry, so it mustn't end the handle creation */
            warned = 1;
            base_log("login: handle answer unreadable (%s) - ignored, still waiting\r\n", cred_why(rc));
        }
        if (base_stop_requested()) {
            base_log(LOG_STOP_REQUESTED
                " while waiting for a handle name - logging out and exiting, the game wasn't started\r\n");
            login_viewer_sign_out(b);
            base_end_pol(STOPPED_EXIT);
        }
        if (base_owner_gone()) {
            base_log("login: Provenance ended while waiting for a handle name - logging out and exiting\r\n");
            login_viewer_sign_out(b);
            base_end_pol(STOPPED_EXIT);
        }
        pc_pump_once(b);
        Sleep(200);
    }
    base_log("login: no handle name within 10 minutes\r\n");
    return -3;
}
/* a new account has no handle and FFXI's world login dies with POL-0001. Handles are permanent and public, so only the
   typed name's used */
static int login_handle_bootstrap(BYTE *b)
{
    static const char *const why[] = { "creation cancelled", "no launcher target", "no answer within 10 minutes" };
    struct pending_load fresh = { "handles (pre-claim)", pc_handles_load, pc_handles_check, 0, 0 };
    struct pending_load put = { "handle save", pc_handles_save, pc_handles_save_check, 0, 0, 1 };
    char name[32] = {0};
    WCHAR wname[32];
    BYTE id[16];
    int n, w, idx, ng;
    n = login_count_handles(b);
    base_log("login: PlayOnline handles on the account: %d\r\n", n);
    if (n > 0) return 0;
    w = login_wait_handle_name(b, name, sizeof name);
    if (w < 0) {
        base_log("login: " LOG_NO_HANDLE " - this account needs a PlayOnline handle before the game "
           "can log in (%s)\r\n", w >= -3 ? why[-w - 1] : "unknown");
        return NO_HANDLE_EXIT;
    }
    if (!peers_ascii_name_ok(name)) {
        base_log("login: " LOG_HANDLE_REJECTED " locally (1-15 characters, not all spaces)\r\n");
        return HANDLE_REFUSED_EXIT;
    }
    /* reload first, the save only goes through when the list stamps match (polcore.dll 0x10403050 >= 0x10403044) */
    login_run_loads(b, &fresh, 1);
    if (fresh.res != 1) return HANDLE_REFUSED_EXIT;
    MultiByteToWideChar(CP_ACP, 0, name, -1, wname, 32);
    ng = my_profile_handle_ngword(b, wname);
    if (ng != 0) {
        base_log(ng > 0 ? "login: " LOG_HANDLE_REJECTED " (the PlayOnline Viewer's word list)\r\n"
                  : "login: " LOG_HANDLE_REJECTED " (the PlayOnline Viewer's word list could not be read)\r\n");
        return HANDLE_REFUSED_EXIT;
    }
    /* the viewer's first-login steps: name -> id (0xa40), claim a free slot (0x314), select it (0x1434), save */
    ZeroMemory(id, sizeof id);
    pc_wide_to_sjis(b, (char *)id, wname, SJIS_GETA);
    idx = pc_claim_handle(b, -1, 1, id);
    if (idx < 0) {
        base_log("login: " LOG_HANDLE_CLAIM_FAILED " %d (0x%08x)\r\n", idx, (unsigned)idx);
        return HANDLE_REFUSED_EXIT;
    }
    pc_select_handle(b, idx);
    login_run_loads(b, &put, 1);
    if (put.res != 1) {
        base_log("login: " LOG_HANDLE_SAVE_FAILED " %d - releasing slot %d\r\n", put.res, idx);
        pc_release_handle(b, idx);
        return HANDLE_REFUSED_EXIT;
    }
    /* the viewer goes on with polcore's own list here, the claimed slot's already in it */
    n = login_count_handles(b);
    if (n < 1) {
        base_log("login: handle save reported ok but the handle list is still empty\r\n");
        return HANDLE_REFUSED_EXIT;
    }
    base_log("login: PlayOnline handle created '%s' (slot %d), handles now %d\r\n", name, idx, n);
    return 0;
}

/* a file name changing in the hook's folder (the go or stop file turning up) wakes the wait */
static HANDLE login_go_watch(void)
{
    char dir[MAX_PATH];
    HANDLE h;
    base_ipc_dir(dir);
    h = FindFirstChangeNotificationA(dir, FALSE, FILE_NOTIFY_CHANGE_FILE_NAME);
    return h ? h : INVALID_HANDLE_VALUE;
}
/* the folder changing or Provenance ending wakes it, and it looks again every 400ms anyway. Without a change handle it
   beats every 16ms. Re-armed before the turn that follows, so a change in between isn't lost */
static void login_go_nap(HANDLE *watch)
{
    HANDLE h[2];
    DWORD n = 0, r;
    if (*watch == INVALID_HANDLE_VALUE) {
        Sleep(16);
        return;
    }
    h[n++] = *watch;
    if (g_owner && !g_owner_gone) h[n++] = g_owner;
    r = WaitForMultipleObjects(n, h, FALSE, 400);
    if (r == WAIT_TIMEOUT || (r == WAIT_OBJECT_0 && FindNextChangeNotification(*watch))) return;
    if (r == WAIT_OBJECT_0 + 1) {
        g_owner_gone = 1;
        return;
    }
    FindCloseChangeNotification(*watch);
    *watch = INVALID_HANDLE_VALUE;
    Sleep(16);
}

/* POL_PRESTART: pol.exe starts at app open with no credentials and waits here having sent nothing. A login writes
   the go file (allow-listed KEY=VALUE lines). */
static void login_prestart_wait(void)
{
    static const char *allowed[] = { ENV_CRED_POL, ENV_CRED_SEA, ENV_CRED_ONESHOT, ENV_CRED_OTP,
                                     ENV_CRED_HANDLE, ENV_CRED_PLAY, ENV_CRED_CHAT, ENV_STATUS, ENV_HOME,
                                     ENV_HANDLE, ENV_LEAN };
    char gp[MAX_PATH + 64];
    HANDLE watch;
    if (!base_getenv_set(ENV_PRESTART)) return;
    base_ipc_file(gp, IPC_GO);
    watch = login_go_watch();
    base_log(LOG_PRESTART_READY " - waiting for the login (nothing sent yet)\r\n");
    for (;;) {
        if (base_stop_requested()) {
            base_log("prestart: stop requested - exiting, nothing was sent\r\n"); base_end_pol(0);
        }
        if (base_owner_gone()) {
            base_log("prestart: Provenance ended - exiting, nothing was sent\r\n"); base_end_pol(0);
        }
        if (GetFileAttributesA(gp) != INVALID_FILE_ATTRIBUTES) {
            char buf[2048], *p, *line, *eq;
            DWORD got = 0;
            HANDLE f = CreateFileA(gp, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
            if (f == INVALID_HANDLE_VALUE) { Sleep(16); continue; }
            ReadFile(f, buf, sizeof buf - 1, &got, NULL);
            CloseHandle(f);
            if (got == 0) { Sleep(16); continue; }
            DeleteFileA(gp);
            buf[got] = 0;
            for (p = buf; *p; ) {
                line = requests_line(&p);
                if (!(eq = strchr(line, '='))) continue;
                *eq = 0;
                for (int k = 0; k < (int)(sizeof allowed / sizeof allowed[0]); k++)
                    if (lstrcmpA(line, allowed[k]) == 0) { SetEnvironmentVariableA(line, eq + 1); break; }
            }
            break;
        }
        login_go_nap(&watch);
    }
    if (watch != INVALID_HANDLE_VALUE) FindCloseChangeNotification(watch);
    base_log("prestart: go\r\n");
}

/* the viewer's FFXI gate (app.dll 0x10199093), a valid Content ID record for content 1 */
static void login_ffxi_gate_read(BYTE *b)
{
    pol_chr rec;
    int any = 0, ffxi = 0;
    for (int i = 0; i < 64; i++)
        if (peers_get_chr(b, i, &rec)) {
            any++;
            if (rec.title == 1) ffxi++;
        }
    g_ffxi_service = ffxi > 0;
    base_log("login: " LOG_CONTENT_IDS "%d valid, %d FFXI\r\n", any, ffxi);
}

/* status, friends, handles and characters, which polcore's four job slots take at once. A lean session skips the
   status, but FFXI reads the friend list and never loads it. Groups come later, see login_groups_go */
static struct pending_load *login_set(struct pending_load *all, int *n)
{
    *n = g_lean ? 3 : 4;
    return g_lean ? all + 1 : all;
}
/* the viewer loads my status on its own and starts the lists from its OnSuccess (app.dll 0x10040822, 0x10040650).
   A lean session made full does the same, a full login starts the status with the lists */
static void login_loads(BYTE *b, struct pending_load *run, int n)
{
    if (run[0].load != pc_status_load) {
        login_run_loads(b, run, n);
        return;
    }
    login_run_loads(b, run, 1);
    if (run[0].res == 1) login_run_loads(b, run + 1, n - 1);
}
/* a load refused with an account status (-0x1450 and down: suspended, IP restricted, password changed...) turns
   the account away, any other failure logs out too, like the viewer */
static void login_loads_check(BYTE *b, struct pending_load *run, int n)
{
    char what[48];
    int i;
    for (i = 0; i < n; i++)
        if (run[i].res <= ACCOUNT_REFUSED_TOP && run[i].res >= ACCOUNT_REFUSED_BOTTOM) {
            wsprintfA(what, "load:%s=%d", run[i].name, run[i].res);
            login_account_blocked(b, what);
        }
    for (i = 0; i < n; i++)
        if (run[i].res != 1) {
            base_log(LOG_LOAD_FAILED "%s=%d - logged out, nothing started\r\n", run[i].name, run[i].res);
            login_viewer_sign_out(b);
            base_end_pol(LIST_LOAD_EXIT);
        }
    if (pc_has(b, SV_BAD_PASSWORD_FLAG) && (pc_bad_password_flag(b) & 0xff)) {
        g_bad_password = 1;
        base_log("login: bad password flag set (PlayOnline Viewer message 0x7007)\r\n");
    }
}

/* a full login's groups load goes out once the hold has written its first snapshot, and the hold's own turns finish it */
enum { GROUPS_NONE, GROUPS_HELD, GROUPS_RUNNING };
static int g_groups_state;
static struct pending_load g_groups_load = { "groups", pc_groups_load, pc_groups_check, 0, 0 };
static ULONGLONG g_groups_started;
static void login_groups_go(void)
{
    if (g_groups_state != GROUPS_HELD) return;
    g_groups_load.ticket = -1;
    g_groups_load.res = 0;
    g_groups_started = 0;
    g_groups_state = GROUPS_RUNNING;
}
/* a refused account is turned away and any other failure logs out, like the loads before it. The snapshot has friends'
   messages in it, so a failure takes it away first */
static void login_groups_pump(BYTE *b, const char *spath)
{
    if (g_groups_state != GROUPS_RUNNING) return;
    if (login_loads_pass(b, &g_groups_load, 1, &g_groups_started)) return;
    g_groups_state = GROUPS_NONE;
    if (g_groups_load.res != 1) {
        base_log(LOG_PROFILE "groups -> %d (FAILED)\r\n", g_groups_load.res);
        DeleteFileA(spath);
        message_store_drop();
    }
    login_loads_check(b, &g_groups_load, 1);
    g_snap_dirty = 1;
}
static void login_groups_cancel(BYTE *b)
{
    if (g_groups_state == GROUPS_RUNNING && g_groups_started && g_groups_load.res == 0)
        pc_cancel_job(b, g_groups_load.ticket);
    g_groups_state = GROUPS_NONE;
}
#define AUTH_CHAT_LOGIN 23
#define AUTH_BURST 4
/* up to AUTH_BURST steps while the state holds. Until the chat connection's up a refused connect is retried each
   step, so that stays at one step and Sleep(16) */
static void login_auth_turn(BYTE *b, unsigned sm)
{
    for (int k = 0; k < AUTH_BURST; k++) {
        pc_pump_once(b);
        if (*pc_auth_state(b) != sm) return;
        if (sm == AUTH_CHAT_LOGIN && !pc_link_connected(b)) {
            Sleep(16);
            return;
        }
    }
    Sleep(1);
}
/* no give-up of my own, polcore fails a stuck step itself and Provenance ends a login that goes quiet */
static void login_auth_run(BYTE *b)
{
    unsigned sm;
    int last = -1;
    ULONGLONG beat = 0;
    for (;;) {
        sm = *pc_auth_state(b);
        if (sm != (unsigned)last) {
            last = (int)sm;
            base_log(LOG_AUTH_STATE "%u res=%d err=%08x\r\n", sm, *pc_auth_result(b), *pc_auth_error(b));
        }
        if (auth_settled(sm)) break;
        if (login_stop_due(&beat)) {
            base_log(LOG_STOP_REQUESTED " during login - exiting, the game wasn't started\r\n");
            base_end_pol(STOPPED_EXIT);
        }
        if (base_owner_gone()) {
            base_log("login: Provenance ended during login - exiting, the game wasn't started\r\n");
            base_end_pol(STOPPED_EXIT);
        }
        login_auth_turn(b, sm);
    }
    sm = *pc_auth_state(b);
    if (sm != AUTH_IN) {
        base_log(LOG_AUTH_FAILED " (state=%u res=%d err=%08x " LOG_REASON "%d) - exiting, the game wasn't started\r\n",
           sm, *pc_auth_result(b), *pc_auth_error(b), *pc_auth_reason(b));
        base_end_pol(AUTH_REFUSED_EXIT);
    }
    base_log(LOG_AUTH_OK " (state 30) - session bound\r\n");
}
/* id and pw are cleared on the way */
#define LOGIN_ID_SIZE 16
#define LOGIN_PW_SIZE 40
static void login_session(BYTE *b, char *id, char *pw)
{
    char what[48];
    static unsigned char zero[0x400];
    struct { int kind; void *dialup[5]; } conn = { 1, { zero, zero, zero, zero, zero } };
    unsigned char encpw[0x12], hostbuf[0x42];
    struct pending_load all[5] = {
        { "status",     pc_status_load, pc_status_check, 0, 0 },
        { "friends",    pc_friends_load, pc_friends_check, 0, 0 },
        { "handles",    pc_handles_load, pc_handles_check, 0, 0 },
        { "content_ids", pc_characters_load, pc_characters_check, 0, 0 },
        { "groups",     pc_groups_load, pc_groups_check, 0, 0 }
    };
    struct pending_load *run;
    unsigned flags;
    int r, rc, nrun, jail;
    base_timer_begin();
    pc_session_init(b);
    /* inet init (polcore.dll 0x1000fe20 sets the flag at 0x100a66d4, the connect helper returns -2071 without it) */
    r = pc_inet_init(b);
    base_log("login: inet init -> %d\r\n", r);
    /* POLCON_OPEN (polcore.dll 0x100448a0) with a kind 1 conn and five zeroed dial-up buffers. It overwrites the
       password and host, so both go in after it. */
    r = pc_con_open(b, CON_TYPE, &conn);
    base_log("login: POLCON_OPEN(%d) -> %d\r\n", CON_TYPE, r);
    pc_encode_password(b, encpw, pw);
    SecureZeroMemory(pw, LOGIN_PW_SIZE);
    /* SET_PASSWORD (polcore.dll 0x10046df0) copies the bytes into polcore, so mine get cleared */
    pc_set_password(b, encpw, 0);
    SecureZeroMemory(encpw, sizeof encpw);
    pc_encode_host(b, hostbuf, "ci000.pol.com");
    pc_set_polcom_host(b, hostbuf);
    /* ENCODE_POLID (polcore.dll 0x10046e70) scrambles the ID into polcore. It's reversible, so never logged. */
    pc_encode_polid(b, id);
    SecureZeroMemory(id, LOGIN_ID_SIZE);
    seed_account_login(b);
    *pc_auth_error(b) = AUTH_NO_ERROR;
    if (*pc_auth_state(b) == 0)
        *pc_auth_state(b) = AUTH_START;
    login_auth_run(b);
    login_notice_read(b);
    /* stricter than the viewer on purpose, a blocked account stops here */
    flags = *pc_login_flags(b);
    jail = pc_jailed_or(b, 0);
    base_log("login: result flags %04x notice %u jail %d\r\n", flags, g_notice, jail);
    if (flags & 1) login_account_blocked(b, "disconnect-bit");
    if (login_notice_blocks(g_notice)) {
        wsprintfA(what, "LM-%02u", g_notice <= 252 ? g_notice - 220 : 1);
        login_account_blocked(b, what);
    }
    if (jail) login_account_blocked(b, "jail");
    login_home_pick(b);
    /* MESSAGE_NOTICES(1), or polcore marks every message as lost. Lean mode reads no messages, so asks for none. */
    if (!g_lean)
        pc_message_notices(b, 1);
    pc_clear_error(b);
    run = login_set(all, &nrun);
    login_run_loads(b, run, nrun);
    g_friends_loaded = all[1].res == 1;
    login_loads_check(b, run, nrun);
    if (!g_lean) {
        g_groups_state = GROUPS_HELD;
        base_log("login: groups load waits for the first snapshot\r\n");
    }
    login_ffxi_gate_read(b);
    rc = login_handle_bootstrap(b);
    if (rc) {
        base_log("login: stopping before the game boots (exit %d) - logging out\r\n", rc);
        login_viewer_sign_out(b);
        base_end_pol((UINT)rc);
    }
    *pc_session_ready(b) = 1;
    base_timer_end();
    base_log("login: session ready (notices and lists loaded) - game can boot\r\n");
}
static void log_in(HMODULE pc)
{
    char id[LOGIN_ID_SIZE] = {0}, pw[LOGIN_PW_SIZE] = {0}, m[24];
    login_prestart_wait();
    g_lean = base_getenv_set(ENV_LEAN);
    cred_read_details(ENV_CRED_POL, "POL", id, sizeof id, pw, sizeof pw);
    /* POL's own password box stops at 16, and polcore's copy of the encoded one has no room for more */
    if (lstrlenA(pw) > 16) {
        SecureZeroMemory(pw, sizeof pw);
        base_log("login: PlayOnline password longer than 16 characters - exiting, the game wasn't started\r\n");
        base_end_pol(DETAILS_UNREADABLE_EXIT);
    }
    base_log(LOG_REAL_AUTH ", PlayOnline ID='%s'\r\n", cred_mask_id(id, m));
    login_session((BYTE *)pc, id, pw);
}

static void fake_session(void)
{
    HMODULE pc = viewer_polcore_module();
    if (!pc) { base_log("pol_hook: polcore.dll not loaded at menu time?!\r\n"); return; }
    addr_require(IN_POLCORE, viewer_region()->polcore, (BYTE *)pc, (DWORD)(ULONG_PTR)pc);
    /* before the login, so a crash during it gets a CRASH line too. Not a VEH, that'd catch IsBadReadPtr's first-chance
       probes and recurse through base_log */
    SetUnhandledExceptionFilter(crash_filter);
    polcore_sockets_patch((BYTE *)pc);
    viewer_init((BYTE *)pc);
    log_in(pc);
    if (!g_home_made && viewer_home_set((BYTE *)pc, g_home))
        base_log("viewer_init: user slot %d (pub\\home%02d)\r\n", g_home, g_home);
}

/* gate 7 (FFXiMain.dll 0x04245cb0) decodes patch.ver from the current directory. Real POL boots from the FFXI folder,
   the hook would inherit POL's and bail silently (silently being the key word) */
static void login_set_cwd_ffxi(void)
{
    HKEY k;
    char path[MAX_PATH] = "", cur[MAX_PATH] = "", sub[64];
    DWORD n = sizeof path - 1, t = 0;
    wsprintfA(sub, "SOFTWARE\\WOW6432Node\\%s\\InstallFolder", viewer_pol_hive());
    if (RegOpenKeyExA(HKEY_LOCAL_MACHINE, sub, 0, KEY_READ, &k) != 0) {
        base_log("pol_hook: InstallFolder key open failed\r\n");
        return;
    }
    if (RegQueryValueExA(k, "0001", 0, &t, (LPBYTE)path, &n) != 0 || t != REG_SZ || !*path) {
        base_log("pol_hook: InstallFolder\\0001 read failed\r\n");
        RegCloseKey(k);
        return;
    }
    RegCloseKey(k);
    GetCurrentDirectoryA(sizeof cur, cur);
    if (SetCurrentDirectoryA(path))
        base_log("pol_hook: cwd '%s' -> '%s'\r\n", cur, path);
    else
        base_log("pol_hook: SetCurrentDirectory('%s') failed\r\n", path);
}
