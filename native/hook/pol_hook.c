/* pol_hook.c - signs in from inside the real pol.exe, then its own FFXiEntry boots the game (Windower and Ashita
   never notice) */
#include <windows.h>
#include <wincred.h>
#include <string.h>
#include <bcrypt.h>
#include <sddl.h>
#include <tlhelp32.h>
#include "pol_cred.h"
#include "base.c"
#include "pol_exe.c"
#include "credentials.c"
#include "viewer_setup.c"
#include "requests.c"
#include "json.c"
#include "peers.c"
#include "mail_inbox.c"
#include "message_store.c"
#include "friends.c"
#include "profile_db.c"
#include "profiles.c"
#include "pol_pic_save.c"
#include "pol_profile_edit.c"

static int g_ffxi_service;
static int g_friends_loaded;
static int g_lean;
static unsigned g_notice, g_notice_days;
static int g_bad_password;
static int g_status_job = -1;
static int g_status_want = -1, g_status_loc = -2;
static char g_presence[32] = "starting";
static int g_me_step;

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
    if (!SVC(0xd20)) return;
    g_notice = ((unsigned char (__cdecl *)(void))SVC(0xd20))();
    lg("phase_c: account notice status %u (LM-%02u)\r\n", g_notice, g_notice > 220 && g_notice <= 252 ? g_notice - 220 : 0);
    if (g_notice == 222 && SVC(0xd24)) {
        g_notice_days = ((unsigned char (__cdecl *)(void))SVC(0xd24))();
        lg("phase_c: LM-02 days left %u\r\n", g_notice_days);
    }
}
static void gc_signed_out(void);
static void gc_group_going(BYTE *b, DWORD lo, DWORD hi);
/* the viewer's sign-out (app.dll 0x101983fb). 0xcc4 runs once, it returns 0, not 7, for the hook's connection type */
static void viewer_sign_out(BYTE *b)
{
    if (SVC(0xcc0)) ((void (__cdecl *)(int))SVC(0xcc0))(1);
    if (SVC(0xcc4)) ((svc_v)SVC(0xcc4))();
    if (SVC(0x6c8)) ((void (__cdecl *)(void))SVC(0x6c8))();
    gc_signed_out();
}
static void account_blocked(BYTE *b, const char *what)
{
    lg("phase_c: ACCOUNT BLOCKED %s - signed out, nothing started\r\n", what);
    viewer_sign_out(b);
    end_pol(7);
}

/* presence only gets pushed once I'm in with a handle, the viewer's last login step is (-2,-1,1,-2,0,1000).
   Handle -1 = signed off */
static void status_start(BYTE *b, int handle, int status, int location)
{
    int chr = location >= 0 ? -1 : -2;   /* a location clears the game, like the viewer's (app.dll 0x10041342) */
    int t = ((int (__cdecl *)(int, int, int, int, int, int))SVC(0x30c))(handle, chr, 1, status, 0, location);
    if (t == NO_SLOT) { g_status_want = status; g_status_loc = location; return; }
    g_status_want = -1;
    lg("presence: ChangeMyStatus(%d,%d,1,%d,0,%d) -> ticket %d\r\n", handle, chr, status, location, t);
    if (t >= 0) g_status_job = t;
    else wsprintfA(g_presence, "failed:%d", t);
}
static void status_pump(BYTE *b)
{
    if (g_status_job < 0) return;
    int r = ((svc_i)SVC(0x310))(g_status_job);
    if (r == 0) return;
    g_status_job = -1;
    lg("presence: ChangeMyStatus done -> %d (current handle %d, my status %d)\r\n", r,
       ((svc_v)SVC(0x2f8))(), ((svc_v)SVC(0x338))());
    if (r == 1) lstrcpyA(g_presence, "online"); else wsprintfA(g_presence, "failed:%d", r);
    g_snap_dirty = 1;
}

struct pload { const char *name; unsigned o_load, o_check; int ticket, res, write; };
/* read only. LOAD_MY_STATUS's answer, via the pointer at polcore.dll 0x10404df8 + job * 0x338: +0 handle slot, +6
   status + 1, at 0 polcore drops the slot */
static void status_reply_log(BYTE *b, int t)
{
    BYTE *r = t >= 0 && t <= 3 ? *(BYTE **)(b + 0x404df8 + t * 0x338) : NULL;
    if (!r || IsBadReadPtr(r, 8)) {
        lg("profile: my status from the server: unreadable (current handle %d)\r\n", ((svc_v)SVC(0x2f8))());
        return;
    }
    lg("profile: my status from the server: handle %u, status byte %u (current handle %d, my status %d)\r\n", r[0],
       r[6], ((svc_v)SVC(0x2f8))(), SVC(0x338) ? ((svc_v)SVC(0x338))() : -1);
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
                t = ((svc_v)SVC(items[i].o_load))();
                if (t >= 0) active++;
                if (t == NO_SLOT) { pending++; continue; }
                items[i].ticket = t;
                started[i] = GetTickCount64();
                lg("profile: %s load -> ticket %d\r\n", items[i].name, t);
                if (t < 0) {
                    items[i].res = t;   /* never retried, the viewer asks once and signs out */
                    continue;
                }
            }
            items[i].res = ((svc_i)SVC(items[i].o_check))(items[i].ticket);
            if (items[i].res == 0) { pending++; continue; }
            lg("profile: %s -> %d after %u ms\r\n", items[i].name, items[i].res,
               (unsigned)(GetTickCount64() - started[i]));
            if (items[i].o_load == 0x5d8 && items[i].res == 1) status_reply_log(b, items[i].ticket);
        }
        status_pump(b);
        if (!pending) break;
        /* a running write (the handle save) ignores the stop file, the server may already have it */
        for (writing = 0, i = 0; i < n; i++) if (items[i].write && started[i] && items[i].res == 0) writing = 1;
        if (!writing && stop_requested()) {
            lg("phase_c: stop requested during profile load - signing out and exiting, FFXI NOT started\r\n");
            for (i = 0; i < n; i++)
                if (items[i].res == 0 && started[i]) CANCEL_JOB(items[i].ticket);
            viewer_sign_out(b);
            end_pol(8);
        }
        POLCON_STEP(b);
        POLCON_IDLE(b);
        Sleep(1);
    }
    for (i = 0; i < n; i++)
        if (items[i].res != 1)
            lg("profile: %s -> %d (FAILED)\r\n", items[i].name, items[i].res);
}

static int count_handles(BYTE *b)
{
    BYTE rec[0x28];
    int n = 0;
    for (int i = 0; i < 64; i++)
        if (get_handle(b, i, rec) >= 0 && (rec[0] & 1)) n++;
    return n;
}
static int wait_handle_name(BYTE *b, char *name, int cap)
{
    WCHAR tbuf[256];
    char user[64], ans[256];
    int warned = 0;
    const WCHAR *t = cred_target("POL_CRED_HANDLE", tbuf, 256);
    if (!t) return -2;
    lg("phase_c: waiting for handle name from launcher\r\n");   /* Provenance watches for this line */
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
            lg("phase_c: stop requested while waiting for a handle name - signing out and exiting, FFXI NOT started\r\n");
            viewer_sign_out(b);
            end_pol(8);
        }
        if (owner_gone()) {
            lg("phase_c: Provenance ended while waiting for a handle name - signing out and exiting\r\n");
            viewer_sign_out(b);
            end_pol(8);
        }
        POLCON_STEP(b);
        POLCON_IDLE(b);
        Sleep(200);
    }
    lg("phase_c: no handle name within 10 minutes\r\n");
    return -3;
}
#define NO_HANDLE_EXIT 4
/* a new account has no handle and FFXI's world login dies with POL-0001 (I'm a damn moron, forgot about the handle).
   Handles are permanent and public, so only the typed name's used */
static int handle_bootstrap(BYTE *b)
{
    static const char *const why[] = { "creation cancelled", "no launcher target", "no answer within 10 minutes" };
    struct pload fresh = { "handles (pre-claim)", 0x2ac, 0x2b0, 0, 0 };
    struct pload put = { "handle save", 0x1030, 0x1034, 0, 0, 1 };
    char name[32] = {0};
    WCHAR wname[32];
    BYTE id[16];
    int n, w, idx, ng;
    n = count_handles(b);
    lg("phase_c: POL handles on account: %d\r\n", n);
    if (n > 0) return 0;
    w = wait_handle_name(b, name, sizeof name);
    if (w < 0) {
        lg("phase_c: NO POL HANDLE - this account needs a PlayOnline handle before FFXI "
           "can log in (%s)\r\n", w >= -3 ? why[-w - 1] : "unknown");
        return NO_HANDLE_EXIT;
    }
    if (!ascii_name_ok(name)) {
        lg("phase_c: handle name rejected locally (1-15 characters, not all spaces)\r\n");
        return 5;
    }
    /* reload first, the save only goes through when the list stamps match (polcore.dll 0x10403050 >= 0x10403044) */
    run_loads(b, &fresh, 1);
    if (fresh.res != 1) return 5;
    MultiByteToWideChar(CP_ACP, 0, name, -1, wname, 32);
    ng = hr_ngword(b, wname);
    if (ng != 0) {
        lg(ng > 0 ? "phase_c: handle name rejected (the Viewer's word list)\r\n"
                  : "phase_c: handle name rejected (the Viewer's word list could not be read)\r\n");
        return 5;
    }
    /* the viewer's first-login steps: name -> id (0xa40), claim a free slot (0x314), select it (0x1434), save */
    ZeroMemory(id, sizeof id);
    ((int (__cdecl *)(void *, const WCHAR *, int))SVC(0xa40))(id, wname, 0x81ac);
    idx = ((int (__cdecl *)(int, int, const void *))SVC(0x314))(-1, 1, id);
    if (idx < 0) {
        lg("phase_c: handle claim failed %d (0x%08x)\r\n", idx, (unsigned)idx);
        return 5;
    }
    ((void (__cdecl *)(int))SVC(0x1434))(idx);
    run_loads(b, &put, 1);
    if (put.res != 1) {
        lg("phase_c: handle save failed %d - releasing slot %d\r\n", put.res, idx);
        ((svc_i)SVC(0x318))(idx);
        return 5;
    }
    /* the viewer goes on with polcore's own list here, the claimed slot's already in it */
    n = count_handles(b);
    if (n < 1) {
        lg("phase_c: handle save reported ok but the handle list is still empty\r\n");
        return 5;
    }
    lg("phase_c: POL HANDLE CREATED '%s' (slot %d), handles now %d\r\n", name, idx, n);
    return 0;
}

/* POL_PRESTART: pol.exe starts at app open with no credentials and waits here having sent nothing. A login writes
   the go file (allow-listed KEY=VALUE lines). */
