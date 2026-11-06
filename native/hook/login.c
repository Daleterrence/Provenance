static int g_ffxi_service;
static int g_friends_loaded;
static int g_lean;
static unsigned g_notice, g_notice_days;
static int g_bad_password;

static char g_presence[32] = "starting";

/* Provenance's own numbering (POL_HOME), from 10 so the viewer's 01-08 are never touched, and never 84 */
static int home_slot_ok(int s) { return s >= 10 && s <= 99 && s != LEGACY_HOME; }
static int home_from_env(void)
{
    char v[8];
    int s;
    DWORD n = GetEnvironmentVariableA(ENV_HOME, v, sizeof v);
    if (n == 0 || n >= sizeof v || !parse_num(v, 2, &s) || !home_slot_ok(s)) return -1;
    return s;
}
/* 1 copied, 0 nothing to do (no source, already there, too big), -1 failed. Never over a file, never through a link */
static int copy_plain(const char *src, const char *dst)
{
    static char buf[0x10000];
    LARGE_INTEGER size;
    DWORD got, put;
    int ok = 1;
    HANDLE in, out;
    if (GetFileAttributesA(src) == INVALID_FILE_ATTRIBUTES) return 0;
    if ((in = open_plain(src)) == INVALID_HANDLE_VALUE) return -1;
    if (!GetFileSizeEx(in, &size) || size.QuadPart > 0x200000) { CloseHandle(in); return 0; }
    out = CreateFileA(dst, GENERIC_WRITE, 0, NULL, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, NULL);
    if (out == INVALID_HANDLE_VALUE) {
        DWORD e = GetLastError();
        CloseHandle(in);
        return e == ERROR_FILE_EXISTS ? 0 : -1;
    }
    while (ok && ReadFile(in, buf, sizeof buf, &got, NULL) && got)
        ok = WriteFile(out, buf, got, &put, NULL) && put == got;
    CloseHandle(in);
    CloseHandle(out);
    if (!ok) DeleteFileA(dst);
    return ok ? 1 : -1;
}
/* home84's option.bin (FFXI's POL settings) and this account's own messages, never over what's there. The messages go
   once, FFXI moves and deletes them here after */
static void home_copy(BYTE *b, int slot, unsigned __int64 me)
{
    char src[MAX_PATH + 160], dst[MAX_PATH + 160], mark[MAX_PATH + 64], done[8] = "";
    storefile *v;
    int s, i, k, r, copied = 0, failed = 0, opt = 0, had_o, had_m;
    DWORD got = 0, wn;
    HANDLE f;
    wsprintfA(dst, "%s\\pub\\home%02d", g_viewer, slot);
    if (!plain_path(dst)) return;
    /* 'o' option.bin, 'm' the messages */
    wsprintfA(mark, "%s\\provenance-copied", dst);
    if ((f = open_plain(mark)) != INVALID_HANDLE_VALUE) {
        if (!ReadFile(f, done, sizeof done - 1, &got, NULL)) got = 0;
        CloseHandle(f);
        done[got] = 0;
    }
    had_o = strchr(done, 'o') != NULL;
    had_m = strchr(done, 'm') != NULL;
    if (had_o && had_m) return;
    if (!had_o) {
        lstrcatA(dst, "\\option.bin");
        wsprintfA(src, "%s\\pub\\home%02d\\option.bin", g_viewer, LEGACY_HOME);
        opt = copy_plain(src, dst);
    }
    for (s = 0; s < 4 && me && !had_m; s++) {
        store_dir(dst, slot, s);
        if (!plain_path(dst)) { failed++; continue; }
        k = store_list(b, LEGACY_HOME, s, me, 0, &v);
        for (i = 0; i < k; i++) {
            store_dir(src, LEGACY_HOME, s);
            store_dir(dst, slot, s);
            lstrcatA(src, "\\"); lstrcatA(src, v[i].name);
            lstrcatA(dst, "\\"); lstrcatA(dst, v[i].name);
            r = copy_plain(src, dst);
            copied += r > 0;
            failed += r < 0;
        }
        if (v) HeapFree(GetProcessHeap(), 0, v);
    }
    wsprintfA(done, "%s%s", had_o || opt >= 0 ? "o" : "", had_m || me ? "m" : "");
    if (done[0]) {
        DeleteFileA(mark);   /* a link someone left there goes, it's never written through */
        f = CreateFileA(mark, GENERIC_WRITE, 0, NULL, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, NULL);
        if (f != INVALID_HANDLE_VALUE) {
            WriteFile(f, done, (DWORD)lstrlenA(done), &wn, NULL);
            CloseHandle(f);
        }
    }
    lg("home: copy from home%02d to home%02d - option.bin %s, %d message(s), %d not copied%s\r\n", LEGACY_HOME, slot,
       had_o ? "done before" : opt > 0 ? "copied" : opt < 0 ? "failed, tried again next time" : "not needed", copied,
       failed, had_m ? " (messages done before)" : !me ? " (messages next time, no POL ID yet)" : "");
}
/* the account's own slot, or home84 when Provenance didn't send one (it's run out of slots) */
static void home_pick(BYTE *b)
{
    int slot = home_from_env();
    if (!g_viewer[0] || slot < 0) {
        lg("home: no user slot from Provenance - staying in home%02d\r\n", g_home);
        return;
    }
    if (!home_set(b, slot)) return;
    lg("home: user slot %d (pub\\home%02d)\r\n", slot, slot);
    if (legacy_home_there()) home_copy(b, slot, pc_has(b, SV_MY_ID) ? pc_my_id(b) : 0);
}