static void prestart_wait(void)
{
    static const char *allowed[] = { "POL_CRED_POL", "POL_CRED_SEA", "POL_CRED_ONESHOT", "POL_CRED_OTP",
                                     "POL_CRED_HANDLE", "POL_CRED_PLAY", "POL_CRED_CHAT", "POL_STATUS", "POL_HOME",
                                     "POL_HANDLE" };
    char gp[MAX_PATH + 64];
    if (!getenv_set("POL_PRESTART")) return;
    ipc_file(gp, "go");
    lg("prestart: ready - waiting for LOG IN (nothing sent yet)\r\n");
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
            for (line = buf; line && *line; line = p) {
                p = line;
                while (*p && *p != '\n') p++;
                if (*p) *p++ = 0;
                if (p > line && p[-1] == '\r') p[-1] = 0;
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
    void *get = SVC(0x2c4);
    BYTE rec[0x68];
    int any = 0, ffxi = 0;
    for (int i = 0; i < 64; i++)
        if (((int (__cdecl *)(unsigned, void *))get)((unsigned)i, rec) == 0 && (rec[0] & 1)) {
            any++;
            if (*(WORD *)(rec + 2) == 1) ffxi++;
        }
    g_ffxi_service = ffxi > 0;
    lg("phase_c: content ids: %d valid, %d FFXI\r\n", any, ffxi);
}
static void home_pick(BYTE *b);
/* status, friends, handles, characters, groups. A lean session only loads handles and characters */
static struct pload *login_set(struct pload *all, int *n)
{
    *n = g_lean ? 2 : 5;
    return g_lean ? all + 2 : all;
}
/* the viewer loads my status on its own and starts the lists from its OnSuccess (app.dll 0x10040822, 0x10040650) */
static void login_loads(BYTE *b, struct pload *run, int n)
{
    if (run[0].o_load != 0x5d8) {
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
        { "status",     0x5d8, 0x5dc, 0, 0 },
        { "friends",    0x28c, 0x290, 0, 0 },
        { "handles",    0x2ac, 0x2b0, 0, 0 },
        { "characters", 0x2bc, 0x2c0, 0, 0 },
        { "groups",     0x5fc, 0x600, 0, 0 }
    };
    struct pload *run;
    unsigned sm, flags;
    int r, i, rc, nrun, jail, last = -1;
    prestart_wait();
    g_lean = getenv_set("POL_LEAN");
    if (!read_details("POL_CRED_POL", "POL", id, sizeof id, pw, sizeof pw)) return;
    /* POL's own password box stops at 16, and polcore's copy of the encoded one has no room for more */
    if (lstrlenA(pw) > 16) {
        SecureZeroMemory(pw, sizeof pw);
        lg("phase_c: POL password longer than 16 characters - exiting, FFXI NOT started\r\n");
        end_pol(11);
        return;
    }
    lg("phase_c: REAL auth: POL ID='%s'\r\n", mask_id(id, m));
    ((svc_v)(b + 0x44390))();   /* session init (svc+0xc90) */
    /* inet init (polcore.dll 0x1000fe20 sets the flag at 0x100a66d4, the connect helper returns -2071 without it).
       One piece of the POL-2071 hunt, which was a fucking nightmare honestly good lord */
    r = ((svc_v)(b + 0xfe20))();
    lg("phase_c: inet init -> %d\r\n", r);
    /* POLCON_OPEN (polcore.dll 0x100448a0) type 3 = network, kind 1 + five zeroed dial-up buffers. It overwrites
       the password and host, so both go in after it. */
    r = ((int (__cdecl *)(int, void *))(b + 0x448a0))(3, &conn);
    lg("phase_c: POLCON_OPEN(3) -> %d\r\n", r);
    ((int (__cdecl *)(void *, const char *))SVC(0xd60))(encpw, pw);   /* ENCODE_PASSWORD */
    SecureZeroMemory(pw, sizeof pw);
    /* SET_PASSWORD (polcore.dll 0x10046df0) copies the bytes into polcore, so mine get cleared */
    ((int (__cdecl *)(void *, unsigned))SVC(0xd50))(encpw, 0);
    SecureZeroMemory(encpw, sizeof encpw);
    ((int (__cdecl *)(void *, const char *))SVC(0xd90))(hostbuf, "ci000.pol.com");   /* ENCODE_HOST */
    ((svc_p)SVC(0xd54))(hostbuf);   /* SET_POLCOM_HOST */
    /* ENCODE_POLID (polcore.dll 0x10046e70) scrambles the ID into polcore. It's reversible, so never logged. */
    ((void (__cdecl *)(void *, const char *))(b + 0x46e70))(b + 0x99280, id);
    SecureZeroMemory(id, sizeof id);
    sea_seed(b);
    *(unsigned *)(b + 0x9aa14) = 0xfffffbee;
    if (*(unsigned *)(b + 0x99408) == 0)
        *(unsigned *)(b + 0x99408) = 0x12;
    /* no give-up of my own, polcore fails a stuck step itself and Provenance ends a login that goes quiet */
    for (;;) {
        sm = *(unsigned *)(b + 0x99408);
        if (sm != (unsigned)last) {
            last = (int)sm;
            lg("auth SM: state=%u res=%d err=%08x\r\n", sm, *(int *)(b + 0x996b4), *(unsigned *)(b + 0x9aa14));
        }
        if (sm == 30 || sm == 32 || sm == 36 || sm == 39) break;
        if (stop_requested()) {
            lg("phase_c: stop requested during sign-in - exiting, FFXI NOT started\r\n");
            end_pol(8);
        }
        if (owner_gone()) {
            lg("phase_c: Provenance ended during sign-in - exiting, FFXI NOT started\r\n");
            end_pol(8);
        }
        POLCON_STEP(b);
        POLCON_IDLE(b);
        if (*(unsigned *)(b + 0x99408) == sm) Sleep(16);
    }
    sm = *(unsigned *)(b + 0x99408);
    if (sm != 30) {
        lg("phase_c: AUTH FAILED (state=%u res=%d err=%08x reason=%d) - exiting, FFXI NOT started\r\n",
           sm, *(int *)(b + 0x996b4), *(unsigned *)(b + 0x9aa14), *(int *)(b + 0x9940c));
        end_pol(3);
        return;
    }
    lg("phase_c: AUTH OK (state 30) - session bound\r\n");
    notice_read(b);
    /* stricter than the viewer on purpose, a blocked account stops here */
    flags = *(WORD *)(b + 0x993f4);
    jail = SVC(0x13e4) ? ((svc_v)SVC(0x13e4))() : 0;
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
        ((svc_i)SVC(0x1020))(1);
    ((svc_v)SVC(0xd10))();   /* POLCON_CLEAR_ERROR */
    run = login_set(all, &nrun);
    login_loads(b, run, nrun);
    g_friends_loaded = all[1].res == 1;
    /* a load refused with an account status (-0x1450 and down: suspended, IP restricted, password changed...) turns
       the account away, any other failure signs out too, like the viewer */
    for (i = 0; i < nrun; i++)
        if (run[i].res <= -0x1450 && run[i].res >= -0x154f) {
            wsprintfA(what, "load:%s=%d", run[i].name, run[i].res);
            account_blocked(b, what);
        }
    for (i = 0; i < nrun; i++)
        if (run[i].res != 1) {
            lg("phase_c: LOAD FAILED %s=%d - signed out, nothing started\r\n", run[i].name, run[i].res);
            viewer_sign_out(b);
            end_pol(10);
        }
    if (SVC(0x734) && (((svc_v)SVC(0x734))() & 0xff)) {
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
    *(unsigned *)(b + 0x99254) = 1;
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
static int is_me(BYTE *b, unsigned lo, unsigned hi);
static void fmt_polid(BYTE *b, unsigned lo, unsigned hi, char out[12]);
static void peer_key(BYTE *b, unsigned lo, unsigned hi, unsigned slot, char out[17]);
static int send_built(void);
static void wr(HANDLE h, const char *s);

static char g_send_token[48];      /* the snapshot's "send", the newest message and its state */
static char g_send_state[48] = "";
/* built messages in the order they were built, job -1 until the PUT starts. Two at once, the viewer's own most */
#define SEND_MAX 2
typedef struct { BYTE *msg; int job; ULONGLONG started; char token[48]; } sendrec;
static sendrec g_sq[SEND_MAX];
static int g_sq_n;
static int sends_going(void) { return g_sq_n > 0; }
static int msg_jobs(void)
{
    int n = g_del_job >= 0, i;
    for (i = 0; i < FETCH_LANES; i++) n += g_rd[i].job >= 0;
    for (i = 0; i < g_sq_n; i++) n += g_sq[i].job >= 0;
    return n;
}
static int slots_free(BYTE *b)
{
    int used = 0, total = 4;
    if (SVC(0xff4)) ((void (__cdecl *)(int *, int *))SVC(0xff4))(&used, &total);
    return total - used;
}
/* the viewer's rule (app.dll 0x100512cf) plus a job. 2 of 4 polcore slots free (svc 0xff4) means a third only
   starts with nothing else running */
#define MSG_JOBS_MAX 3
static int msg_slot_ok(BYTE *b) { return msg_jobs() < MSG_JOBS_MAX && slots_free(b) >= 2; }
static int send_built(void)
{
    for (int i = 0; i < g_sq_n; i++) if (g_sq[i].job < 0) return 1;
    return 0;
}

/* sends are capped, so nothing (a stuck app included) can hammer the server with them */
#define PACE_PER_MIN 40
#define PACE_PER_HOUR 600
enum { PACE_OK, PACE_MIN, PACE_HOUR, PACE_HELD, PACE_STOPPED };
typedef struct {
    ULONGLONG take[PACE_PER_HOUR];   /* ring of PUT start times */
    int next, count;
    ULONGLONG hold, failed;          /* no PUT before hold, when the last failure landed */
    int fails, stopped;              /* failed sends in a row, no more sends this session */
} send_pace;
static send_pace g_pace;
static int pace_check(const send_pace *p, ULONGLONG now)
{
    if (p->stopped) return PACE_STOPPED;
    if (now < p->hold) return PACE_HELD;
    if (p->count >= PACE_PER_MIN &&
        now - p->take[(p->next + PACE_PER_HOUR - PACE_PER_MIN) % PACE_PER_HOUR] < 60000) return PACE_MIN;
    if (p->count >= PACE_PER_HOUR && now - p->take[p->next] < 3600000) return PACE_HOUR;
    return PACE_OK;
}
static void pace_take(send_pace *p, ULONGLONG now)
{
    p->take[p->next] = now;
    p->next = (p->next + 1) % PACE_PER_HOUR;
    if (p->count < PACE_PER_HOUR) p->count++;
}
/* r is a send's result (1 = stored), ms how long its PUT took. Only sends come here, a delete's -0x1540 (a slot
   this account lost) never does */
static void pace_done(send_pace *p, ULONGLONG now, int r, ULONGLONG ms)
{
    ULONGLONG h;
    if (r == NO_SLOT) return;
    if (r == 1) p->fails = 0;
    /* one already on the wire when the last failure landed is the same blip, not another strike */
    else if (!(p->fails && now - ms <= p->failed) && ++p->fails >= 3) p->stopped = 1;
    if (r != 1 && !p->stopped) {
        p->failed = now;
        h = now + (p->fails == 1 ? 10000 : 60000);
        if (p->hold < h) p->hold = h;
    }
    if (ms > 5000 && p->hold < now + 5000) p->hold = now + 5000;
}
static void send_paced(int r, ULONGLONG ms)
{
    int was = g_pace.stopped;
    pace_done(&g_pace, GetTickCount64(), r, ms);
    if (g_pace.stopped) {
        if (!was) lg("mail: send failed %d - no more sends this session\r\n", r);
    } else if (r != 1) {
        lg("mail: send failed %d - sends wait %d s\r\n", r, g_pace.fails == 1 ? 10 : 60);
    } else if (ms > 5000) {
        lg("mail: slow send (%u ms) - the next one waits 5 s\r\n", (unsigned)ms);
    }
}
/* a message that can't go this session or this hour is refused, not kept waiting */
static const char *pace_refusal(void)
{
    int why = pace_check(&g_pace, GetTickCount64());
    return why == PACE_STOPPED ? "failed:stopped" : why == PACE_HOUR ? "failed:hourly limit" : NULL;
}

#define SEND_HIST 8
static struct { char token[48]; char state[48]; DWORD time, seq, to_lo, to_hi; int from_slot; } g_send_hist[SEND_HIST];
static int g_send_hist_next;
static void send_note(const char *token, const char *state)
{
    if (!lstrcmpA(token, g_send_token)) lstrcpynA(g_send_state, state, sizeof g_send_state);
}
/* a line that went to another POL ID, remembered so the same line coming back can be told from a real one */
static void sent_record(BYTE *b, const BYTE *hdr)
{
    DWORD lo = *(const DWORD *)(hdr + 8), hi = *(const DWORD *)(hdr + 0xc);
    if (!polid_ok(b, lo, hi) || is_me(b, lo, hi)) return;
    g_sent_ids[g_sent_ids_next].time = *(const DWORD *)(hdr + 0x34);
    g_sent_ids[g_sent_ids_next].seq = *(const DWORD *)(hdr + 0x30);
    g_sent_ids[g_sent_ids_next].from_slot = hdr[0x3c];
    g_sent_ids_next = (g_sent_ids_next + 1) % SENT_IDS;
    if (g_sent_ids_n < SENT_IDS) g_sent_ids_n++;
    g_mail_raw_new = 1;
}
/* hdr is the message that went, its header time and seq let the app put my line where the friend sees it */
static void send_result(const char *token, const char *state, const BYTE *hdr)
{
    send_note(token, state);
    lstrcpynA(g_send_hist[g_send_hist_next].token, token, sizeof g_send_hist[0].token);
    lstrcpynA(g_send_hist[g_send_hist_next].state, state, sizeof g_send_hist[0].state);
    g_send_hist[g_send_hist_next].time = hdr ? *(const DWORD *)(hdr + 0x34) : 0;
    g_send_hist[g_send_hist_next].seq = hdr ? *(const DWORD *)(hdr + 0x30) : 0;
    g_send_hist[g_send_hist_next].to_lo = hdr ? *(const DWORD *)(hdr + 0x08) : 0;
    g_send_hist[g_send_hist_next].to_hi = hdr ? *(const DWORD *)(hdr + 0x0c) : 0;
    g_send_hist[g_send_hist_next].from_slot = hdr ? hdr[0x3c] : -1;
    g_send_hist_next = (g_send_hist_next + 1) % SEND_HIST;
    g_snap_dirty = 1;
}
static void send_fail(const char *why)
{
    lg("mail: send refused - %s\r\n", why);
    send_result(g_send_token, why, NULL);
}
static void send_drop(int i)
{
    HeapFree(GetProcessHeap(), 0, g_sq[i].msg);
    for (; i < g_sq_n - 1; i++) g_sq[i] = g_sq[i + 1];
    g_sq_n--;
}
/* chat lines, answers and group messages all get paced here (a chat take only checks first), oldest built first */
static void mail_send_start(BYTE *b)
{
    ULONGLONG now = GetTickCount64();
    int why = pace_check(&g_pace, now), i, t;
    const char *no = pace_refusal();
    char st[24];
    sendrec *s;
    for (i = 0; i < g_sq_n && g_sq[i].job >= 0; i++) ;
    if (i == g_sq_n) return;
    s = &g_sq[i];
    if (no) {
        lg("mail: send refused - %s\r\n", no);
        send_result(s->token, no, NULL);
        send_drop(i);
        return;
    }
    /* in a drain (Play, log out) a built message goes once a message job's free, the pace's waits don't hold it */
    t = (g_draining ? msg_jobs() < MSG_JOBS_MAX : why == PACE_OK && msg_slot_ok(b)) ? ((svc_p)SVC(0x510))(s->msg)
                                                                                    : NO_SLOT;
    if (t == NO_SLOT) { send_note(s->token, "queued"); return; }
    lg("mail: send start -> %d\r\n", t);
    if (t < 0) {
        wsprintfA(st, "failed:%d", t);
        send_result(s->token, st, NULL);
        /* polcore refusing it locally (-0x1551 for a bad header) sent nothing, only the account codes count */
        if (t == -0x153a || t == -0x1540) send_paced(t, 0);
        send_drop(i);
    } else {
        pace_take(&g_pace, now);
        s->job = t;
        s->started = now;
        send_note(s->token, "sending");
        g_snap_dirty = 1;   /* the app hands over its next line once it sees this one's been taken */
    }
}
/* FFXI only prints a message's text when it's 300 bytes or less, counted in the Shift-JIS that goes */
#define TEXT_MAX_BYTES 300

/* like the viewer (app.dll 0x100556e0), a 0xa8 header (svc 0x438) then the body (svc 0x440). It joins the send
   queue under g_send_token */
static int msg_build(BYTE *b, int kind, unsigned toLo, unsigned toHi, int fromSlot, int toSlot,
                     const char *myName, const char *sj_subj, const char *sj_text, const BYTE *att, int attType)
{
    unsigned __int64 me = my_id(b);
    int attLen = att ? 0x20 : 0;
    int need = lstrlenA(sj_subj) + 1 + lstrlenA(sj_text) + 1 + attLen, n;
    BYTE *msg;
    if (g_sq_n >= SEND_MAX) return -1;
    msg = (BYTE *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, 0xa8 + need + 8);
    if (!msg || ((ULONG_PTR)msg & 7)) {
        if (msg) HeapFree(GetProcessHeap(), 0, msg);
        return -1;
    }
    n = ((int (__cdecl *)(const char *, const char *, int, const void *, char *))SVC(0x440))(
            sj_subj, sj_text, attLen, att, (char *)msg + 0xa8);
    /* 0x438's 8th argument is the subject line the recipient's list shows, not a name */
    ((int (__cdecl *)(void *, int, unsigned, unsigned, unsigned, unsigned, const char *, const char *,
                      int, int, int, int, int))SVC(0x438))(
            msg, kind, (unsigned)me, (unsigned)(me >> 32), toLo, toHi, myName, sj_subj,
            fromSlot, toSlot, 1000, att ? attType : 0, n);
    mail_log_hdr("send", msg);
    g_sq[g_sq_n].msg = msg;
    g_sq[g_sq_n].job = -1;
    g_sq[g_sq_n].started = 0;
    lstrcpynA(g_sq[g_sq_n].token, g_send_token, sizeof g_sq[0].token);
    g_sq_n++;
    g_snap_dirty = 1;
    return n;
}
static void mail_send_parse(BYTE *b, char *buf)
{
    static char sj_subj[0x84], sj_text[0x1004];
    BYTE fr[0x140];
    char *p = buf, *tok, *who, *subj, *text, *q, myName[16], frName[64];
    int row = 0, digits = 0, mySlot, n, wl;
    g_send_token[0] = 0;   /* the token first, so a refusal reports under its own token */
    if ((q = strchr(buf, '\n')) != NULL) {
        int tl = (int)strcspn(++q, "\r\n");
        lstrcpynA(g_send_token, q, (tl < (int)sizeof g_send_token - 1 ? tl : (int)sizeof g_send_token - 1) + 1);
    }
    if (pace_refusal()) { send_fail(pace_refusal()); return; }
    while (*p >= '0' && *p <= '9' && digits < 4) { row = row * 10 + (*p++ - '0'); digits++; }
    if (*p == '\r') p++;
    if (!digits || *p != '\n') { send_fail("failed:bad request"); return; }
    p++;
    tok = p; while (*p && *p != '\n') p++;
    if (*p) *p++ = 0;
    who = p; while (*p && *p != '\n') p++;
    if (*p) *p++ = 0;
    subj = p; while (*p && *p != '\n') p++;
    if (*p) *p++ = 0;
    text = p;
    lstrcpynA(g_send_token, tok, sizeof g_send_token);
    if (row >= 200 || !get_friend(b, row, fr)) { send_fail("failed:no such friend"); return; }
    n = lstrlenA((const char *)fr + 0xa0);
    sjis_to_utf8((const char *)fr + 0xa0, n > 15 ? 15 : n, frName, sizeof frName);
    if (lstrcmpA(frName, who) != 0) { send_fail("failed:friend list changed"); return; }
    if (*(DWORD *)(fr + 0x08) & 0x10000000) { send_fail("failed:pending"); return; }
    if (is_me(b, *(DWORD *)(fr + 0x00), *(DWORD *)(fr + 0x04))) { send_fail("failed:self"); return; }
    wl = MultiByteToWideChar(CP_UTF8, 0, text, -1, NULL, 0);
    if (wl <= 0 || wl - 1 > TEXT_MAX_BYTES) { send_fail("failed:too long"); return; }
    for (q = text; *q; q++) if ((BYTE)*q < 0x20 && *q != '\n') *q = ' ';
    for (q = subj; *q; q++) if ((BYTE)*q < 0x20) *q = ' ';
    mySlot = my_handle(b, myName);
    utf8_to_sjis(subj, sj_subj, 0x80);
    n = utf8_to_sjis(text, sj_text, 0x1000);
    if (!sj_text[0]) { send_fail("failed:empty"); return; }
    if (n > TEXT_MAX_BYTES) { send_fail("failed:too long"); return; }
    n = msg_build(b, 0, *(DWORD *)(fr + 0x00), *(DWORD *)(fr + 0x04), mySlot, (int)FR_SLOT(fr), myName,
                  sj_subj, sj_text, NULL, 0);
    if (n < 0) { send_fail("failed:alloc"); return; }
    lg("mail: send to row %d (my slot %d, body %d bytes)\r\n", row, mySlot, n);
    mail_send_start(b);
}
static void mail_send_pump(BYTE *b)
{
    static int said;
    char *buf, st[24];
    int i, r, why, again = 0;
    ULONGLONG ms;
    for (i = 0; i < g_sq_n; i++) {
        sendrec *s = &g_sq[i];
        if (s->job < 0 || (r = ((svc_i)SVC(0x514))(s->job)) == 0) continue;
        s->job = -1;
        /* nothing went, the viewer starts it again next frame (its send state 4 -> 2) */
        if (r == NO_SLOT) { send_note(s->token, "queued"); again = 1; continue; }
        if (r == 1) lstrcpyA(st, "sent"); else wsprintfA(st, "failed:%d", r);
        send_result(s->token, st, s->msg);
        if (r == 1) sent_record(b, s->msg);
        ms = GetTickCount64() - s->started;
        lg("mail: send done -> %d (%u ms)\r\n", r, (unsigned)ms);
        send_paced(r, ms);
        send_drop(i--);   /* only now, the upload read it during the polls */
    }
    /* a built message is a write the user asked for, so it still starts during a drain (Play, log out), which waits
       for it. No new requests then though */
    if (send_built()) { if (!again) mail_send_start(b); return; }
    /* an answer owns the send slots from its request to its message. A waiting answer or group request goes
       before the next line, they only start with nothing sending */
    if (g_sq_n >= SEND_MAX || g_draining || g_answer_kind || handle_switching() || !request_waiting("send") ||
        request_new("answer") || request_new("group"))
        return;
    /* stopped or at the hourly cap, a request is still taken so the app hears it wasn't sent */
    why = pace_check(&g_pace, GetTickCount64());
    if (why != PACE_OK && why != PACE_STOPPED && why != PACE_HOUR) {
        if (why == PACE_MIN && said != why)
            lg("mail: %d sends in a minute - the next chat line waits\r\n", PACE_PER_MIN);
        said = why;
        return;
    }
    if (why == PACE_OK && !msg_slot_ok(b)) return;
    buf = (char *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, 0x4000);
    if (!buf) return;
    if (take_request("send", buf, 0x4000) >= 0) {
        said = 0;
        mail_send_parse(b, buf);
    }
    HeapFree(GetProcessHeap(), 0, buf);
}
static int g_xi_step;   /* my FFXI character's privacy: 1 load waits for a slot, 2 load job */
static int g_pic_want;
static int g_cr_db;   /* chat's zone and room lists, 0 idle, the steps are pol_chat.c's cr_db_pump */
static int db_free(void)
{
    return GetTickCount64() - g_db_last >= 3000 && !g_srch_step && !g_prof_step && !g_ch_step && !g_fp_step &&
           !g_cr_db && !g_me_step && !g_xi_step && g_pic_want < 0;
}

#pragma pack(push, 1)
typedef struct grp_hdr {
    DWORD idLo, idHi, notice, flags;             /* flags: bit 0 valid, 1-3 my class, 4-9 my slot, 10-12 my status */
    char  name[0x14];
    BYTE  z24, nMembers, myIndex, stale;
} grp_hdr;
typedef struct grp_mem {
    DWORD idLo, idHi, flags;                     /* flags: bit 0 valid, 1 ignored, 4-6 status, 18-20 class, 21 me */
    DWORD content[3];
    DWORD tLo, tHi, hLo, hHi;
    char  name[16];
    DWORD pic;
    char  text[0x18];
    WCHAR comment[0x33];
    WORD  stale;
    BYTE  pad[4];
} grp_mem;
#pragma pack(pop)
typedef char gr_chk_hdr[sizeof(grp_hdr) == 0x28 ? 1 : -1];
typedef char gr_chk_mem[sizeof(grp_mem) == 0xc0 ? 1 : -1];
static int get_group(BYTE *b, DWORD lo, DWORD hi, grp_hdr *g)
{
    memset(g, 0, sizeof *g);
    return ((int (__cdecl *)(DWORD, DWORD, void *))SVC(0x618))(lo, hi, g) == 1;
}
static int get_member(BYTE *b, DWORD lo, DWORD hi, int i, grp_mem *m)
{
    memset(m, 0, sizeof *m);
    return ((int (__cdecl *)(DWORD, DWORD, unsigned, void *))SVC(0x61c))(lo, hi, (unsigned)i, m) == 1 && (m->flags & 1);
}

/* saveprofile levels: 1 private, 2 friends, 3 everyone, 0 keep */
static int g_me_job = -1;
static char g_me_token[48], g_me_state[32] = "";
static pdb_rec *g_me_rec;
static pdb_cond g_me_cond;
static pe_req g_me_req;
static char g_me_mail[0x150];
static WCHAR g_me_name[32];
static ULONGLONG g_me_t0;
static unsigned __int64 g_me_hid;

static void me_done(const char *state)
{
    lstrcpynA(g_me_state, state, sizeof g_me_state);
    g_me_step = 0;
    g_me_job = -1;
    lg("profile edit: %s\r\n", state);
    g_snap_dirty = 1;
}
static const char *const g_me_fields[] = { "z_mail", "z_age", "z_age_f", "z_sex", "z_job", "z_area",
                                           "z_lang0", "z_lang1", "z_lang2", "z_fav0", "z_fav1", "z_fav2", "z_ficon" };
#define ME_EDITABLE ((int)(sizeof g_me_fields / sizeof g_me_fields[0]) - 1)
static int me_table(BYTE *b, char *out, int cap, const pfb_item *pi, int parts)
{
    static char part[0x80], js[0x400];
    int o = 0;
    if (!pi || !pi->tab) { lstrcpynA(out, "[]", cap); return 2; }
    out[o++] = '[';
    for (int i = 0; i < pi->tab->n && o < cap - 0x1000; i++) {
        o += wsprintfA(out + o, "%s[%lu", i ? "," : "", pi->tab->e[i].code);
        for (int k = 0; k < (parts > 0 ? parts : 1); k++) {
            if (parts > 0) pf_part(pi->tab, i, k, part, sizeof part);
            else pf_last(pi->tab->e[i].text, part, sizeof part);
            pol_json(b, part, 0x180, js, sizeof js);
            o += wsprintfA(out + o, ",%s", js);
        }
        out[o++] = ']';
    }
    out[o++] = ']';
    out[o] = 0;
    return o;
}
/* the e-mail address from my POL profile goes in masked (first letter, ***, then the domain). No @ means it's
   the viewer's "No Mail address" text, left alone */
static void mask_mail(const char *in, char *out, int cap)
{
    const char *at = strchr(in, '@');
    int lead;
    if (!at) { lstrcpynA(out, in, cap); return; }
    lead = ((BYTE)in[0] >= 0x81 && (BYTE)in[0] <= 0x9f) || (BYTE)in[0] >= 0xe0 ? 2 : 1;   /* SJIS lead byte */
    if (lead > at - in) lead = (int)(at - in);
    lstrcpynA(out, in, lead + 1);
    lstrcatA(out, "***");
    lstrcatA(out, at);
}
static int get_chr(BYTE *b, int i, BYTE *rec);

/* my FFXI characters, the viewer's edit page tabs (app.dll 0x10224e5c): my record's z_phead slots when it's this
   handle's, else the Content IDs linked to the handle. Only title 1 gets one here */
typedef struct me_chr { int slot; DWORD ctsid, lo, hi; } me_chr;
static int me_chars(BYTE *b, me_chr *out, const char **from)
{
    pdb_item *ph = g_me_rec && g_me_rec->items && g_fs_pib ? pdb_get(b, g_fs_pib, "z_phead", g_me_rec) : NULL;
    const BYTE *h = ph && (ph->flags & 1) ? (const BYTE *)ph->v.bytes.p : NULL;
    BYTE c[0x68];
    pol_handle me;
    int n = 0, i;
    *from = "nowhere";
    if (h && !IsBadReadPtr(h, 0xb0) && (*(const DWORD *)(h + 0x98) & 1) && fr_hid(h) == g_me_hid) {
        *from = "z_phead";
        for (i = 0; i < 8; i++) {
            const BYTE *sl = h + 0x18 + i * 0x10;
            if (!(*(const WORD *)sl & 1) || *(const WORD *)(sl + 2) != 1) continue;
            out[n].slot = i;
            out[n].ctsid = *(const DWORD *)(sl + 4);
            out[n].lo = *(const DWORD *)(sl + 8);
            out[n].hi = *(const DWORD *)(sl + 0xc);
            n++;
        }
        return n;
    }
    memset(&me, 0, sizeof me);
    get_handle(b, g_my_slot, &me);
    if (((((unsigned __int64)me.hi << 32 | me.lo) >> 1) & 0xfffffffffffULL) != g_me_hid) return 0;
    *from = "handle";
    for (i = 0; i < 8; i++) {
        if (!(me.chars[i] & 1) || !get_chr(b, (me.chars[i] >> 1) & 0x3f, c) || *(WORD *)(c + 2) != 1) continue;
        out[n].slot = (me.chars[i] >> 1) & 0x3f;
        out[n].ctsid = *(DWORD *)(c + 4);
        out[n].lo = *(DWORD *)(c + 8);
        out[n].hi = *(DWORD *)(c + 0xc);
        n++;
    }
    return n;
}
static void me_chr_key(const me_chr *c, char k[25])
{
    wsprintfA(k, "%08x%08x%08x", c->ctsid, c->lo, c->hi);
}
/* the name's the Content ID list's, z_phead doesn't have one */
static int me_chars_json(BYTE *b, char *out, int cap)
{
    me_chr ch[8];
    BYTE c[0x68];
    char k[25], nm[64];
    const char *from;
    int n = me_chars(b, ch, &from), o, i, j;
    o = wsprintfA(out, ",\"characters\":[");
    for (i = 0; i < n && o < cap - 200; i++) {
        lstrcpyA(nm, "\"\"");
        for (j = 0; j < 64; j++)
            if (get_chr(b, j, c) && *(DWORD *)(c + 4) == ch[i].ctsid && *(DWORD *)(c + 8) == ch[i].lo &&
                *(DWORD *)(c + 0xc) == ch[i].hi) {
                sjis_json(nm, sizeof nm, c + 0x18, 0x0f);
                break;
            }
        me_chr_key(&ch[i], k);
        o += wsprintfA(out + o, "%s{\"slot\":%d,\"key\":\"%s\",\"world\":%u,\"name\":%s}", i ? "," : "", ch[i].slot, k,
                       (ch[i].ctsid >> 16) & 0xff, nm);
    }
    out[o++] = ']';
    out[o] = 0;
    lg("profile edit: %d FFXI characters (from %s)\r\n", n, from);
    return o;
}
static int me_write_json(BYTE *b)
{
    static const struct { const char *name; int parts; } tabs[] = {
        { "z_sex", 0 }, { "z_job", 0 }, { "z_area", 3 }, { "z_lang0", 0 }, { "z_fav0", 2 } };
    static char js[0xc00];
    pdb_schema *s = g_fs_pib;
    pfb_hdr *p = g_pfb;
    const int cap = 0x40000;
    char *out = (char *)HeapAlloc(GetProcessHeap(), 0, cap), path[MAX_PATH + 64], tmp[MAX_PATH + 80], tk[128];
    int o = 0, i, ok = 0;
    DWORD n;
    pol_handle h;
    if (!out) return 0;
    memset(&h, 0, sizeof h);
    get_handle(b, g_my_slot, &h);
    h.name[15] = 0;
    pol_json(b, h.name, 0x400, js, sizeof js);
    json_str(tk, sizeof tk, g_me_token);
    o += wsprintfA(out + o, "{\"token\":%s,\"handle\":%s,\"slot\":%d,\"key\":\"%03x%08x\",\"level\":%u,\"fields\":{",
                   tk, js, g_my_slot, (unsigned)(g_me_hid >> 32), (unsigned)g_me_hid, g_me_rec->level);
    for (i = 0; i < (int)(sizeof g_me_fields / sizeof g_me_fields[0]); i++) {
        pdb_item *it = pdb_get(b, s, g_me_fields[i], g_me_rec);
        int idx = ((svc4bc_t)SVC(0x4bc))(s, g_me_fields[i]);
        const pfb_item *pi = idx >= 0 ? pf_item(p, idx) : NULL;
        DWORD v = 0;
        if (!it || idx < 0) continue;
        if (pi) pol_json(b, pi->label, 0x100, js, sizeof js);
        else lstrcpyA(js, "\"\"");
        o += wsprintfA(out + o, "%s\"%s\":{\"label\":%s,\"level\":%u", i ? "," : "", g_me_fields[i], js, it->level);
        if (!strcmp(g_me_fields[i], "z_mail")) {
            char masked[0x150];
            mask_mail(it->v.str && !IsBadStringPtrA(it->v.str, 0x140) ? it->v.str : "", masked, sizeof masked);
            pol_json(b, masked, 0x400, js, sizeof js);
            memcpy(out + o, ",\"s\":", 5); o += 5;
            lstrcpyA(out + o, js); o += lstrlenA(js);
        } else if (!strcmp(g_me_fields[i], "z_area")) {
            pdb_item *c = pdb_get(b, s, "z_cntry", g_me_rec), *w = pdb_get(b, s, "z_wide", g_me_rec),
                     *l = pdb_get(b, s, "z_local", g_me_rec);
            v = (it->v.bytes.p ? (DWORD)*(BYTE *)it->v.bytes.p << 24 : 0) |
                (c && c->v.bytes.p ? (DWORD)*(BYTE *)c->v.bytes.p << 16 : 0) |
                (w && w->v.bytes.p ? (DWORD)*(BYTE *)w->v.bytes.p << 8 : 0) |
                (l && l->v.bytes.p ? (DWORD)*(BYTE *)l->v.bytes.p : 0);
            o += wsprintfA(out + o, ",\"v\":%lu", v);
        } else {
            switch (s->fields[idx].type) {
            case 2: v = it->v.bytes.p ? *(BYTE *)it->v.bytes.p : 0; break;
            case 3: case 4: v = it->v.u16; break;
            default: v = it->v.u32; break;
            }
            o += wsprintfA(out + o, ",\"v\":%lu", v);
        }
        out[o++] = '}';
    }
    lstrcpyA(out + o, "},\"tables\":{"); o += lstrlenA(out + o);
    for (i = 0; i < 5; i++) {
        int idx = ((svc4bc_t)SVC(0x4bc))(s, tabs[i].name);
        o += wsprintfA(out + o, "%s\"%s\":", i ? "," : "", tabs[i].name);
        o += me_table(b, out + o, cap - o - 16, idx >= 0 ? pf_item(p, idx) : NULL, tabs[i].parts);
    }
    out[o++] = '}';
    o += me_chars_json(b, out + o, cap - o - 4);
    out[o++] = '}';
    ipc_file(path, "myprofile.json");
    lstrcpyA(tmp, path); lstrcatA(tmp, ".tmp");
    HANDLE f = CreateFileA(tmp, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f != INVALID_HANDLE_VALUE) {
        ok = WriteFile(f, out, (DWORD)o, &n, NULL) && n == (DWORD)o;
        CloseHandle(f);
        ok = ok && MoveFileExA(tmp, path, MOVEFILE_REPLACE_EXISTING);
        if (!ok) DeleteFileA(tmp);
    }
    HeapFree(GetProcessHeap(), 0, out);
    return ok;
}
static void me_abandon(BYTE *b)
{
    if (g_me_step == 2 && g_me_job >= 0) {
        ((svc4dc_t)SVC(0x4dc))(g_me_job);
        g_me_rec = NULL;                    /* a cancelled read can still write, so never freed */
    }
    if (g_me_step == 1 || g_me_step == 2) me_done("cancelled");
}
static void me_load_start(BYTE *b)
{
    int t = ((svc4b4_t)SVC(0x4b4))(g_fs_pib, &g_me_cond, g_me_rec);
    if (t == NO_SLOT) { g_me_step = 1; return; }
    lg("profile edit: load -> %d\r\n", t);
    if (t < 0) { me_done(failed(t)); return; }
    g_me_job = t;
    g_me_step = 2;
}
static void me_save_start(BYTE *b)
{
    int t = pe_update_start(b, g_fs_pib, g_me_rec, g_my_slot, &g_me_req);
    if (t == NO_SLOT) { g_me_step = 3; return; }
    lg("profile edit: save %d field(s), level %d -> %d\r\n", g_me_req.n, g_me_req.level, t);
    if (t < 0) { me_done(failed(t)); return; }
    g_me_job = t;
    g_me_step = 4;
}
static int me_field_ok(const char *n)
{
    for (int i = 0; i < ME_EDITABLE; i++) if (!strcmp(g_me_fields[i], n)) return i + 1;
    return 0;
}
static void me_request(BYTE *b)
{
    char buf[2048], *p, *tok, *lvl, *line, *name, nomail[0x80], jp[MAX_PATH + 64];
    int kind, level = 0, idx;
    pol_handle h;
    if (!db_free()) return;
    kind = take_request("myprofile", buf, sizeof buf) >= 0 ? 1
         : take_request("saveprofile", buf, sizeof buf) >= 0 ? 2
         : take_request("rename", buf, sizeof buf) >= 0 ? 3 : 0;
    if (!kind) return;
    g_db_last = GetTickCount64();
    p = buf;
    tok = req_line(&p);
    lstrcpynA(g_me_token, tok, sizeof g_me_token);
    if (!fs_load_pib(b) || !pf_load(b)) { me_done("failed:no schema"); return; }
    g_me_t0 = GetTickCount64();
    if (kind == 1) {
        ipc_file(jp, "myprofile.json");
        DeleteFileA(jp);
        memset(&h, 0, sizeof h);
        get_handle(b, g_my_slot, &h);
        g_me_hid = (((unsigned __int64)h.hi << 32 | h.lo) >> 1) & 0xfffffffffffULL;
        if (!g_me_rec) g_me_rec = (pdb_rec *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, g_fs_pib->rec_size + 8);
        else memset(g_me_rec, 0, g_fs_pib->rec_size + 8);
        if (!g_me_rec) { me_done("failed:alloc"); return; }
        memset(&g_me_cond, 0, sizeof g_me_cond);
        ((svc4a0_t)SVC(0x4a0))(g_fs_pib, &g_me_cond.item, "z_hid", &g_me_hid, 1);
        if (!(g_me_cond.item.flags & 1)) { me_done("failed:schema"); return; }
        ((svc4a8_t)SVC(0x4a8))(&g_me_cond, 1);
        ((svc4ac_t)SVC(0x4ac))(1, &g_me_cond);
        lstrcpyA(g_me_state, "loading");
        g_snap_dirty = 1;
        me_load_start(b);
        return;
    }
    if (kind == 3) {
        char *key, now[17];
        name = req_line(&p);
        key = req_line(&p);
        memset(&h, 0, sizeof h);
        get_handle(b, g_my_slot, &h);
        /* the handle the editor loaded, so one left open across a switch can't rename the new one */
        wsprintfA(now, "%03x%08x", (h.hi >> 1) & 0xfff, h.lo >> 1 | h.hi << 31);
        if (lstrcmpA(key, now) != 0) { me_done("failed:not loaded"); return; }
        memset(g_me_name, 0, sizeof g_me_name);
        if (!name[0] || MultiByteToWideChar(CP_UTF8, 0, name, -1, g_me_name, 31) <= 0) {
            me_done("failed:bad name");
            return;
        }
        lstrcpyA(g_me_state, "saving");
        g_me_step = 5;
        g_me_t0 = GetTickCount64();
        g_snap_dirty = 1;
        return;
    }
    lvl = req_line(&p);
    nomail[0] = 0;
    memset(&h, 0, sizeof h);
    get_handle(b, g_my_slot, &h);
    /* one loaded under another handle (before a switch) isn't this one's */
    if (!g_me_rec || !g_me_rec->items || ((((unsigned __int64)h.hi << 32 | h.lo) >> 1) & 0xfffffffffffULL) != g_me_hid) {
        me_done("failed:not loaded");
        return;
    }
    if (lvl[0] >= '0' && lvl[0] <= '3' && !lvl[1]) level = lvl[0] - '0';
    else { me_done("failed:bad request"); return; }
    memset(&g_me_req, 0, sizeof g_me_req);
    g_me_req.level = level;
    g_me_req.utime = 1;
    while (*(line = req_line(&p))) {
        char *eq = strchr(line, '=');
        if (!eq) { me_done("failed:bad request"); return; }
        *eq++ = 0;
        if (!strcmp(line, "nomail")) { utf8_to_sjis(eq, nomail, sizeof nomail); continue; }
        if (!(idx = me_field_ok(line)) || g_me_req.n >= PE_MAXF) { me_done("failed:bad field"); return; }
        g_me_req.f[g_me_req.n].name = g_me_fields[idx - 1];   /* static, buf's gone by the time the save runs */
        if (idx == 1) {
            utf8_to_sjis(eq, g_me_mail, sizeof g_me_mail);
            if (lstrlenA(g_me_mail) > 0x13f) { me_done("failed:mail too long"); return; }
            /* the viewer checks an address with svc 0x6e8, and its own "No Mail address" text isn't one */
            if (!(nomail[0] && !lstrcmpA(g_me_mail, nomail)) && (!g_me_mail[0] || !pe_mail_ok(b, g_me_mail))) {
                me_done("failed:bad mail");
                return;
            }
            g_me_req.f[g_me_req.n].str = g_me_mail;
        } else {
            DWORD v = 0;
            int d = 0;
            for (; *eq >= '0' && *eq <= '9' && d < 10; eq++, d++) v = v * 10 + (DWORD)(*eq - '0');
            if (!d || *eq) { me_done("failed:bad value"); return; }
            if (idx == 2 && v > 999) { me_done("failed:bad value"); return; }
            if (idx == 3 && v > 1) { me_done("failed:bad value"); return; }
            g_me_req.f[g_me_req.n].v = v;
        }
        g_me_req.n++;
    }
    if (g_me_req.n == 0 && level == 0) { me_done("failed:nothing to save"); return; }
    lstrcpyA(g_me_state, "saving");
    g_snap_dirty = 1;
    me_save_start(b);
}
static void me_pump(BYTE *b, int ctl)
{
    pdb_item *it;
    int r;
    switch (g_me_step) {   /* 1 load waits for a slot, 2 load job, 3 save waits, 4 save job, 5 rename */
    case 0:
        if (ctl && !g_draining) me_request(b);
        return;
    case 1:
        if (g_draining) me_done("cancelled");
        else if (GetTickCount64() - g_me_t0 >= 120000) me_done("failed:busy");
        else me_load_start(b);
        return;
    case 2:
        r = ((svc4b8_t)SVC(0x4b8))(g_me_job);
        if (r == 0) {
            if (g_draining) me_abandon(b);
            return;
        }
        g_me_job = -1;
        if (r < 0) { me_done(failed(r)); return; }
        if (!g_me_rec->items) { me_done("failed:no profile"); return; }
        /* as in the profile pic save, the record must be this handle's (the viewer doesn't check) */
        it = pdb_get(b, g_fs_pib, "z_hid", g_me_rec);
        if (!it || it->v.u64 != g_me_hid) { me_done("failed:mismatch"); return; }
        it = pdb_get(b, g_fs_pib, "z_pnum", g_me_rec);
        if (FS_STRICT_PNUM && it && (it->flags & 1) && it->v.bytes.p && *(BYTE *)it->v.bytes.p != (BYTE)g_my_slot) {
            me_done("failed:mismatch");
            return;
        }
        me_done(me_write_json(b) ? "ready" : "failed:write");
        return;
    case 3:
        if (GetTickCount64() - g_me_t0 >= 120000) me_done("failed:busy");
        else me_save_start(b);   /* a write, so it still runs during a drain */
        return;
    case 4:
        r = ((svc4d8_t)SVC(0x4d8))(g_me_job);
        if (r == 0) return;
        g_me_job = -1;
        me_done(r == 1 ? "saved" : failed(r));
        return;
    case 5:
        r = handle_rename(b, g_my_slot, g_me_name);
        if (r == 0) {
            if (GetTickCount64() - g_me_t0 >= 240000) { g_hr.st = HR_IDLE; me_done("failed:timeout"); }
            return;
        }
        if (r == 1) me_done("saved");
        else me_done(r == HR_E_REVERT ? "failed:reloaded" : r == HR_E_NAME ? "failed:bad name"
                     : r == HR_E_NGWORD ? "failed:word" : failed(r));
        return;
    }
}

static int g_grp_job = -1, g_grp_poll;
static DWORD g_grp_new[2];
static char g_grp_token[48], g_grp_state[32] = "";

#define group_fail(why) refuse(g_grp_state, sizeof g_grp_state, "groups: refused - %s\r\n", why)
static int hex8(const char *x, DWORD *v)
{
    *v = 0;
    for (int i = 0; i < 8; i++) {
        if (!((x[i] >= '0' && x[i] <= '9') || (x[i] >= 'a' && x[i] <= 'f') || (x[i] >= 'A' && x[i] <= 'F'))) return 0;
        *v = *v << 4 | (DWORD)(x[i] <= '9' ? x[i] - '0' : (x[i] | 0x20) - 'a' + 10);
    }
    return 1;
}
static void group_start(int t, int poll, const char *what)
{
    lg("groups: %s -> %d\r\n", what, t);
    if (t < 0) { group_fail(failed(t)); return; }
    g_grp_job = t;
    g_grp_poll = poll;
    lstrcpyA(g_grp_state, "saving");
    g_snap_dirty = 1;
}
static void group_send(BYTE *b, int kind, DWORD toLo, DWORD toHi, int toSlot, int fromSlot,
                       const char *subj, const char *text, const BYTE att[0x20], const char *tok)
{
    char myName[16], sj_subj[0x84], sj_text[0x104];
    lstrcpynA(g_send_token, tok, sizeof g_send_token);
    if (!slot_name(b, fromSlot, myName)) { send_fail("failed:no handle"); return; }
    utf8_to_sjis(subj, sj_subj, 0x80);
    utf8_to_sjis(text, sj_text, 0x100);
    int n = msg_build(b, kind, toLo, toHi, fromSlot, toSlot, myName, sj_subj, sj_text, att, 10);
    lg("groups: message kind 0x%x from slot %d to slot %d (body %d)\r\n", kind, fromSlot, toSlot, n);
    if (n < 0) { send_fail("failed:alloc"); return; }
    mail_send_start(b);
    g_snap_dirty = 1;
}
/* the group id comes from the invitation, never the request */
static void group_answer(BYTE *b, int accept, int mid, const char *subj, const char *tok)
{
    const mailrec *m = NULL;
    DWORD lo, hi;
    grp_hdr gh;
    lstrcpynA(g_grp_token, tok, sizeof g_grp_token);
    for (int i = 0; i < g_mail_n; i++) if (g_mail[i].mid == mid) { m = &g_mail[i]; break; }
    if (!m || ((*(const WORD *)(m->hdr + 0x3e) >> 7) & 0x1f) != 0xe || !m->has_att) {
        group_fail("failed:no such invitation");
        return;
    }
    memcpy(&lo, m->att, 4);
    memcpy(&hi, m->att + 4, 4);
    if (!lo && !hi) { group_fail("failed:no such invitation"); return; }
    if (accept && get_group(b, lo, hi, &gh) && (gh.flags & 1)) {
        int cls = (int)((gh.flags >> 1) & 7), slot = (int)((gh.flags >> 4) & 0x3f);
        if (cls >= 3 && slot != m->hdr[0x3d]) { group_fail("failed:already a member"); return; }
    }
    lstrcpyA(g_grp_state, "sent as a message");
    group_send(b, accept ? 0xf : 0x10, *(const DWORD *)(m->hdr + 0x00), *(const DWORD *)(m->hdr + 0x04),
               m->hdr[0x3c], m->hdr[0x3d], subj, "", m->att, tok);
}
static void group_request(BYTE *b)
{
    char buf[1024], *p, *op, *tok, *gid, *a, *bb, *c, *d, k[17], mk[17], nm[0x80];
    static WCHAR w[0x33];
    const char *x;
    DWORD lo = 0, hi = 0;
    BYTE fr[0x140], att[0x20];
    grp_hdr gh;
    grp_mem m;
    int myClass = 0, haveGroup = 0, mid, slot, idx, cls, tcls, me, row, used = 0;
    /* a group job's -0x100 is a failure, so wait for a free polcore slot */
    if (g_sq_n || g_answer_kind || slots_free(b) < 1) return;
    if (take_request("group", buf, sizeof buf) < 0) return;
    p = buf;
    op = req_line(&p); tok = req_line(&p); gid = req_line(&p); a = req_line(&p); bb = req_line(&p); c = req_line(&p);
    d = req_line(&p);
    lstrcpynA(g_grp_token, tok, sizeof g_grp_token);
    if (pace_refusal() && (!lstrcmpA(op, "accept") || !lstrcmpA(op, "decline") || !lstrcmpA(op, "invite"))) {
        group_fail(pace_refusal());
        return;
    }
    if (!SVC(0x618) || !SVC(0x61c) || !SVC(0x608)) { group_fail("failed:no group services"); return; }
    if (lstrlenA(gid) == 16 && hex8(gid, &hi) && hex8(gid + 8, &lo)) {
        haveGroup = get_group(b, lo, hi, &gh) && (gh.flags & 1);
        myClass = haveGroup ? (int)((gh.flags >> 1) & 7) : 0;
    }
    if (lstrcmpA(op, "accept") == 0 || lstrcmpA(op, "decline") == 0) {
        if (!parse_num(a, 6, &mid)) { group_fail("failed:bad request"); return; }
        group_answer(b, op[0] == 'a', mid, bb, tok);
        return;
    }
    if (lstrcmpA(op, "create") == 0) {
        slot = ((svc_v)SVC(0x2f8))();
        for (x = a; *x; x++) if ((BYTE)*x < 0x20) break;
        memset(nm, 0, sizeof nm);
        if (*x || !a[0]) { group_fail("failed:bad name"); return; }
        utf8_to_sjis(a, nm, sizeof nm);
        if (!nm[0] || lstrlenA(nm) > 0x14) { group_fail("failed:name too long"); return; }
        if (slot < 0) slot = g_my_slot;
        g_grp_new[0] = g_grp_new[1] = 0xffffffff;
        group_start(((int (__cdecl *)(const char *, int))SVC(0x608))(nm, slot), 0x60c, "create");
        return;
    }
    if (!haveGroup) { group_fail("failed:no such group"); return; }
    if (lstrcmpA(op, "disband") == 0) {
        if (myClass != 5) { group_fail("failed:not master"); return; }
        gc_group_going(b, lo, hi);
        group_start(((int (__cdecl *)(DWORD, DWORD))SVC(0x610))(lo, hi), 0x614, "disband");
    } else if (lstrcmpA(op, "leave") == 0) {
        if (myClass == 5) { group_fail("failed:master cannot leave"); return; }
        if (myClass < 3) { group_fail("failed:not a member"); return; }
        gc_group_going(b, lo, hi);
        group_start(((int (__cdecl *)(DWORD, DWORD, unsigned, int))SVC(0x620))(lo, hi, gh.myIndex, 1), 0x624, "leave");
    } else if (lstrcmpA(op, "class") == 0) {
        if (!parse_num(a, 2, &idx) || idx > 63 || !parse_num(bb, 1, &cls)) { group_fail("failed:bad request"); return; }
        if (!get_member(b, lo, hi, idx, &m)) { group_fail("failed:no such member"); return; }
        peer_key(b, m.idLo, m.idHi, (m.hHi >> 13) & 0x3f, k);
        if (lstrcmpA(k, d) != 0) { group_fail("failed:no such member"); return; }
        tcls = (int)((m.flags >> 18) & 7);
        me = (int)((m.flags >> 21) & 1);
        /* the viewer's member menu: remove (1) a non-master as master or sub-master, relieve (3) a sub-master, and as
           master only make a member sub-master (4) or anyone below master (5) */
        if (me ||
            (cls == 1 && ((myClass != 4 && myClass != 5) || tcls == 5)) ||
            (cls == 3 && ((myClass != 4 && myClass != 5) || tcls != 4)) ||
            (cls == 4 && (myClass != 5 || tcls != 3)) ||
            (cls == 5 && (myClass != 5 || (tcls != 3 && tcls != 4))) ||
            (cls != 1 && cls != 3 && cls != 4 && cls != 5)) { group_fail("failed:not allowed"); return; }
        group_start(((int (__cdecl *)(DWORD, DWORD, unsigned, int))SVC(0x620))(lo, hi, (unsigned)idx, cls), 0x624,
                    "class");
    } else if (lstrcmpA(op, "comment") == 0) {
        if (myClass < 3) { group_fail("failed:not a member"); return; }
        memset(w, 0, sizeof w);
        if (a[0] && !comment_text(a, w)) { group_fail("failed:bad text"); return; }
        group_start(((int (__cdecl *)(DWORD, DWORD, int, int, const WCHAR *))SVC(0x630))(lo, hi, -3, -3, w), 0x634,
                    "comment");
    } else if (lstrcmpA(op, "invite") == 0) {
        if (myClass != 4 && myClass != 5) { group_fail("failed:not allowed"); return; }
        if (!parse_num(a, 3, &row) || row >= 200) { group_fail("failed:bad request"); return; }
        if (!get_friend(b, row, fr) || (*(DWORD *)(fr + 0x08) & 0x10000000)) {
            group_fail("failed:no such friend");
            return;
        }
        friend_key(b, fr, k);
        if (lstrcmpA(k, d) != 0) { group_fail("failed:no such friend"); return; }
        if (is_me(b, *(DWORD *)(fr + 0x00), *(DWORD *)(fr + 0x04))) { group_fail("failed:self"); return; }
        for (int mi = 0; mi < 64; mi++) {
            if (!get_member(b, lo, hi, mi, &m)) continue;
            used++;
            peer_key(b, m.idLo, m.idHi, (m.hHi >> 13) & 0x3f, mk);
            if (lstrcmpA(mk, k) == 0) { group_fail("failed:already in group"); return; }
        }
        if (used >= 64) { group_fail("failed:group full"); return; }
        memset(att, 0, sizeof att);
        memcpy(att, &gh.idLo, 4);
        memcpy(att + 4, &gh.idHi, 4);
        memcpy(att + 8, gh.name, 0x14);
        lstrcpyA(g_grp_state, "sent as a message");
        group_send(b, 0xe, *(DWORD *)(fr + 0x00), *(DWORD *)(fr + 0x04), (int)FR_SLOT(fr),
                   (int)((gh.flags >> 4) & 0x3f), bb, c, att, tok);
    } else {
        group_fail("failed:bad request");
    }
}
static void group_pump(BYTE *b, int ctl)
{
    if (g_grp_job < 0) {
        if (ctl && !g_draining && g_edit_job < 0) group_request(b);
        return;
    }
    int r = g_grp_poll == 0x60c ? ((int (__cdecl *)(int, DWORD *))SVC(0x60c))(g_grp_job, g_grp_new)
                                : ((svc_i)SVC(g_grp_poll))(g_grp_job);
    if (r == 0) return;
    g_grp_job = -1;
    if (r == 1) lstrcpyA(g_grp_state, "saved"); else wsprintfA(g_grp_state, "failed:%d", r);
    lg("groups: job done -> %d\r\n", r);
    g_snap_dirty = 1;
}

/* request and accept do AddFriend + a list upload before their message, decline replies at once, ignore does
   AddBlock first. The viewer's fixed subject, no text. */
static int g_answer_wait;
static BYTE g_answer_hdr[0x48];
static char g_answer_subj[0x84];
static char g_answer_text[0x84];
static char g_answer_token[48];

static void answer_fail(const char *why)
{
    lstrcpynA(g_send_token, g_answer_token, sizeof g_send_token);
    send_fail(why);
    g_answer_kind = 0;
    g_answer_wait = 0;
}
static void answer_send(BYTE *b)
{
    char myName[16];
    int mySlot = g_answer_hdr[0x3d], n;
    if ((g_answer_kind == 1 || g_answer_kind == 9) &&
        find_friend_row(b, *(DWORD *)(g_answer_hdr + 0x00), *(DWORD *)(g_answer_hdr + 0x04), g_answer_hdr[0x3c]) < 0) {
        answer_fail("failed:no such friend");
        return;
    }
    if (!slot_name(b, mySlot, myName)) { answer_fail("failed:no handle"); return; }
    lstrcpynA(g_send_token, g_answer_token, sizeof g_send_token);
    n = msg_build(b, g_answer_kind, *(DWORD *)(g_answer_hdr + 0x00), *(DWORD *)(g_answer_hdr + 0x04),
                  mySlot, g_answer_hdr[0x3c], myName, g_answer_subj, g_answer_text, NULL, 0);
    lg("friends: answer kind %d from slot %d to slot %d (body %d)\r\n", g_answer_kind, mySlot, g_answer_hdr[0x3c], n);
    if (n < 0) { answer_fail("failed:alloc"); return; }
    g_answer_kind = 0;
    mail_send_start(b);
    g_snap_dirty = 1;
}
static void answer_request(BYTE *b)
{
    char buf[512], *p, *op, *tok, *mids, *subj, *arg, *hkey, nm[16], k[17];
    int mid, i, r, used = 0;
    BYTE row[0x140];
    const mailrec *m = NULL;
    const srchrec *h;
    if (take_request("answer", buf, sizeof buf) < 0) return;
    p = buf;
    op = req_line(&p); tok = req_line(&p); mids = req_line(&p); subj = req_line(&p); arg = req_line(&p);
    hkey = req_line(&p);
    lstrcpynA(g_answer_token, tok, sizeof g_answer_token);
    /* every answer ends in a message, so refuse it before AddFriend or a list upload */
    if (pace_refusal()) { answer_fail(pace_refusal()); return; }
    if (!parse_num(mids, 6, &mid)) { answer_fail("failed:bad request"); return; }
    if (lstrcmpA(op, "request") == 0) {
        if (mid >= g_srch_n || g_srch_step != 0) { answer_fail("failed:no such result"); return; }
        h = &g_srch[mid];
        /* the hit must be the one the launcher showed, a newer search replaces the list */
        peer_key(b, h->lo, h->hi, (unsigned)h->slot, k);
        if (lstrcmpA(k, hkey) != 0) { answer_fail("failed:no such result"); return; }
        if (is_me(b, h->lo, h->hi)) { answer_fail("failed:self"); return; }
        if (find_friend_row(b, h->lo, h->hi, h->slot) >= 0) { answer_fail("failed:already a friend"); return; }
        r = ((svc_add)SVC(0x2ec))(-1, 0, h->lo, h->hi, (unsigned)h->slot, h->name);
        lg("friends: request to search hit %d -> AddFriend %d\r\n", mid, r);
        if (r < 0) { answer_fail(failed(r)); return; }
        memset(g_answer_hdr, 0, sizeof g_answer_hdr);
        *(DWORD *)(g_answer_hdr + 0x00) = h->lo;
        *(DWORD *)(g_answer_hdr + 0x04) = h->hi;
        g_answer_hdr[0x3c] = (BYTE)h->slot;
        g_answer_hdr[0x3d] = (BYTE)g_my_slot;
        utf8_to_sjis(subj, g_answer_subj, 0x80);
        utf8_to_sjis(arg, g_answer_text, 0x80);
        g_answer_kind = 1;
        g_answer_wait = 1;
        lstrcpynA(g_edit_token, tok, sizeof g_edit_token);
        lstrcpynA(g_send_token, tok, sizeof g_send_token);
        lstrcpyA(g_send_state, "saving");
        friend_upload_start(b);
        return;
    }
    g_answer_text[0] = 0;
    for (i = 0; i < g_mail_n; i++) if (g_mail[i].mid == mid) { m = &g_mail[i]; break; }
    if (!m || ((*(const WORD *)(m->hdr + 0x3e) >> 7) & 0x1f) != 1) { answer_fail("failed:no such request"); return; }
    memcpy(g_answer_hdr, m->hdr, 0x48);
    utf8_to_sjis(subj, g_answer_subj, 0x80);
    memcpy(nm, m->hdr + 0x10, 15);
    nm[15] = 0;
    if (lstrcmpA(op, "decline") != 0 && is_me(b, *(DWORD *)(m->hdr + 0x00), *(DWORD *)(m->hdr + 0x04))) {
        answer_fail("failed:self"); return;
    }
    if (lstrcmpA(op, "decline") == 0) {
        g_answer_kind = 10;
        answer_send(b);
        return;
    }
    if (lstrcmpA(op, "accept") == 0) {
        /* the viewer refuses an acceptance into a full list (200) first, even for someone already on it */
        for (i = 0; i < 200; i++) used += get_friend(b, i, row);
        if (used >= 200) { answer_fail("failed:-7188"); return; }
        r = ((svc_add)SVC(0x2ec))(-1, 0, *(DWORD *)(m->hdr + 0x00), *(DWORD *)(m->hdr + 0x04), m->hdr[0x3c], nm);
        lg("friends: accept request mid %d -> AddFriend %d\r\n", mid, r);
        if (r == -0x1c13) {   /* already on my list: no upload, just the acceptance */
            g_answer_kind = 9;
            answer_send(b);
            return;
        }
        if (r < 0) { answer_fail(failed(r)); return; }
        g_answer_kind = 9;
    } else if (lstrcmpA(op, "ignore") == 0) {
        /* this handle only (0u), the viewer's way from a request */
        r = ((svc_add)SVC(0x2e4))(-1, 0u, *(DWORD *)(m->hdr + 0x00), *(DWORD *)(m->hdr + 0x04), m->hdr[0x3c], nm);
        lg("friends: ignore requester mid %d -> AddBlock %d\r\n", mid, r);
        if (r == -0x1c13) { answer_fail("failed:already ignored"); return; }
        if (r < 0) { answer_fail(failed(r)); return; }
        g_answer_kind = 10;
    } else {
        answer_fail("failed:bad request");
        return;
    }
    g_answer_wait = 1;
    lstrcpynA(g_edit_token, tok, sizeof g_edit_token);
    lstrcpynA(g_send_token, tok, sizeof g_send_token);
    lstrcpyA(g_send_state, "saving");
    friend_upload_start(b);
}
static void answer_pump(BYTE *b, int ctl)
{
    if (g_answer_kind && g_answer_wait) {
        if (g_edit_job >= 0 || g_edit_upload_want) return;
        g_answer_wait = 0;
        if (lstrcmpA(g_edit_state, "saved") != 0) {
            char why[48];
            wsprintfA(why, "failed:list %s", g_edit_state);
            answer_fail(why);
            return;
        }
    }
    /* in a drain too, the upload with the new row is done so its message has to follow */
    if (g_answer_kind && !g_answer_wait) {
        if (g_sq_n) return;
        answer_send(b);
        return;
    }
    if (ctl && !g_draining && g_edit_job < 0 && !g_edit_upload_want && !g_comment_wanted && !g_sq_n)
        answer_request(b);
}

static int g_pic_want = -1;
static char g_pic_state[32] = "";
static char g_pic_token[48];
#define pic_fail(why) refuse(g_pic_state, sizeof g_pic_state, "pic: request refused - %s\r\n", why)
static void pic_pump(BYTE *b, int ctl)
{
    int r;
    if (g_pic_want < 0) {
        char buf[96], *p;
        int i, v = 0, d = 0;
        if (!ctl || g_draining || !db_free()) return;
        if (take_request("pic", buf, sizeof buf) < 0) return;
        g_db_last = GetTickCount64();
        for (i = 0; buf[i] >= '0' && buf[i] <= '9' && d < 5; i++, d++) v = v * 10 + (buf[i] - '0');
        p = buf + i;
        if (*p == '\r') p++;
        if (*p == '\n') p++;
        lstrcpynA(g_pic_token, p, sizeof g_pic_token);
        for (p = g_pic_token; *p; p++) if (*p == '\r' || *p == '\n') { *p = 0; break; }
        if (!d || v > 9999 || (buf[i] && buf[i] != '\r' && buf[i] != '\n')) { pic_fail("failed:bad request"); return; }
        g_pic_want = v;
        lstrcpyA(g_pic_state, "saving");
        lg("pic: save pic %d on slot %d\r\n", v, g_my_slot);
        g_snap_dirty = 1;
    }
    if (g_draining && g_fs.st != ST_LOAD_POLL && g_fs.st != ST_UPD_POLL) {
        fs_reset();
        g_pic_want = -1;
        lstrcpyA(g_pic_state, "failed:cancelled");
        return;
    }
    r = profile_pic_save(b, g_my_slot, (unsigned)g_pic_want, g_draining);
    if (r == 0) return;
    if (r == 1) lstrcpyA(g_pic_state, "saved");
    else wsprintfA(g_pic_state, "failed:%d", r);
    g_pic_want = -1;
    g_snap_dirty = 1;
}

/* my characters' tabs on the viewer's editor (app.dll 0x10228190), read only */
static int g_xi_job = -1;
static char g_xi_token[48], g_xi_state[32] = "", g_xi_json[0x200], g_xi_key[25];
static __declspec(align(8)) pdb_cond g_xi_cond[2];
static pdb_cond *g_xi_where;
static pdb_rec *g_xi_rec;
static unsigned __int64 g_xi_ctid;
static DWORD g_xi_ctsid;
static ULONGLONG g_xi_t0, g_xi_last;

static void xi_done(const char *state)
{
    lstrcpynA(g_xi_state, state, sizeof g_xi_state);
    g_xi_step = 0;
    g_xi_job = -1;
    lg("ffxi profile edit: %s\r\n", state);
    g_snap_dirty = 1;
}
static void xi_load_start(BYTE *b)
{
    int t = ((svc4b4_t)SVC(0x4b4))(g_ch_pib, g_xi_where, g_xi_rec);
    if (t == NO_SLOT) { g_xi_step = 1; return; }
    lg("ffxi profile edit: load -> %d\r\n", t);
    if (t < 0) { xi_done(failed(t)); return; }
    g_xi_job = t;
    g_xi_step = 2;
    g_xi_t0 = GetTickCount64();
}
/* only a character from the loaded profile's own list, the ids are never the app's */
static int xi_find(BYTE *b, const char *key, me_chr *out)
{
    me_chr ch[8];
    char k[25];
    const char *from;
    int n = me_chars(b, ch, &from);
    for (int i = 0; i < n; i++) {
        me_chr_key(&ch[i], k);
        if (!lstrcmpA(k, key)) { *out = ch[i]; return 1; }
    }
    return 0;
}
static void xi_request(BYTE *b)
{
    char buf[256], *p, *hk, *ck, now[17];
    me_chr c;
    pol_handle h;
    if (GetTickCount64() - g_xi_last < 3000 || !db_free()) return;
    if (take_request("xiprofile", buf, sizeof buf) < 0) return;
    g_xi_last = g_db_last = GetTickCount64();
    p = buf;
    lstrcpynA(g_xi_token, req_line(&p), sizeof g_xi_token);
    hk = req_line(&p);
    ck = req_line(&p);
    memset(&h, 0, sizeof h);
    get_handle(b, g_my_slot, &h);
    wsprintfA(now, "%03x%08x", (h.hi >> 1) & 0xfff, h.lo >> 1 | h.hi << 31);
    /* my profile has to be loaded, and for this handle, since the characters come from it */
    if (lstrcmpA(hk, now) != 0 || !g_me_rec || !g_me_rec->items ||
        ((((unsigned __int64)h.hi << 32 | h.lo) >> 1) & 0xfffffffffffULL) != g_me_hid) {
        xi_done("failed:not loaded");
        return;
    }
    if (!xi_find(b, ck, &c)) { xi_done("failed:no character"); return; }
    if (!ch_load_pib(b)) { xi_done("failed:no schema"); return; }
    if (!g_xi_rec) g_xi_rec = (pdb_rec *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, g_ch_pib->rec_size + 8);
    else memset(g_xi_rec, 0, g_ch_pib->rec_size + 8);
    if (!g_xi_rec) { xi_done("failed:alloc"); return; }
    g_xi_json[0] = 0;
    lstrcpyA(g_xi_key, ck);
    g_xi_ctsid = c.ctsid;
    g_xi_ctid = (unsigned __int64)c.hi << 32 | c.lo;
    memset(g_xi_cond, 0, sizeof g_xi_cond);
    ((svc4a0_t)SVC(0x4a0))(g_ch_pib, &g_xi_cond[0].item, "z_ctsid", &g_xi_ctsid, 1);
    ((svc4a0_t)SVC(0x4a0))(g_ch_pib, &g_xi_cond[1].item, "z_ctid", &g_xi_ctid, 1);
    if (!(g_xi_cond[0].item.flags & 1) || !(g_xi_cond[1].item.flags & 1)) { xi_done("failed:schema"); return; }
    ((svc4a8_t)SVC(0x4a8))(&g_xi_cond[0], 1);
    ((svc4a8_t)SVC(0x4a8))(&g_xi_cond[1], 1);
    g_xi_where = ((svc4ac_t)SVC(0x4ac))(2, &g_xi_cond[0], &g_xi_cond[1]);
    if (!g_xi_where) { xi_done("failed:schema"); return; }
    lg("ffxi profile edit: slot %d\r\n", c.slot);
    lstrcpyA(g_xi_state, "loading");
    g_snap_dirty = 1;
    g_xi_t0 = GetTickCount64();
    xi_load_start(b);
}
static void xi_pump(BYTE *b, int ctl)
{
    pdb_item *a, *c;
    char nm[0x100], wn[0x100];
    int r;
    switch (g_xi_step) {
    case 0:
        if (ctl && !g_draining) xi_request(b);
        return;
    case 1:
        if (g_draining) xi_done("cancelled");
        else if (GetTickCount64() - g_xi_t0 >= 120000) xi_done("failed:busy");
        else xi_load_start(b);
        return;
    case 2:
        r = ((svc4b8_t)SVC(0x4b8))(g_xi_job);
        if (r == 0) {
            if (GetTickCount64() - g_xi_t0 >= 120000 || g_draining) {
                ((svc4dc_t)SVC(0x4dc))(g_xi_job);
                g_xi_rec = NULL;   /* a cancelled read can still write, so never freed */
                xi_done(g_draining ? "cancelled" : "failed:timeout");
            }
            return;
        }
        g_xi_job = -1;
        if (r < 0) { xi_done(failed(r)); return; }
        if (!g_xi_rec->items) { xi_done("failed:no profile"); return; }
        a = pdb_get(b, g_ch_pib, "z_ctsid", g_xi_rec);
        c = pdb_get(b, g_ch_pib, "z_ctid", g_xi_rec);
        if (!a || !c || a->v.u32 != g_xi_ctsid || c->v.u64 != g_xi_ctid) { xi_done("failed:mismatch"); return; }
        ch_text(b, g_xi_rec, "z_name", nm, sizeof nm, NULL);
        ch_text(b, g_xi_rec, "z_worldname", wn, sizeof wn, NULL);
        wsprintfA(g_xi_json, "\"key\":\"%s\",\"level\":%u,\"name\":%s,\"worldName\":%s", g_xi_key, g_xi_rec->level, nm, wn);
        lg("ffxi profile edit: record level %u\r\n", g_xi_rec->level);
        xi_done("ready");
        return;
    }
}

/* handles and Content IDs, listed from memory (svc 0x2b4/0x2c4). A switch is the viewer's one ChangeMyStatus (app.dll
   0x10040c9e), a link or unlink a local 0x2dc/0x2e0 and then one PutChrList (0x1038) */
#define CHR_LINKED 0x8000
#define CHR_HANDLE(f) (((f) >> 16) & 0x3f)
enum { HOP_NONE, HOP_SWITCH, HOP_LINK, HOP_UNLINK, HOP_LINKALL };
static int g_hop, g_hjob = -1, g_hwant, g_hslot = -1, g_hidx = -1, g_hfrom = -1, g_hpos = -1, g_hreload;
static int g_hall[8], g_hall_n;
static char g_htoken[48], g_hstate[48] = "";
static int g_in_hold;
static int writes_pending(void);
static void wr_job(HANDLE h, const char *fmt, const char *token, const char *state);
static int chat_room_open(void);
static void chat_end(BYTE *b);
static void chat_join_refused(const char *tok, const char *haskey);
static void chat_send_refused(const char *tok);
static void chat_create_refused(const char *tok, const char *haskey);
static void chat_master_refused(const char *tok, const char *haskey);
static int gc_open(void);
static void gc_end(BYTE *b, const char *state, int reset);
static void gc_join_refused(const char *tok, const char *state);
static void gc_send_refused(const char *tok, const char *state);
static int gc_joined(BYTE *b);
static int handle_switching(void) { return g_hop == HOP_SWITCH; }

#define handle_fail(why) refuse(g_hstate, sizeof g_hstate, "handles: refused - %s\r\n", why)
static void handle_done(const char *state)
{
    lstrcpynA(g_hstate, state, sizeof g_hstate);
    lg("handles: %s\r\n", state);
    g_hop = HOP_NONE;
    g_hjob = -1;
    g_hwant = 0;
    g_snap_dirty = 1;
}
static void handle_switch_start(BYTE *b)
{
    int t = ((int (__cdecl *)(int, int, int, int, int, int))SVC(0x30c))(g_hslot, -2, 1, -2, 0, -2);
    g_hwant = t == NO_SLOT;
    if (g_hwant) return;
    lg("handles: ChangeMyStatus(%d,-2,1,-2,0,-2) -> ticket %d\r\n", g_hslot, t);
    if (t < 0) { handle_done(failed(t)); return; }
    g_hjob = t;
}
/* polcore links into a handle's first free slot of 8, not where it was, so it's put back by hand in the records
   (Content IDs at polcore.dll 0x10403080, handles 0x10405800) on this thread */
static void handle_place(BYTE *b, int idx, int slot, int pos)
{
    BYTE *hs = b + 0x405820 + slot * 0x28;
    DWORD *f = (DWORD *)(b + 0x403090 + idx * 0x68);
    int now = (int)((*f >> 22) & 7);
    if (pos < 0 || pos > 7 || now == pos || !(*f & CHR_LINKED) || (int)CHR_HANDLE(*f) != slot || (hs[pos] & 1) ||
        !(hs[now] & 1) || ((hs[now] >> 1) & 0x3f) != idx)
        return;
    hs[pos] = hs[now];
    hs[now] &= 0xfe;   /* what polcore's own unlink leaves (polcore.dll 0x10022d00) */
    *f = (*f & ~(7u << 22)) | (DWORD)pos << 22;
}
/* a put that didn't go puts polcore back how the server has it, which costs nothing (no reload, the viewer doesn't) */
static void handle_undo(BYTE *b)
{
    if (g_hop == HOP_LINKALL) {
        for (int i = 0; i < g_hall_n; i++) ((int (__cdecl *)(int, int))SVC(0x2e0))(g_hall[i], g_hslot);
    } else if (g_hop == HOP_LINK) {
        ((int (__cdecl *)(int, int))SVC(0x2e0))(g_hidx, g_hslot);
        if (g_hfrom >= 0 && ((int (__cdecl *)(int, int))SVC(0x2dc))(g_hidx, g_hfrom) >= 0) handle_place(b, g_hidx, g_hfrom, g_hpos);
    } else if (((int (__cdecl *)(int, int))SVC(0x2dc))(g_hidx, g_hslot) >= 0) {
        handle_place(b, g_hidx, g_hslot, g_hpos);
    }
}
/* the put only goes when polcore's stamps say the server's list isn't newer, else it quietly loads instead and
   still says 1 (polcore.dll 0x10023350) */
static void handle_put_start(BYTE *b)
{
    int reload = *(unsigned *)(b + 0x403048) > *(unsigned *)(b + 0x403054);
    int t = ((svc_v)SVC(0x1038))();
    g_hwant = t == NO_SLOT;
    if (g_hwant) return;
    lg("handles: Content ID list upload -> %d%s\r\n", t, reload ? " (the server list is newer: reload)" : "");
    g_hreload = reload;
    if (t < 0) {
        handle_undo(b);
        handle_done(failed(t));
        return;
    }
    g_hjob = t;
}
static void handle_put_done(BYTE *b, int r)
{
    BYTE c[0x68];
    DWORD f;
    int linked;
    if (r != 1) {
        handle_undo(b);
        handle_done(failed(r));
        return;
    }
    /* a put that became a load already has the server's copy in polcore */
    f = get_chr(b, g_hidx, c) ? *(DWORD *)(c + 0x10) : 0;
    linked = (f & CHR_LINKED) && (int)CHR_HANDLE(f) == g_hslot;
    for (int i = 0; g_hop == HOP_LINKALL && i < g_hall_n; i++) {
        f = get_chr(b, g_hall[i], c) ? *(DWORD *)(c + 0x10) : 0;
        if (!(f & CHR_LINKED) || (int)CHR_HANDLE(f) != g_hslot) linked = 0;
    }
    if (g_hreload) handle_done("failed:reloaded");
    else if (linked != (g_hop != HOP_UNLINK)) handle_done("failed:not saved");
    else handle_done("saved");
}
/* my profile's the handle's, so a loaded one's the old handle's now */
static void handle_switched(BYTE *b)
{
    char jp[MAX_PATH + 64];
    g_my_slot = ((svc_v)SVC(0x2f8))();
    ipc_file(jp, "myprofile.json");
    DeleteFileA(jp);
    if (!lstrcmpA(g_me_state, "ready")) g_me_state[0] = 0;
    lg("handles: active handle now slot %d\r\n", g_my_slot);
}

/* while a switch runs, a write is taken and answered as not done, it'd go from the new handle otherwise. A chat
   enter waiting when the switch is taken is called off the same way */
#define SWITCHING "failed:handle changing"
static void writes_refuse(BYTE *b)
{
    static const char *const ext[] = { "send", "friend", "comment", "answer", "group", "pic", "status", "myprofile",
                                       "saveprofile", "rename", "chatjoin", "chatsend", "chatcreate", "chatmaster",
                                       "groupchatjoin", "groupchatsend", "xiprofile" };
    char buf[512], *p, *l1, *l2, *l3;
    for (int i = 0; i < (int)(sizeof ext / sizeof ext[0]); i++) {
        if (take_request(ext[i], buf, sizeof buf) < 0) continue;
        lg("handles: %s request refused - the handle's changing\r\n", ext[i]);
        p = buf;
        l1 = req_line(&p); l2 = req_line(&p); l3 = req_line(&p);
        switch (i) {
        case 0: case 3: lstrcpynA(g_send_token, l2, sizeof g_send_token); send_fail(SWITCHING); break;
        case 1: lstrcpynA(g_edit_token, l2, sizeof g_edit_token); edit_fail(SWITCHING); break;
        case 2: lstrcpynA(g_edit_token, l1, sizeof g_edit_token); edit_fail(SWITCHING); break;
        case 4: lstrcpynA(g_grp_token, l2, sizeof g_grp_token); group_fail(SWITCHING); break;
        case 5: lstrcpynA(g_pic_token, l2, sizeof g_pic_token); pic_fail(SWITCHING); break;
        case 7: case 8: case 9: lstrcpynA(g_me_token, l1, sizeof g_me_token); me_done(SWITCHING); break;
        case 10: chat_join_refused(l1, l3); break;
        case 11: chat_send_refused(l1); break;
        case 12: chat_create_refused(l1, l2); break;
        case 13: chat_master_refused(l1, l2); break;
        case 14: gc_join_refused(l1, SWITCHING); break;
        case 15: gc_send_refused(l1, SWITCHING); break;
        case 16: lstrcpynA(g_xi_token, l1, sizeof g_xi_token); xi_done(SWITCHING); break;
        }
    }
}
/* written by the app for the handle that's active now, but not taken yet */
static int writes_asked(void)
{
    static const char *const ext[] = { "send", "friend", "comment", "answer", "group", "pic", "status" };
    for (int i = 0; i < (int)(sizeof ext / sizeof ext[0]); i++) if (request_waiting(ext[i])) return 1;
    return 0;
}

/* the viewer's link before it starts a game (app.dll 0x100235d5): unlinked ones go on the active handle, all in one
   PutChrList. The app only sends what fits, so it's all or nothing here */
static void handle_linkall(BYTE *b, int slot, BYTE *h, char *ix, char *ck, char *p)
{
    char k[17];
    BYTE c[0x68];
    int idx[8], n = 0, r;
    if (slot != ((svc_v)SVC(0x2f8))()) { handle_fail("failed:not active"); return; }
    while (ix[0]) {
        if (n == 8 || !parse_num(ix, 2, &idx[n]) || idx[n] > 63) { handle_fail("failed:bad request"); return; }
        for (int i = 0; i < n; i++) if (idx[i] == idx[n]) { handle_fail("failed:bad request"); return; }
        if (!get_chr(b, idx[n], c) || *(WORD *)(c + 2) != 1) { handle_fail("failed:list changed"); return; }
        chr_key(c, k);
        if (lstrcmpA(k, ck) != 0) { handle_fail("failed:list changed"); return; }
        if (*(DWORD *)(c + 0x10) & CHR_LINKED) { handle_fail("failed:already linked"); return; }
        n++;
        ix = req_line(&p); ck = req_line(&p);
    }
    if (!n) { handle_fail("failed:bad request"); return; }
    if (handle_used(h) + n > 8) { handle_fail("failed:handle full"); return; }
    for (int i = 0; i < n; i++) {
        r = ((int (__cdecl *)(int, int))SVC(0x2dc))(idx[i], slot);
        if (r < 0) {
            while (i-- > 0) ((int (__cdecl *)(int, int))SVC(0x2e0))(idx[i], slot);
            handle_fail(failed(r));
            return;
        }
    }
    g_hop = HOP_LINKALL;
    g_hslot = slot;
    g_hidx = idx[0];
    g_hfrom = g_hpos = -1;
    memcpy(g_hall, idx, sizeof idx);
    g_hall_n = n;
    lstrcpyA(g_hstate, "working");
    lg("handles: %d unlinked content index(es) linked to the active slot %d\r\n", n, slot);
    g_snap_dirty = 1;
    handle_put_start(b);
}

/* <op> switch|link|unlink|linkall, token, slot, handle key, then index/key pairs. The keys are the snapshot's, so a
   request never lands on a record that changed */
static void handle_request(BYTE *b)
{
    char buf[512], *p, *op, *tok, *sl, *hk, *ix, *ck, k[17];
    BYTE h[0x28], c[0x68];
    int slot, idx = -1, kind, cur, old = -1, r;
    DWORD f;
    /* a background character lookup finishes first, rather than the switch being turned away as busy */
    if (g_ch_step) return;
    if (take_request("handle", buf, sizeof buf) < 0) return;
    p = buf;
    op = req_line(&p); tok = req_line(&p); sl = req_line(&p); hk = req_line(&p); ix = req_line(&p); ck = req_line(&p);
    lstrcpynA(g_htoken, tok, sizeof g_htoken);
    kind = !lstrcmpA(op, "switch") ? HOP_SWITCH : !lstrcmpA(op, "link") ? HOP_LINK : !lstrcmpA(op, "unlink") ? HOP_UNLINK
         : !lstrcmpA(op, "linkall") ? HOP_LINKALL : 0;
    if (!kind || !tok[0] || !parse_num(sl, 2, &slot) || slot > 63 ||
        (kind != HOP_SWITCH && kind != HOP_LINKALL && (!parse_num(ix, 2, &idx) || idx > 63))) {
        handle_fail("failed:bad request");
        return;
    }
    if (!g_in_hold) { handle_fail("failed:not in hold"); return; }
    if (g_draining) { handle_fail("failed:draining"); return; }
    if (!handle_ok(b, slot, h)) { handle_fail("failed:list changed"); return; }
    handle_key(h, k);
    if (lstrcmpA(k, hk) != 0) { handle_fail("failed:list changed"); return; }
    if (kind == HOP_SWITCH) {
        cur = ((svc_v)SVC(0x2f8))();
        if (slot == cur) { handle_fail("failed:already active"); return; }
        /* they went under the old handle, some with its slot in them, and a read's answer is what the old handle
           was allowed to see */
        if (writes_pending() || writes_asked() || g_me_step || g_status_want >= 0 || g_srch_step || g_prof_step ||
            g_fp_step || g_xi_step || g_cr_db) {
            handle_fail("failed:busy");
            return;
        }
        g_hop = HOP_SWITCH;
        g_hslot = slot;
        lstrcpyA(g_hstate, "working");
        lg("handles: switch from slot %d to slot %d\r\n", cur, slot);
        g_snap_dirty = 1;
        /* the viewer quits private chat before a switch, so a room's left first, through the one PART there is */
        if (chat_room_open()) chat_end(b);
        if (gc_open() || gc_joined(b)) gc_end(b, "left:handle", 0);
        handle_switch_start(b);
        if (handle_switching()) writes_refuse(b);
        return;
    }
    if (kind == HOP_LINKALL) { handle_linkall(b, slot, h, ix, ck, p); return; }
    if (!get_chr(b, idx, c) || *(WORD *)(c + 2) != 1) { handle_fail("failed:list changed"); return; }
    chr_key(c, k);
    if (lstrcmpA(k, ck) != 0) { handle_fail("failed:list changed"); return; }
    f = *(DWORD *)(c + 0x10);
    if (kind == HOP_LINK) {
        if ((f & CHR_LINKED) && (int)CHR_HANDLE(f) == slot) { handle_fail("failed:already linked"); return; }
        if (handle_used(h) >= 8) { handle_fail("failed:handle full"); return; }
        /* polcore won't link one that's linked anywhere, so a move's an unlink first, like the viewer's. One linked to
           a slot with no handle can't be unlinked either (-0x1c04), so it's left as it is */
        if (f & CHR_LINKED) {
            BYTE oh[0x28];
            old = (int)CHR_HANDLE(f);
            if (!handle_ok(b, old, oh)) { handle_fail("failed:orphan"); return; }
            r = ((int (__cdecl *)(int, int))SVC(0x2e0))(idx, old);
            if (r < 0) { handle_fail(failed(r)); return; }
        }
        r = ((int (__cdecl *)(int, int))SVC(0x2dc))(idx, slot);
        if (r < 0) {
            if (old >= 0) ((int (__cdecl *)(int, int))SVC(0x2dc))(idx, old);
            handle_fail(failed(r));
            return;
        }
    } else {
        if (!(f & CHR_LINKED) || (int)CHR_HANDLE(f) != slot) { handle_fail("failed:not linked"); return; }
        r = ((int (__cdecl *)(int, int))SVC(0x2e0))(idx, slot);
        if (r < 0) { handle_fail(failed(r)); return; }
    }
    g_hop = kind;
    g_hslot = slot;
    g_hidx = idx;
    g_hfrom = old;
    g_hpos = (f & CHR_LINKED) ? (int)((f >> 22) & 7) : -1;
    lstrcpyA(g_hstate, "working");
    lg("handles: %s content index %d %s slot %d%s\r\n", kind == HOP_LINK ? "link" : "unlink", idx,
       kind == HOP_LINK ? "to" : "from", slot, old >= 0 ? " (moved)" : "");
    g_snap_dirty = 1;
    handle_put_start(b);
}
static void handle_pump(BYTE *b, int ctl)
{
    int r;
    if (g_hop == HOP_NONE) {
        if (ctl && !g_lean) handle_request(b);
        return;
    }
    if (g_hwant) {
        /* a switch still waiting for a slot isn't worth starting once the game or a log out is on its way */
        if (g_hop == HOP_SWITCH && g_draining) handle_done("failed:draining");
        else if (g_hop == HOP_SWITCH) handle_switch_start(b);
        else handle_put_start(b);   /* applied locally already, so it still goes in a drain */
        return;
    }
    if (g_hjob < 0) return;
    r = ((svc_i)SVC(g_hop == HOP_SWITCH ? 0x310 : 0x103c))(g_hjob);
    if (r == 0) return;
    g_hjob = -1;
    if (g_hop != HOP_SWITCH) { handle_put_done(b, r); return; }
    if (r == 1) {
        handle_switched(b);
        handle_done("saved");
    } else {
        handle_done(failed(r));
    }
}
/* Play with a change still going: FFXI's world login reads polcore's records, so a link the server never confirmed
   is taken back first */
static void handle_abandon(BYTE *b)
{
    if (g_hop == HOP_NONE) return;
    if (g_hjob >= 0) CANCEL_JOB(g_hjob);
    if (g_hop != HOP_SWITCH) handle_undo(b);
    handle_done("failed:cancelled");
}
static void handles_json(BYTE *b, HANDLE out)
{
    char line[900], nm[64], tx[200], k[17], ids[48];
    BYTE rec[0x68];
    int cur = SVC(0x2f8) ? ((svc_v)SVC(0x2f8))() : -1, first = 1, i, j, o;
    DWORD f;
    wr_job(out, ",\"handleJob\":{\"token\":%s,\"state\":\"%s\"", g_htoken, g_hstate);
    wr(out, g_hop == HOP_SWITCH ? ",\"op\":\"switch\"}" : g_hop == HOP_LINK ? ",\"op\":\"link\"}"
                                : g_hop == HOP_UNLINK ? ",\"op\":\"unlink\"}" : g_hop == HOP_LINKALL ? ",\"op\":\"linkall\"}"
                                : "}");
    wr(out, ",\"handles\":[");
    for (i = 0; i < 64; i++) {
        if (!handle_ok(b, i, rec)) continue;
        sjis_json(nm, sizeof nm, rec + 0x08, 0x0f);
        handle_key(rec, k);
        for (o = 0, j = 0; j < 8; j++)
            if (rec[0x20 + j] & 1) o += wsprintfA(ids + o, "%s%u", o ? "," : "", (rec[0x20 + j] >> 1) & 0x3f);
        ids[o] = 0;
        wsprintfA(line, "%s{\"slot\":%d,\"name\":%s,\"key\":\"%s\",\"pic\":%u,\"order\":%u,\"active\":%s,"
                        "\"contentIds\":[%s]}",
                  first ? "" : ",", i, nm, k, *(unsigned *)(rec + 0x1c), (*(unsigned *)(rec + 0x18) >> 2) & 0x3f,
                  i == cur ? "true" : "false", ids);
        wr(out, line);
        first = 0;
    }
    wr(out, "],\"contentIds\":[");
    for (i = 0, first = 1; i < 64; i++) {
        if (!get_chr(b, i, rec)) continue;
        f = *(DWORD *)(rec + 0x10);
        sjis_json(nm, sizeof nm, rec + 0x18, 0x0f);
        sjis_json(tx, sizeof tx, rec + 0x28, 0x3f);
        chr_key(rec, k);
        wsprintfA(line, "%s{\"index\":%d,\"type\":%u,\"name\":%s,\"text\":%s,\"order\":%u,\"handle\":%d,\"key\":\"%s\"}",
                  first ? "" : ",", i, *(WORD *)(rec + 2), nm, tx, f & 0xff, (f & CHR_LINKED) ? (int)CHR_HANDLE(f) : -1, k);
        wr(out, line);
        first = 0;
    }
    wr(out, "]");
}
#include "pol_chat.c"
#include "pol_groupchat.c"
static void snapshot_social(BYTE *b)
{
    static char one[900], mj[28000];
    char path[MAX_PATH + 64], tmp[MAX_PATH + 80], line[900], nm[64], cm[192], gm[112], tk[100], sj[110], sk[17];
    char gn[96], mt[96], mc[0x33 * 3 + 8], cu[0x33 * 3 + 1], u8[0x33 * 3 + 1], js[0x33 * 6 + 8], hn[64] = "\"\"", hk[17];
    WCHAR wc[0x34];
    DWORD n, ids[8];
    int i, k, ng, st, first = 1, held = 0;
    unsigned pic = 0;
    unsigned __int64 me;
    const char *name;
    grp_hdr gh;
    grp_mem m;
    if (!b) return;
    mail_recheck(b);
    ipc_file(path, "social.json");
    lstrcpyA(tmp, path); lstrcatA(tmp, ".tmp");
    HANDLE h = CreateFileA(tmp, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    wsprintfA(line, "{\"ffxiService\":\"%s\",\"notice\":%u,\"noticeDays\":%u,\"presence\":\"%s\","
                    "\"friendsLoaded\":%s,\"mail\":\"%s\",\"statusBusy\":%s,\"badPassword\":%s",
              g_ffxi_service ? "active" : "none", g_notice, g_notice_days,
              g_presence, g_friends_loaded ? "true" : "false",
              g_mail_state, (g_status_job >= 0 || g_status_want >= 0) ? "true" : "false",
              g_bad_password ? "true" : "false");
    wr(h, line);
    wr_job(h, ",\"send\":{\"token\":%s,\"state\":\"%s\"}", g_send_token, g_send_state);
    wr(h, ",\"sends\":[");
    for (i = 0; i < SEND_HIST; i++) {
        k = (g_send_hist_next + i) % SEND_HIST;
        if (!g_send_hist[k].token[0]) continue;
        json_str(tk, sizeof tk, g_send_hist[k].token);
        json_str(sj, sizeof sj, g_send_hist[k].state);
        n = (DWORD)wsprintfA(line, "%s{\"token\":%s,\"state\":%s,\"time\":%lu,\"seq\":%lu", first ? "" : ",", tk, sj,
                             g_send_hist[k].time, g_send_hist[k].seq);
        if (g_send_hist[k].from_slot >= 0)
            n += (DWORD)wsprintfA(line + n, ",\"fromSlot\":%d,\"toOther\":%s", g_send_hist[k].from_slot,
                                  polid_ok(b, g_send_hist[k].to_lo, g_send_hist[k].to_hi) &&
                                  !is_me(b, g_send_hist[k].to_lo, g_send_hist[k].to_hi) ? "true" : "false");
        lstrcpyA(line + n, "}");
        wr(h, line);
        first = 0;
    }
    wr(h, "],\"returned\":[");
    for (i = 0; i < g_returned_n; i++) {
        wsprintfA(line, "%s{\"time\":%lu,\"seq\":%lu,\"fromSlot\":%u}", i ? "," : "", g_returned[i].time,
                  g_returned[i].seq, g_returned[i].from_slot);
        wr(h, line);
    }
    /* the messages the hook has, and whether each one's PUT has started */
    wr(h, "],\"queue\":[");
    for (i = 0; i < g_sq_n; i++) {
        json_str(tk, sizeof tk, g_sq[i].token);
        wsprintfA(line, "%s{\"token\":%s,\"state\":\"%s\"}", i ? "," : "", tk, g_sq[i].job >= 0 ? "sending" : "queued");
        wr(h, line);
    }
    wr(h, "]");
    wr_job(h, ",\"edit\":{\"token\":%s,\"state\":\"%s\"}", g_edit_token, g_edit_state);
    json_str(tk, sizeof tk, g_pic_token);
    wsprintfA(line, ",\"pic\":{\"token\":%s,\"want\":%d,\"state\":\"%s\"}", tk, g_pic_want, g_pic_state);
    wr(h, line);
    search_mark(b);
    json_str(tk, sizeof tk, g_srch_token);
    wsprintfA(line, ",\"search\":{\"token\":%s,\"state\":\"%s\",\"kind\":\"%s\",\"total\":%d,\"tooMany\":%s,\"results\":[",
              tk, g_srch_state, g_srch_kinds[g_srch_kind], g_srch_total, g_srch_many ? "true" : "false");
    wr(h, line);
    for (i = 0; i < g_srch_n; i++) {
        char hd[64], wd[64];
        const char *shown = g_srch_kind == SK_CHARACTER ? g_srch[i].chname : g_srch[i].name;
        sjis_json(nm, sizeof nm, (const BYTE *)shown, 0x0f);
        sjis_json(hd, sizeof hd, (const BYTE *)g_srch[i].name, 0x0f);
        sjis_json(wd, sizeof wd, (const BYTE *)g_srch[i].world, 0x0f);
        wsprintfA(line, "%s{\"index\":%d,\"name\":%s,\"handle\":%s,\"world\":%s,\"pic\":%u,\"key\":\"%s\",\"friend\":%s,"
                  "\"me\":%s}",
                  i ? "," : "", i, nm, hd, wd, g_srch[i].pic, g_srch[i].key, g_srch[i].isfriend ? "true" : "false",
                  g_srch[i].me ? "true" : "false");
        wr(h, line);
    }
    wr(h, "]}");
    wr_job(h, ",\"me\":{\"token\":%s,\"state\":\"%s\"}", g_me_token, g_me_state);
    wr_job(h, ",\"xi\":{\"token\":%s,\"state\":\"%s\"", g_xi_token, g_xi_state);
    if (g_xi_json[0] && !lstrcmpA(g_xi_state, "ready")) { wr(h, ","); wr(h, g_xi_json); }
    wr(h, "}");
    wr_job(h, ",\"group\":{\"token\":%s,\"state\":\"%s\"}", g_grp_token, g_grp_state);
    wr_job(h, ",\"profile\":{\"token\":%s,\"state\":\"%s\"", g_prof_token, g_prof_state);
    if (g_prof_json && g_prof_json[0] && lstrcmpA(g_prof_state, "done") == 0) { wr(h, ","); wr(h, g_prof_json); }
    wr(h, "}");
    wr_job(h, ",\"character\":{\"token\":%s,\"state\":\"%s\"", g_ch_token, g_ch_state);
    wsprintfA(line, ",\"ctsid\":%u,\"ctidLow\":%u,\"ctidHigh\":%u", g_ch_ctsid, g_ch_lo, g_ch_hi);
    wr(h, line);
    if (g_ch_json[0] && lstrcmpA(g_ch_state, "done") == 0) { wr(h, ","); wr(h, g_ch_json); }
    wr(h, "}");
    wr_job(h, ",\"ffxiprofile\":{\"token\":%s,\"state\":\"%s\"", g_fp_token, g_fp_state);
    wsprintfA(line, ",\"slot\":%d,\"ctsid\":%u,\"ctidLow\":%u,\"ctidHigh\":%u", g_fp_slot, g_fp_ctsid, g_fp_lo,
              g_fp_hi);
    wr(h, line);
    if (g_fp_json[0] && lstrcmpA(g_fp_state, "done") == 0) { wr(h, ","); wr(h, g_fp_json); }
    wr(h, "}");
    chat_snapshot(b, h);
    gc_snapshot(b, h);
    if (SVC(0x648) && SVC(0x618) && SVC(0x61c)) {
        ng = ((int (__cdecl *)(DWORD *))SVC(0x648))(ids);
        wr(h, ",\"groupList\":[");
        for (int g = 0, gfirst = 1; g < ng && g < 4; g++) {
            if (!get_group(b, ids[2 * g], ids[2 * g + 1], &gh)) continue;
            sjis_json(gn, sizeof gn, (const BYTE *)gh.name, 0x14);
            wsprintfA(one, "%s{\"id\":\"%08x%08x\",\"name\":%s,\"myClass\":%u,\"myStatus\":%u,\"members\":[",
                      gfirst ? "" : ",", gh.idHi, gh.idLo, gn, (gh.flags >> 1) & 7, (gh.flags >> 10) & 7);
            wr(h, one);
            gfirst = 0;
            for (int mi = 0, mfirst = 1; mi < 64; mi++) {
                if (!get_member(b, ids[2 * g], ids[2 * g + 1], mi, &m)) continue;
                sjis_json(nm, sizeof nm, (const BYTE *)m.name, 0x0f);
                sjis_json(mt, sizeof mt, (const BYTE *)m.text, 0x17);
                m.comment[0x32] = 0;
                cu[0] = 0;
                WideCharToMultiByte(CP_UTF8, 0, m.comment, -1, cu, (int)sizeof cu - 1, NULL, NULL);
                json_str(mc, sizeof mc, cu);
                peer_key(b, m.idLo, m.idHi, (m.hHi >> 13) & 0x3f, sk);
                /* +0x20 is a handle word like a handle record's +0x00: H-ID in bits 1-44, slot in 45-50 */
                handle_key((const BYTE *)&m.hLo, hk);
                wsprintfA(one, "%s{\"index\":%d,\"name\":%s,\"key\":\"%s\",\"slot\":%u,\"hid\":\"%s\",\"mine\":%s,"
                               "\"pic\":%u,\"class\":%u,\"status\":%u,\"me\":%s,\"ignored\":%s,\"inChat\":%s,\"text\":%s,"
                               "\"comment\":%s}",
                          mfirst ? "" : ",", mi, nm, sk, (m.hHi >> 13) & 0x3f, hk, is_me(b, m.idLo, m.idHi) ? "true" : "false",
                          m.pic, (m.flags >> 18) & 7, (m.flags >> 4) & 7,
                          ((m.flags >> 21) & 1) ? "true" : "false", ((m.flags >> 1) & 1) ? "true" : "false",
                          ((m.flags >> 22) & 1) ? "true" : "false", mt, mc);
                wr(h, one);
                mfirst = 0;
            }
            wr(h, "]}");
        }
        wr(h, "]");
    }
    /* GetMyComment (svc 0x688) and GetMyStatus (0x338) are local reads, fine every 2 s */
    if (SVC(0x688)) {
        memset(wc, 0, sizeof wc);
        ((int (__cdecl *)(int, WCHAR *))SVC(0x688))(-2, wc);
        wc[0x33] = 0;
        u8[0] = 0;
        WideCharToMultiByte(CP_UTF8, 0, wc, -1, u8, (int)sizeof u8 - 1, NULL, NULL);
        json_str(js, sizeof js, u8);
        wr(h, ",\"myComment\":"); wr(h, js);
    }
    wr(h, ",\"blocked\":[");
    for (int bi = 0, bfirst = 1; bi < 100; bi++) {
        BYTE *a = b + 0xafc18 + bi * 0xb0;
        if (IsBadReadPtr(a, 0xb0)) break;
        if (!(*(unsigned *)(a + 0x98) & 1)) continue;
        sjis_json(nm, sizeof nm, a + 0xa0, 0x0f);
        friend_key(b, a, sk);
        wsprintfA(line, "%s{\"index\":%d,\"name\":%s,\"key\":\"%s\",\"allHandles\":%s}", bfirst ? "" : ",", bi, nm, sk,
                  (*(unsigned *)(a + 0x08) & 3) ? "true" : "false");
        wr(h, line);
        bfirst = 0;
    }
    wr(h, "]");
    st = SVC(0x338) ? ((svc_v)SVC(0x338))() : 4;
    name = st == 0 ? "online" : st == 1 ? "away" : st == 2 ? "busy" : "appear-offline";
    wr(h, ",\"myStatus\":\""); wr(h, name); wr(h, "\"");
    /* the current handle's record: +0x08 the name in SJIS, +0x1c the profile pic id */
    if (SVC(0x2f8) && SVC(0x2b4)) {
        int slot = ((svc_v)SVC(0x2f8))();
        BYTE rec[0x28] = {0};
        if (slot < 0) slot = g_my_slot;
        if (get_handle(b, slot, rec) == 0) {
            sjis_json(hn, sizeof hn, rec + 0x08, 0x0f);
            pic = *(unsigned *)(rec + 0x1c);
        }
    }
    wr(h, ",\"myHandle\":"); wr(h, hn);
    me = my_id(b);
    sk[0] = 0;
    if (me) peer_key(b, (unsigned)me, (unsigned)(me >> 32), (unsigned)g_my_slot, sk);
    wr(h, ",\"myKey\":\""); wr(h, sk); wr(h, "\"");
    wsprintfA(line, ",\"myPic\":%u", pic);
    wr(h, line);
    handles_json(b, h);
    wr(h, ",\"friends\":[");
    for (i = 0, first = 1; i < 200; i++) {
        BYTE *a = b + 0xb40d8 + i * 0xb0, *bn = b + 0x3fc920 + i * 0x84;
        WCHAR fw[0x33] = {0};
        char combuf[0x33 * 3 + 1] = {0};
        unsigned idLo, idHi, fa;
        DWORD cs, cl, chi;
        if (IsBadReadPtr(a, 0xb0) || IsBadReadPtr(bn, 0x84)) break;
        if (!(*(unsigned *)(a + 0x98) & 1)) continue;   /* valid bit (polcore.dll 0x10024d30) */
        idLo = *(unsigned *)(a + 0x00); idHi = *(unsigned *)(a + 0x04);
        fa = *(unsigned *)(a + 0x08);
        sjis_json(nm, sizeof nm, a + 0xa0, 0x0f);
        /* the comment's UTF-16 while the names are SJIS (learnt that from a list of one-letter comments :D) */
        memcpy(fw, bn + 0x1c, 0x32 * sizeof(WCHAR));
        WideCharToMultiByte(CP_UTF8, 0, fw, -1, combuf, (int)sizeof combuf - 1, NULL, NULL);
        json_str(cm, sizeof cm, combuf);
        friend_key(b, a, sk);
        gm[0] = 0;
        if (friend_ffxi(a, &cs, &cl, &chi))
            wsprintfA(gm, ",\"game\":{\"world\":%u,\"ctsid\":%u,\"ctidLow\":%u,\"ctidHigh\":%u}", (cs >> 16) & 0xff,
                      cs, cl, chi);
        wsprintfA(line, "%s{\"row\":%d,\"key\":\"%s\",\"name\":%s,\"comment\":%s,\"status\":\"%s\",\"online\":%s,"
                        "\"pending\":%s,\"pic\":%u,\"me\":%s%s}",
                  first ? "" : ",", i, sk, nm, cm, friend_status(fa),
                  friend_online(fa) ? "true" : "false", (fa & 0x10000000) ? "true" : "false",
                  *(unsigned *)(bn + 0x00), is_me(b, idLo, idHi) ? "true" : "false", gm);
        wr(h, line);
        first = 0;
    }
    wr(h, "],\"messages\":[");
    /* states 0-3, oldest first since slots get reused */
    for (i = mail_next(0, 0xf), first = 1; i >= 0; i = mail_next(g_mail[i].mid, 0xf)) {
        const mailrec *ml = &g_mail[i];
        int o = 0, hold = mail_held(ml) || mail_undecided(b, ml);
        held += hold;
        if (!first) mj[o++] = ',';
        o += msg_json(b, mj + o, (int)sizeof mj - o, ml->hdr, ml->mid,
                      hold ? "pending" : ml->state == 3 ? "realtime" : ml->state == 2 ? "failed"
                                                     : ml->state == 1 ? "read" : "pending",
                      -1,   /* no read bit, a server copy hasn't got one */
                      hold ? "" : ml->subj, hold ? "" : ml->text, ml->has_att && !hold ? ml->att : NULL);
        WriteFile(h, mj, (DWORD)o, &n, NULL);
        first = 0;
    }
    wr(h, "]}");
    CloseHandle(h);
    MoveFileExA(tmp, path, MOVEFILE_REPLACE_EXISTING);
    if (g_mail_fresh && !held) {
        g_mail_fresh = 0;
        lg("mail: snapshot has the new message\r\n");
    }
}
/* the server's handle, else the first in use (app.dll 0x10028a76). Provenance's last one (POL_HANDLE) goes before
   the first in use, since so far the server's never given my sessions one back */
static int login_handle(BYTE *b, const char **why)
{
    BYTE rec[0x28];
    char want[24], k[17];
    int slot = ((svc_v)SVC(0x2f8))(), i;
    DWORD n = GetEnvironmentVariableA("POL_HANDLE", want, sizeof want);
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
        lg("hold: NO POL HANDLE - signing out (exit %d)\r\n", NO_HANDLE_EXIT);
        viewer_sign_out(b);
        end_pol(NO_HANDLE_EXIT);
    }
    if (g_lean) return;
    if (GetEnvironmentVariableA("POL_STATUS", sv, sizeof sv) > 0 &&
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
    if (!g_lean && wctl && g_status_job < 0 && take_request("status", sv, sizeof sv) >= 0) {
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
        lg("hold: no signed-in session (auth state %u) - exiting, FFXI NOT started\r\n", *(unsigned *)(b + 0x99408));
        end_pol(3);
    }
    ipc_file(spath, "social.json");
    /* a login that finished after its Provenance went never gets held */
    if (owner_gone()) {
        lg("hold: Provenance ended during the login - signing out and exiting\r\n");
        hold_sign_out(b, spath);
    }
    lg("hold: POL-only session held; waiting for Play (POL_CRED_PLAY)\r\n");
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
            ipc_file(path, "ended");
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
                if (!said) lg("hold: Play refused:no-content-id\r\n");
                said = 1;
            } else {
                lg("hold: Play signalled - finishing PlayOnline writes, then booting FFXI\r\n");
                play_drain(b, spath, 15000);
                /* the game keeps going whatever happens to Provenance */
                if (g_owner) CloseHandle(g_owner);
                g_owner = NULL;
                lg("hold: booting FFXI\r\n");
                return;
            }
        }
        /* only an admin can write in Provenance's folder, so a stop file there really is Provenance asking */
        ipc_file(path, "stop");
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

#define GAME_START_EXIT 15
#define NOT_HOOKED_EXIT 17   /* no detour, so it'd only ever get as far as the viewer's own menu */

/* add-ons and game plugins already unloaded in FFXI's exit. Ashita hooks ExitProcess to unload its POL plugins and
   finish its log, so it gets a clean one, the rest just get more places to crash */
static void game_over(UINT code)
{
    if (GetModuleHandleW(L"Ashita.dll")) {
        saver_restore();
        ExitProcess(code);
    }
    end_pol(code);
}

/* 1.18.15e: the menu session's prologue (push -1; push 0x43fe5a; mov eax, fs:[0]), and the content launcher's
   GameStart call and the empty trace call after it (call eax; push L"Return from FFXI"; mov esi, eax;
   call 0x00412430) */
static const BYTE MENU_PROLOGUE[] = { 0x6a, 0xff, 0x68, 0x5a, 0xfe, 0x43, 0x00, 0x64, 0xa1, 0x00, 0x00, 0x00, 0x00 };
static const BYTE FFXI_RETURN[] = { 0xff, 0xd0, 0x68, 0xa4, 0x2b, 0x44, 0x00, 0x8b, 0xf0, 0xe8, 0x18, 0xf2, 0xff, 0xff };

static int g_game_started;
/* first thing after GameStart returns, ok or not. On a failed one pol.exe would go on to pass the FFXiEntry it's just
   released (NULL) to 0x00408620, which falls over on it :( */
static void __cdecl ffxi_returned(HRESULT hr)
{
    if (!g_game_started) return;
    if (FAILED(hr)) {
        lg("pol_hook: GameStart failed hr=%08x - ending pol.exe (exit %d)\r\n", (unsigned)hr, GAME_START_EXIT);
        game_over(GAME_START_EXIT);
    } else {
        lg("pol_hook: content exited (back from FFXI) - ending pol.exe\r\n");
        game_over(0);
    }
}
/* GameStart's HRESULT is in esi by the trace call, and the caller pops the string itself */
static __declspec(naked) void ffxi_returned_thunk(void)
{
    __asm {
        push esi
        call ffxi_returned
        add esp, 4
        ret
    }
}

static int hook_game_end(BYTE *exe)
{
    BYTE *p = exe + 0x1320a;
    DWORD old;
    if (memcmp(p, FFXI_RETURN, sizeof FFXI_RETURN) != 0) {
        lg("pol_hook: content launcher not as expected (%02x %02x %02x) - game end not hooked\r\n", p[0], p[2], p[9]);
        return 0;
    }
    if (!VirtualProtect(p + 9, 5, PAGE_EXECUTE_READWRITE, &old)) {
        lg("pol_hook: content launcher protect failed - game end not hooked\r\n");
        return 0;
    }
    *(DWORD *)(p + 10) = (DWORD)((BYTE *)ffxi_returned_thunk - (p + 14));
    VirtualProtect(p + 9, 5, old, &old);
    lg("pol_hook: content launcher's return hooked\r\n");
    return 1;
}

/* the menu session detour, thiscall (this in ecx) + 4 args, callee pops 16 bytes. Returning 1 takes pol.exe's
   dispatch (0x0041406e) down its FFXI case: 0x00413140 CoCreates FFXiEntry {989D790D}, which boots FFXiMain. */
static unsigned __stdcall menu_stub(unsigned a, unsigned b, unsigned c, unsigned d)
{
    (void)a; (void)b; (void)c; (void)d;
    prime_session();
    set_cwd_ffxi();
    /* pol.exe 0x00406a10 destroys the hidden splash on the thread that made it */
    ((void (__cdecl *)(void))((BYTE *)GetModuleHandleW(NULL) + 0x6a10))();
    pol_hold((BYTE *)polcore_module());
    lg("pol_hook: menu session -> content code 1 (FFXiEntry)\r\n");
    g_game_started = 1;   /* from here ffxi_returned ends pol.exe when the game does, so this only runs once */
    return 1;
}

static void not_hooked(void)
{
    lg("pol_hook: menu session not detoured - ending pol.exe (exit %d)\r\n", NOT_HOOKED_EXIT);
    end_pol_or_exit(NOT_HOOKED_EXIT);
}

static void install(void)
{
    HMODULE exe = GetModuleHandleW(NULL);
    DWORD old, rel;
    /* WinMain reaches CreateMutexW and OpenMutexW through pol.exe's MSLU slots, not the IAT. In a Windower or
       Ashita pol.exe they may already exist, and renaming would make later opens miss. */
    if (getenv_set("PROVENANCE_LAUNCH_ID")) {
        lg("pol_hook: attached to a third-party hook's pol.exe; mutex names left to it\r\n");
    } else {
        HMODULE k32 = GetModuleHandleW(L"kernel32.dll");
        g_pCreateMutexW = GetProcAddress(k32, "CreateMutexW");
        g_pOpenMutexW   = GetProcAddress(k32, "OpenMutexW");
        if (g_pCreateMutexW) hook_slot(exe, 0x5c7c4, 0x15674, (void *)my_CreateMutexW, "CreateMutexW");
        if (g_pOpenMutexW)   hook_slot(exe, 0x5c834, 0x15b39, (void *)my_OpenMutexW,   "OpenMutexW");
    }
    hook_slot(exe, 0x5c864, 0x15493, (void *)my_CreateDialogParamW, "CreateDialogParamW");
    /* an E9 jmp over the menu session (pol.exe 0x00413710), only in the 1.18.15e pol.exe I wrote this against.
       Other builds (1.18.00n for one) have other code here. */
    BYTE *p = (BYTE *)exe + 0x13710;
    if (memcmp(p, MENU_PROLOGUE, sizeof MENU_PROLOGUE) != 0) {
        lg("pol_hook: pol.exe is not the 1.18.15e build (menu session %02x %02x %02x) - not hooked\r\n",
           p[0], p[1], p[2]);
        not_hooked();
    }
    /* the game's end is only ever caught at the content launcher's return, so no detour without that hook */
    if (!hook_game_end((BYTE *)exe) || !VirtualProtect(p, 5, PAGE_EXECUTE_READWRITE, &old)) not_hooked();
    rel = (DWORD)((BYTE *)menu_stub - (p + 5));
    p[0] = 0xE9;
    *(DWORD *)(p + 1) = rel;
    VirtualProtect(p, 5, old, &old);
    lg("pol_hook: menu session detoured -> %08x\r\n", (unsigned)(ULONG_PTR)menu_stub);
}

/* PROVENANCE_LAUNCH_ID ends up in a file name in ipc_dir, so only what Provenance makes gets through: 1 to 32
   characters of 0-9 a-f (n = what GetEnvironmentVariableA returned) */
static int launch_id_ok(const char *id, DWORD n)
{
    if (n < 1 || n > 32) return 0;
    for (DWORD i = 0; i < n; i++)
        if (!((id[i] >= '0' && id[i] <= '9') || (id[i] >= 'a' && id[i] <= 'f'))) return 0;
    return 1;
}

BOOL WINAPI DllMain(HINSTANCE h, DWORD why, void *r)
{
    char id[64], path[MAX_PATH + 96], num[16];
    DWORD n, idn;
    HANDLE f;
    SYSTEM_INFO si;
    (void)r;
    if (why != DLL_PROCESS_ATTACH) return TRUE;
    DisableThreadLibraryCalls(h);
    saver_save();
    InitializeCriticalSection(&g_lgcs);
    g_lgcs_ready = 1;
    /* shows whether Large Address Aware took: 0xfffeffff with the bit on pol.exe, 0x7ffeffff without. Keep it the
       first line, the app reads a reused pid's log from the last one on */
    GetSystemInfo(&si);
    lg("pol_hook: highest user address %08x\r\n", (unsigned)(ULONG_PTR)si.lpMaximumApplicationAddress);
    env_check();
    owner_open();
    install();
    idn = GetEnvironmentVariableA("PROVENANCE_LAUNCH_ID", id, sizeof id);
    if (idn > 0 && !launch_id_ok(id, idn)) {
        lg("pol_hook: third-party hook launch id refused\r\n");
    } else if (idn > 0) {
        ipc_dir(path);
        wsprintfA(path + lstrlenA(path), "provenance.%s.pid", id);
        f = CreateFileA(path, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
        if (f != INVALID_HANDLE_VALUE) {
            wsprintfA(num, "%u", GetCurrentProcessId());
            WriteFile(f, num, lstrlenA(num), &n, NULL);
            CloseHandle(f);
        }
        lg("pol_hook: third-party hook launch %s (pid file written)\r\n", id);
    }
    return TRUE;
}