/* notices whose viewer screen only offers log out or a fix-it page (closed, suspended, banned...). The JP, US and EU
   blocks of app.dll's table block the same codes */
static int notice_blocks(unsigned st)
{
    static const unsigned char block[] = { 221, 226, 227, 228, 229, 230, 231, 233, 235, 237, 239,
                                           240, 241, 242, 243, 244, 245, 246, 247, 248, 251 };
    if (st >= 253 && st <= 255) return 1;   /* no table row: the viewer shows LM-01 (log out only) */
    for (int i = 0; i < (int)sizeof block; i++) if (block[i] == st) return 1;
    return 0;
}
/* LM-02's text has a day count in it. The viewer only asks svc 0xd24 for it on that row (polcore 0x10046100: days
   from now to the date in the login result, today included) */
static void notice_read(BYTE *b)
{
    if (!pc_has(b, SV_NOTICE_STATUS)) return;
    g_notice = pc_notice_status(b);
    lg("phase_c: account notice status %u (LM-%02u)\r\n", g_notice, g_notice > 220 && g_notice <= 252 ? g_notice - 220 : 0);
    if (g_notice == 222 && pc_has(b, SV_NOTICE_DAYS)) {
        g_notice_days = pc_notice_days(b);
        lg("phase_c: LM-02 days left %u\r\n", g_notice_days);
    }
}

/* the viewer's sign-out (app.dll 0x101983fb). 0xcc4 runs once, it returns 0, not 7, for the hook's connection type */
static void viewer_sign_out(BYTE *b)
{
    if (pc_has(b, SV_SIGN_OUT_PREPARE)) pc_sign_out_prepare(b, 1);
    if (pc_has(b, SV_SIGN_OUT)) pc_sign_out(b);
    if (pc_has(b, SV_GCHAT_SIGN_OUT)) pc_gchat_sign_out(b);
    gc_signed_out();
}
NORETURN static void account_blocked(BYTE *b, const char *what)
{
    lg(LOG_ACCOUNT_BLOCKED "%s - signed out, nothing started\r\n", what);
    viewer_sign_out(b);
    end_pol(TURNED_AWAY_EXIT);
}

/* presence only gets pushed once I'm in with a handle, the viewer's last login step is (-2,-1,1,-2,0,1000).
   Handle -1 = signed off */
static void status_start(BYTE *b, int handle, int status, int location)
{
    int chr = location >= 0 ? -1 : -2;   /* a location clears the game, like the viewer's (app.dll 0x10041342) */
    int t = pc_change_my_status(b, handle, chr, 1, status, 0, location);
    if (t == NO_SLOT) { g_s.status_want = status; g_s.status_loc = location; return; }
    g_s.status_want = -1;
    lg("presence: ChangeMyStatus(%d,%d,1,%d,0,%d) -> ticket %d\r\n", handle, chr, status, location, t);
    if (t >= 0) g_s.status_job = t;
    else lstrcpyA(g_presence, failed(t));
}
static void status_pump(BYTE *b)
{
    if (g_s.status_job < 0) return;
    int r = pc_change_my_status_check(b, g_s.status_job);
    if (r == 0) return;
    g_s.status_job = -1;
    lg("presence: ChangeMyStatus done -> %d (current handle %d, my status %d)\r\n", r,
       pc_current_handle(b), pc_my_status(b));
    if (r == 1) lstrcpyA(g_presence, "online"); else lstrcpyA(g_presence, failed(r));
    g_snap_dirty = 1;
}

struct pload { const char *name; int (*load)(BYTE *); int (*check)(BYTE *, int); int ticket, res, write; };
/* read only. LOAD_MY_STATUS's answer, via the pointer at polcore.dll 0x10404df8 + job * 0x338: +0 handle slot, +6
   status + 1, at 0 polcore drops the slot */
static void status_reply_log(BYTE *b, int t)
{
    BYTE *r = t >= 0 && t <= 3 ? pc_status_answer(b, t) : NULL;
    if (!r || IsBadReadPtr(r, 8)) {
        lg(LOG_PROFILE "my status from the server: unreadable (current handle %d)\r\n", pc_current_handle(b));
        return;
    }
    lg(LOG_PROFILE "my status from the server: handle %u, status byte %u (current handle %d, my status %d)\r\n", r[0],
       r[6], pc_current_handle(b), pc_has(b, SV_MY_STATUS) ? pc_my_status(b) : -1);
}
#define LOADS_IN_FLIGHT 4   /* polcore's job slots, the viewer's own peak */
static void run_loads(BYTE *b, struct pload *items, int n)
{
    ULONGLONG started[8] = {0};
    int i, pending, active, writing;
    if (n > 8) n = 8;
    for (i = 0; i < n; i++) { items[i].ticket = -1; items[i].res = 0; }
    for (;;) {
        pending = 0;
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
                lg(LOG_PROFILE "%s load -> ticket %d\r\n", items[i].name, t);
                if (t < 0) {
                    items[i].res = t;   /* never retried, the viewer asks once and signs out */
                    continue;
                }
            }
            items[i].res = items[i].check(b, items[i].ticket);
            if (items[i].res == 0) { pending++; continue; }
            lg(LOG_PROFILE "%s -> %d after %u ms\r\n", items[i].name, items[i].res,
               (unsigned)(GetTickCount64() - started[i]));
            if (items[i].load == pc_status_load && items[i].res == 1) status_reply_log(b, items[i].ticket);
        }
        status_pump(b);
        if (!pending) break;
        /* a running write (the handle save) ignores the stop file, the server may already have it */
        for (writing = 0, i = 0; i < n; i++) if (items[i].write && started[i] && items[i].res == 0) writing = 1;
        if (!writing && stop_requested()) {
            lg(LOG_STOP_REQUESTED " during profile load - signing out and exiting, FFXI NOT started\r\n");
            for (i = 0; i < n; i++)
                if (items[i].res == 0 && started[i]) pc_cancel_job(b, items[i].ticket);
            viewer_sign_out(b);
            end_pol(STOPPED_EXIT);
        }
        pump_once(b);
        Sleep(1);
    }
    for (i = 0; i < n; i++)
        if (items[i].res != 1)
            lg(LOG_PROFILE "%s -> %d (FAILED)\r\n", items[i].name, items[i].res);
}

static int count_handles(BYTE *b)
{
    pol_handle rec;
    int n = 0;
    for (int i = 0; i < 64; i++)
        if (handle_get(b, i, &rec)) n++;
    return n;
}

static int wait_handle_name(BYTE *b, char *name, int cap)
{
    WCHAR tbuf[256];
    char user[64], ans[256];
    int warned = 0;
    const WCHAR *t = cred_target(ENV_CRED_HANDLE, tbuf, 256);
    if (!t) return -2;
    lg(LOG_HANDLE_WANTED " from launcher\r\n");   /* Provenance watches for this line */
    /* waits rather than exits, as exiting skips the QUIT and a stale session blocks the next login for a while */
    for (int ticks = 0; ticks < 10 * 60 * 5; ticks++) {
        int rc = cred_read(t, user, sizeof user, ans, sizeof ans);
        if (rc == PC_OK) {
            if (strcmp(user, "handle") != 0) {
                SecureZeroMemory(ans, sizeof ans);
            } else {
                lstrcpynA(name, ans, cap);
                SecureZeroMemory(ans, sizeof ans);
                if (launch_target(t)) CredDeleteW(t, CRED_TYPE_GENERIC, 0);
                return name[0] ? 1 : -1;
            }
        } else if (rc != PC_NOT_FOUND && !warned) {
            /* any program of the user can plant an unreadable entry, so it mustn't end the handle creation */
            warned = 1;
            lg("phase_c: handle answer unreadable (%s) - ignored, still waiting\r\n", cred_why(rc));
        }
        if (stop_requested()) {
            lg(LOG_STOP_REQUESTED " while waiting for a handle name - signing out and exiting, FFXI NOT started\r\n");
            viewer_sign_out(b);
            end_pol(STOPPED_EXIT);
        }
        if (owner_gone()) {
            lg("phase_c: Provenance ended while waiting for a handle name - signing out and exiting\r\n");
            viewer_sign_out(b);
            end_pol(STOPPED_EXIT);
        }
        pump_once(b);
        Sleep(200);
    }
    lg("phase_c: no handle name within 10 minutes\r\n");
    return -3;
}
/* a new account has no handle and FFXI's world login dies with POL-0001 (I'm a damn moron, forgot about the handle).
   Handles are permanent and public, so only the typed name's used */
static int handle_bootstrap(BYTE *b)
{
    static const char *const why[] = { "creation cancelled", "no launcher target", "no answer within 10 minutes" };
    struct pload fresh = { "handles (pre-claim)", pc_handles_load, pc_handles_check, 0, 0 };
    struct pload put = { "handle save", pc_handles_save, pc_handles_save_check, 0, 0, 1 };
    char name[32] = {0};
    WCHAR wname[32];
    BYTE id[16];
    int n, w, idx, ng;
    n = count_handles(b);
    lg("phase_c: POL handles on account: %d\r\n", n);
    if (n > 0) return 0;
    w = wait_handle_name(b, name, sizeof name);
    if (w < 0) {
        lg("phase_c: " LOG_NO_HANDLE " - this account needs a PlayOnline handle before FFXI "
           "can log in (%s)\r\n", w >= -3 ? why[-w - 1] : "unknown");
        return NO_HANDLE_EXIT;
    }
    if (!ascii_name_ok(name)) {
        lg("phase_c: " LOG_HANDLE_REJECTED " locally (1-15 characters, not all spaces)\r\n");
        return HANDLE_REFUSED_EXIT;
    }
    /* reload first, the save only goes through when the list stamps match (polcore.dll 0x10403050 >= 0x10403044) */
    run_loads(b, &fresh, 1);
    if (fresh.res != 1) return HANDLE_REFUSED_EXIT;
    MultiByteToWideChar(CP_ACP, 0, name, -1, wname, 32);
    ng = hr_ngword(b, wname);
    if (ng != 0) {
        lg(ng > 0 ? "phase_c: " LOG_HANDLE_REJECTED " (the Viewer's word list)\r\n"
                  : "phase_c: " LOG_HANDLE_REJECTED " (the Viewer's word list could not be read)\r\n");
        return HANDLE_REFUSED_EXIT;
    }
    /* the viewer's first-login steps: name -> id (0xa40), claim a free slot (0x314), select it (0x1434), save */
    ZeroMemory(id, sizeof id);
    pc_wide_to_sjis(b, (char *)id, wname, SJIS_GETA);
    idx = pc_claim_handle(b, -1, 1, id);
    if (idx < 0) {
        lg("phase_c: " LOG_HANDLE_CLAIM_FAILED " %d (0x%08x)\r\n", idx, (unsigned)idx);
        return HANDLE_REFUSED_EXIT;
    }
    pc_select_handle(b, idx);
    run_loads(b, &put, 1);
    if (put.res != 1) {
        lg("phase_c: " LOG_HANDLE_SAVE_FAILED " %d - releasing slot %d\r\n", put.res, idx);
        pc_release_handle(b, idx);
        return HANDLE_REFUSED_EXIT;
    }
    /* the viewer goes on with polcore's own list here, the claimed slot's already in it */
    n = count_handles(b);
    if (n < 1) {
        lg("phase_c: handle save reported ok but the handle list is still empty\r\n");
        return HANDLE_REFUSED_EXIT;
    }
    lg("phase_c: POL HANDLE CREATED '%s' (slot %d), handles now %d\r\n", name, idx, n);
    return 0;
}

/* POL_PRESTART: pol.exe starts at app open with no credentials and waits here having sent nothing. A login writes
   the go file (allow-listed KEY=VALUE lines). */
static void prestart_wait(void)
{
    static const char *allowed[] = { ENV_CRED_POL, ENV_CRED_SEA, ENV_CRED_ONESHOT, ENV_CRED_OTP,
                                     ENV_CRED_HANDLE, ENV_CRED_PLAY, ENV_CRED_CHAT, ENV_STATUS, ENV_HOME,
                                     ENV_HANDLE };
    char gp[MAX_PATH + 64];
    if (!getenv_set(ENV_PRESTART)) return;
    ipc_file(gp, IPC_GO);
    lg(LOG_PRESTART_READY " - waiting for LOG IN (nothing sent yet)\r\n");
    for (;;) {
        if (stop_requested()) { lg("prestart: stop requested - exiting, nothing was sent\r\n"); end_pol(0); }
        if (owner_gone()) { lg("prestart: Provenance ended - exiting, nothing was sent\r\n"); end_pol(0); }
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
                line = req_line(&p);
                if (!(eq = strchr(line, '='))) continue;
                *eq = 0;
                for (int k = 0; k < (int)(sizeof allowed / sizeof allowed[0]); k++)
                    if (lstrcmpA(line, allowed[k]) == 0) { SetEnvironmentVariableA(line, eq + 1); break; }
            }
            break;
        }
        Sleep(16);
    }
    lg("prestart: go\r\n");
}

/* the viewer's FFXI gate (app.dll 0x10199093), a valid Content ID record for content 1 */
static void ffxi_gate_read(BYTE *b)
{
    pol_chr rec;
    int any = 0, ffxi = 0;
    for (int i = 0; i < 64; i++)
        if (get_chr(b, i, &rec)) {
            any++;
            if (rec.title == 1) ffxi++;
        }
    g_ffxi_service = ffxi > 0;
    lg("phase_c: " LOG_CONTENT_IDS "%d valid, %d FFXI\r\n", any, ffxi);
}

/* status, friends, handles, characters, groups. A lean session only loads handles and characters */
static struct pload *login_set(struct pload *all, int *n)
{
    *n = g_lean ? 2 : 5;
    return g_lean ? all + 2 : all;
}
/* the viewer loads my status on its own and starts the lists from its OnSuccess (app.dll 0x10040822, 0x10040650) */
static void login_loads(BYTE *b, struct pload *run, int n)
{
    if (run[0].load != pc_status_load) {
        run_loads(b, run, n);
        return;
    }
    run_loads(b, run, 1);
    if (run[0].res == 1) run_loads(b, run + 1, n - 1);
}
static void phase_c_seed(HMODULE pc)
{
    BYTE *b = (BYTE *)pc;
    char id[16] = {0}, pw[40] = {0}, m[24], what[48];
    static unsigned char zero[0x400];
    struct { int kind; void *dialup[5]; } conn = { 1, { zero, zero, zero, zero, zero } };
    unsigned char encpw[0x12], hostbuf[0x42];
    struct pload all[5] = {
        { "status",     pc_status_load, pc_status_check, 0, 0 },
        { "friends",    pc_friends_load, pc_friends_check, 0, 0 },
        { "handles",    pc_handles_load, pc_handles_check, 0, 0 },
        { "characters", pc_characters_load, pc_characters_check, 0, 0 },
        { "groups",     pc_groups_load, pc_groups_check, 0, 0 }
    };
    struct pload *run;
    unsigned sm, flags;
    int r, i, rc, nrun, jail, last = -1;
    prestart_wait();
    g_lean = getenv_set(ENV_LEAN);
    if (!read_details(ENV_CRED_POL, "POL", id, sizeof id, pw, sizeof pw)) return;
    /* POL's own password box stops at 16, and polcore's copy of the encoded one has no room for more */
    if (lstrlenA(pw) > 16) {
        SecureZeroMemory(pw, sizeof pw);
        lg("phase_c: POL password longer than 16 characters - exiting, FFXI NOT started\r\n");
        end_pol(DETAILS_UNREADABLE_EXIT);
    }
    lg(LOG_REAL_AUTH ": POL ID='%s'\r\n", mask_id(id, m));
    pc_session_init(b);
    /* inet init (polcore.dll 0x1000fe20 sets the flag at 0x100a66d4, the connect helper returns -2071 without it).
       One piece of the POL-2071 hunt, which was a fucking nightmare honestly good lord */
    r = pc_inet_init(b);
    lg("phase_c: inet init -> %d\r\n", r);
    /* POLCON_OPEN (polcore.dll 0x100448a0) type 3 = network, kind 1 + five zeroed dial-up buffers. It overwrites
       the password and host, so both go in after it. */
    r = pc_con_open(b, 3, &conn);
    lg("phase_c: POLCON_OPEN(3) -> %d\r\n", r);
    pc_encode_password(b, encpw, pw);   /* ENCODE_PASSWORD */
    SecureZeroMemory(pw, sizeof pw);
    /* SET_PASSWORD (polcore.dll 0x10046df0) copies the bytes into polcore, so mine get cleared */
    pc_set_password(b, encpw, 0);
    SecureZeroMemory(encpw, sizeof encpw);
    pc_encode_host(b, hostbuf, "ci000.pol.com");   /* ENCODE_HOST */
    pc_set_polcom_host(b, hostbuf);   /* SET_POLCOM_HOST */
    /* ENCODE_POLID (polcore.dll 0x10046e70) scrambles the ID into polcore. It's reversible, so never logged. */
    pc_encode_polid(b, id);
    SecureZeroMemory(id, sizeof id);
    sea_seed(b);
    *pc_auth_error(b) = AUTH_NO_ERROR;
    if (*pc_auth_state(b) == 0)
        *pc_auth_state(b) = AUTH_START;
    /* no give-up of my own, polcore fails a stuck step itself and Provenance ends a login that goes quiet */
    for (;;) {
        sm = *pc_auth_state(b);
        if (sm != (unsigned)last) {
            last = (int)sm;
            lg(LOG_AUTH_STATE "%u res=%d err=%08x\r\n", sm, *pc_auth_result(b), *pc_auth_error(b));
        }
        if (auth_settled(sm)) break;
        if (stop_requested()) {
            lg(LOG_STOP_REQUESTED " during sign-in - exiting, FFXI NOT started\r\n");
            end_pol(STOPPED_EXIT);
        }
        if (owner_gone()) {
            lg("phase_c: Provenance ended during sign-in - exiting, FFXI NOT started\r\n");
            end_pol(STOPPED_EXIT);
        }
        pump_once(b);
        if (*pc_auth_state(b) == sm) Sleep(16);
    }
    sm = *pc_auth_state(b);
    if (sm != AUTH_IN) {
        lg(LOG_AUTH_FAILED " (state=%u res=%d err=%08x " LOG_REASON "%d) - exiting, FFXI NOT started\r\n",
           sm, *pc_auth_result(b), *pc_auth_error(b), *pc_auth_reason(b));
        end_pol(AUTH_REFUSED_EXIT);
    }
    lg(LOG_AUTH_OK " (state 30) - session bound\r\n");
    notice_read(b);
    /* stricter than the viewer on purpose, a blocked account stops here */
    flags = *pc_login_flags(b);
    jail = pc_has(b, SV_JAILED) ? pc_jailed(b) : 0;
    lg("phase_c: login result flags %04x notice %u jail %d\r\n", flags, g_notice, jail);
    if (flags & 1) account_blocked(b, "disconnect-bit");
    if (notice_blocks(g_notice)) {
        wsprintfA(what, "LM-%02u", g_notice <= 252 ? g_notice - 220 : 1);
        account_blocked(b, what);
    }
    if (jail) account_blocked(b, "jail");
    home_pick(b);
    /* MESSAGE_NOTICES(1), or polcore marks every message as lost. Lean mode reads no messages, so asks for none. */
    if (!g_lean)
        pc_message_notices(b, 1);
    pc_clear_error(b);   /* POLCON_CLEAR_ERROR */
    run = login_set(all, &nrun);
    login_loads(b, run, nrun);
    g_friends_loaded = all[1].res == 1;
    /* a load refused with an account status (-0x1450 and down: suspended, IP restricted, password changed...) turns
       the account away, any other failure signs out too, like the viewer */
    for (i = 0; i < nrun; i++)
        if (run[i].res <= ACCOUNT_REFUSED_TOP && run[i].res >= ACCOUNT_REFUSED_BOTTOM) {
            wsprintfA(what, "load:%s=%d", run[i].name, run[i].res);
            account_blocked(b, what);
        }
    for (i = 0; i < nrun; i++)
        if (run[i].res != 1) {
            lg(LOG_LOAD_FAILED "%s=%d - signed out, nothing started\r\n", run[i].name, run[i].res);
            viewer_sign_out(b);
            end_pol(LIST_LOAD_EXIT);
        }
    if (pc_has(b, SV_BAD_PASSWORD_FLAG) && (pc_bad_password_flag(b) & 0xff)) {
        g_bad_password = 1;
        lg("phase_c: bad password flag set (Viewer message 0x7007)\r\n");
    }
    ffxi_gate_read(b);
    rc = handle_bootstrap(b);
    if (rc) {
        lg("phase_c: stopping before FFXI boots (exit %d) - signing out\r\n", rc);
        viewer_sign_out(b);
        end_pol((UINT)rc);
    }
    *pc_session_ready(b) = 1;
    lg("phase_c: session shaped (notices+lists) - game can boot\r\n");
}

static void prime_session(void)
{
    HMODULE pc = polcore_module();
    if (!pc) { lg("pol_hook: polcore.dll not loaded at menu time?!\r\n"); return; }
    /* before the login, so a crash during it gets a CRASH line too. Not a VEH, that'd catch IsBadReadPtr's
       first-chance probes and recurse through lg (yes, IsBadReadPtr, I know) */
    SetUnhandledExceptionFilter(veh_proc);
    viewer_init((BYTE *)pc);
    phase_c_seed(pc);
    if (!g_home_made && home_set((BYTE *)pc, g_home))
        lg("viewer_init: user slot %d (pub\\home%02d)\r\n", g_home, g_home);
}

/* gate 7 (FFXiMain.dll 0x04245cb0) decodes patch.ver from the current directory. Real POL boots content from the
   FFXI folder, the hook would inherit POL's and bail silently. silently being the key word, my lord */
static void set_cwd_ffxi(void)
{
    HKEY k;
    char path[MAX_PATH], cur[MAX_PATH], sub[64];
    DWORD n = sizeof path, t = 0;
    wsprintfA(sub, "SOFTWARE\\WOW6432Node\\%s\\InstallFolder", pol_hive());
    if (RegOpenKeyExA(HKEY_LOCAL_MACHINE, sub, 0, KEY_READ, &k) != 0) {
        lg("pol_hook: InstallFolder key open failed\r\n");
        return;
    }
    if (RegQueryValueExA(k, "0001", 0, &t, (LPBYTE)path, &n) != 0 || !*path) {
        lg("pol_hook: InstallFolder\\0001 read failed\r\n");
        RegCloseKey(k);
        return;
    }
    RegCloseKey(k);
    GetCurrentDirectoryA(sizeof cur, cur);
    if (SetCurrentDirectoryA(path))
        lg("pol_hook: cwd '%s' -> '%s'\r\n", cur, path);
    else
        lg("pol_hook: SetCurrentDirectory('%s') failed\r\n", path);
}
