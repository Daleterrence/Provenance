/* pol_hook.c - signs in from inside the real pol.exe, then its own FFXiEntry boots the game (Windower and Ashita
   never notice) */
#include <windows.h>
#include <wincred.h>
#include <string.h>
#include <shlobj.h>
#include <bcrypt.h>
#include <sddl.h>
#include <tlhelp32.h>
#include "pol_cred.h"
#pragma comment(lib, "bcrypt.lib")

static FARPROC g_pCreateMutexW, g_pOpenMutexW;
static CRITICAL_SECTION g_lgcs;
static int g_lgcs_ready;

static int getenv_set(const char *name) { return GetEnvironmentVariableA(name, NULL, 0) > 0; }

/* pol.exe switches the screen saver off at start and only puts it back in its own cleanup, which end_pol skips. The
   app's POL_SCREENSAVER is read before any pol.exe runs, by the time DllMain does one may have switched it off */
static int g_saver = -1;
static void saver_save(void)
{
    char v[4];
    BOOL on;
    if (GetEnvironmentVariableA("POL_SCREENSAVER", v, sizeof v) == 1 && (v[0] == '0' || v[0] == '1'))
        g_saver = v[0] - '0';
    else if (SystemParametersInfoW(SPI_GETSCREENSAVEACTIVE, 0, &on, 0))
        g_saver = on != 0;
}
/* other copies of this exe, so another pol.exe still in game keeps it off and the last one out puts it back */
static int others_running(void)
{
    WCHAR self[MAX_PATH], *name = self, *s;
    PROCESSENTRY32W pe;
    HANDLE snap;
    BOOL ok;
    int n = 0;
    DWORD len = GetModuleFileNameW(NULL, self, MAX_PATH);
    if (!len || len >= MAX_PATH) return 0;
    for (s = self; *s; s++)
        if (*s == L'\\' || *s == L'/') name = s + 1;
    snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return 0;
    pe.dwSize = sizeof pe;
    for (ok = Process32FirstW(snap, &pe); ok; ok = Process32NextW(snap, &pe))
        if (pe.th32ProcessID != GetCurrentProcessId() && !lstrcmpiW(pe.szExeFile, name)) n++;
    CloseHandle(snap);
    return n;
}
static void saver_restore(void)
{
    if (g_saver >= 0 && !others_running()) SystemParametersInfoW(SPI_SETSCREENSAVEACTIVE, (UINT)g_saver, NULL, 0);
}

/* not ExitProcess: Windower 4's Hook.dll fails fast (0xc0000409) in its detach code when the game never started, so
   it's TerminateProcess, the big hammer */
static void end_pol(UINT code)
{
    saver_restore();
    TerminateProcess(GetCurrentProcess(), code);
}

/* Provenance's admin-only POL_IPC_DIR, or %TEMP% when it's unset or empty (manual runs) */
static void ipc_dir(char out[MAX_PATH])
{
    DWORD n = GetEnvironmentVariableA("POL_IPC_DIR", out, MAX_PATH);
    if (n > 0) {
        DWORD a = n < MAX_PATH - 64 ? GetFileAttributesA(out) : INVALID_FILE_ATTRIBUTES;
        if (a == INVALID_FILE_ATTRIBUTES || !(a & FILE_ATTRIBUTE_DIRECTORY)) {
            /* too long or not a folder: end pol.exe so nothing lands anywhere readable. No lg() here, it'd recurse */
            end_pol(12);
            ExitProcess(12);
        }
        if (out[n - 1] != '\\' && out[n - 1] != '/') { out[n] = '\\'; out[n + 1] = 0; }
        return;
    }
    n = GetTempPathA(MAX_PATH, out);
    if (n == 0 || n >= MAX_PATH) out[0] = 0;
}

static void ipc_file(char *out, const char *ext)
{
    ipc_dir(out);
    wsprintfA(out + lstrlenA(out), "pol_hook.%u.%s", GetCurrentProcessId(), ext);
}

static void lg(const char *fmt, ...)
{
    char buf[1100], path[MAX_PATH + 64];
    SYSTEMTIME st;
    va_list ap;
    DWORD n;
    if (g_lgcs_ready) EnterCriticalSection(&g_lgcs);
    GetLocalTime(&st);
    wsprintfA(buf, "[%02u:%02u:%02u.%03u] ", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
    va_start(ap, fmt);
    wvsprintfA(buf + lstrlenA(buf), fmt, ap);
    va_end(ap);
    ipc_file(path, "log");
    HANDLE h = CreateFileA(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h != INVALID_HANDLE_VALUE) {
        WriteFile(h, buf, lstrlenA(buf), &n, NULL);
        CloseHandle(h);
    }
    if (g_lgcs_ready) LeaveCriticalSection(&g_lgcs);
}

/* WinMain's single-instance gates are mutexes. Each name gets "_X" + 16 bytes of HMAC-SHA256(per-process secret,
   name) in hex, so nothing without admin can guess it and squat it first. */
#define MUTEX_EXIT 13   /* no secret or HMAC: end pol.exe, never fall back to the stock names */
#define MUTEX_NAME_KEEP 200
#define MUTEX_SUFFIX_LEN 34
static BYTE g_mutex_secret[32];
static int g_mutex_secret_ok;
static INIT_ONCE g_mutex_secret_once = INIT_ONCE_STATIC_INIT;
/* pol.exe's first mutex call runs this, so never under the loader lock */
static BOOL CALLBACK mutex_secret_init(PINIT_ONCE once, PVOID param, PVOID *ctx)
{
    BOOLEAN (WINAPI *rtl_gen_random)(PVOID, ULONG);
    (void)once; (void)param; (void)ctx;
    g_mutex_secret_ok =
        BCryptGenRandom(NULL, g_mutex_secret, sizeof g_mutex_secret, BCRYPT_USE_SYSTEM_PREFERRED_RNG) == 0;
    if (g_mutex_secret_ok) return TRUE;
    HMODULE a = GetModuleHandleW(L"advapi32.dll");
    rtl_gen_random = a ? (BOOLEAN (WINAPI *)(PVOID, ULONG))GetProcAddress(a, "SystemFunction036") : NULL;
    if (rtl_gen_random && rtl_gen_random(g_mutex_secret, sizeof g_mutex_secret)) {
        lg("pol_hook: BCryptGenRandom failed; the mutex names use RtlGenRandom\r\n");
        g_mutex_secret_ok = 1;
    }
    return TRUE;
}
static void mutex_name(LPCWSTR name, WCHAR *out)
{
    BYTE mac[32];
    int i, n = lstrlenW(name);
    InitOnceExecuteOnce(&g_mutex_secret_once, mutex_secret_init, NULL, NULL);
    if (!g_mutex_secret_ok
        || BCryptHash(BCRYPT_HMAC_SHA256_ALG_HANDLE, g_mutex_secret, sizeof g_mutex_secret,
                      (PUCHAR)name, (ULONG)n * sizeof(WCHAR), mac, sizeof mac) != 0) {
        lg("pol_hook: no safe mutex name (%s) - ending pol.exe (exit %d)\r\n",
           g_mutex_secret_ok ? "HMAC failed" : "BCryptGenRandom and RtlGenRandom failed", MUTEX_EXIT);
        end_pol(MUTEX_EXIT);
        ExitProcess(MUTEX_EXIT);
    }
    if (n > MUTEX_NAME_KEEP) n = MUTEX_NAME_KEEP;
    CopyMemory(out, name, n * sizeof(WCHAR));
    out[n++] = L'_';
    out[n++] = L'X';
    for (i = 0; i < 16; i++) {
        out[n++] = L"0123456789abcdef"[mac[i] >> 4];
        out[n++] = L"0123456789abcdef"[mac[i] & 15];
    }
    out[n] = 0;
    SecureZeroMemory(mac, sizeof mac);
}
static HANDLE WINAPI my_CreateMutexW(void *sa, BOOL own, LPCWSTR name)
{
    WCHAR buf[MUTEX_NAME_KEEP + MUTEX_SUFFIX_LEN + 1];
    if (!name || !name[0])
        return ((HANDLE(WINAPI *)(void *, BOOL, LPCWSTR))g_pCreateMutexW)(sa, own, name);
    mutex_name(name, buf);
    return ((HANDLE(WINAPI *)(void *, BOOL, LPCWSTR))g_pCreateMutexW)(sa, own, buf);
}
static HANDLE WINAPI my_OpenMutexW(DWORD acc, BOOL inh, LPCWSTR name)
{
    WCHAR buf[MUTEX_NAME_KEEP + MUTEX_SUFFIX_LEN + 1];
    if (!name || !name[0])
        return ((HANDLE(WINAPI *)(DWORD, BOOL, LPCWSTR))g_pOpenMutexW)(acc, inh, name);
    mutex_name(name, buf);
    return ((HANDLE(WINAPI *)(DWORD, BOOL, LPCWSTR))g_pOpenMutexW)(acc, inh, buf);
}

static FARPROC user32_proc(const char *name)
{
    HMODULE u = GetModuleHandleW(L"user32.dll");
    if (!u) u = LoadLibraryW(L"user32.dll");
    return u ? GetProcAddress(u, name) : NULL;
}
/* pol.exe's splash (dialog 0x67, proc exe+0x6800), made hidden so pol.exe 0x00406a10 skips its 5s dwell. Took a
   fair few attempts to scrap it */
typedef HWND (WINAPI *cdip_t)(HINSTANCE, LPCDLGTEMPLATEW, HWND, DLGPROC, LPARAM);
static DWORD g_splash_tmpl[64];   /* DWORDs keep the DLGTEMPLATE copy aligned */
static HWND hidden_splash(HINSTANCE hInst, LPCWSTR tmpl, HWND parent, DLGPROC proc, LPARAM init)
{
    cdip_t cdip = (cdip_t)user32_proc("CreateDialogIndirectParamW");
    HRSRC rs = FindResourceW(hInst, tmpl, (LPCWSTR)RT_DIALOG);
    HGLOBAL hg = rs ? LoadResource(hInst, rs) : NULL;
    const BYTE *src = hg ? (const BYTE *)LockResource(hg) : NULL;
    DWORD n = rs ? SizeofResource(hInst, rs) : 0, *style;
    BYTE *t = (BYTE *)g_splash_tmpl;
    if (!cdip || !src || n < 18 || n > sizeof g_splash_tmpl) return NULL;
    CopyMemory(t, src, n);
    /* DLGTEMPLATE: style @0. DLGTEMPLATEEX (dlgVer 1, sig 0xFFFF): style @12 */
    style = (*(WORD *)t == 1 && *(WORD *)(t + 2) == 0xFFFF) ? (DWORD *)(t + 12) : (DWORD *)t;
    *style &= ~(DWORD)(WS_VISIBLE | DS_SETFOREGROUND);
    HWND h = cdip(hInst, (LPCDLGTEMPLATEW)t, parent, proc, init);
    if (h)
        SetWindowPos(h, NULL, -32000, -32000, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOREDRAW);
    return h;
}

static HWND (WINAPI *g_realCreateDialogParamW)(HINSTANCE, LPCWSTR, HWND, DLGPROC, LPARAM);
static HWND WINAPI my_CreateDialogParamW(HINSTANCE hInst, LPCWSTR tmpl, HWND parent, DLGPROC proc, LPARAM init)
{
    BYTE *exe = (BYTE *)GetModuleHandleW(NULL);
    if (tmpl == (LPCWSTR)(ULONG_PTR)0x67 && (void *)proc == (void *)(exe + 0x6800)) {
        HWND h = hidden_splash(hInst, tmpl, parent, proc, init);
        lg("pol_hook: startup splash created hidden hwnd=%08x%s\r\n",
           (unsigned)(ULONG_PTR)h, h ? "" : " (NULL fallback)");
        return h;   /* NULL is pol.exe's own no-splash path, never the real call (it'd flash) */
    }
    if (!g_realCreateDialogParamW)
        g_realCreateDialogParamW = (HWND (WINAPI *)(HINSTANCE, LPCWSTR, HWND, DLGPROC, LPARAM))
            user32_proc("CreateDialogParamW");
    return g_realCreateDialogParamW ? g_realCreateDialogParamW(hInst, tmpl, parent, proc, init) : NULL;
}

/* the real target comes from GetProcAddress, never *slot (the thunk would overwrite the hook) */
static int hook_slot(HMODULE exe, DWORD rva, DWORD thunk_rva, void *hook, const char *what)
{
    void **slot = (void **)((BYTE *)exe + rva);
    DWORD o;
    if (*slot != (void *)((BYTE *)exe + thunk_rva)) {
        lg("pol_hook: %s slot +%05x = %08x, expected thunk +%05x - skipped\r\n",
           what, (unsigned)rva, (unsigned)(ULONG_PTR)*slot, (unsigned)thunk_rva);
        return 0;
    }
    if (!VirtualProtect(slot, sizeof(void *), PAGE_READWRITE, &o)) {
        lg("pol_hook: %s slot protect failed\r\n", what);
        return 0;
    }
    *slot = hook;
    VirtualProtect(slot, sizeof(void *), o, &o);
    lg("pol_hook: %s slot +%05x hooked\r\n", what, (unsigned)rva);
    return 1;
}

static int patch_iat(HMODULE exe, const char *dll, const char *fn, int ord, void *hook, FARPROC *orig)
{
    IMAGE_DOS_HEADER *dh = (IMAGE_DOS_HEADER *)exe;
    IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)((BYTE *)exe + dh->e_lfanew);
    IMAGE_IMPORT_DESCRIPTOR *imp = (IMAGE_IMPORT_DESCRIPTOR *)
        ((BYTE *)exe + nt->OptionalHeader.DataDirectory[1].VirtualAddress);
    for (; imp->Name; imp++) {
        if (lstrcmpiA((char *)((BYTE *)exe + imp->Name), dll)) continue;
        IMAGE_THUNK_DATA *t = (IMAGE_THUNK_DATA *)((BYTE *)exe + imp->FirstThunk);
        IMAGE_THUNK_DATA *n = imp->OriginalFirstThunk
            ? (IMAGE_THUNK_DATA *)((BYTE *)exe + imp->OriginalFirstThunk) : t;
        for (; n->u1.AddressOfData; t++, n++) {
            int by_ord = IMAGE_SNAP_BY_ORDINAL(n->u1.Ordinal);
            IMAGE_IMPORT_BY_NAME *ibn = (IMAGE_IMPORT_BY_NAME *)((BYTE *)exe + n->u1.AddressOfData);
            if (fn && (by_ord || lstrcmpA((char *)ibn->Name, fn))) continue;
            if (!fn && (!by_ord || (int)(n->u1.Ordinal & 0xffff) != ord)) continue;
            DWORD old;
            VirtualProtect(&t->u1.Function, sizeof(void *), PAGE_EXECUTE_READWRITE, &old);
            *orig = (FARPROC)t->u1.Function;
            t->u1.Function = (ULONG_PTR)hook;
            VirtualProtect(&t->u1.Function, sizeof(void *), old, &old);
            return 1;
        }
    }
    return 0;
}

static LONG WINAPI veh_proc(EXCEPTION_POINTERS *ep)
{
    unsigned eip = (unsigned)(ULONG_PTR)ep->ExceptionRecord->ExceptionAddress;
    unsigned code = ep->ExceptionRecord->ExceptionCode;
    MEMORY_BASIC_INFORMATION mbi;
    char mod[MAX_PATH] = "?", *name = mod, *slash;
    unsigned base = 0;
    if (code != 0x406d1388 && code != 0x80000004) {   /* thread naming, single step */
        mbi.AllocationBase = 0;
        if (VirtualQuery((void *)(ULONG_PTR)eip, &mbi, sizeof mbi)) {
            base = (unsigned)(ULONG_PTR)mbi.AllocationBase;
            GetModuleFileNameA((HMODULE)mbi.AllocationBase, mod, sizeof mod);
        }
        /* just the file name, the full viewer path ate the whole buffer and hid which dll it was */
        if ((slash = strrchr(mod, '\\')) != NULL) name = slash + 1;
        lg("CRASH %08x at %08x = %s+%05x\r\n", code, eip, name, eip - base);
    }
    return EXCEPTION_CONTINUE_SEARCH;
}

/* FFXiMain imports ws2_32 itself (by ordinal bar two), so its sockets skip polcore. UINT_PTR stands in for SOCKET. */
static int (__stdcall *g_fx_connect)(UINT_PTR, const void *, int);
static int (__stdcall *g_fx_closesocket)(UINT_PTR);
static int (__stdcall *g_fx_send)(UINT_PTR, char *, int, int);
static int (__stdcall *g_fx_recv)(UINT_PTR, char *, int, int);
static int (__stdcall *g_fxi_socket)(int, int, int);
static int (__stdcall *g_fx_recvfrom)(UINT_PTR, char *, int, int, void *, int *);
static int (__stdcall *g_fx_sendto)(UINT_PTR, const char *, int, int, const void *, int);
static int (__stdcall *g_fx_evsel)(UINT_PTR, HANDLE, long);
static int (__stdcall *g_fx_enumev)(UINT_PTR, HANDLE, void *);
static int (__stdcall *g_fx_wsagle)(void);
static void (__stdcall *g_fx_wsaset)(int);
static int g_fx_iat_done;
/* WSAGetLastError is GetLastError underneath, an lg() would clobber the caller's EWOULDBLOCK (cue mystery -2068s) */
static int le_save(void) { return g_fx_wsagle ? g_fx_wsagle() : 0; }
static void le_restore(int e) { if (g_fx_wsaset) g_fx_wsaset(e); }

static int __stdcall fx_connect(UINT_PTR s, const void *name, int len)
{
    const unsigned char *a = (const unsigned char *)name;
    int r = g_fx_connect(s, name, len);
    int le = le_save();   /* straight away: polcore checks WSAGetLastError == 10035 */
    if (len >= 8 && a && (unsigned short)(a[0] | (a[1] << 8)) == 2)
        lg("wireF connect fd=%u -> %u.%u.%u.%u:%u = %d err=%u\r\n",
           (unsigned)s, a[4], a[5], a[6], a[7], (unsigned)((a[2] << 8) | a[3]), r, (unsigned)le);
    else
        lg("wireF connect fd=%u len=%d = %d err=%u\r\n", (unsigned)s, len, r, (unsigned)le);
    le_restore(le);
    return r;
}
static int __stdcall fx_send(UINT_PTR s, const char *buf, int len, int flags)
{
    int r = g_fx_send(s, (char *)buf, len, flags);
    int le = le_save();
    if (r < 0)
        lg("wireF send fd=%u = -1 err=%u\r\n", (unsigned)s, (unsigned)le);
    le_restore(le);
    return r;
}
static int __stdcall fx_recv(UINT_PTR s, char *buf, int len, int flags)
{
    int r = g_fx_recv(s, buf, len, flags);
    int le = le_save();
    if (r == 0) lg("wireF recv fd=%u = 0 (closed)\r\n", (unsigned)s);
    else if (r < 0 && le != 10035)
        lg("wireF recv fd=%u = -1 err=%u\r\n", (unsigned)s, (unsigned)le);
    le_restore(le);
    return r;
}
static int __stdcall fx_closesocket(UINT_PTR s)
{
    int r = g_fx_closesocket(s);
    int le = le_save();
    lg("wireF close fd=%u\r\n", (unsigned)s);
    le_restore(le);
    return r;
}
static int __stdcall fxi_socket(int af, int type, int proto)
{
    int r = g_fxi_socket(af, type, proto);
    int le = le_save();
    lg("wireF open af=%d type=%d proto=%d -> fd=%d\r\n", af, type, proto, r);
    le_restore(le);
    return r;
}
static int __stdcall fx_recvfrom(UINT_PTR s, char *buf, int len, int flags, void *from, int *fromlen)
{
    return g_fx_recvfrom(s, buf, len, flags, from, fromlen);
}
static int __stdcall fx_sendto(UINT_PTR s, const char *buf, int len, int flags, const void *to, int tolen)
{
    static unsigned cnt;
    const unsigned char *sa = (const unsigned char *)to;
    int r = g_fx_sendto(s, buf, len, flags, to, tolen);
    int le = le_save();
    if (++cnt <= 1) {   /* zone leg (UDP): only the first send, the launcher's "In game" line */
        if (sa && (sa[0] | (sa[1] << 8)) == 2)
            lg("wireF %s fd=%u peer=%u.%u.%u.%u:%u len=%d #%u\r\n", ">>U", (unsigned)s,
               sa[4], sa[5], sa[6], sa[7], (unsigned)((sa[2] << 8) | sa[3]), len, cnt);
        else
            lg("wireF %s fd=%u len=%d #%u\r\n", ">>U", (unsigned)s, len, cnt);
    }
    if (r < 0) lg("wireF sendto fd=%u = -1 err=%u\r\n", (unsigned)s, (unsigned)le);
    le_restore(le);
    return r;
}
static int __stdcall fx_evsel(UINT_PTR s, HANDLE ev, long mask)
{
    int r = g_fx_evsel(s, ev, mask);
    int le = le_save();
    lg("wireF eventselect fd=%u mask=%03x = %d\r\n", (unsigned)s, (unsigned)mask, r);
    le_restore(le);
    return r;
}
/* WSANETWORKEVENTS = { long lNetworkEvents; int iErrorCode[10]; }, connect is 0x10 (error 4), close 0x20 (5) */
static int __stdcall fx_enumev(UINT_PTR s, HANDLE ev, void *out)
{
    int r = g_fx_enumev(s, ev, out);
    int le = le_save();
    if (r == 0 && out) {
        long m = *(long *)out;
        int *ec = (int *)((BYTE *)out + 4);
        if (m & 0x30)
            lg("wireF events fd=%u ev=%02x connect_err=%d close_err=%d\r\n",
               (unsigned)s, (unsigned)m, (m & 0x10) ? ec[4] : 0, (m & 0x20) ? ec[5] : 0);
    }
    le_restore(le);
    return r;
}
static void patch_fx_sockets(BYTE *fx)
{
    HMODULE ws = GetModuleHandleA("ws2_32.dll");
    int n = patch_iat((HMODULE)fx, "ws2_32.dll", NULL, 4, fx_connect, (FARPROC *)&g_fx_connect);
    n += patch_iat((HMODULE)fx, "ws2_32.dll", NULL, 16, fx_recv, (FARPROC *)&g_fx_recv);
    n += patch_iat((HMODULE)fx, "ws2_32.dll", NULL, 19, fx_send, (FARPROC *)&g_fx_send);
    n += patch_iat((HMODULE)fx, "ws2_32.dll", NULL, 3, fx_closesocket, (FARPROC *)&g_fx_closesocket);
    n += patch_iat((HMODULE)fx, "ws2_32.dll", NULL, 23, fxi_socket, (FARPROC *)&g_fxi_socket);
    n += patch_iat((HMODULE)fx, "ws2_32.dll", NULL, 17, fx_recvfrom, (FARPROC *)&g_fx_recvfrom);
    n += patch_iat((HMODULE)fx, "ws2_32.dll", NULL, 20, fx_sendto, (FARPROC *)&g_fx_sendto);
    n += patch_iat((HMODULE)fx, "ws2_32.dll", "WSAEventSelect", 0, fx_evsel, (FARPROC *)&g_fx_evsel);
    n += patch_iat((HMODULE)fx, "ws2_32.dll", "WSAEnumNetworkEvents", 0, fx_enumev, (FARPROC *)&g_fx_enumev);
    if (ws) {
        g_fx_wsagle = (void *)GetProcAddress(ws, "WSAGetLastError");
        g_fx_wsaset = (void *)GetProcAddress(ws, "WSASetLastError");
    }
    lg("wireF: FFXiMain IAT patched %d/9 (gle=%d)\r\n", n, g_fx_wsagle != 0);
    g_fx_iat_done = 1;
}

/* polcore's own SHA-1 (polcore.dll 0x1002d6a0/d6d0/dac0), the exact hash the wire uses. 0x5c-byte context. */
static void pc_sha1(BYTE *b, const void *data, unsigned len, BYTE out[20])
{
    BYTE ctx[0x80];
    ((void (__cdecl *)(void *))(b + 0x2d6a0))(ctx);
    ((void (__cdecl *)(void *, const void *, unsigned))(b + 0x2d6d0))(ctx, data, len);
    ((void (__cdecl *)(void *, void *))(b + 0x2dac0))(out, ctx);
}

static int launcher_mode(void)
{
    return getenv_set("POL_CRED_ONESHOT");   /* Provenance always sets it, "OTP" at least */
}
/* In launcher mode a missing or bad name is NULL, never def (that could be another account's) */
static const WCHAR *cred_target(const char *env, const WCHAR *def, WCHAR *buf, int n, int *from_env)
{
    char a[256];
    DWORD k = GetEnvironmentVariableA(env, a, sizeof a);
    int strict = launcher_mode();
    if (from_env) *from_env = 0;
    if (k == 0) return strict && def[0] ? NULL : def;
    if (k >= sizeof a || !MultiByteToWideChar(CP_ACP, strict ? MB_ERR_INVALID_CHARS : 0, a, -1, buf, n)) {
        if (!strict) return def;
        lg("phase_c: %s target refused (too long or not convertible)\r\n", env);
        return NULL;
    }
    if (from_env) *from_env = 1;
    return buf;
}
static int launch_target(const WCHAR *t)
{
    static const WCHAR pre[] = L"Provenance:LAUNCH:";
    if (!t) return 0;
    for (int i = 0; pre[i]; i++)
        if (t[i] != pre[i]) return 0;
    return 1;
}
/* Manual runs use <ProgramData>\Provenance\ipc\cred.key, ProgramData from Windows, never the env (whoever starts
   pol.exe sets that) */
static const char *cred_keyfile(char *buf, DWORD n)
{
    typedef HRESULT (WINAPI *known_folder_fn)(REFKNOWNFOLDERID, DWORD, HANDLE, PWSTR *);
    static const WCHAR tail[] = L"\\Provenance\\ipc\\cred.key";
    static int warned;
    WCHAR w[MAX_PATH + 64];
    PWSTR pd = NULL;
    BOOL lossy = FALSE;
    int utf8 = GetACP() == CP_UTF8, ok;
    DWORD k = GetEnvironmentVariableA("POL_CRED_KEYFILE", buf, n);
    if (k > 0 && k < n) return buf;
    if (launcher_mode()) {
        if (!warned) {
            warned = 1;
            lg("phase_c: POL_CRED_KEYFILE unusable (unset, empty or too long) - no key file, no saved entry is read\r\n");
        }
        return NULL;
    }
    /* looked up, not imported, so the hook adds no DLL to pol.exe's start-up (a KnownDLL, so always the system copy) */
    HMODULE sh = LoadLibraryW(L"shell32.dll");
    known_folder_fn kf = sh ? (known_folder_fn)GetProcAddress(sh, "SHGetKnownFolderPath") : NULL;
    ok = kf && SUCCEEDED(kf(&FOLDERID_ProgramData, KF_FLAG_DEFAULT, NULL, &pd)) && pd &&
         lstrlenW(pd) + (int)(sizeof tail / sizeof tail[0]) <= (int)(sizeof w / sizeof w[0]);
    if (ok) {
        lstrcpyW(w, pd);
        lstrcatW(w, tail);
    }
    CoTaskMemFree(pd);
    if (sh) FreeLibrary(sh);
    if (!ok) return NULL;
    /* no best fit, so a folder name the code page lacks never narrows to another path */
    if (WideCharToMultiByte(CP_ACP, utf8 ? 0 : WC_NO_BEST_FIT_CHARS, w, -1, buf, (int)n,
                            NULL, utf8 ? NULL : &lossy) <= 0 || lossy) {
        if (n) buf[0] = 0;
        return NULL;
    }
    return buf;
}
/* launcher mode only takes wrapped secrets, any program of the user can write a plain entry */
static int cred_read(const WCHAR *target, char *user, int ucap, char *secret, int scap)
{
    char kf[MAX_PATH + 64];
    return pc_cred_read(target, cred_keyfile(kf, sizeof kf), launcher_mode(), user, ucap, secret, scap);
}
static const char *cred_why(int rc)
{
    static const char *const why[] = { "unknown", "not found", "not wrapped", "unwrap failed", "too long" };
    return rc >= PC_NOT_FOUND && rc <= PC_TOO_LONG ? why[rc] : why[0];
}
static void cred_consume(const WCHAR *target, int from_env, const char *kind)
{
    char kinds[64];
    DWORD k = GetEnvironmentVariableA("POL_CRED_ONESHOT", kinds, sizeof kinds);
    if (!from_env || k == 0 || k >= sizeof kinds || !strstr(kinds, kind)) return;
    if (!launch_target(target)) {
        lg("phase_c: one-shot %s target is not a launch entry - left in place\r\n", kind);
        return;
    }
    if (!CredDeleteW(target, CRED_TYPE_GENERIC, 0))
        lg("phase_c: one-shot target '%ls' delete failed err=%u\r\n", target, GetLastError());
}
/* launcher mode ends pol.exe on a failure (exit 11) so Provenance asks for the details again, manual runs carry on */
static int read_details(const char *env, const WCHAR *def, const char *kind, const char *manual,
                        const char *bad_shape, char *id, int idcap, char *pw, int pwcap)
{
    WCHAR tbuf[256];
    int from_env = 0, rc = PC_OK;
    const WCHAR *t = cred_target(env, def, tbuf, 256, &from_env);
    if (t) {
        rc = cred_read(t, id, idcap, pw, pwcap);
        if (rc != PC_NOT_FOUND) cred_consume(t, from_env, kind);
    }
    const char *tail = launcher_mode() ? "pol.exe ends" : manual;
    if (!t) lg("phase_c: no %s target - %s\r\n", kind, tail);
    else if (rc == PC_NOT_FOUND) lg("phase_c: no CredMan '%ls' creds - %s\r\n", t, tail);
    else if (rc != PC_OK) lg("phase_c: CredMan '%ls' %s - %s\r\n", t, cred_why(rc), tail);
    else if (id[0] == 0 || pw[0] == 0) {
        SecureZeroMemory(id, idcap);
        SecureZeroMemory(pw, pwcap);
        lg(bad_shape, tail);
    } else return 1;
    if (launcher_mode()) {
        lg("phase_c: SAVED DETAILS UNREADABLE (%s) - exiting, FFXI NOT started\r\n", kind);
        end_pol(11);
    }
    return 0;
}
/* account IDs in the log: first 2 chars + length only (the log stays on disk) */
static const char *mask_id(const char *id, char *out)
{
    out[0] = id && id[0] ? id[0] : '?';
    out[1] = id && id[0] && id[1] ? id[1] : '?';
    wsprintfA(out + 2, "***(%d)", id ? lstrlenA(id) : 0);
    return out;
}
/* secret = SHA1(hex_lower(SHA1(password)) || "playonline") like the viewer (app.dll 0x1019ab29), into
   polcore.dll 0x1001e4b0 (svc+0x1778) with mode 1 (no OTP) or 2 (OTP) */
static void sea_seed(BYTE *b)
{
    char seid[32] = {0}, sepw[64] = {0}, hex[41], otp[8] = {0}, m[24];
    BYTE h1[20], secret[20], material[64];
    WCHAR obuf[256];
    int mode = 1, otp_env = 0, orc;
    if (!read_details("POL_CRED_SEA", L"POL-SEA", "SEA", "SEA upload unseeded",
                      "phase_c: bad POL-SEA cred shape - %s\r\n", seid, sizeof seid, sepw, sizeof sepw))
        return;
    pc_sha1(b, sepw, lstrlenA(sepw), h1);
    for (int i = 0; i < 20; i++) wsprintfA(hex + i * 2, "%02x", h1[i]);
    lstrcpyA((char *)material, hex);
    lstrcatA((char *)material, "playonline");
    pc_sha1(b, material, 50, secret);
    SecureZeroMemory(sepw, sizeof sepw);
    SecureZeroMemory(material, sizeof material);
    SecureZeroMemory(h1, sizeof h1);
    SecureZeroMemory(hex, sizeof hex);
    /* launcher mode without an OTP target means no OTP, never a manual POL-OTP that may be another account's */
    const WCHAR *totp = cred_target("POL_CRED_OTP", L"POL-OTP", obuf, 256, &otp_env);
    orc = (!launcher_mode() || otp_env) && totp ? cred_read(totp, NULL, 0, otp, sizeof otp) : PC_NOT_FOUND;
    if (orc != PC_NOT_FOUND) cred_consume(totp, otp_env, "OTP");
    if (orc != PC_OK && orc != PC_NOT_FOUND)
        lg("phase_c: CredMan '%ls' %s - no OTP\r\n", totp, cred_why(orc));
    if (orc == PC_OK) {
        int ok = lstrlenA(otp) == 6;
        for (int k = 0; ok && k < 6; k++)
            if (otp[k] < '0' || otp[k] > '9') ok = 0;
        if (ok) mode = 2;
        else { lg("phase_c: POL-OTP malformed (need 6 digits) - ignoring\r\n"); otp[0] = 0; }
    }
    ((int (__cdecl *)(int, const char *, const void *, const void *))(b + 0x1e4b0))(
        mode, seid, secret, mode == 2 ? otp : NULL);
    SecureZeroMemory(otp, sizeof otp);
    SecureZeroMemory(secret, sizeof secret);
    lg("phase_c: SEA seeded mode %d (%s), SE ID='%s'\r\n", mode, mode == 2 ? "OTP" : "no-OTP", mask_id(seid, m));
    SecureZeroMemory(seid, sizeof seid);
}

#define SVC(off) (*(void **)(b + 0x6fbe8 + (off)))   /* polcore's service table, byte offset off */
#define PSLOT(n) SVC((n) * 4)
typedef int (__cdecl *svc_v)(void);
typedef int (__cdecl *svc_i)(int);
typedef int (__cdecl *svc_p)(void *);
typedef int (__cdecl *svc_ip)(int, void *);
#define CANCEL_JOB(j) ((svc_i)SVC(0x520))(j)
#define NO_SLOT (-0x100)   /* polcore has no free job slot, try next turn */
#define POLCON_STEP(b) ((svc_v)((b) + 0x44a50))()
#define POLCON_IDLE(b) ((svc_v)((b) + 0x45480))()

/* the unwrapped GetHandle: the wrapper's "polhandle scan" line would read as an in-game failure */
static void *g_svc_2c4, *g_svc_2b4;
#define FS_LOG lg
#define FS_GETHANDLE_FN (g_svc_2b4 ? g_svc_2b4 : SVC(0x2b4))
#include "pol_pic_save.c"
#include "pol_profile_edit.c"
static int get_handle(BYTE *b, int slot, void *rec) { return ((svc_ip)FS_GETHANDLE_FN)(slot, rec); }

/* each region as its app.dll sets polcore up. svc 0x17dc(1) swaps in the JP half of polcore's conversion table, and
   svc 0x690's language is what FFXI reads back through 0x694 as its area code */
static const struct pol_region {
    const char *name, *polcore, *hive, *maker, *strings;
    int selector, jp_table, lang;   /* lang: JP 0 always, -1 Settings\Language or 1, -2 it or the UI language */
} g_regions[3] = {
    { "JP", "polcore.dll",   "PlayOnline",   "Square",     "viewer\\data\\common", 0, 1, 0 },
    { "US", "polcore.dll",   "PlayOnlineUS", "SquareEnix", "viewer\\data\\common", 1, 0, -1 },
    { "EU", "polcoreEU.dll", "PlayOnlineEU", "SquareEnix", "EU\\EN",               1, 0, -2 },
};

#define REGION_EXIT 14
#define REGION_MISMATCH ((const struct pol_region *)0)
static const struct pol_region *g_region;

/* pol.exe's index (0 JP, 1 US, 2 EU) and Provenance's POL_REGION (n characters). Only both there and different is a
   mismatch, anything else falls back to the other one, then the loaded polcore */
static const struct pol_region *region_of(int idx, const char *passed, DWORD n)
{
    int want = n == 1 && passed[0] >= '0' && passed[0] <= '2' ? passed[0] - '0' : -1;
    if (idx >= 0 && idx <= 2) return want >= 0 && want != idx ? REGION_MISMATCH : &g_regions[idx];
    if (want >= 0) return &g_regions[want];
    return !GetModuleHandleA("polcore.dll") && GetModuleHandleA("polcoreEU.dll") ? &g_regions[2] : &g_regions[1];
}

/* pol.exe's region index (0x0045cd90), set from env.dat before it loads a polcore. A mismatch ends pol.exe before
   polcore gets touched */
static const struct pol_region *region(void)
{
    char want[4] = {0};
    DWORD n;
    int idx;
    if (g_region) return g_region;
    idx = *(int *)((BYTE *)GetModuleHandleW(NULL) + 0x5cd90);
    n = GetEnvironmentVariableA("POL_REGION", want, sizeof want);
    if (n > 1 || (n == 1 && (want[0] < '0' || want[0] > '2')))
        lg("pol_hook: POL_REGION isn't 0, 1 or 2 - going by pol.exe's own region\r\n");
    if ((g_region = region_of(idx, want, n)) == REGION_MISMATCH) {
        lg("pol_hook: region mismatch (pol.exe %d, Provenance '%s') - ending pol.exe (exit %d)\r\n", idx, want, REGION_EXIT);
        end_pol(REGION_EXIT);
        ExitProcess(REGION_EXIT);
    }
    return g_region;
}
static HMODULE polcore_module(void) { return GetModuleHandleA(region()->polcore); }
static const char *pol_hive(void) { return region()->hive; }

/* app.dll 0x102afa80's EU answer when Settings has no Language: German 4, French 3, anything else 2 */
static DWORD eu_lang(LANGID id)
{
    switch (id) {
    case 0x407: case 0x807: case 0xc07: case 0x1007: case 0x1407: return 4;
    case 0x40c: case 0x80c: case 0xc0c: case 0x100c: case 0x140c: case 0x180c: return 3;
    default: return 2;
    }
}

static DWORD viewer_lang(void)
{
    const struct pol_region *r = region();
    HKEY hk;
    char sub[96];
    DWORD fallback, lang, sz = sizeof lang;
    if (r->lang >= 0) return (DWORD)r->lang;
    fallback = r->lang == -2 ? eu_lang(GetUserDefaultLangID()) : 1;
    lang = fallback;
    wsprintfA(sub, "SOFTWARE\\%s\\%s\\PlayOnlineViewer\\Settings", r->hive, r->maker);
    if (RegOpenKeyExA(HKEY_LOCAL_MACHINE, sub, 0, KEY_QUERY_VALUE | KEY_WOW64_32KEY, &hk) == ERROR_SUCCESS) {
        if (RegQueryValueExA(hk, "Language", NULL, NULL, (LPBYTE)&lang, &sz) != ERROR_SUCCESS || lang > 8)
            lang = fallback;
        RegCloseKey(hk);
    }
    return lang;
}

/* svc 0xf14 and 0x690 in app.dll's order, with JP's 0x17dc in between and its 0x17e0 after */
static void region_init(BYTE *b, DWORD lang)
{
    const struct pol_region *r = region();
    ((int (__cdecl *)(int))PSLOT(0x3C5))(r->selector);
    if (r->jp_table) ((int (__cdecl *)(int))PSLOT(0x5F7))(1);
    ((int (__cdecl *)(int))PSLOT(0x1A4))((int)lang);
    if (r->jp_table) ((int (__cdecl *)(int))PSLOT(0x5F8))((int)lang);
    lg("viewer_init: region %s, selector %d, conversion table %d\r\n", r->name, r->selector, r->jp_table);
}

/* StringTable.bin's bits, LSB first from +0x10. -1 when it runs out */
static int pex_bits(const BYTE *d, DWORD bits, DWORD *bit, int k)
{
    int v = 0, i;
    for (i = 0; i < k; i++, (*bit)++) {
        if (*bit >= bits) return -1;
        v |= ((d[0x10 + (*bit >> 3)] >> (*bit & 7)) & 1) << i;
    }
    return v;
}

/* <folder>\<sub>\StringTable.bin unpacked: "PEX\0", the plain size at +4, then LZ77 bits over a zeroed 0x1000 window
   (0 literal, 1 a 12-bit offset and a 4-bit length + 2) */
static BYTE *string_table(const char *folder, const char *sub, DWORD *len)
{
    char path[MAX_PATH + 48];
    HANDLE f;
    DWORD size, got, n, bit = 0, bits, pos = 0x1000;
    BYTE *d = NULL, *buf = NULL;
    int t, off, cnt, ok = 0;
    wsprintfA(path, "%s\\%s\\StringTable.bin", folder, sub);
    f = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return NULL;
    size = GetFileSize(f, NULL);
    if (size >= 0x10 && size < (8u << 20) && (d = HeapAlloc(GetProcessHeap(), 0, size)) != NULL &&
        ReadFile(f, d, size, &got, NULL) && got == size && !memcmp(d, "PEX", 4) &&
        (n = *(DWORD *)(d + 4)) > 0x10 && n < (16u << 20) &&
        (buf = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, 0x1000 + n)) != NULL) {
        bits = (size - 0x10) * 8;
        ok = 1;
        while (ok && pos < 0x1000 + n) {
            if ((t = pex_bits(d, bits, &bit, 1)) < 0) ok = 0;
            else if (t == 0) {
                if ((t = pex_bits(d, bits, &bit, 8)) < 0) ok = 0;
                else buf[pos++] = (BYTE)t;
            } else if ((off = pex_bits(d, bits, &bit, 12)) < 0 || (cnt = pex_bits(d, bits, &bit, 4)) < 0) ok = 0;
            else
                for (cnt += 2; cnt > 0 && pos < 0x1000 + n; cnt--, pos++) buf[pos] = buf[pos - 0x1000 + off];
        }
    }
    CloseHandle(f);
    if (d) HeapFree(GetProcessHeap(), 0, d);
    if (!ok) {
        if (buf) HeapFree(GetProcessHeap(), 0, buf);
        return NULL;
    }
    *len = n;
    MoveMemory(buf, buf + 0x1000, n);
    return buf;
}

/* records from +0x10: u16 size, u16 id, NUL-ended UTF-16. Copied out, at most cap - 1 characters */
static int table_text(const BYTE *raw, DWORD n, int id, WCHAR *out, int cap, int drop_cr)
{
    DWORD o = 0x10, i;
    int k = 0;
    while (o + 4 <= n) {
        WORD size = *(const WORD *)(raw + o), sid = *(const WORD *)(raw + o + 2);
        if (size < 4 || o + size > n) return 0;
        if (sid == id) {
            for (i = o + 4; i + 1 < o + size && k < cap - 1; i += 2) {
                WCHAR c = (WCHAR)(raw[i] | raw[i + 1] << 8);
                if (!c) break;
                if (c != L'\r' || !drop_cr) out[k++] = c;
            }
            out[k] = 0;
            return k > 0;
        }
        o += size;
    }
    return 0;
}

typedef int (__cdecl *to_sjis)(char *, const WCHAR *, int);

/* message #i (0-9 titles, 10-19 texts) as app.dll (0x10057900) makes it: the table's string, CRs dropped from the
   texts, through conv (svc 0xa40). english falls back to the US table's bytes. Its length, or -1 */
static int message_bytes(const BYTE *raw, DWORD n, int i, int english, to_sjis conv, char out[0x100])
{
    static const WORD ids[20] = { 0x46c9, 0x46d0, 0x46d1, 0x46d2, 0x46d3, 0x46d4, 0x46d6, 0x46d8, 0x46dc, 0x46da,
                                  0x46e6, 0x46ea, 0x46ec, 0x4ab8, 0x3bad, 0x46d7, 0x46d9, 0x46dd, 0x46db, 0x46d5 };
    static const char *const en[20] = {
        "Let's be friends!", "Friend registration accepted", "Friend registration declined", "Deleted",
        "Please delete.", "Would you like to join a friend group?", "Group registration accepted",
        "Group registration declined", "Removed from friend group", "Friend group disbanded",
        "Would you like to be friends?", "%s accepted friend registration.\nWill be added to Friend List.",
        "%s declined friend registration.", "You don't have access to %s's away message.",
        "The name you entered is unavailable.\nPlease choose another name.",
        "%s has agreed to join\nthe group \"%s.\"", "%s has declined to join\nthe group \"%s.\"",
        "You have been removed from\nthe group \"%s.\"", "The group \"%s\"\nhas been disbanded.",
        "Would you like to join the group \"%s\"?" };
    WCHAR w[0x200];
    int cap = i < 10 ? 0x80 : 0x100;
    if (raw && table_text(raw, n, ids[i], w, 0x200, i >= 10) && conv(NULL, w, 0x81ac) < cap)
        return conv(out, w, 0x81ac);
    if (!english) return -1;
    lstrcpyA(out, en[i]);
    return lstrlenA(out);
}

/* SET_MESSAGE_TITLE/TEXT from the region's own StringTable like its viewer (EU's English one), so a friend gets the
   same bytes. Text #4 is the name dialog FFXI-3322 needs */
static void set_messages(BYTE *b, const char *folder)
{
    const struct pol_region *r = region();
    DWORD n = 0;
    BYTE *raw = string_table(folder, r->strings, &n);
    char s[0x100];
    int i, set = 0;
    for (i = 0; i < 20; i++) {
        /* JP keeps polcore's own Japanese for anything its table hasn't got */
        if (message_bytes(raw, n, i, !r->jp_table, (to_sjis)PSLOT(0x290), s) < 0) continue;
        if (i < 10) ((int (__cdecl *)(int, const char *))PSLOT(0x430))(i, s);
        else ((int (__cdecl *)(int, const char *))PSLOT(0x516))(i - 10, s);
        set++;
    }
    lg("viewer_init: %d message texts set, %s, FILE_INIT done\r\n", set,
       raw ? "from StringTable.bin" : r->jp_table ? "StringTable.bin unreadable, polcore's own kept" : "StringTable.bin unreadable, English");
    if (raw) HeapFree(GetProcessHeap(), 0, raw);
}

/* drops trailing backslashes (Locate existing install writes one) but never a drive's own */
static void trim_slash(char *s)
{
    int n = lstrlenA(s);
    while (n > 3 && s[n - 1] == '\\') s[--n] = 0;
}

/* the region's InstallFolder\1000 like the app reads it, into its own buffer so a failed read keeps the default */
static void viewer_folder(char folder[MAX_PATH])
{
    HKEY hk;
    char sub[48], got[MAX_PATH];
    DWORD sz = sizeof got;
    wsprintfA(sub, "SOFTWARE\\%s\\InstallFolder", pol_hive());
    if (RegOpenKeyExA(HKEY_LOCAL_MACHINE, sub, 0, KEY_QUERY_VALUE | KEY_WOW64_32KEY, &hk) != ERROR_SUCCESS) return;
    if (RegGetValueA(hk, NULL, "1000", RRF_RT_REG_SZ, NULL, got, &sz) == ERROR_SUCCESS) {
        trim_slash(got);
        if (got[0]) lstrcpyA(folder, got);
    }
    RegCloseKey(hk);
}

static char g_viewer[MAX_PATH];
static int g_home = -1, g_home_made;

static int plain_path(const char *path);
/* SET_USER_SLOT, then pub\homeNN\msg\{s/a,s/b,r/a,r/b}, folders polcore can't make itself. Never a slot that goes
   through a link, FFXI writes there as admin */
static int home_set(BYTE *b, int slot)
{
    static const char *const sub[4] = { "s\\a", "s\\b", "r\\a", "r\\b" };
    char full[MAX_PATH + 48], *p;
    int i;
    for (i = 0; i < 4; i++) {
        wsprintfA(full, "%s\\pub\\home%02d\\msg\\%s", g_viewer, slot, sub[i]);
        if (!plain_path(full)) {
            lg("home: pub\\home%02d goes through a link - not used\r\n", slot);
            return 0;
        }
    }
    ((int (__cdecl *)(int))PSLOT(0x366))(slot);
    g_home = slot;
    g_home_made = 1;
    for (i = 0; i < 4; i++) {
        wsprintfA(full, "%s\\pub\\home%02d\\msg\\%s", g_viewer, slot, sub[i]);
        for (p = full; *p; p++)
            if (*p == '\\') { *p = 0; CreateDirectoryA(full, NULL); *p = '\\'; }
        CreateDirectoryA(full, NULL);
    }
    return 1;
}

/* The viewer's init before it launches content (REGISTRY_LANG, FFXI_LANG, REGISTRY_KEY, INSTALL_FOLDER, FILE_INIT),
   which the menu detour skips. Without it, game spits out FFXI-3322 at character creation (fun bonus of the error being
   in Japanese), took me a whole night to nail it... solid work on my part. */
static void viewer_init(BYTE *b)
{
    DWORD lang = viewer_lang();
    char key[32];
    char folder[MAX_PATH] = "C:\\Program Files (x86)\\PlayOnline\\SquareEnix\\PlayOnlineViewer";
    char polid[16] = {0};
    unsigned h = 0;
    int slot, i;
    wsprintfA(key, "SOFTWARE\\%s", pol_hive());
    viewer_folder(folder);
    lg("viewer_init: lang=%u key='%s' folder='%s'\r\n", lang, key, folder);
    region_init(b, lang);
    ((int (__cdecl *)(const char *))PSLOT(0x16F))(key);
    ((int (__cdecl *)(const char *))PSLOT(0x07D))(folder);
    ((int (__cdecl *)(void))PSLOT(0x3A4))();
    lstrcpynA(g_viewer, folder, sizeof g_viewer);
    /* the placeholder ID still, so always 84. home_pick moves the account to its own after the login, then the folders
       get made */
    CopyMemory(polid, b + 0x99280, 8);
    for (i = 0; i < 8; i++) h = h * 31 + (unsigned char)polid[i];
    slot = 50 + (int)(h % 50);
    ((int (__cdecl *)(int))PSLOT(0x366))(slot);
    g_home = slot;
    lg("viewer_init: user slot %d for now\r\n", slot);
    set_messages(b, folder);
}

static void *g_polcon_poll_orig;
static int g_real_auth;
/* a negative POLCON_POLL ends the game with a POL-xxxx dialog: the fake session's noise gets masked, a real
   session's loss still gets through */
static int __cdecl poll_masked(void)
{
    static int last = 0x7fffffff;
    int r = ((svc_v)g_polcon_poll_orig)();
    if (r >= 0) return r;
    /* another login -0x40f, dropped -0x400/-0x401/-0x40e, KILL -251, revoked -207, LM refusals -221..-252 */
    if (g_real_auth && (r == -0x40f || r == -0x400 || r == -0x401 || r == -0x40e || r == -251 || r == -207 ||
                        (r <= -221 && r >= -252))) {
        lg("poll mask: POLCON_POLL %d passed to the game (session lost)\r\n", r);
        return r;
    }
    if (r != last) {
        lg("poll mask: POLCON_POLL %d -> 0 (POL-xxxx suppressed)\r\n", r);
        last = r;
    }
    return 0;
}
/* the lobby ticket legs (FFXiMain.dll 0x0431d7f0, states 0xb-0xd), only logged. FFXiMain reads the table every call. */
static void *g_svc_30c, *g_svc_310, *g_svc_ea0, *g_svc_fac;
static int __cdecl w_svc_30c(int a, int b, int c, int d, int e, int f)
{
    int r = ((int (__cdecl *)(int, int, int, int, int, int))g_svc_30c)(a, b, c, d, e, f);
    DWORD le = GetLastError();
    lg("svc 0x30c ChangeMyStatus(%d,%d,%d,%d,%d,%d) -> ticket %d\r\n", a, b, c, d, e, f, r);
    SetLastError(le);
    return r;
}
static int __cdecl w_svc_310(int t)
{
    static int last_t = -99, last_r = -99;
    int r = ((int (__cdecl *)(int))g_svc_310)(t);
    if (t != last_t || r != last_r) {
        DWORD le = GetLastError();
        lg("svc 0x310 poll(ticket %d) -> %d\r\n", t, r);
        SetLastError(le);
        last_t = t; last_r = r;
    }
    return r;
}
/* only the result, the ticket and account bytes are sign-in material */
static int log_out(void *orig, void *out, const char *what)
{
    int r = ((int (__cdecl *)(void *))orig)(out);
    DWORD le = GetLastError();
    lg("%s -> %d\r\n", what, r);
    SetLastError(le);
    return r;
}
/* 0xea0 gives the 0x34B ticket that becomes the 0x26 AuthCode, 0xfac the 16-byte 0x26 PolAccount */
static int __cdecl w_svc_ea0(void *out) { return log_out(g_svc_ea0, out, "svc 0xea0 ticket(0x34B)"); }
static int __cdecl w_svc_fac(void *out) { return log_out(g_svc_fac, out, "svc 0xfac account(16B)"); }
static int wrap_slot(BYTE *b, unsigned off, void *w, void **orig)
{
    DWORD vp;
    void **slot = &SVC(off);
    *orig = *slot;
    if (!*orig || !VirtualProtect(slot, 4, PAGE_EXECUTE_READWRITE, &vp)) return 0;
    *slot = w;
    VirtualProtect(slot, 4, vp, &vp);
    return 1;
}
/* FFXiMain's world login at 0x0431e170 wants the character linked to a handle (svc 0x2c4, flag 0x8000) or a
   handle with a free character slot (svc 0x2b4), else it's POL-0001 */
static int __cdecl w_svc_2c4(unsigned i, void *out)
{
    static int budget = 120;
    int r = ((int (__cdecl *)(unsigned, void *))g_svc_2c4)(i, out);
    DWORD le = GetLastError();
    const BYTE *e = (const BYTE *)out;
    if (r >= 0 && e && budget > 0 && IsBadReadPtr(e, 0x14) == 0 && (e[0] & 1) && budget--)
        lg("polchar[%u] type=%u flags=%08x linked=%d\r\n", i, *(const unsigned short *)(e + 2),
           *(const unsigned *)(e + 0x10), (*(const unsigned *)(e + 0x10) & 0x8000) != 0);
    SetLastError(le);
    return r;
}
static int __cdecl w_svc_2b4(unsigned i, void *out)
{
    static int valid, budget = 40;
    int r = ((int (__cdecl *)(unsigned, void *))g_svc_2b4)(i, out);
    DWORD le = GetLastError();
    const BYTE *e = (const BYTE *)out;
    if (i == 0) valid = 0;
    if (r >= 0 && e && IsBadReadPtr(e, 0x28) == 0 && (e[0] & 1)) {
        int used = 0;
        for (int k = 0; k < 8; k++) used += e[0x20 + k] & 1;
        valid++;
        if (budget > 0 && budget--)
            lg("polhandle[%u] valid, char slots used %d/8\r\n", i, used);
    }
    if (i == 63 && budget > 0 && budget--)
        lg("polhandle scan: %d valid handle(s)\r\n", valid);
    SetLastError(le);
    return r;
}

static int g_ffxi_service = -1;
static int g_friends_loaded, g_groups_loaded;
static int g_lean;
static unsigned g_notice, g_notice_days;
static int g_bad_password;
static volatile int g_snap_dirty;
static int g_my_slot = -1;
static int g_draining;
static int g_status_job = -1;
static int g_status_want = -1, g_status_loc = -2;
static char g_play_state[48] = "";
static char g_presence[32] = "starting";
static int g_answer_kind;
static int g_me_step;

static char *failed(int r)
{
    static char why[24];
    wsprintfA(why, "failed:%d", r);
    return why;
}
static void refuse(char *state, int cap, const char *fmt, const char *why)
{
    lstrcpynA(state, why, cap);
    lg(fmt, why);
    g_snap_dirty = 1;
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
    if (!SVC(0xd20)) return;
    g_notice = ((unsigned char (__cdecl *)(void))SVC(0xd20))();
    lg("phase_c: account notice status %u (LM-%02u)\r\n", g_notice, g_notice > 220 && g_notice <= 252 ? g_notice - 220 : 0);
    if (g_notice == 222 && SVC(0xd24)) {
        g_notice_days = ((unsigned char (__cdecl *)(void))SVC(0xd24))();
        lg("phase_c: LM-02 days left %u\r\n", g_notice_days);
    }
}
/* the viewer's sign-out (app.dll 0x101983fb). 0xcc4 runs once, it returns 0, not 7, for the hook's connection type */
static void viewer_sign_out(BYTE *b)
{
    if (SVC(0xcc0)) ((void (__cdecl *)(int))SVC(0xcc0))(1);
    if (SVC(0xcc4)) ((svc_v)SVC(0xcc4))();
    if (SVC(0x6c8)) ((void (__cdecl *)(void))SVC(0x6c8))();
}
static void account_blocked(BYTE *b, const char *what)
{
    lg("phase_c: ACCOUNT BLOCKED %s - signed out, nothing started\r\n", what);
    viewer_sign_out(b);
    end_pol(7);
}
static int stop_requested(void)
{
    char p[MAX_PATH + 64];
    ipc_file(p, "stop");
    return GetFileAttributesA(p) != INVALID_FILE_ATTRIBUTES;
}

/* POL_OWNER is the Provenance that started this pol.exe. Held by handle, so a reused pid can't stand in for it */
static HANDLE g_owner;
static int g_owner_gone;
static ULONGLONG g_owner_checked;
static void owner_open(void)
{
    char v[16];
    DWORD n = GetEnvironmentVariableA("POL_OWNER", v, sizeof v), pid, err;
    if (n == 0 || n >= sizeof v || (pid = (DWORD)atoi(v)) == 0) return;
    g_owner = OpenProcess(SYNCHRONIZE, FALSE, pid);
    if (g_owner) { lg("pol_hook: owned by Provenance pid %u\r\n", pid); return; }
    err = GetLastError();
    g_owner_gone = err == ERROR_INVALID_PARAMETER;   /* no such process, it went before the hook even loaded */
    lg("pol_hook: Provenance pid %u can't be watched (error %u)\r\n", pid, err);
}
static int owner_gone(void)
{
    if (g_owner && !g_owner_gone && GetTickCount64() - g_owner_checked >= 1000) {
        g_owner_checked = GetTickCount64();
        g_owner_gone = WaitForSingleObject(g_owner, 0) == WAIT_OBJECT_0;
    }
    return g_owner_gone;
}

/* presence only gets pushed once we're there with a handle, the viewer's last sign-in step is ChangeMyStatus(slot,
   location 1000). Handle -1 = signed off. */
static void status_start(BYTE *b, int handle, int status, int location)
{
    int t = ((int (__cdecl *)(int, int, int, int, int, int))SVC(0x30c))(handle, -2, 1, status, 0, location);
    if (t == NO_SLOT) { g_status_want = status; g_status_loc = location; return; }
    g_status_want = -1;
    lg("presence: ChangeMyStatus(%d,-2,1,%d,0,%d) -> ticket %d\r\n", handle, status, location, t);
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
#define LOADS_IN_FLIGHT 4   /* polcore's job slots, the viewer's own peak */
static void run_loads(BYTE *b, struct pload *items, int n)
{
    ULONGLONG started[8] = {0}, t0 = GetTickCount64();
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
        if (GetTickCount64() - t0 > 240000) {
            for (i = 0; i < n; i++)
                if (items[i].res == 0 && started[i]) CANCEL_JOB(items[i].ticket);
            break;
        }
        POLCON_STEP(b);
        POLCON_IDLE(b);
        Sleep(1);
    }
    for (i = 0; i < n; i++)
        if (items[i].res != 1)
            lg("profile: %s -> %d%s\r\n", items[i].name, items[i].res,
               items[i].res == 0 ? " (TIMEOUT)" : " (FAILED)");
}

static int count_handles(BYTE *b)
{
    BYTE rec[0x28];
    int n = 0;
    for (int i = 0; i < 64; i++)
        if (get_handle(b, i, rec) >= 0 && (rec[0] & 1)) n++;
    return n;
}
static int ascii_name_ok(const char *s)
{
    int n = 0, visible = 0;
    for (; *s; s++, n++) {
        if ((BYTE)*s < 0x20 || (BYTE)*s > 0x7e) return 0;
        if (*s != ' ') visible = 1;
    }
    return n >= 1 && n <= 15 && visible;
}
static int wait_handle_name(BYTE *b, char *name, int cap)
{
    WCHAR tbuf[256];
    char user[64], ans[256];
    int from_env = 0, warned = 0;
    const WCHAR *t = cred_target("POL_CRED_HANDLE", L"", tbuf, 256, &from_env);
    if (!t) return -2;
    if (!from_env) return 0;
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
/* a new account has no handle, so FFXI's world login dies with a POL-0001 (I'm a damn moron and forgot about the
   handle). Handles are permanent and public, so only the name the user typed gets used */
static int handle_bootstrap(BYTE *b, int handles_loaded)
{
    static const char *const why[] = { "no launcher to ask", "creation cancelled", "launcher target refused",
                                       "no answer within 10 minutes" };
    struct pload fresh = { "handles (pre-claim)", 0x2ac, 0x2b0, 0, 0 };
    struct pload put = { "handle save", 0x1030, 0x1034, 0, 0, 1 };
    struct pload re[2] = { { "handles (post-save)", 0x2ac, 0x2b0, 0, 0 },
                           { "characters (post-save)", 0x2bc, 0x2c0, 0, 0 } };
    char name[32] = {0};
    WCHAR wname[32];
    BYTE id[16];
    int n, w, idx, ng;
    if (!handles_loaded) {
        lg("phase_c: handle list did not load - skipping handle check\r\n");
        return 0;
    }
    n = count_handles(b);
    lg("phase_c: POL handles on account: %d\r\n", n);
    if (n > 0) return 0;
    w = wait_handle_name(b, name, sizeof name);
    if (w <= 0) {
        lg("phase_c: NO POL HANDLE - this account needs a PlayOnline handle before FFXI "
           "can log in (%s)\r\n", w >= -3 ? why[-w] : "unknown");
        return 4;
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
    run_loads(b, re, 2);
    n = count_handles(b);
    if (n < 1) {
        lg("phase_c: handle save reported ok but the server list is still empty\r\n");
        return 5;
    }
    lg("phase_c: POL HANDLE CREATED '%s' (slot %d), handles now %d\r\n", name, idx, n);
    return 0;
}

/* POL_PRESTART: pol.exe starts at app open with no credentials and waits here having sent nothing. Sign in writes
   the go file (allow-listed KEY=VALUE lines). */
static void prestart_wait(void)
{
    static const char *allowed[] = { "POL_CRED_POL", "POL_CRED_SEA", "POL_CRED_ONESHOT", "POL_CRED_OTP",
                                     "POL_CRED_HANDLE", "POL_CRED_PLAY", "POL_CRED_CHAT", "POL_STATUS", "POL_HOME" };
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
    void *get = g_svc_2c4 ? g_svc_2c4 : SVC(0x2c4);
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
    if (!read_details("POL_CRED_POL", L"POL-POL", "POL", "staying on fake session",
                      "phase_c: bad cred shape (id/pw empty) - %s\r\n", id, sizeof id, pw, sizeof pw))
        return;
    /* POL's own password box stops at 16, and polcore's copy of the encoded one has no room for more */
    if (lstrlenA(pw) > 16) {
        SecureZeroMemory(pw, sizeof pw);
        lg("phase_c: POL password longer than 16 characters - exiting, FFXI NOT started\r\n");
        if (launcher_mode()) end_pol(11);
        return;
    }
    lg("phase_c: REAL auth: POL ID='%s'\r\n", mask_id(id, m));
    ((svc_v)(b + 0x44390))();   /* session init (svc+0xc90) */
    /* inet init (polcore.dll 0x1000fe20 sets the flag at 0x100a66d4, the connect helper returns -2071 without it).
       One piece of the POL-2071 hunt, which was a fucking nightmare honestly good lord */
    r = ((svc_v)(b + 0xfe20))();
    lg("phase_c: inet init -> %d\r\n", r);
    if (wrap_slot(b, 0xb04, (void *)poll_masked, &g_polcon_poll_orig))
        lg("poll mask: POLCON_POLL wrapped (negative -> 0)\r\n");
    wrap_slot(b, 0x30c, (void *)w_svc_30c, &g_svc_30c);
    wrap_slot(b, 0x310, (void *)w_svc_310, &g_svc_310);
    wrap_slot(b, 0xea0, (void *)w_svc_ea0, &g_svc_ea0);
    wrap_slot(b, 0xfac, (void *)w_svc_fac, &g_svc_fac);
    wrap_slot(b, 0x2c4, (void *)w_svc_2c4, &g_svc_2c4);
    wrap_slot(b, 0x2b4, (void *)w_svc_2b4, &g_svc_2b4);
    lg("lobby: svc slots wrapped (0x30c/0x310/0xea0/0xfac/0x2c4/0x2b4, log only)\r\n");
    /* POLCON_OPEN (polcore.dll 0x100448a0) type 3 = network, kind 1 + five zeroed dial-up buffers. It overwrites
       the password and host, so both go in after it. */
    r = ((int (__cdecl *)(int, void *))(b + 0x448a0))(3, &conn);
    lg("phase_c: POLCON_OPEN(3) -> %d\r\n", r);
    ((int (__cdecl *)(void *, const char *))SVC(0xd60))(encpw, pw);   /* ENCODE_PASSWORD */
    SecureZeroMemory(pw, sizeof pw);
    /* SET_PASSWORD (polcore.dll 0x10046df0) copies the bytes into polcore, so ours get cleared */
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
    ULONGLONG tState = GetTickCount64();
    for (;;) {
        sm = *(unsigned *)(b + 0x99408);
        /* 110 s per state, 180 s for 23, the chat-server login (polcore itself allows that one 400 s) */
        if (GetTickCount64() - tState > (sm == 23 ? 180000ULL : 110000ULL)) {
            lg("auth SM: no progress in state %u - giving up\r\n", sm);
            break;
        }
        if (sm != (unsigned)last) {
            tState = GetTickCount64();
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
    g_real_auth = 1;
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
    run = g_lean ? all + 2 : all;
    nrun = g_lean ? 2 : 5;
    run_loads(b, run, nrun);
    g_friends_loaded = all[1].res == 1;
    g_groups_loaded = all[4].res == 1;
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
    /* the FFXI gate goes before the handle bootstrap, no FFXI service means no handle prompt */
    if (all[3].res == 1) ffxi_gate_read(b);
    else lg("phase_c: content ids: unknown (characters load %d)\r\n", all[3].res);
    if (!getenv_set("POL_HOLD")) {
        if (g_ffxi_service == 0) {
            lg("phase_c: NO FFXI CONTENT ID - FFXI NOT started (Viewer string 0x65d9)\r\n");
            viewer_sign_out(b);
            end_pol(6);
        }
        if (g_ffxi_service < 0) {
            lg("phase_c: FFXI SERVICE UNKNOWN - the Content ID list did not load, FFXI NOT started\r\n");
            viewer_sign_out(b);
            end_pol(9);
        }
        if (notice_blocks(g_notice)) {
            lg("phase_c: ACCOUNT NOTICE LM-%02u blocks play - FFXI NOT started\r\n", g_notice - 220);
            viewer_sign_out(b);
            end_pol(7);
        }
    }
    rc = handle_bootstrap(b, all[2].res == 1);
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
    const struct pol_region *r = region();
    HMODULE pc = polcore_module();
    static const unsigned char id64[8] = { 0xef,0xbe,0xad,0xde,0x78,0x56,0x34,0x12 };
    static const char name17[17] = "Kupo";
    if (!pc) { lg("pol_hook: polcore.dll not loaded at menu time?!\r\n"); return; }
    /* before the login, so a crash during it gets a CRASH line too. Not a VEH, that'd catch IsBadReadPtr's
       first-chance probes and recurse through lg (yes, IsBadReadPtr, I know) */
    SetUnhandledExceptionFilter(veh_proc);
    *(unsigned *)((BYTE *)pc + 0x992e0) = 0;   /* login status >= 0 = in */
    *(unsigned *)((BYTE *)pc + 0x99254) = 1;   /* ready flag (svc+0xd10) */
    CopyMemory((BYTE *)pc + 0x99280, id64, 8);
    CopyMemory((BYTE *)pc + 0x99288, name17, 17);
    /* svc+0x730's state (polcore.dll 0x1009aa58) is -1 without a real menu session, 0 suits the pre-flight */
    *(unsigned *)((BYTE *)pc + 0x9aa58) = 0;
    /* the JP/US selector (polcore.dll 0x100aa318), 0 JP, 1 anything else. svc 0xf14 sets it again in viewer_init */
    *(unsigned *)((BYTE *)pc + 0xaa318) = (unsigned)r->selector;
    lg("pol_hook: fake session poked into polcore @ %08x\r\n", (unsigned)(ULONG_PTR)pc);
    viewer_init((BYTE *)pc);
    phase_c_seed(pc);
    if (!g_home_made && home_set((BYTE *)pc, g_home))
        lg("viewer_init: user slot %d (pub\\home%02d)\r\n", g_home, g_home);
}

/* Gate 7 (FFXiMain.dll 0x04245cb0) decodes patch.ver from the current directory. Real POL boots content from the
   FFXI folder, the hook would inherit POL's and bail silently. SILENTLY BEING THE KEY WORD MY LORD */
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

static void json_str(char *out, int cap, const char *s)
{
    int o = 0;
    if (cap < 3) { if (cap > 0) out[0] = 0; return; }
    out[o++] = '"';
    for (int i = 0; s && s[i] && o < cap - 2; i++) {
        unsigned char c = (unsigned char)s[i];
        if (c == '"' || c == '\\') { if (o < cap - 3) { out[o++] = '\\'; out[o++] = (char)c; } }
        else if (c >= 0x20) out[o++] = (char)c;
        else if (o < cap - 3) { out[o++] = ' '; }
    }
    out[o++] = '"';
    out[o] = 0;
}
/* keeps newlines as \n where json_str makes them spaces, message text needs them */
static int json_text(char *out, int cap, const char *s)
{
    int o = 0;
    if (cap < 3) return 0;
    out[o++] = '"';
    for (int i = 0; s[i] && o < cap - 3; i++) {
        unsigned char c = (unsigned char)s[i];
        if (c == '"' || c == '\\') { out[o++] = '\\'; out[o++] = (char)c; }
        else if (c == '\n') { out[o++] = '\\'; out[o++] = 'n'; }
        else if (c == '\r') ;
        else if (c < 0x20) out[o++] = ' ';
        else out[o++] = (char)c;
    }
    out[o++] = '"';
    out[o] = 0;
    return o;
}
/* access denied counts as alive, so the sweep never deletes a live session's snapshot */
static int pid_alive(DWORD pid)
{
    if (pid == 0) return 0;
    HANDLE p = OpenProcess(SYNCHRONIZE, FALSE, pid);
    if (!p) return GetLastError() == ERROR_ACCESS_DENIED;
    DWORD w = WaitForSingleObject(p, 0);
    CloseHandle(p);
    return w == WAIT_TIMEOUT;
}
/* Deletes social snapshots a killed session left behind (other people's data). Another live pol.exe's stays, and
   one with our pid is from an earlier process, so it goes. */
static void sweep_stale(const char *pattern);
static void sweep_stale_social(void)
{
    sweep_stale("pol_hook.*.social.json*");   /* the end * catches crash-left .tmp files */
    sweep_stale("pol_hook.*.store.json*");
    sweep_stale("pol_hook.*.chatroom.json*");
    sweep_stale("pol_hook.*.chatjoin*");
}
static void sweep_stale(const char *pattern)
{
    char dir[MAX_PATH + 32], glob[MAX_PATH + 48], full[MAX_PATH + 64];
    WIN32_FIND_DATAA fd;
    ipc_dir(dir);
    if (!dir[0]) return;
    lstrcpyA(glob, dir); lstrcatA(glob, pattern);
    HANDLE h = FindFirstFileA(glob, &fd);
    if (h == INVALID_HANDLE_VALUE) return;
    do {
        const char *q = lstrlenA(fd.cFileName) > 9 ? fd.cFileName + 9 : "";
        ULONGLONG v = 0;
        int nd = 0;
        for (; *q >= '0' && *q <= '9' && nd < 11; q++, nd++) v = v * 10 + (ULONGLONG)(*q - '0');
        if (nd > 0 && *q == '.' && v <= 0xFFFFFFFFull && (DWORD)v != GetCurrentProcessId() && pid_alive((DWORD)v))
            continue;
        if (lstrlenA(dir) + lstrlenA(fd.cFileName) < (int)sizeof full) {
            lstrcpyA(full, dir); lstrcatA(full, fd.cFileName);
            DeleteFileA(full);
        }
    } while (FindNextFileA(h, &fd));
    FindClose(h);
}
static void sjis_json(char *out, int cap, const BYTE *src, int srcMax)
{
    WCHAR w[64];
    char u[192], tmp[64];
    int i, n;
    for (i = 0; i < srcMax && i < (int)sizeof tmp - 1 && src[i]; i++) tmp[i] = (char)src[i];
    tmp[i] = 0;
    n = MultiByteToWideChar(932, 0, tmp, -1, w, 64);
    if (n <= 0) { out[0] = '"'; out[1] = '"'; out[2] = 0; return; }
    WideCharToMultiByte(CP_UTF8, 0, w, -1, u, sizeof u - 1, NULL, NULL);
    json_str(out, cap, u);
}
static void sjis_to_utf8(const char *src, int srcLen, char *out, int cap)
{
    out[0] = 0;
    if (srcLen <= 0) return;
    WCHAR *w = (WCHAR *)HeapAlloc(GetProcessHeap(), 0, (srcLen + 1) * sizeof(WCHAR));
    if (!w) return;
    int n = MultiByteToWideChar(932, 0, src, srcLen, w, srcLen);
    if (n > 0) {
        int u = WideCharToMultiByte(CP_UTF8, 0, w, n, out, cap - 1, NULL, NULL);
        out[u > 0 ? u : 0] = 0;
    }
    HeapFree(GetProcessHeap(), 0, w);
}
static int utf8_to_sjis(const char *src, char *out, int cap)
{
    int n = MultiByteToWideChar(CP_UTF8, 0, src, -1, NULL, 0), k, m = 0;
    out[0] = 0;
    if (n <= 1) return 0;
    WCHAR *w = (WCHAR *)HeapAlloc(GetProcessHeap(), 0, n * sizeof(WCHAR));
    if (!w) return 0;
    MultiByteToWideChar(CP_UTF8, 0, src, -1, w, n);
    k = n - 1;
    if (k > cap) k = cap;
    while (k > 0 && WideCharToMultiByte(932, 0, w, k, NULL, 0, NULL, NULL) > cap - 1) k--;
    if (k > 0) m = WideCharToMultiByte(932, 0, w, k, out, cap - 1, NULL, NULL);
    out[m > 0 ? m : 0] = 0;
    HeapFree(GetProcessHeap(), 0, w);
    return m > 0 ? m : 0;
}

/* Friend rows live in two polcore arrays (the ignore list is 100 rows of A's shape just before it, b+0xafc18):
     A  b+0xb40d8, stride 0xb0    +0x08 flags (bits 13-15 status, 0x800 online, 0x10000 a game slot's in use,
                                        bits 17-19 which slot, 0x10000000 pending request)
                                  +0x18 eight 16-byte game slots, +0x98 valid, +0xa0 handle name (SJIS)
     B  b+0x3fc920, stride 0x84   +0x00 profile pic, +0x1c comment (UTF-16)
   (flags >> 13) & 7 is status + 1 (1 online, 2 away, 3 busy), 0 offline. Online also needs 0x800. */
static int friend_online(unsigned fa)
{
    unsigned s = (fa >> 13) & 7;
    return s >= 1 && s <= 3 && ((fa & 0x800) || s != 1);
}
static const char *friend_status(unsigned fa)
{
    if (!friend_online(fa)) return "offline";
    switch ((fa >> 13) & 7) {
        case 2: return "away";
        case 3: return "busy";
        default: return "online";
    }
}
/* a game slot: u16 flags (bit 0 valid), u16 title (1 = FFXI), u32 ctsid, u64 ctid. FFXI's world id is the ctsid's
   byte 2, FFXiMain checks it against your own world the same way */
static int friend_ffxi(const BYTE *a, DWORD *ctsid, DWORD *ctLo, DWORD *ctHi)
{
    unsigned fa = *(const unsigned *)(a + 0x08);
    const BYTE *s = a + 0x18 + ((fa >> 17) & 7) * 0x10;
    if (!(fa & 0x800) || !(fa & 0x10000) || (fa & 0x10000000) || !friend_online(fa)) return 0;
    if (!(*(const WORD *)s & 1) || *(const WORD *)(s + 2) != 1) return 0;
    *ctsid = *(const DWORD *)(s + 4);
    *ctLo = *(const DWORD *)(s + 8);
    *ctHi = *(const DWORD *)(s + 0xc);
    return 1;
}

static void request_paths(const char *ext, char *path, char *taken)
{
    ipc_file(path, ext);
    lstrcpyA(taken, path);
    lstrcatA(taken, ".taking");
}
static BOOL request_waiting(const char *ext)
{
    char path[MAX_PATH + 64], taken[MAX_PATH + 80];
    request_paths(ext, path, taken);
    return GetFileAttributesA(path) != INVALID_FILE_ATTRIBUTES || GetFileAttributesA(taken) != INVALID_FILE_ATTRIBUTES;
}
static BOOL request_new(const char *ext)
{
    char path[MAX_PATH + 64], taken[MAX_PATH + 80];
    request_paths(ext, path, taken);
    return GetFileAttributesA(path) != INVALID_FILE_ATTRIBUTES;
}
/* delete-on-close, so it's only acted on once the delete's certain. -1 = not now, it stays .taking, never runs twice */
static int read_taken(const char *taken, char *buf, int cap, char **grown, DWORD most)
{
    FILE_DISPOSITION_INFO del = { TRUE };
    LARGE_INTEGER size;
    DWORD got = 0, want = cap > 0 ? (DWORD)(cap - 1) : 0;
    int cut = 0;
    BOOL ok;
    HANDLE f = CreateFileA(taken, GENERIC_READ | DELETE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           NULL, OPEN_EXISTING, 0, NULL);
    if (f == INVALID_HANDLE_VALUE) return -1;
    if (grown) {
        if (!GetFileSizeEx(f, &size)) { CloseHandle(f); return -1; }
        cut = size.QuadPart > (LONGLONG)most;
        want = cut ? most : (DWORD)size.QuadPart;
        if (!(buf = *grown = (char *)HeapAlloc(GetProcessHeap(), 0, want + 1))) { CloseHandle(f); return -1; }
    }
    ok = ReadFile(f, buf, want, &got, NULL) && SetFileInformationByHandle(f, FileDispositionInfo, &del, sizeof del);
    CloseHandle(f);
    if (!ok) {
        if (grown) { HeapFree(GetProcessHeap(), 0, *grown); *grown = NULL; }
        return -1;
    }
    if (cut) while (got > 0 && buf[got - 1] != '\n') got--;
    buf[got] = 0;
    return (int)got;
}
static ULONGLONG file_age_ms(const char *p)
{
    WIN32_FILE_ATTRIBUTE_DATA a;
    ULARGE_INTEGER t, n;
    if (!GetFileAttributesExA(p, GetFileExInfoStandard, &a)) return 0;
    GetSystemTimeAsFileTime((FILETIME *)&n);
    t.LowPart = a.ftLastWriteTime.dwLowDateTime;
    t.HighPart = a.ftLastWriteTime.dwHighDateTime;
    return n.QuadPart > t.QuadPart ? (n.QuadPart - t.QuadPart) / 10000 : 0;
}
/* past a .taking that can't be read, the next request's renamed to .taking2 first like any take, so the app
   withdrawing it and this read can't both happen. One left there is tried again, then dropped after 10 s */
static int take_second(const char *ext, const char *path, const char *second, char *buf, int cap, char **grown,
                       DWORD most)
{
    int n;
    if (GetFileAttributesA(second) == INVALID_FILE_ATTRIBUTES && !MoveFileExA(path, second, MOVEFILE_REPLACE_EXISTING))
        return -1;
    n = read_taken(second, buf, cap, grown, most);
    if (n < 0 && file_age_ms(second) >= 10000 && DeleteFileA(second))
        lg("request: %s.taking2 could not be read for 10 s - dropped\r\n", ext);
    return n;
}
static int take_request_ex(const char *ext, char *buf, int cap, char **grown, DWORD most)
{
    char path[MAX_PATH + 64], taken[MAX_PATH + 80], second[MAX_PATH + 80];
    int n;
    if (grown) *grown = NULL;
    request_paths(ext, path, taken);
    lstrcpyA(second, taken);
    lstrcatA(second, "2");
    /* a .taking left by a failed read goes first, so requests keep their order (Away, then back) */
    if (GetFileAttributesA(taken) != INVALID_FILE_ATTRIBUTES) {
        static ULONGLONG lastSaid;
        n = read_taken(taken, buf, cap, grown, most);
        if (n < 0 && ((GetFileAttributesA(path) == INVALID_FILE_ATTRIBUTES &&
                       GetFileAttributesA(second) == INVALID_FILE_ATTRIBUTES) || file_age_ms(taken) < 10000))
            return -1;
        if (n < 0) {
            if (GetTickCount64() - lastSaid > 60000) {
                lastSaid = GetTickCount64();
                lg("request: %s.taking could not be read for 10 s - newer requests are read in place\r\n", ext);
            }
            n = take_second(ext, path, second, buf, cap, grown, most);
        }
    } else if (GetFileAttributesA(second) != INVALID_FILE_ATTRIBUTES) {
        n = take_second(ext, path, second, buf, cap, grown, most);
    } else {
        if (GetFileAttributesA(path) == INVALID_FILE_ATTRIBUTES) return -1;
        /* Rename first, so anything written after this is a new file for next turn, never one deleted unread. A file a
           scanner still holds can't be renamed, so it waits.
           i am vry gud with computer and this was easy to find, honest! :X */
        if (!MoveFileExA(path, taken, MOVEFILE_REPLACE_EXISTING)) return -1;
        n = read_taken(taken, buf, cap, grown, most);
    }
    if (n > 0) return n;
    if (grown && *grown) { HeapFree(GetProcessHeap(), 0, *grown); *grown = NULL; }
    return -1;
}
static int take_request(const char *ext, char *buf, int cap) { return take_request_ex(ext, buf, cap, NULL, 0); }
static char *req_line(char **p)
{
    char *s = *p, *e = s;
    while (*e && *e != '\n') e++;
    if (*e) { *e = 0; *p = e + 1; } else *p = e;
    if (e > s && e[-1] == '\r') e[-1] = 0;
    return s;
}
static int parse_num(const char *x, int maxDigits, int *out)
{
    int v = 0, d = 0;
    while (*x >= '0' && *x <= '9' && d < maxDigits) { v = v * 10 + (*x++ - '0'); d++; }
    *out = v;
    return d > 0 && !*x;
}

/* Messages, list and reads on two lanes, delete on a third, polled once a turn:
     list  svc 0x524(out, max), poll 0x528 (1 = done) into a zeroed 8-aligned array, stride 0x50, valid entries
           have W(+0x3e) & 0x8000
     read  svc 0x518, poll 0x51c on a copy of the entry (first 0x48 bytes = the server key), body at +0x4c
     push  svc 0xd9c registers evt(0, H) for a stored message, raw(H) for a real-time one (body at H+0x4c) */
#define MAIL_MAX 64
#define MAIL_SEEN 256
typedef struct {
    __declspec(align(8)) BYTE hdr[0x50];   /* 8-aligned: 0x518 rejects anything else */
    int state;                             /* 0 listed, 1 read, 2 failed, 3 real-time, 4 saved, 5 deleted, 6 skip */
    int mid;
    char subj[400];
    char text[13000];
    BYTE att[0x20];
    int has_att;
} mailrec;
static mailrec g_mail[MAIL_MAX];
static int g_mail_n, g_mail_mid;
/* identities of messages whose slot got reused, so a re-listed one isn't fetched again */
static BYTE g_mail_seen[MAIL_SEEN][0x1a];
static int g_mail_seen_n, g_mail_seen_next;
static __declspec(align(8)) BYTE g_inbox[0x50 * MAIL_MAX];
/* two reads at once like the viewer's fetch queue, kind 1 list (one at a time), 2 read. 1 goes back to one lane */
#ifndef MAIL_LANES
#define MAIL_LANES 2
#endif
#if MAIL_LANES != 1 && MAIL_LANES != 2
#error MAIL_LANES is 1 or 2
#endif
static struct { int job, kind, idx; char *body; } g_rd[MAIL_LANES] = {
    { -1 },
#if MAIL_LANES == 2
    { -1 },
#endif
};
static int g_del_job = -1, g_del_idx;
static int g_mail_fresh;
static char g_mail_state[16] = "pending";
static ULONGLONG g_mail_retry_at;
static volatile int g_mail_refresh = 1;
static volatile int g_mail_raw_new;
static int g_mail_fails;
static BYTE g_push_q[8][0x48];
static volatile LONG g_push_n;

/* Body "subject 0x07 text 0x00 [attachment]". Group invitations and their answers add a type-10 {u64 group id,
   char[0x14] name} (svc 0x440 appends it raw). */
static void mail_parse(mailrec *m, const char *body, int len)
{
    int i, bel = -1, end;
    for (i = 0; i < len; i++) if (body[i] == 0x07) { bel = i; break; }
    if (bel < 0) m->subj[0] = 0;
    else sjis_to_utf8(body, bel > 0x80 ? 0x80 : bel, m->subj, sizeof m->subj);
    for (end = bel + 1; end < len && body[end]; end++) ;
    sjis_to_utf8(body + bel + 1, end - bel - 1, m->text, sizeof m->text);
    if (bel >= 0 && (*(WORD *)(m->hdr + 0x3e) & 0x7f) == 10 && len - (end + 1) >= 0x20) {
        memcpy(m->att, body + end + 1, 0x20);
        m->has_att = 1;
    }
}
/* the viewer's message identity (app.dll 0x1005245d): ids, seq, time and slots. Other bytes can differ in a pushed
   copy. */
static int mail_same(const BYTE *a, const BYTE *c)
{
    return memcmp(a, c, 0x10) == 0 && memcmp(a + 0x30, c + 0x30, 8) == 0 && a[0x3c] == c[0x3c] && a[0x3d] == c[0x3d];
}
static void mail_ident(const BYTE *h, BYTE out[0x1a])
{
    memcpy(out, h, 0x10);
    memcpy(out + 0x10, h + 0x30, 8);
    out[0x18] = h[0x3c];
    out[0x19] = h[0x3d];
}
/* the slot with the lowest mid above after and a state in mask (a bit per state), or -1. Mids only go up, so it's
   arrival order */
static int mail_next(int after, unsigned mask)
{
    int best = -1;
    for (int i = 0; i < g_mail_n; i++)
        if (g_mail[i].mid > after && ((mask >> g_mail[i].state) & 1) && (best < 0 || g_mail[i].mid < g_mail[best].mid))
            best = i;
    return best;
}
static mailrec *mail_by_mid(int mid)
{
    for (int i = 0; i < g_mail_n; i++) if (g_mail[i].mid == mid) return &g_mail[i];
    return NULL;
}
static ULONGLONG mail_order(const BYTE *h)
{
    return (ULONGLONG)*(const DWORD *)(h + 0x34) << 32 | *(const DWORD *)(h + 0x30);
}
/* a chat line waits for an earlier one from the same handle that's still to be read, two sends or two reads at once
   can finish in either order */
static int mail_held(const mailrec *m)
{
    const BYTE *e = m->hdr, *o;
    if (m->state != 1 || ((*(const WORD *)(e + 0x3e) >> 7) & 0x1f) != 0) return 0;
    for (int i = 0; i < g_mail_n; i++) {
        o = g_mail[i].hdr;
        if (g_mail[i].state == 0 && ((*(const WORD *)(o + 0x3e) >> 7) & 0x1f) == 0 && !memcmp(o, e, 8) &&
            o[0x3c] == e[0x3c] && mail_order(o) < mail_order(e))
            return 1;
    }
    return 0;
}
/* the oldest saved one whose delete hasn't started */
static int mail_saved_spare(void)
{
    for (int i = mail_next(0, 1 << 4); i >= 0; i = mail_next(g_mail[i].mid, 1 << 4))
        if (g_del_job < 0 || i != g_del_idx) return i;
    return -1;
}
static mailrec *mail_add(const BYTE *hdr)
{
    BYTE id[0x1a];
    int i;
    for (i = 0; i < g_mail_n; i++)
        if (mail_same(g_mail[i].hdr, hdr)) return NULL;
    mail_ident(hdr, id);
    for (i = 0; i < g_mail_seen_n; i++)
        if (memcmp(g_mail_seen[i], id, sizeof id) == 0) return NULL;
    if (g_mail_n < MAIL_MAX) {
        i = g_mail_n++;
    } else if ((i = mail_next(0, 1 << 2 | 1 << 5 | 1 << 6)) >= 0 || (i = mail_saved_spare()) >= 0) {
        /* the oldest failed, deleted or skipped one makes room, then a saved one whose delete hasn't started (the next
           login fetches it again). Its identity goes in the ring */
        mail_ident(g_mail[i].hdr, g_mail_seen[g_mail_seen_next]);
        g_mail_seen_next = (g_mail_seen_next + 1) % MAIL_SEEN;
        if (g_mail_seen_n < MAIL_SEEN) g_mail_seen_n++;
    } else {
        lg("mail: cache full (%d) - message not kept this session (still on the server)\r\n", MAIL_MAX);
        return NULL;
    }
    memset(&g_mail[i], 0, sizeof g_mail[0]);
    memcpy(g_mail[i].hdr, hdr, 0x48);
    g_mail[i].mid = ++g_mail_mid;
    return &g_mail[i];
}
static void mail_log_hdr(const char *what, const BYTE *h)
{
    char nm[64];
    sjis_json(nm, sizeof nm, h + 0x10, 0x0f);
    lg("mail: %s from=%s slot=%u kind=%u len=%u seq=%u time=%u 3e=%04x 42=%04x\r\n", what, nm,
       h[0x3c], (unsigned)((*(WORD *)(h + 0x3e) >> 7) & 0x1f), *(DWORD *)(h + 0x38),
       *(DWORD *)(h + 0x30), *(DWORD *)(h + 0x34), *(WORD *)(h + 0x3e), *(WORD *)(h + 0x42));
}
/* whether a notice carries its body inline (H+0x4c), which the viewer never trusts. Its length and where the 0x07
   sits only, never the text */
static void mail_inline_log(const BYTE *h)
{
    const char *s = *(const char *const *)(h + 0x4c);
    int n = 0, bel = -1;
    if (s)
        for (; n < 0x1000; n++) {
            if ((n == 0 || !((ULONG_PTR)(s + n) & 0xfff)) && IsBadReadPtr(s + n, 1)) break;
            if (!s[n]) break;
            if (s[n] == 0x07 && bel < 0) bel = n;
        }
    lg("mail: notice inline text %d bytes, 0x07 at %d, body %u bytes\r\n", s ? n : -1, bel, *(const DWORD *)(h + 0x38));
}
/* push callbacks run inside polcore's IRC processing on the hold loop's thread, H on its stack, so copy and get out */
static void __cdecl mail_evt(int code, void *arg)
{
    lg("mail: evt code=%d arg=%p\r\n", code, arg);
    /* 2 friend presence, 3 kind-9 notice, 4-6 groups: polcore already updated its arrays */
    if (code >= 2 && code <= 6) g_snap_dirty = 1;
    if (code == 0 && arg && !IsBadReadPtr(arg, 0x48)) {
        LONG n = g_push_n;
        if (n < 8) { memcpy(g_push_q[n], arg, 0x48); g_push_n = n + 1; }
        if (!IsBadReadPtr(arg, 0x50)) mail_inline_log((const BYTE *)arg);
    }
}
static void __cdecl mail_raw(void *arg)
{
    BYTE *h = (BYTE *)arg;
    if (!h || IsBadReadPtr(h, 0x50)) return;
    mail_log_hdr("real-time", h);
    if (((*(WORD *)(h + 0x3e) >> 12) & 3) == 1) return;   /* delivery receipt */
    mailrec *m = mail_add(h);
    if (m) {
        const char *body = *(const char **)(h + 0x4c);
        DWORD len = *(DWORD *)(h + 0x38);
        if (body && len <= 0x180 && !IsBadReadPtr(body, len)) mail_parse(m, body, (int)len);
        m->state = 3;
        g_snap_dirty = g_mail_fresh = g_mail_raw_new = 1;
    }
}
static int is_me(BYTE *b, unsigned lo, unsigned hi);
/* a chat line from my own POL ID is never one I received. Sent from a handle the friend's handle isn't friends with,
   it can come back to me instead of reaching them */
static int mail_mine(BYTE *b, const BYTE *h)
{
    return ((*(const WORD *)(h + 0x3e) >> 7) & 0x1f) == 0 && is_me(b, *(const DWORD *)h, *(const DWORD *)(h + 4));
}
static int sender_ignored(BYTE *b, const BYTE *h)
{
    for (int bi = 0; bi < 100; bi++) {
        BYTE *a = b + 0xafc18 + bi * 0xb0;
        if (IsBadReadPtr(a, 0xb0)) break;
        if (!(*(unsigned *)(a + 0x98) & 1)) continue;
        if (*(DWORD *)(a + 0x00) != *(const DWORD *)(h + 0x00) || *(DWORD *)(a + 0x04) != *(const DWORD *)(h + 0x04))
            continue;
        if ((*(unsigned *)(a + 0x08) & 3) || ((*(unsigned *)(a + 0x9c) >> 13) & 0x3f) == h[0x3c]) return 1;
    }
    return 0;
}
static void mail_list_done(int r)
{
    int found = 0, added = 0;
    if (r != 1) {
        if (g_mail_fails++ == 0) {
            lg("mail: list failed %d - retry once in 30 s\r\n", r);
            g_mail_retry_at = GetTickCount64() + 30000;
            g_mail_refresh = 1;
        } else {
            lg("mail: list failed %d again - waiting for new mail or a request\r\n", r);
            if (lstrcmpA(g_mail_state, "ok") != 0) lstrcpyA(g_mail_state, "failed");
            g_snap_dirty = 1;
        }
        return;
    }
    g_mail_fails = 0;
    lstrcpyA(g_mail_state, "ok");
    g_snap_dirty = 1;
    for (int i = 0; i < MAIL_MAX; i++) {
        BYTE *e = g_inbox + i * 0x50;
        if (!(*(WORD *)(e + 0x3e) & 0x8000)) continue;
        found++;
        mail_log_hdr("listed", e);
        if (mail_add(e)) added++;
    }
    lg("mail: list done, %d on server, %d new\r\n", found, added);
}
static void mail_read_done(int lane, int r)
{
    mailrec *m = &g_mail[g_rd[lane].idx];
    if (r == 1) {
        mail_parse(m, g_rd[lane].body, (int)*(DWORD *)(m->hdr + 0x38));
        m->state = 1;
        g_mail_fresh = 1;
        lg("mail: body read (%u bytes)\r\n", *(DWORD *)(m->hdr + 0x38));
    } else {
        m->state = 2;
        lg("mail: body read failed %d\r\n", r);
    }
    *(char **)(m->hdr + 0x4c) = NULL;
    HeapFree(GetProcessHeap(), 0, g_rd[lane].body);
    g_rd[lane].body = NULL;
    g_snap_dirty = 1;
}
static int msg_jobs(void);
static int msg_slot_ok(BYTE *b);
static int send_built(void);
static int writes_waiting(void);
static int handle_switching(void);
static int mail_lane_busy(int kind, int idx)
{
    for (int l = 0; l < MAIL_LANES; l++)
        if (g_rd[l].job >= 0 && g_rd[l].kind == kind && (kind == 1 || g_rd[l].idx == idx)) return 1;
    return 0;
}
/* list or read, -0x100 (no free polcore slot, refused locally) tries again next turn. Only the list waits out the
   30 s after a failed one, reads still go */
static void mail_fetch_start(BYTE *b, int lane)
{
    int i, t;
    char *body;
    if (g_mail_refresh && !mail_lane_busy(1, 0) && GetTickCount64() >= g_mail_retry_at) {
        memset(g_inbox, 0, sizeof g_inbox);
        t = ((int (__cdecl *)(void *, int))SVC(0x524))(g_inbox, MAIL_MAX);
        if (t == NO_SLOT) return;
        lg("mail: list start -> %d\r\n", t);
        g_mail_refresh = 0;
        if (t < 0) { mail_list_done(t); return; }
        g_rd[lane].job = t; g_rd[lane].kind = 1;
        return;
    }
    for (i = mail_next(0, 1); i >= 0; i = mail_next(g_mail[i].mid, 1)) {
        mailrec *m = &g_mail[i];
        DWORD len = *(DWORD *)(m->hdr + 0x38);
        unsigned kd = (*(WORD *)(m->hdr + 0x3e) >> 7) & 0x1f;
        if (mail_lane_busy(2, i)) continue;
        if (len == 0 || len > 0x101100) { m->state = 2; continue; }
        if (kd == 0 && sender_ignored(b, m->hdr)) {
            lg("mail: chat message from an ignored sender - deleted unread\r\n");
            m->state = 4;
            continue;
        }
        if (mail_mine(b, m->hdr)) {
            lg("mail: chat message from my own POL ID - not fetched or shown\r\n");
            m->state = 6;
            continue;
        }
        /* chat 0, friend request 1, answers 9/10, group invitation 0xe and its notices, the rest stay unread */
        if (!(kd == 0 || kd == 1 || kd == 9 || kd == 10 || kd == 0xe || kd == 0xf || kd == 0x10 ||
              kd == 0x12 || kd == 0x13)) {
            lg("mail: kind %u is not shown by the launcher - not fetched\r\n", kd);
            m->state = 6;
            continue;
        }
        body = (char *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, len + 1);
        if (!body) return;
        *(char **)(m->hdr + 0x4c) = body;
        t = ((svc_p)SVC(0x518))(m->hdr);
        if (t != NO_SLOT) lg("mail: read start -> %d\r\n", t);
        if (t < 0) {
            *(char **)(m->hdr + 0x4c) = NULL;
            HeapFree(GetProcessHeap(), 0, body);
            if (t != NO_SLOT) m->state = 2;
            return;
        }
        g_rd[lane].job = t; g_rd[lane].kind = 2; g_rd[lane].idx = i; g_rd[lane].body = body;
        return;
    }
}
/* saved by the launcher: delete the server copy (svc 0x508, keyed like the GET). One to a slot this account lost
   is refused (-0x1540) and stays, as in the viewer. */
static void mail_delete_start(BYTE *b)
{
    int i, t;
    for (i = mail_next(0, 1 << 4); i >= 0; i = mail_next(g_mail[i].mid, 1 << 4)) {
        BYTE *h = g_mail[i].hdr;
        t = ((svc_p)SVC(0x508))(h);
        if (t == NO_SLOT) return;
        lg("mail: delete start -> %d (from slot %u to slot %u)\r\n", t, h[0x3c], h[0x3d]);
        if (t < 0) { g_mail[i].state = 5; continue; }
        g_del_job = t; g_del_idx = i;
        return;
    }
}
/* reads first, two at once, then deletes on a lane of their own */
static void mail_pump(BYTE *b)
{
    int i, r, done = 0;
    for (i = 0; i < g_push_n; i++) {   /* arrival order */
        mail_log_hdr("notice", g_push_q[i]);
        mail_add(g_push_q[i]);
    }
    g_push_n = 0;
    if (g_mail_raw_new) {
        g_mail_raw_new = 0;
        for (i = 0; i < g_mail_n; i++)
            if (g_mail[i].state == 3 && mail_mine(b, g_mail[i].hdr)) {
                lg("mail: real-time chat line from my own POL ID - not shown\r\n");
                g_mail[i].state = 6;
            }
    }
    /* never cancelled here, polcore fails a stuck job at 100 s itself and svc 0x520 stalls every job poll 10 s */
    for (i = 0; i < MAIL_LANES; i++)
        if (g_rd[i].job >= 0 && (r = ((svc_i)SVC(g_rd[i].kind == 1 ? 0x528 : 0x51c))(g_rd[i].job)) != 0) {
            g_rd[i].job = -1;
            if (g_rd[i].kind == 1) mail_list_done(r); else mail_read_done(i, r);
            done = 1;
        }
    if (g_del_job >= 0 && (r = ((svc_i)SVC(0x50c))(g_del_job)) != 0) {
        g_del_job = -1;
        g_mail[g_del_idx].state = 5;
        if (r == 1) lg("mail: deleted from the server after saving\r\n");
        else lg("mail: delete FAILED %d - the message stays on the server\r\n", r);
        done = 1;
    }
    /* a freed lane waits a turn, so a waiting send gets it first */
    if (done) return;
    if (!g_draining)
        for (i = 0; i < MAIL_LANES && msg_slot_ok(b); i++) {
            if (g_rd[i].job >= 0) continue;
            mail_fetch_start(b, i);
            if (g_rd[i].job < 0) break;   /* nothing to read, or polcore said no */
        }
    /* a delete keeps to the viewer's 2 jobs. In a drain it only goes with no write waiting for a slot, and nothing
       waits for it */
    if (g_del_job < 0 && msg_jobs() < 2 && (!g_draining || (!msg_jobs() && !writes_waiting())) && msg_slot_ok(b))
        mail_delete_start(b);
}
#define ACK_MAX (1024 * 1024)   /* about 140,000 mids, far more than a session ever has */
static void mail_ack_request(void)
{
    char *buf;
    int got, v = -1;
    if (!request_waiting("ack")) return;
    if ((got = take_request_ex("ack", NULL, 0, &buf, ACK_MAX)) < 0) return;
    for (int i = 0; i <= got; i++) {
        char c = buf[i];
        if (c >= '0' && c <= '9') v = v > 9999999 ? v : (v < 0 ? 0 : v * 10) + (c - '0');
        else {
            mailrec *m = v > 0 ? mail_by_mid(v) : NULL;
            if (m && (m->state == 1 || m->state == 3)) {
                m->state = m->state == 1 ? 4 : 5;
                m->text[0] = 0; m->subj[0] = 0;
                g_snap_dirty = 1;
            }
            v = -1;
        }
    }
    HeapFree(GetProcessHeap(), 0, buf);
}

/* Ids are masked per session and names change, so conversations are keyed on HMAC-SHA256("<my POL ID>/<peer POL
   ID>/<slot>"), 8 bytes in hex. No POL ID ever leaves the hook. */
static char g_my_polid[12];
static BYTE g_peer_secret[32];
static int g_peer_secret_ok;
static unsigned __int64 my_id(BYTE *b) { return ((unsigned __int64 (__cdecl *)(void))SVC(0x558))(); }
static void fmt_polid(BYTE *b, unsigned lo, unsigned hi, char out[12])
{
    memset(out, 0, 12);
    ((void (__cdecl *)(unsigned, unsigned, char *))SVC(0x584))(lo, hi, out);
    out[8] = 0;
}
static int peer_secret_read(const WCHAR *path)
{
    DWORD n = 0;
    HANDLE f = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, NULL);
    if (f == INVALID_HANDLE_VALUE) return -1;
    g_peer_secret_ok = ReadFile(f, g_peer_secret, sizeof g_peer_secret, &n, NULL) && n == sizeof g_peer_secret;
    CloseHandle(f);
    return g_peer_secret_ok;
}
/* a peerkey.bin left in %APPDATA%\Provenance is taken once so chat history keeps its keys. Anything of the user's can
   plant links there, so only through a handle whose real path is that file, and it's deleted through that handle */
static HANDLE peer_secret_old(void)
{
    WCHAR dir[MAX_PATH], path[MAX_PATH + 32], want[MAX_PATH + 32], got[MAX_PATH + 32];
    BY_HANDLE_FILE_INFORMATION fi;
    HANDLE f;
    DWORD n = GetEnvironmentVariableW(L"APPDATA", dir, MAX_PATH);
    if (!n || n >= MAX_PATH) return INVALID_HANDLE_VALUE;
    f = CreateFileW(dir, 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING,
                    FILE_FLAG_BACKUP_SEMANTICS, NULL);
    if (f == INVALID_HANDLE_VALUE) return f;
    n = GetFinalPathNameByHandleW(f, want, MAX_PATH, 0);
    CloseHandle(f);
    if (!n || n >= MAX_PATH) return INVALID_HANDLE_VALUE;
    lstrcatW(want, L"\\Provenance\\peerkey.bin");
    lstrcpyW(path, dir); lstrcatW(path, L"\\Provenance\\peerkey.bin");
    /* delete sharing so two pol.exe starting together both get the same one */
    f = CreateFileW(path, GENERIC_READ | DELETE, FILE_SHARE_READ | FILE_SHARE_DELETE, NULL, OPEN_EXISTING,
                    FILE_FLAG_OPEN_REPARSE_POINT, NULL);
    if (f == INVALID_HANDLE_VALUE) return f;
    n = GetFinalPathNameByHandleW(f, got, MAX_PATH + 32, 0);
    if (!n || n >= MAX_PATH + 32 || lstrcmpiW(got, want) || !GetFileInformationByHandle(f, &fi) ||
        (fi.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)) || fi.nNumberOfLinks != 1 ||
        fi.nFileSizeHigh || fi.nFileSizeLow != sizeof g_peer_secret ||
        !ReadFile(f, g_peer_secret, sizeof g_peer_secret, &n, NULL) || n != sizeof g_peer_secret) {
        CloseHandle(f);
        return INVALID_HANDLE_VALUE;
    }
    return f;
}
/* POL_IPC_DIR is one folder for the whole PC, so each Windows user gets their own file there, named by their SID */
static int peer_secret_name(WCHAR *out, int cap)
{
    BYTE buf[sizeof(TOKEN_USER) + SECURITY_MAX_SID_SIZE];
    WCHAR *sid = NULL;
    HANDLE t;
    DWORD n = 0;
    int ok;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &t)) return 0;
    ok = GetTokenInformation(t, TokenUser, buf, sizeof buf, &n) &&
         ConvertSidToStringSidW(((TOKEN_USER *)buf)->User.Sid, &sid);
    CloseHandle(t);
    if (!ok) return 0;
    ok = lstrlenW(sid) + 13 <= cap;
    if (ok) wsprintfW(out, L"peerkey-%s.bin", sid);
    LocalFree(sid);
    return ok;
}
/* in the admin-only POL_IPC_DIR, so manual runs without one get no conversation keys */
static int peer_secret(void)
{
    WCHAR path[MAX_PATH + 256], name[256];
    FILE_DISPOSITION_INFO gone = { TRUE };
    HANDLE f, old;
    DWORD n = 0;
    int r;
    if (g_peer_secret_ok) return 1;
    n = GetEnvironmentVariableW(L"POL_IPC_DIR", path, MAX_PATH);
    if (!n || n >= MAX_PATH || !peer_secret_name(name, 256)) return 0;
    if (path[n - 1] != L'\\' && path[n - 1] != L'/') lstrcatW(path, L"\\");
    lstrcatW(path, name);
    if ((r = peer_secret_read(path)) >= 0) return r;
    old = peer_secret_old();
    if (old == INVALID_HANDLE_VALUE &&
        BCryptGenRandom(NULL, g_peer_secret, sizeof g_peer_secret, BCRYPT_USE_SYSTEM_PREFERRED_RNG) != 0) return 0;
    f = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) r = peer_secret_read(path) > 0;
    else {
        g_peer_secret_ok = WriteFile(f, g_peer_secret, sizeof g_peer_secret, &n, NULL) && n == sizeof g_peer_secret;
        CloseHandle(f);
        r = g_peer_secret_ok;
    }
    if (old != INVALID_HANDLE_VALUE) {
        if (r) SetFileInformationByHandle(old, FileDispositionInfo, &gone, sizeof gone);
        CloseHandle(old);
    }
    return r;
}
static void peer_key(BYTE *b, unsigned lo, unsigned hi, unsigned slot, char out[17])
{
    char id[12], msg[40];
    BYTE mac[32];
    out[0] = 0;
    if (!SVC(0x584) || !peer_secret()) return;
    if (!g_my_polid[0]) {
        unsigned __int64 me = my_id(b);
        if (!me) return;
        fmt_polid(b, (unsigned)me, (unsigned)(me >> 32), g_my_polid);
    }
    fmt_polid(b, lo, hi, id);
    wsprintfA(msg, "%s/%s/%u", g_my_polid, id, slot & 0x3f);
    if (BCryptHash(BCRYPT_HMAC_SHA256_ALG_HANDLE, g_peer_secret, sizeof g_peer_secret,
                   (PUCHAR)msg, (ULONG)lstrlenA(msg), mac, sizeof mac) != 0) return;
    for (int i = 0; i < 8; i++) {
        out[2 * i] = "0123456789abcdef"[mac[i] >> 4];
        out[2 * i + 1] = "0123456789abcdef"[mac[i] & 15];
    }
    out[16] = 0;
}
static int is_me(BYTE *b, unsigned lo, unsigned hi)
{
    char id[12], mine[12];
    if (!SVC(0x584)) return 0;
    unsigned __int64 me = my_id(b);
    if (!me) return 0;
    fmt_polid(b, (unsigned)me, (unsigned)(me >> 32), mine);
    fmt_polid(b, lo, hi, id);
    return mine[0] && lstrcmpA(id, mine) == 0;
}
#define FR_SLOT(fr) ((*(DWORD *)((fr) + 0x9c) >> 13) & 0x3f)
static int get_friend(BYTE *b, int row, BYTE *fr)
{
    memset(fr, 0, 0x140);
    return ((svc_ip)SVC(0x5cc))(row, fr) >= 0 && (*(DWORD *)(fr + 0x98) & 1);
}
static void friend_key(BYTE *b, const BYTE *fr, char k[17])
{
    peer_key(b, *(DWORD *)(fr + 0x00), *(DWORD *)(fr + 0x04), FR_SLOT(fr), k);
}
/* the same test as polcore.dll 0x100254d0 */
static int find_friend_row(BYTE *b, DWORD lo, DWORD hi, int slot)
{
    BYTE r[0x140];
    for (int i = 0; i < 200; i++)
        if (get_friend(b, i, r) && *(DWORD *)r == lo && *(DWORD *)(r + 4) == hi && (int)FR_SLOT(r) == slot) return i;
    return -1;
}

static int my_handle(BYTE *b, char *name16)
{
    BYTE rec[0x28];
    int slot = ((svc_v)SVC(0x2f8))();
    name16[0] = 0;
    if (slot < 0)
        for (int i = 0; i < 64; i++)
            if (get_handle(b, i, rec) == 0 && (rec[0] & 1)) { slot = i; break; }
    if (slot >= 0 && get_handle(b, slot, rec) == 0) {
        memcpy(name16, rec + 0x08, 15);
        name16[15] = 0;
    }
    return slot;
}
static int slot_name(BYTE *b, int slot, char *name16)
{
    BYTE rec[0x28];
    memset(rec, 0, sizeof rec);
    if (get_handle(b, slot, rec) != 0 || !(rec[0] & 1)) return 0;
    memcpy(name16, rec + 0x08, 15);
    name16[15] = 0;
    return 1;
}

static int msg_json(BYTE *b, char *out, int cap, const BYTE *e, int mid, const char *state, int read,
                    const char *subj, const char *text, const BYTE *att)
{
    char nm[64], sk[17], grp[80] = "";
    WORD f = *(const WORD *)(e + 0x3e);
    unsigned kind = (f >> 7) & 0x1f;
    int o;
    sjis_json(nm, sizeof nm, e + 0x10, 0x0f);
    peer_key(b, *(const DWORD *)(e + 0x00), *(const DWORD *)(e + 0x04), e[0x3c], sk);
    /* removed from a group (0x12) or it was disbanded (0x13): the name's in the type-10 attachment, the text's empty */
    if (att && (kind == 0x12 || kind == 0x13)) {
        lstrcpyA(grp, ",\"group\":");
        sjis_json(grp + 9, sizeof grp - 9, att + 8, 0x14);
    }
    o = wsprintfA(out, "{\"mid\":%d,\"from\":%s,\"fromKey\":\"%s\",\"fromSlot\":%u,\"me\":%s,\"kind\":%u,\"time\":%u,"
                       "\"seq\":%u,\"state\":\"%s\"%s%s,\"subject\":",
                  mid, nm, sk, e[0x3c],
                  is_me(b, *(const DWORD *)(e + 0x00), *(const DWORD *)(e + 0x04)) ? "true" : "false",
                  kind, *(const DWORD *)(e + 0x34), *(const DWORD *)(e + 0x30), state,
                  read < 0 ? "" : read ? ",\"read\":true" : ",\"read\":false", grp);
    o += json_text(out + o, cap - o - 16, subj);
    lstrcpyA(out + o, ",\"text\":"); o += 8;
    o += json_text(out + o, cap - o - 4, text);
    out[o++] = '}';
    out[o] = 0;
    return o;
}

/* pub\homeNN\msg: r\b unread, r\a read (FFXI moves them there), s\b the game's outbox, s\a sent. A file's name is
   its header in polcore's base64 */
#define STORE_KEEP 200   /* per folder, the viewer's own cap */
#define STORE_SCAN_MAX 20000
#define STORE_BODY_MAX 0x1100
#define STORE_JSON_MAX (3 * 1024 * 1024)
#define LEGACY_HOME 84   /* the placeholder ID's slot, shared by every account without one of its own */
static const char *const g_store_sub[4] = { "r\\a", "r\\b", "s\\a", "s\\b" };
typedef struct { BYTE hdr[0x48]; char name[100]; int home, sub, alt_home, alt_sub; char alt[100]; } storefile;

static int store_name_ok(const char *n)
{
    static const char abc[] = "TSG8IncW3HFKokOg79qzeCmZs2yBYEQVAUxR5rbwi4P@jMDLtpvad0f_J1hlN6uX";
    int i;
    for (i = 0; n[i]; i++) if (i >= 96 || !strchr(abc, n[i])) return 0;
    return i == 96;
}
static int store_newer(const void *x, const void *y)
{
    const BYTE *a = ((const storefile *)x)->hdr, *c = ((const storefile *)y)->hdr;
    DWORD ta = *(const DWORD *)(a + 0x34), tc = *(const DWORD *)(c + 0x34);
    DWORD sa = *(const DWORD *)(a + 0x30), sc = *(const DWORD *)(c + 0x30);
    if (ta != tc) return ta < tc ? 1 : -1;
    return sa == sc ? 0 : sa < sc ? 1 : -1;
}
static void store_dir(char *out, int home, int sub)
{
    wsprintfA(out, "%s\\pub\\home%02d\\msg\\%s", g_viewer, home, g_store_sub[sub]);
}
/* pol.exe runs elevated, so it never goes through a junction or link someone else put in the viewer folder */
static int plain_path(const char *path)
{
    char p[MAX_PATH + 160];
    int base = lstrlenA(g_viewer);
    DWORD a;
    if (lstrlenA(path) >= (int)sizeof p || lstrlenA(path) <= base) return 0;
    lstrcpyA(p, path);
    for (char *c = p + base + 1;; c++) {
        char was = *c;
        if (was != '\\' && was) continue;
        *c = 0;
        a = GetFileAttributesA(p);
        if (a != INVALID_FILE_ATTRIBUTES && (!(a & FILE_ATTRIBUTE_DIRECTORY) || (a & FILE_ATTRIBUTE_REPARSE_POINT)))
            return 0;
        if (!was) return 1;
        *c = was;
    }
}
static HANDLE open_plain(const char *path)
{
    BY_HANDLE_FILE_INFORMATION fi;
    HANDLE h = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL,
                           OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, NULL);
    if (h == INVALID_HANDLE_VALUE) return h;
    if (!GetFileInformationByHandle(h, &fi) || fi.nNumberOfLinks != 1 ||
        (fi.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY))) {
        CloseHandle(h);
        return INVALID_HANDLE_VALUE;
    }
    return h;
}
static int store_owned(const BYTE *h, int sub, unsigned __int64 me)
{
    return memcmp(h + (sub >= 2 ? 0x00 : 0x08), &me, 8) == 0;
}
/* requests and invitations can't be answered from a copy, the server's is gone */
static int store_wanted(const BYTE *h, int sub)
{
    WORD f = *(const WORD *)(h + 0x3e);
    unsigned kd = (f >> 7) & 0x1f;
    if (((f >> 12) & 3) == 1) return 0;
    if (sub >= 2) return kd == 0;
    return kd == 0 || kd == 9 || kd == 10 || kd == 0xf || kd == 0x10 || kd == 0x12 || kd == 0x13;
}
/* my newest STORE_KEEP in one folder, headers decoded by polcore (svc 0x540) so the ids are in this session's form */
static int store_list(BYTE *b, int home, int sub, unsigned __int64 me, int wanted, storefile **out)
{
    char glob[MAX_PATH + 64];
    WIN32_FIND_DATAA fd;
    storefile *v = NULL;
    int n = 0, looked = 0, cap = STORE_KEEP * 4;
    HANDLE h;
    *out = NULL;
    if (!SVC(0x540) || !g_viewer[0]) return 0;
    store_dir(glob, home, sub);
    if (!plain_path(glob)) return 0;
    lstrcatA(glob, "\\*");
    if ((h = FindFirstFileA(glob, &fd)) == INVALID_HANDLE_VALUE) return 0;
    if (!(v = (storefile *)HeapAlloc(GetProcessHeap(), 0, (cap + 1) * sizeof *v))) { FindClose(h); return 0; }
    do {
        if ((fd.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) ||
            !store_name_ok(fd.cFileName))
            continue;
        if (++looked > STORE_SCAN_MAX) {
            lg("store: over %d files in home%02d\\msg\\%s, the rest aren't read\r\n", STORE_SCAN_MAX, home,
               g_store_sub[sub]);
            break;
        }
        memset(&v[n], 0, sizeof v[n]);
        if (((int (__cdecl *)(void *, const char *))SVC(0x540))(v[n].hdr, fd.cFileName) != 0) continue;
        if (!(*(WORD *)(v[n].hdr + 0x3e) & 0x8000)) continue;   /* the viewer's loader skips those too */
        if (!store_owned(v[n].hdr, sub, me) || (wanted && !store_wanted(v[n].hdr, sub))) continue;
        lstrcpyA(v[n].name, fd.cFileName);
        v[n].home = home;
        v[n].sub = sub;
        v[n].alt_home = -1;
        if (++n == cap) {
            qsort(v, n, sizeof *v, store_newer);
            n = STORE_KEEP;
        }
    } while (FindNextFileA(h, &fd));
    FindClose(h);
    if (n > 1) qsort(v, n, sizeof *v, store_newer);
    *out = v;
    return n > STORE_KEEP ? STORE_KEEP : n;
}
static int store_read(const char *path, char *buf, int cap)
{
    DWORD got = 0;
    BOOL ok;
    HANDLE h = open_plain(path);
    if (h == INVALID_HANDLE_VALUE) return -1;
    ok = ReadFile(h, buf, (DWORD)cap, &got, NULL);
    CloseHandle(h);
    return ok ? (int)got : -1;
}
/* the other home's copy of the same message if this one won't read */
static int store_body(const storefile *f, char *buf, int cap)
{
    char path[MAX_PATH + 160];
    DWORD len = *(const DWORD *)(f->hdr + 0x38);
    int got;
    if (len == 0 || len > 0x101100) return -1;
    if (cap > (int)len) cap = (int)len;
    store_dir(path, f->home, f->sub);
    lstrcatA(path, "\\");
    lstrcatA(path, f->name);
    if ((got = store_read(path, buf, cap)) > 0 || f->alt_home < 0) return got;
    store_dir(path, f->alt_home, f->alt_sub);
    lstrcatA(path, "\\");
    lstrcatA(path, f->alt);
    return store_read(path, buf, cap);
}
static int dir_exists(const char *p)
{
    DWORD a = GetFileAttributesA(p);
    return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY);
}
static int legacy_home_there(void)
{
    char p[MAX_PATH + 32];
    wsprintfA(p, "%s\\pub\\home%02d", g_viewer, LEGACY_HOME);
    return g_viewer[0] && dir_exists(p) && plain_path(p);
}

static int sent_json(BYTE *b, char *out, int cap, const BYTE *e, int mid, const char *state, const char *subj,
                     const char *text)
{
    char nm[64] = "\"\"", sk[17];
    BYTE fr[0x140];
    DWORD lo = *(const DWORD *)(e + 0x08), hi = *(const DWORD *)(e + 0x0c);
    int row = find_friend_row(b, lo, hi, e[0x3d]), o;
    peer_key(b, lo, hi, e[0x3d], sk);
    if (row >= 0 && get_friend(b, row, fr)) sjis_json(nm, sizeof nm, fr + 0xa0, 0x0f);
    o = wsprintfA(out, "{\"mid\":%d,\"to\":%s,\"toKey\":\"%s\",\"toSlot\":%u,\"time\":%u,\"seq\":%u,\"state\":\"%s\","
                       "\"subject\":",
                  mid, nm, sk, e[0x3d], *(const DWORD *)(e + 0x34), *(const DWORD *)(e + 0x30), state);
    o += json_text(out + o, cap - o - 16, subj);
    lstrcpyA(out + o, ",\"text\":"); o += 8;
    o += json_text(out + o, cap - o - 4, text);
    out[o++] = '}';
    out[o] = 0;
    return o;
}
typedef struct { char *p; int n, cap; } gbuf;
static int gb_add(gbuf *g, const char *s, int len)
{
    if (g->n + len + 1 > g->cap) {
        int cap = g->cap ? g->cap * 2 : 0x10000;
        char *q;
        while (cap < g->n + len + 1) cap *= 2;
        if (cap > STORE_JSON_MAX + 0x10000) cap = STORE_JSON_MAX + 0x10000;
        if (cap < g->n + len + 1) return 0;
        q = (char *)(g->p ? HeapReAlloc(GetProcessHeap(), 0, g->p, cap) : HeapAlloc(GetProcessHeap(), 0, cap));
        if (!q) return 0;
        g->p = q;
        g->cap = cap;
    }
    memcpy(g->p + g->n, s, len);
    g->n += len;
    g->p[g->n] = 0;
    return 1;
}
static void wr(HANDLE h, const char *s);
static void store_drop(void)
{
    char p[MAX_PATH + 64];
    ipc_file(p, "store.json");
    DeleteFileA(p);
    ipc_file(p, "chatroom.json");   /* the chat room's lines, other people's words like the messages */
    DeleteFileA(p);
    /* and an enter that never got taken */
    for (int i = 0; i < 3; i++) {
        ipc_file(p, "chatjoin");
        lstrcatA(p, i == 0 ? "" : i == 1 ? ".taking" : ".taking2");
        DeleteFileA(p);
    }
}
/* FFXI deletes what it fetches from the server, so the store's the only copy of what was handled in game. Read
   once, nothing on the wire */
static void store_import(BYTE *b)
{
    static mailrec scratch;
    static char body[STORE_BODY_MAX];
    char path[MAX_PATH + 64], tmp[MAX_PATH + 80];
    unsigned __int64 me = my_id(b);
    storefile *all = NULL, *v, *g;
    gbuf in = {0}, out = {0};
    int homes[2], nh = 0, n = 0, cap = 0, k, i, j, h, s, got, nin = 0, nout = 0, left = 0;
    DWORD wn;
    HANDLE f;
    store_drop();
    if (!me || g_home < 0) return;
    homes[nh++] = g_home;
    if (g_home != LEGACY_HOME && legacy_home_there()) homes[nh++] = LEGACY_HOME;
    for (h = 0; h < nh; h++)
        for (s = 0; s < 4; s++) {   /* s\a before s\b, so a sent copy beats the outbox's */
            k = store_list(b, homes[h], s, me, 1, &v);
            for (i = 0; i < k; i++) {
                if (s < 2 && ((*(WORD *)(v[i].hdr + 0x3e) >> 7) & 0x1f) == 0 && !memcmp(v[i].hdr, &me, 8)) continue;
                for (j = 0; j < n; j++)
                    if ((all[j].sub >= 2) == (s >= 2) && mail_same(all[j].hdr, v[i].hdr)) break;
                if (j < n) {
                    /* the same message twice: read if either copy is, sent if either went, the other's the spare */
                    if (s < 2 && (s == 0 || (*(WORD *)(v[i].hdr + 0x42) & 0x100)))
                        *(WORD *)(all[j].hdr + 0x42) |= 0x100;
                    if (s == 2 && all[j].sub == 3) all[j] = v[i];
                    else if (all[j].alt_home < 0) {
                        all[j].alt_home = v[i].home;
                        all[j].alt_sub = v[i].sub;
                        lstrcpyA(all[j].alt, v[i].name);
                    }
                    continue;
                }
                if (n == cap) {
                    cap = cap ? cap * 2 : 64;
                    g = (storefile *)(all ? HeapReAlloc(GetProcessHeap(), 0, all, cap * sizeof *all)
                                          : HeapAlloc(GetProcessHeap(), 0, cap * sizeof *all));
                    if (!g) break;
                    all = g;
                }
                all[n] = v[i];
                if (s == 0) *(WORD *)(all[n].hdr + 0x42) |= 0x100;
                n++;
            }
            if (v) HeapFree(GetProcessHeap(), 0, v);
        }
    if (n > 1) qsort(all, n, sizeof *all, store_newer);
    for (i = 0; i < n; i++) {
        int sent = all[i].sub >= 2, need, len;
        char *json;
        if ((got = store_body(&all[i], body, sizeof body)) <= 0) continue;
        memset(&scratch, 0, sizeof scratch);
        memcpy(scratch.hdr, all[i].hdr, 0x48);
        mail_parse(&scratch, body, got);
        need = 512 + 2 * (lstrlenA(scratch.subj) + lstrlenA(scratch.text));
        if (!(json = (char *)HeapAlloc(GetProcessHeap(), 0, need))) continue;
        if (sent) len = sent_json(b, json, need, all[i].hdr, 0, all[i].sub == 2 ? "sent" : "unsent", scratch.subj,
                                  scratch.text);
        else len = msg_json(b, json, need, all[i].hdr, 0, "local", (*(WORD *)(all[i].hdr + 0x42) >> 8) & 1,
                            scratch.subj, scratch.text, scratch.has_att ? scratch.att : NULL);
        /* newest first, so a store too big for the file loses its oldest lines */
        if (in.n + out.n + len + 2 > STORE_JSON_MAX ||
            !gb_add(sent ? &out : &in, (sent ? nout : nin) ? "," : "", (sent ? nout : nin) ? 1 : 0) ||
            !gb_add(sent ? &out : &in, json, len)) {
            left = n - i;
            HeapFree(GetProcessHeap(), 0, json);
            break;
        }
        HeapFree(GetProcessHeap(), 0, json);
        if (sent) nout++; else nin++;
    }
    if (all) HeapFree(GetProcessHeap(), 0, all);
    SecureZeroMemory(body, sizeof body);
    SecureZeroMemory(&scratch, sizeof scratch);
    if (nin || nout) {
        ipc_file(path, "store.json");
        lstrcpyA(tmp, path); lstrcatA(tmp, ".tmp");
        f = CreateFileA(tmp, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
        if (f != INVALID_HANDLE_VALUE) {
            wr(f, "{\"messages\":[");
            if (in.n) WriteFile(f, in.p, (DWORD)in.n, &wn, NULL);
            wr(f, "],\"gameSent\":[");
            if (out.n) WriteFile(f, out.p, (DWORD)out.n, &wn, NULL);
            wr(f, "]}");
            CloseHandle(f);
            MoveFileExA(tmp, path, MOVEFILE_REPLACE_EXISTING);
        }
    }
    if (in.p) { SecureZeroMemory(in.p, in.cap); HeapFree(GetProcessHeap(), 0, in.p); }
    if (out.p) { SecureZeroMemory(out.p, out.cap); HeapFree(GetProcessHeap(), 0, out.p); }
    lg("store: %d received and %d sent line(s) from the message store (home%02d%s)%s\r\n", nin, nout, g_home,
       nh > 1 ? " and home84" : "", left ? ", the oldest left out (too big)" : "");
}

/* Provenance's own numbering (POL_HOME), from 10 so the viewer's 01-08 are never touched, and never 84 */
static int home_slot_ok(int s) { return s >= 10 && s <= 99 && s != LEGACY_HOME; }
static int home_from_env(void)
{
    char v[8];
    int s;
    DWORD n = GetEnvironmentVariableA("POL_HOME", v, sizeof v);
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
/* the account's own slot, or home84 when Provenance didn't send one (a manual run) */
static void home_pick(BYTE *b)
{
    int slot = home_from_env();
    if (!g_viewer[0] || slot < 0) {
        lg("home: no user slot from Provenance - staying in home%02d\r\n", g_home);
        return;
    }
    if (!home_set(b, slot)) return;
    lg("home: user slot %d (pub\\home%02d)\r\n", slot, slot);
    if (legacy_home_there()) home_copy(b, slot, SVC(0x558) ? my_id(b) : 0);
}

static char g_send_token[48];      /* the snapshot's "send", the newest message and its state */
static char g_send_state[48] = "";
/* built messages in the order they were built, job -1 until the PUT starts. Two at once, the viewer's own most */
#ifndef SEND_MAX
#define SEND_MAX 2
#endif
typedef struct { BYTE *msg; int job; ULONGLONG started; char token[48]; } sendrec;
static sendrec g_sq[SEND_MAX];
static int g_sq_n;
static int msg_jobs(void)
{
    int n = g_del_job >= 0, i;
    for (i = 0; i < MAIL_LANES; i++) n += g_rd[i].job >= 0;
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
#ifndef MSG_JOBS_MAX
#define MSG_JOBS_MAX 3
#endif
static int msg_slot_ok(BYTE *b) { return msg_jobs() < MSG_JOBS_MAX && slots_free(b) >= 2; }
static int send_built(void)
{
    for (int i = 0; i < g_sq_n; i++) if (g_sq[i].job < 0) return 1;
    return 0;
}

/* manual runs keep requests in %TEMP% where anything of the user's can write one, so sends are capped. The gap
   after each send is for a back-off build */
#ifndef PACE_PER_MIN
#define PACE_PER_MIN 40
#endif
#ifndef PACE_PER_HOUR
#define PACE_PER_HOUR 600
#endif
#ifndef PACE_GAP_MS
#define PACE_GAP_MS 0
#endif
enum { PACE_OK, PACE_GAP, PACE_MIN, PACE_HOUR, PACE_HELD, PACE_STOPPED };
typedef struct {
    ULONGLONG take[PACE_PER_HOUR];   /* ring of PUT start times */
    int next, count;
    ULONGLONG done, hold, failed;    /* the last result, no PUT before hold, when the last failure landed */
    int fails, stopped;              /* failed sends in a row, no more sends this session */
} send_pace;
static send_pace g_pace;
static int pace_check(const send_pace *p, ULONGLONG now)
{
    if (p->stopped) return PACE_STOPPED;
    if (now < p->hold) return PACE_HELD;
    if (PACE_GAP_MS && p->done && now - p->done < PACE_GAP_MS) return PACE_GAP;
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
    p->done = now;
    if (r == 1) p->fails = 0;
    /* -0x153a suspended, -0x1540 not authorised: that's the account, not the line */
    else if (r == -0x153a || r == -0x1540) p->stopped = 1;
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
static struct { char token[48]; char state[48]; DWORD time, seq; } g_send_hist[SEND_HIST];
static int g_send_hist_next;
static void send_note(const char *token, const char *state)
{
    if (!lstrcmpA(token, g_send_token)) lstrcpynA(g_send_state, state, sizeof g_send_state);
}
/* hdr is the message that went, its header time and seq let the app put my line where the friend sees it */
static void send_result(const char *token, const char *state, const BYTE *hdr)
{
    send_note(token, state);
    lstrcpynA(g_send_hist[g_send_hist_next].token, token, sizeof g_send_hist[0].token);
    lstrcpynA(g_send_hist[g_send_hist_next].state, state, sizeof g_send_hist[0].state);
    g_send_hist[g_send_hist_next].time = hdr ? *(const DWORD *)(hdr + 0x34) : 0;
    g_send_hist[g_send_hist_next].seq = hdr ? *(const DWORD *)(hdr + 0x30) : 0;
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
    if (mySlot < 0) { send_fail("failed:no handle"); return; }
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

static char g_edit_token[48];
static char g_edit_state[48] = "";
static int g_edit_job = -1;
static int g_edit_kind;   /* 1 list upload, 2 comment, 3 upload that became a reload */
static int g_edit_upload_want;
static WCHAR g_comment_want[0x33];
static int g_comment_wanted;
static ULONGLONG g_edit_last, g_edit_started;
typedef int (__cdecl *svc_add)(int, unsigned, unsigned, unsigned, unsigned, const char *);

#define edit_fail(why) refuse(g_edit_state, sizeof g_edit_state, "friends: edit refused - %s\r\n", why)
/* like the viewer, a local edit (svc 0x5f4/0x2f0/0x2e4/0x2e8) marks the row, then one list upload sends it */
static void friend_upload_start(BYTE *b)
{
    int reload = *(unsigned *)(b + 0x403040) > *(unsigned *)(b + 0x40304c);
    int t = ((svc_v)SVC(0x1028))();
    if (t == NO_SLOT) { g_edit_upload_want = 1; return; }
    g_edit_upload_want = 0;
    lg("friends: list upload start -> %d%s\r\n", t, reload ? " (the server list is newer: reload)" : "");
    if (t < 0) { wsprintfA(g_edit_state, "failed:%d", t); g_snap_dirty = 1; return; }
    g_edit_job = t;
    g_edit_started = GetTickCount64();
    g_edit_kind = reload ? 3 : 1;
    lstrcpyA(g_edit_state, "saving");
    g_snap_dirty = 1;
}
static void comment_start(BYTE *b)
{
    int t = ((int (__cdecl *)(int, const WCHAR *))SVC(0x67c))(-2, g_comment_want[0] ? g_comment_want : NULL);
    if (t == NO_SLOT) return;
    g_comment_wanted = 0;
    lg("friends: my comment start -> %d\r\n", t);
    if (t < 0) { wsprintfA(g_edit_state, "failed:%d", t); g_snap_dirty = 1; return; }
    g_edit_job = t;
    g_edit_started = GetTickCount64();
    g_edit_kind = 2;
    lstrcpyA(g_edit_state, "saving");
    g_snap_dirty = 1;
}
/* UTF-8 -> a comment of 0x31 UTF-16 chars at most (into a zeroed out), control chars as spaces, never half a
   surrogate pair. 0 = not valid UTF-8. */
static int comment_text(const char *s, WCHAR *out)
{
    static WCHAR wide[0x400];
    int i;
    if (MultiByteToWideChar(CP_UTF8, 0, s, -1, wide, 0x400) <= 0) return 0;
    for (i = 0; i < 0x31 && wide[i]; i++) out[i] = wide[i] < 0x20 ? L' ' : wide[i];
    if (i == 0x31 && out[i - 1] >= 0xd800 && out[i - 1] < 0xdc00) out[i - 1] = 0;
    return 1;
}
static void friend_edit_request(BYTE *b)
{
    char buf[1024], *p, *op, *tok, *rows, *key, *arg, k[17], nm[16];
    WCHAR old[0x34];
    int row, r;
    BYTE fr[0x140];
    if (GetTickCount64() - g_edit_last < 2000) return;
    if (g_answer_kind) return;
    if (take_request("friend", buf, sizeof buf) < 0) {
        if (take_request("comment", buf, sizeof buf) < 0) return;
        g_edit_last = GetTickCount64();
        p = buf;
        tok = req_line(&p);
        lstrcpynA(g_edit_token, tok, sizeof g_edit_token);
        /* no token line means it isn't the launcher's, and a stray empty file mustn't clear the comment */
        if (!tok[0] || p == tok + lstrlenA(tok)) { edit_fail("failed:bad request"); return; }
        if (notice_blocks(g_notice)) { edit_fail("failed:account notice"); return; }
        if (g_my_slot < 0) { edit_fail("failed:no handle"); return; }
        { char *e = p + lstrlenA(p); while (e > p && (e[-1] == '\n' || e[-1] == '\r')) *--e = 0; }
        memset(g_comment_want, 0, sizeof g_comment_want);
        if (*p && !comment_text(p, g_comment_want)) { edit_fail("failed:bad text"); return; }
        memset(old, 0, sizeof old);
        ((int (__cdecl *)(int, WCHAR *))SVC(0x688))(-2, old);
        lg("friends: my comment was %04x %04x %04x (%d chars), new %d chars\r\n",
           old[0], old[1], old[2], lstrlenW(old), lstrlenW(g_comment_want));
        g_comment_wanted = 1;
        comment_start(b);
        return;
    }
    g_edit_last = GetTickCount64();
    p = buf;
    op = req_line(&p); tok = req_line(&p); rows = req_line(&p); key = req_line(&p); arg = req_line(&p);
    lstrcpynA(g_edit_token, tok, sizeof g_edit_token);
    if (!parse_num(rows, 4, &row)) { edit_fail("failed:bad request"); return; }
    if (!g_friends_loaded) { edit_fail("failed:friends not loaded"); return; }
    if (notice_blocks(g_notice)) { edit_fail("failed:account notice"); return; }
    /* <key> is the row's snapshot key, so an edit never lands on a row that changed */
    if (lstrcmpA(op, "unignore") == 0) {
        memset(fr, 0, sizeof fr);
        if (row >= 100 || ((svc_ip)SVC(0x2a4))(row, fr) < 0 || !(*(DWORD *)(fr + 0x98) & 1)) {
            edit_fail("failed:no such entry"); return;
        }
        friend_key(b, fr, k);
        if (lstrcmpA(k, key) != 0) { edit_fail("failed:list changed"); return; }
        r = ((svc_i)SVC(0x2e8))(row);
        lg("friends: unignore index %d -> %d\r\n", row, r);
        if (r < 0) { wsprintfA(g_edit_state, "failed:%d", r); g_snap_dirty = 1; return; }
        friend_upload_start(b);
        return;
    }
    if (row >= 200 || !get_friend(b, row, fr)) { edit_fail("failed:no such friend"); return; }
    friend_key(b, fr, k);
    if (lstrcmpA(k, key) != 0) { edit_fail("failed:friend list changed"); return; }
    if ((*(DWORD *)(fr + 0x08) & 0x10000000) && lstrcmpA(op, "delete") != 0) { edit_fail("failed:pending"); return; }
    if (lstrcmpA(op, "rename") == 0) {
        if (!ascii_name_ok(arg)) { edit_fail("failed:bad name"); return; }
        if (lstrcmpA((const char *)fr + 0xa0, arg) == 0) { lstrcpyA(g_edit_state, "saved"); g_snap_dirty = 1; return; }
        memset(fr + 0xa0, 0, 0x10);
        lstrcpynA((char *)fr + 0xa0, arg, 16);
        ((void (__cdecl *)(int, void *))SVC(0x5f4))(row, fr);
        lg("friends: rename row %d\r\n", row);
    } else if (lstrcmpA(op, "delete") == 0) {
        r = ((svc_i)SVC(0x2f0))(row);
        lg("friends: delete row %d -> %d\r\n", row, r);
        if (r < 0) { wsprintfA(g_edit_state, "failed:%d", r); g_snap_dirty = 1; return; }
    } else if (lstrcmpA(op, "ignore") == 0) {
        if (is_me(b, *(DWORD *)(fr + 0x00), *(DWORD *)(fr + 0x04))) { edit_fail("failed:self"); return; }
        memcpy(nm, fr + 0xa0, 15); nm[15] = 0;
        /* AddBlock takes them off the friend list as well (polcore.dll 0x10024ad0) */
        r = ((svc_add)SVC(0x2e4))(-1, arg[0] == '1' ? 1u : 0u, *(DWORD *)(fr + 0x00), *(DWORD *)(fr + 0x04),
                                  FR_SLOT(fr), nm);
        lg("friends: ignore row %d (%s) -> %d\r\n", row, arg[0] == '1' ? "every handle" : "this handle", r);
        if (r < 0) { wsprintfA(g_edit_state, "failed:%d", r); g_snap_dirty = 1; return; }
    } else {
        edit_fail("failed:bad request");
        return;
    }
    g_snap_dirty = 1;
    friend_upload_start(b);
}
static void friend_edit_pump(BYTE *b, int ctl)
{
    if (g_edit_job < 0) {
        if (g_edit_upload_want) friend_upload_start(b);   /* applied locally already, so it still goes in a drain */
        else if (g_comment_wanted) comment_start(b);
        else if (ctl && !g_draining) friend_edit_request(b);
        return;
    }
    /* a comment (svc 0x67c) is polled with 0x684. Not 0x680, however much it looks like it should be */
    int r = ((svc_i)SVC(g_edit_kind == 2 ? 0x684 : 0x102c))(g_edit_job);
    if (r == 0) {
        if (GetTickCount64() - g_edit_started < 120000) return;
        CANCEL_JOB(g_edit_job);
        r = -0x104;
    }
    g_edit_job = -1;
    if (r == 1) lstrcpyA(g_edit_state, g_edit_kind == 3 ? "failed:reloaded" : "saved");
    else wsprintfA(g_edit_state, "failed:%d", r);
    lg("friends: %s done -> %d\r\n", g_edit_kind == 2 ? "my comment" : "list upload", r);
    g_snap_dirty = 1;
}

static pdb_item *pdb_get(BYTE *b, pdb_schema *s, const char *name, pdb_rec *rec)
{
    return ((svc49c_t)SVC(0x49c))(s, name, rec);
}

/* a search, a friend's profile, a character lookup, an FFXI profile, my own profile and a picture save all go to the
   profile server, so they share the 3 s gap and go one at a time, never more than the viewer has going */
static ULONGLONG g_db_last;
static int g_srch_step;   /* 0 idle, 1 start waits, 2 search job, 3 fetch waits, 4 fetch job */
static int g_prof_step, g_ch_step, g_fp_step;   /* 0 idle, 1 load running, 2 start waits for a slot */
static int g_pic_want;
static int g_cr_db;   /* the chat rooms' zone and room lists, pol_chat.c */
static int db_free(void)
{
    return GetTickCount64() - g_db_last >= 3000 && !g_srch_step && !g_prof_step && !g_ch_step && !g_fp_step &&
           !g_cr_db && (g_me_step < 1 || g_me_step > 5) && g_pic_want < 0;
}

/* prof_001, the FFXI table (title 1). z_phead's what a search hit gets added from */
static pdb_schema *g_ch_pib;
static int ch_check_schema(BYTE *b, pdb_schema *s)
{
    static const struct { const char *n; BYTE type; WORD size; } want[] = {
        { "z_ctsid", 6, 4 }, { "z_ctid", 8, 8 }, { "z_name", 1, 0 }, { "z_worldname", 1, 0 }, { "z_phead", 9, 0 },
    };
    int i, idx;
    if (s->table_id != 1) return 0;
    for (i = 0; i < (int)(sizeof want / sizeof want[0]); i++) {
        const pdb_field *f = fs_field(s, want[i].n, &idx);
        if (!f || f->type != want[i].type || (want[i].size && f->size != want[i].size)) return 0;
        if (((svc4bc_t)SVC(0x4bc))(s, want[i].n) != idx) return 0;
    }
    if (0x17 + (DWORD)s->nfields * 0x10 + s->rec_raw > s->rec_size + 8) return 0;
    return 1;
}
static pdb_schema *ch_load_pib(BYTE *b)
{
    if (!g_ch_pib) g_ch_pib = pib_load(b, L"prof_001.pib", "prof_001", ch_check_schema);
    return g_ch_pib;
}
/* cut at whole SJIS characters, never between a lead byte and its trail */
static void sjis_copy(char *out, const char *src, int cap)
{
    int i = 0, n;
    while (src[i]) {
        BYTE c = (BYTE)src[i];
        n = ((c >= 0x81 && c <= 0x9f) || (c >= 0xe0 && c <= 0xfc)) && src[i + 1] ? 2 : 1;
        if (i + n > cap - 1) break;
        i += n;
    }
    memcpy(out, src, (size_t)i);
    out[i] = 0;
}

/* the viewer's page is 100, and at 1000 or more it doesn't fetch at all */
#define SEARCH_MAX 100
#define SEARCH_TOO_MANY 1000
enum { SK_HANDLE, SK_HID, SK_CHARACTER };
static const char *const g_srch_kinds[] = { "handle", "hid", "character" };
/* name is z_phead's handle name, what AddFriend gets whatever the kind */
typedef struct {
    DWORD lo, hi;
    int slot;
    char name[16], chname[16], world[16], key[17];
    unsigned pic;
    BYTE isfriend, me;
} srchrec;
static srchrec g_srch[SEARCH_MAX];
static int g_srch_n, g_srch_kind, g_srch_total, g_srch_many;
static int g_srch_job = -1;
static char g_srch_token[48], g_srch_state[32] = "";
static __declspec(align(8)) BYTE g_srch_cond[0x18];
static char g_srch_clause[64];
static BYTE *g_srch_buf;
static pdb_schema *g_srch_pib;
static DWORD g_srch_set, g_srch_want;
static ULONGLONG g_srch_last, g_srch_t0;
static int g_srch_marked;
static DWORD g_srch_marked_for;

static void search_done(const char *state)
{
    lstrcpynA(g_srch_state, state, sizeof g_srch_state);
    if (g_srch_buf) { HeapFree(GetProcessHeap(), 0, g_srch_buf); g_srch_buf = NULL; }
    g_srch_step = 0;
    g_srch_job = -1;
    g_srch_marked = 0;
    lg("friends: search %s (%d hits)\r\n", state, g_srch_n);
    g_snap_dirty = 1;
}
/* polcore can still write into a cancelled read, so its buffer's never freed (the mail reads too). A small leak
   beats polcore going ham over freed memory */
static void search_abandon(BYTE *b, const char *state)
{
    if (g_srch_job >= 0 && (g_srch_step == 2 || g_srch_step == 4))
        ((svc_i)SVC(0x4dc))(g_srch_job);
    g_srch_buf = NULL;
    search_done(state);
}
static void search_start(BYTE *b)
{
    int t = ((int (__cdecl *)(void *, void *, void *))SVC(0x4c0))(g_srch_pib, g_srch_cond, NULL);
    if (t == NO_SLOT) { g_srch_step = 1; return; }
    lg("friends: search start -> %d\r\n", t);
    if (t < 0) { search_done(failed(t)); return; }
    g_srch_job = t;
    g_srch_step = 2;
    g_srch_t0 = GetTickCount64();
}
static void search_fetch(BYTE *b)
{
    int r = ((int (__cdecl *)(int, void *, int, int, void *))SVC(0x4c8))((int)g_srch_set, g_srch_pib, 0,
                                                                        (int)g_srch_want, g_srch_buf);
    if (r == NO_SLOT) { g_srch_step = 3; return; }
    lg("friends: search fetching %u -> %d\r\n", g_srch_want, r);
    if (r < 0) { search_done(failed(r)); return; }
    g_srch_job = r;
    g_srch_step = 4;
    g_srch_t0 = GetTickCount64();
}
/* an H-ID as the viewer shows it (00-1234567) or just the number: '-' and spaces go, then 1-13 digits */
static int hid_parse(const char *s, unsigned __int64 *out)
{
    unsigned __int64 v = 0;
    int d = 0;
    if (lstrlenA(s) > 20) return 0;
    for (; *s; s++) {
        if (*s == '-' || *s == ' ') continue;
        if (*s < '0' || *s > '9' || ++d > 13) return 0;
        v = v * 10 + (unsigned)(*s - '0');
    }
    *out = v;
    return d > 0 && v >= 1 && v <= 0xfffffffffffULL;
}
static int ffxi_name_ok(const char *s)
{
    int n = 0;
    for (; *s; s++, n++)
        if (!((*s >= 'A' && *s <= 'Z') || (*s >= 'a' && *s <= 'z'))) return 0;
    return n >= 1 && n <= 15;
}
static void search_request(BYTE *b)
{
    char buf[256], *p, *tok, *kind, *val, dec[24];
    unsigned __int64 hid = 0;
    int k = 0;
    if (GetTickCount64() - g_srch_last < 5000 || !db_free()) return;
    if (take_request("search", buf, sizeof buf) < 0) return;
    g_srch_last = g_db_last = GetTickCount64();
    p = buf;
    tok = req_line(&p);
    kind = req_line(&p);
    val = req_line(&p);
    lstrcpynA(g_srch_token, tok, sizeof g_srch_token);
    g_srch_n = 0;
    g_srch_total = 0;
    g_srch_many = 0;
    while (k < 3 && lstrcmpA(kind, g_srch_kinds[k]) != 0) k++;
    g_srch_kind = k < 3 ? k : SK_HANDLE;
    if (k == 3) { search_done("failed:bad request"); return; }
    if (notice_blocks(g_notice)) { search_done("failed:account notice"); return; }
    if (k == SK_HANDLE) {
        if (!ascii_name_ok(val)) { search_done("failed:bad name"); return; }
        /* a name with a ' never goes out in a handle search (ascii_name_ok keeps 0x04 out) */
        if (strchr(val, '\'')) { search_done("failed:quote"); return; }
    } else if (k == SK_HID) {
        if (!hid_parse(val, &hid)) { search_done("failed:bad hid"); return; }
    } else if (!ffxi_name_ok(val)) {
        search_done("failed:bad character");
        return;
    }
    g_srch_pib = k == SK_CHARACTER ? ch_load_pib(b) : fs_load_pib(b);
    if (!g_srch_pib) { search_done("failed:no schema"); return; }
    memset(g_srch_cond, 0, sizeof g_srch_cond);
    /* the clauses are the viewer's, from the .pfb files. The H-ID goes back out as a plain number like svc 0x784
       leaves it, and INITCAP is how FFXI names are kept */
    if (k == SK_HANDLE) {
        wsprintfA(g_srch_clause, "z_up_name=UPPER(\x04%s\x04)", val);
    } else if (k == SK_HID) {
        int n = 0, i;
        char t[24];
        do { t[n++] = (char)('0' + (int)(hid % 10)); hid /= 10; } while (hid);
        for (i = 0; i < n; i++) dec[i] = t[n - 1 - i];
        dec[n] = 0;
        wsprintfA(g_srch_clause, "z_hid=%s", dec);
    } else {
        wsprintfA(g_srch_clause, "z_name=INITCAP(\x04%s\x04)", val);
    }
    ((void (__cdecl *)(void *, int, const char *))SVC(0x4a4))(g_srch_cond, 7, g_srch_clause);
    lg("friends: search %s \"%s\"\r\n", g_srch_kinds[k], val);
    lstrcpyA(g_srch_state, "searching");
    g_snap_dirty = 1;
    search_start(b);
}
static void search_pump(BYTE *b, int ctl)
{
    int r, guard = 0;
    if (g_srch_step == 0) {
        if (ctl && !g_draining) search_request(b);
        return;
    }
    if (g_srch_step == 1) { if (!g_draining) search_start(b); else search_done("cancelled"); return; }
    if (g_srch_step == 3) { if (!g_draining) search_fetch(b); else search_done("cancelled"); return; }
    if (g_srch_step == 2) {
        DWORD out[3] = { 0, 0, 0 };
        r = ((int (__cdecl *)(int, DWORD *))SVC(0x4e0))(g_srch_job, out);
        if (r == 0) {
            if (GetTickCount64() - g_srch_t0 >= 120000) search_abandon(b, "failed:timeout");
            return;
        }
        g_srch_job = -1;
        if (r == -0x14cc) { search_done("done"); return; }   /* "No matching results were found." */
        if (r < 0) { search_done(failed(r)); return; }
        if ((int)out[0] < 0) { search_done("done"); return; }
        lg("friends: search %d hits%s\r\n", (int)out[1], out[2] ? " (too many)" : "");
        g_srch_total = (int)out[1] > 0 ? (int)out[1] : 0;
        /* the viewer goes by the too-many flag before it looks at the count */
        if (out[2] || (int)out[1] >= SEARCH_TOO_MANY) { g_srch_many = 1; search_done("done"); return; }
        if ((int)out[1] <= 0) { search_done("done"); return; }
        g_srch_set = out[0];
        g_srch_want = out[1] > SEARCH_MAX ? SEARCH_MAX : out[1];
        g_srch_buf = (BYTE *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY,
                                       (SIZE_T)g_srch_pib->rec_size * g_srch_want + 64);
        if (!g_srch_buf) { search_done("failed:alloc"); return; }
        search_fetch(b);
        return;
    }
    r = ((svc_i)SVC(0x4cc))(g_srch_job);
    if (r == 0) {
        if (GetTickCount64() - g_srch_t0 >= 120000) search_abandon(b, "failed:timeout");
        return;
    }
    g_srch_job = -1;
    if (r < 0) { search_done(failed(r)); return; }
    for (BYTE *rec = g_srch_buf; rec && g_srch_n < SEARCH_MAX && guard++ < SEARCH_MAX * 2; rec = *(BYTE **)rec) {
        pdb_item *ph, *it;
        BYTE *fr;
        srchrec *h = &g_srch[g_srch_n];
        if (!*(DWORD *)(rec + 0xc)) continue;
        ph = pdb_get(b, g_srch_pib, "z_phead", (pdb_rec *)rec);
        if (!ph || !(ph->flags & 1) || !(fr = (BYTE *)ph->v.bytes.p) || IsBadReadPtr(fr, 0xb0)) continue;
        if (!(*(DWORD *)(fr + 0x98) & 1)) continue;
        memset(h, 0, sizeof *h);
        h->lo = *(DWORD *)(fr + 0x00);
        h->hi = *(DWORD *)(fr + 0x04);
        h->slot = (int)FR_SLOT(fr);
        memcpy(h->name, fr + 0xa0, 15);
        if (g_srch_kind == SK_CHARACTER) {
            /* prof_001 has no profile picture. The world stays out of a Private record */
            it = pdb_get(b, g_srch_pib, "z_name", (pdb_rec *)rec);
            if (it && (it->flags & 1) && it->v.str && !IsBadStringPtrA(it->v.str, 0x40))
                sjis_copy(h->chname, it->v.str, sizeof h->chname);
            it = pdb_get(b, g_srch_pib, "z_worldname", (pdb_rec *)rec);
            if (((pdb_rec *)rec)->level >= 1 && it && (it->flags & 1) && it->v.str && !IsBadStringPtrA(it->v.str, 0x40))
                sjis_copy(h->world, it->v.str, sizeof h->world);
        } else {
            it = pdb_get(b, g_srch_pib, "z_ficon", (pdb_rec *)rec);
            h->pic = it && (it->flags & 1) ? it->v.u32 : 0;
        }
        g_srch_n++;
    }
    search_done("done");
}

#pragma pack(push, 1)
typedef struct pfb_ent  { DWORD code; char *text; } pfb_ent;
typedef struct pfb_tab  { WORD n; WORD pad; pfb_ent *e; } pfb_tab;
typedef struct pfb_item { char *label; BYTE field, ui, r6, r7; DWORD r8; pfb_tab *tab; DWORD r10; } pfb_item;
typedef struct pfb_hdr  { BYTE nItems, nConds, nTabs, relocated; pfb_item *items; void *conds; pfb_tab *tabs;
                          DWORD r10, r14; WORD nBlocks; BYTE r1a, encrypted; char *title; } pfb_hdr;
#pragma pack(pop)
typedef char pf_chk_item[sizeof(pfb_item) == 0x14 ? 1 : -1];
typedef char pf_chk_hdr[sizeof(pfb_hdr) == 0x20 ? 1 : -1];

static pfb_hdr *g_pfb;
static pfb_hdr *pf_load(BYTE *b)
{
    WCHAR path[0x200 + 32];
    DWORD sz, got = 0;
    if (g_pfb) return g_pfb;
    memset(path, 0, sizeof path);
    if (((svc16c4_t)SVC(0x16c4))(0x14, path, 0x200, 0) <= 0) return NULL;   /* "<root>\data\db\" */
    path[0x200 - 1] = 0;
    lstrcatW(path, L"prof_pol.pfb");
    HANDLE f = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (f == INVALID_HANDLE_VALUE) return NULL;
    sz = GetFileSize(f, NULL);
    if (sz < 0x20 || sz > 0x100000) { CloseHandle(f); return NULL; }
    BYTE *buf = (BYTE *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sz + 16);
    if (!buf) { CloseHandle(f); return NULL; }
    if (!ReadFile(f, buf, sz, &got, NULL) || got != sz || buf[3] != 0) {
        CloseHandle(f);
        HeapFree(GetProcessHeap(), 0, buf);
        return NULL;
    }
    CloseHandle(f);
    g_pfb = ((pfb_hdr *(__cdecl *)(void *))SVC(0x498))(buf);
    if (g_pfb) lg("profile: prof_pol.pfb loaded, %u items, %u tables\r\n", g_pfb->nItems, g_pfb->nTabs);
    return g_pfb;
}
static const pfb_item *pf_item(const pfb_hdr *p, int field)
{
    for (int i = 0; i < p->nItems; i++) if (p->items[i].field == field) return &p->items[i];
    return NULL;
}
static int pf_find(const pfb_tab *t, DWORD v)
{
    if (!t) return -1;
    for (int i = 0; i < t->n; i++) if (t->e[i].code == v) return i;
    return -1;
}
/* level k (0..2) of entry i, an empty level taken from the nearest entry before it (app.dll 0x102264a9) */
static void pf_part(const pfb_tab *t, int i, int k, char *out, int cap)
{
    out[0] = 0;
    for (; i >= 0; i--) {
        const char *x = t->e[i].text;
        int j;
        if (!x) continue;
        for (j = 0; j < k && *x; x++) if (*x == '\n') j++;
        if (*x == '\n' || !*x) continue;
        for (j = 0; x[j] && x[j] != '\n' && j < cap - 1; j++) out[j] = x[j];
        out[j] = 0;
        return;
    }
}
static void pf_last(const char *x, char *out, int cap)
{
    const char *best = x, *q;
    int j;
    out[0] = 0;
    if (!x) return;
    for (q = x; *q; q++) if (*q == '\n' && q[1] && q[1] != '\n') best = q + 1;
    for (j = 0; best[j] && best[j] != '\n' && j < cap - 1; j++) out[j] = best[j];
    out[j] = 0;
}
enum { PV_OK, PV_PRIVATE, PV_NOTSET, PV_UNDER10 };
static int pf_value(BYTE *b, pdb_schema *s, pfb_hdr *p, pdb_rec *rec, const char *name, int V,
                    char out[3][0x80], int *lines)
{
    int idx = ((svc4bc_t)SVC(0x4bc))(s, name), e;
    DWORD v = 0;
    pdb_item *it = pdb_get(b, s, name, rec);
    int header = !strcmp(name, "z_name") || !strcmp(name, "z_mail");
    *lines = 1;
    out[0][0] = out[1][0] = out[2][0] = 0;
    if (idx < 0 || idx >= s->nfields || !it || rec->level < V) return PV_PRIVATE;
    if (!header && (it->level == 0 || ((s->fields[idx].flags & 0x40) && it->level < V))) return PV_PRIVATE;
    const pfb_item *pi = pf_item(p, idx);
    if (!strcmp(name, "z_area")) {
        pdb_item *c = pdb_get(b, s, "z_cntry", rec), *w = pdb_get(b, s, "z_wide", rec);
        pdb_item *l = pdb_get(b, s, "z_local", rec);
        BYTE a = it->v.bytes.p ? *(BYTE *)it->v.bytes.p : 0;
        if (!a || !c || !w || !l || !c->v.bytes.p || !w->v.bytes.p || !l->v.bytes.p) return PV_NOTSET;
        v = (DWORD)a << 24 | (DWORD)*(BYTE *)c->v.bytes.p << 16 | (DWORD)*(BYTE *)w->v.bytes.p << 8 |
            *(BYTE *)l->v.bytes.p;
        if (!pi || (e = pf_find(pi->tab, v)) < 0) return PV_NOTSET;
        pf_part(pi->tab, e, 0, out[0], 0x80);
        pf_part(pi->tab, e, 1, out[1], 0x80);
        pf_part(pi->tab, e, 2, out[2], 0x80);
        *lines = 3;
        return PV_OK;
    }
    if (!strncmp(name, "z_fav", 5)) {
        if (!(v = it->v.u16) || !pi || (e = pf_find(pi->tab, v)) < 0) return PV_NOTSET;
        pf_part(pi->tab, e, 0, out[0], 0x80);
        pf_part(pi->tab, e, 1, out[1], 0x80);
        *lines = 2;
        return PV_OK;
    }
    if (!strcmp(name, "z_age")) {
        pdb_item *f = pdb_get(b, s, "z_age_f", rec);
        v = it->v.u32;
        if (v < 1 || v > 999) return PV_NOTSET;
        if (!f || !f->v.bytes.p || !*(BYTE *)f->v.bytes.p) wsprintfA(out[0], "%u", v);
        else if (v < 10) return PV_UNDER10;
        else wsprintfA(out[0], "%u0's", v / 10);
        return PV_OK;
    }
    switch (s->fields[idx].type) {
    case 1: if (it->v.str && !IsBadStringPtrA(it->v.str, 0x140)) lstrcpynA(out[0], it->v.str, 0x80); break;
    case 2: v = it->v.bytes.p ? *(BYTE *)it->v.bytes.p : 0; break;
    case 3: case 4: v = it->v.u16; break;
    case 5: case 6: v = it->v.u32; break;
    }
    if (s->fields[idx].type != 1) {
        if (pi && (pi->ui == 5 || pi->ui == 6)) {
            if ((e = pf_find(pi->tab, v)) < 0) return PV_NOTSET;
            pf_last(pi->tab->e[e].text, out[0], 0x80);
        } else wsprintfA(out[0], "%lu", v);
    }
    return out[0][0] ? PV_OK : PV_NOTSET;
}
/* POL's Shift-JIS (with the Latin extension the EU tables use) -> UTF-8 through svc 0xa38, cut at u8cap -> JSON */
static void pol_json(BYTE *b, const char *sj, int u8cap, char *js, int jscap)
{
    static WCHAR w[0x420];
    static char u8[0x600];
    char tmp[0x400];
    u8[0] = 0;
    if (sj && sj[0]) {
        lstrcpynA(tmp, sj, sizeof tmp);                 /* svc 0xa38 has no bound, so the input gets capped */
        memset(w, 0, sizeof w);
        ((int (__cdecl *)(WCHAR *, const char *, int))SVC(0xa38))(w, tmp, 0x3013);
        w[0x41f] = 0;
        if (WideCharToMultiByte(CP_UTF8, 0, w, -1, u8, u8cap - 1, NULL, NULL) <= 0) u8[0] = 0;
        u8[u8cap - 1] = 0;
    }
    json_text(js, jscap, u8);
}

static pdb_cond g_prof_cond;
static int g_prof_job = -1, g_prof_row;
static unsigned __int64 g_prof_hid;
static char g_prof_token[48], g_prof_state[32] = "";
static char *g_prof_json;
static pdb_rec *g_prof_rec;
static ULONGLONG g_prof_t0;
/* game slots (z_phead, or the friend row) from the last 16 profiles read, by H-ID. The app shows a profile again from
   its own copy and the tabs still need them */
#define PROF_SLOTS_KEPT 16
static struct { unsigned __int64 hid; BYTE slots[8 * 0x10]; } g_prof_slots[PROF_SLOTS_KEPT];
static int g_prof_slots_next;
static const BYTE *prof_slots_for(unsigned __int64 hid)
{
    for (int i = 0; hid && i < PROF_SLOTS_KEPT; i++) if (g_prof_slots[i].hid == hid) return g_prof_slots[i].slots;
    return NULL;
}
/* NULL forgets the handle's */
static void prof_slots_keep(unsigned __int64 hid, const BYTE *src)
{
    int i = 0;
    if (!hid) return;
    while (i < PROF_SLOTS_KEPT && g_prof_slots[i].hid != hid) i++;
    if (i == PROF_SLOTS_KEPT) {
        if (!src) return;
        i = g_prof_slots_next;
        g_prof_slots_next = (g_prof_slots_next + 1) % PROF_SLOTS_KEPT;
    }
    g_prof_slots[i].hid = src ? hid : 0;
    if (src) memcpy(g_prof_slots[i].slots, src, sizeof g_prof_slots[i].slots);
}

/* H-ID = friend record +0x98 bits 1..44 */
static unsigned __int64 fr_hid(const BYTE *fr)
{
    return ((unsigned __int64)((*(const DWORD *)(fr + 0x9c) >> 1) & 0xfff) << 32) |
           ((*(const DWORD *)(fr + 0x98) >> 1) | (*(const DWORD *)(fr + 0x9c) << 31));
}

static void profile_done(const char *state)
{
    lstrcpynA(g_prof_state, state, sizeof g_prof_state);
    if (g_prof_rec) { HeapFree(GetProcessHeap(), 0, g_prof_rec); g_prof_rec = NULL; }
    g_prof_step = 0;
    g_prof_job = -1;
    lg("profile: %s\r\n", state);
    g_snap_dirty = 1;
}
static void profile_abandon(BYTE *b, const char *state)
{
    if (g_prof_step == 1 && g_prof_job >= 0) ((svc4dc_t)SVC(0x4dc))(g_prof_job);
    g_prof_rec = NULL;   /* a cancelled read can still write, so never freed */
    profile_done(state);
}
static void profile_build(BYTE *b)
{
    static const char *aa[] = { "z_aa_mt", "z_aa_dt", "z_aa_t", "z_aa_m", "z_aa_c", "z_aa_w", "z_aa_d", "z_aa_e" };
    static char lines[3][0x80], js[0xc00], t[0x400], f[0x400];
    pdb_schema *s = g_fs_pib;
    pfb_hdr *p = g_pfb;
    char hid[0x24], *q;
    int o = 0, i, k, first = 1, n, st, set = 0, r;
    const int cap = 0x8000;
    pdb_item *it;
    if (!g_prof_json) g_prof_json = (char *)HeapAlloc(GetProcessHeap(), 0, cap);
    if (!g_prof_json) return;
#define PJ(...) do { if (o < cap - 0x1000) o += wsprintfA(g_prof_json + o, __VA_ARGS__); } while (0)
/* wsprintfA stops at 1024 chars, so long strings (the away text) get appended as they are */
#define PS(str) \
    do { int _l = lstrlenA(str); if (o + _l < cap - 16) { memcpy(g_prof_json + o, (str), _l); o += _l; } } while (0)
    st = pf_value(b, s, p, g_prof_rec, "z_name", 1, lines, &n);
    pol_json(b, lines[0], 0x600, js, sizeof js);
    PJ("\"name\":%s,\"nameState\":%d", js, st);
    st = pf_value(b, s, p, g_prof_rec, "z_mail", 1, lines, &n);
    pol_json(b, lines[0], 0x600, js, sizeof js);
    PJ(",\"mail\":%s,\"mailState\":%d", js, st);
    it = pdb_get(b, s, "z_hid", g_prof_rec);
    memset(hid, 0, sizeof hid);
    /* the viewer prints the record's low 32 bits, or the H-ID it asked for when that's 0 (a hidden one) */
    if (SVC(0x76c)) {
        DWORD lo = it ? (DWORD)it->v.u64 : 0, hi = 0;
        if (!lo) { lo = (DWORD)g_prof_hid; hi = (DWORD)(g_prof_hid >> 32); }
        ((int (__cdecl *)(char *, DWORD, DWORD))SVC(0x76c))(hid, lo, hi);
    }
    hid[0x20] = 0;
    json_str(js, sizeof js, hid);
    PJ(",\"hid\":%s", js);
    it = pdb_get(b, s, "z_utime", g_prof_rec);
    PJ(",\"updated\":%u,\"level\":%u", it ? it->v.u32 : 0, g_prof_rec->level);
    PJ(",\"rows\":[");
    for (i = 0; p && i < p->nItems; i++) {
        const pfb_item *pi = &p->items[i];
        const char *fname;
        if (pi->field >= s->nfields || pi->ui == 6) continue;
        fname = s->fields[pi->field].name;
        if (!strcmp(fname, "z_age_f") || !strcmp(fname, "z_name") || !strcmp(fname, "z_hid") ||
            !strcmp(fname, "z_mail") || !strcmp(fname, "z_utime")) continue;
        pol_json(b, pi->label, 0x100, js, sizeof js);
        if (!strcmp(fname, "z_ficon")) {
            pdb_item *fi = pdb_get(b, s, "z_ficon", g_prof_rec);
            st = (!fi || g_prof_rec->level < 1 || fi->level == 0 ||
                  ((s->fields[pi->field].flags & 0x40) && fi->level < 1)) ? PV_PRIVATE
               : (int)fi->v.u32 <= 0 ? PV_NOTSET : PV_OK;
            PJ("%s{\"label\":%s,\"field\":\"z_ficon\",\"state\":%d,\"pic\":%d}", first ? "" : ",", js, st,
               st == PV_OK ? (int)fi->v.u32 : 0);
            first = 0;
            continue;
        }
        st = pf_value(b, s, p, g_prof_rec, fname, 1, lines, &n);
        PJ("%s{\"label\":%s,\"field\":\"%s\",\"state\":%d,\"lines\":[", first ? "" : ",", js, fname, st);
        for (k = 0; k < n; k++) {
            pol_json(b, lines[k], 0x600, js, sizeof js);
            PJ("%s%s", k ? "," : "", js);
        }
        PJ("]}");
        first = 0;
    }
    PJ("]");
    /* the FFXI tabs: z_phead's slots like the viewer (readable whatever the level), else the friend row's. Only title
       1, other games' profiles never get a tab here */
    {
        BYTE fr[0x140];
        pdb_item *ph = pdb_get(b, s, "z_phead", g_prof_rec);
        const BYTE *h = ph && (ph->flags & 1) ? (const BYTE *)ph->v.bytes.p : NULL, *src = NULL;
        const char *from = "nowhere";
        if (h && !IsBadReadPtr(h, 0xb0) && (*(const DWORD *)(h + 0x98) & 1) && fr_hid(h) == g_prof_hid) {
            src = h + 0x18;
            from = "z_phead";
        } else if (get_friend(b, g_prof_row, fr) && fr_hid(fr) == g_prof_hid) {
            src = fr + 0x18;
            from = "the friend row";
        }
        prof_slots_keep(g_prof_hid, src);
        PJ(",\"characters\":[");
        for (i = 0, k = 0; src && i < 8; i++) {
            const BYTE *sl = src + i * 0x10;
            DWORD cs = *(const DWORD *)(sl + 4);
            if (!(*(const WORD *)sl & 1) || *(const WORD *)(sl + 2) != 1) continue;
            PJ("%s{\"slot\":%d,\"ctsid\":%u,\"ctidLow\":%u,\"ctidHigh\":%u,\"world\":%u}", k++ ? "," : "", i, cs,
               *(const DWORD *)(sl + 8), *(const DWORD *)(sl + 0xc), (cs >> 16) & 0xff);
        }
        PJ("]");
        lg("profile: %d FFXI characters (from %s)\r\n", k, from);
    }
    /* svc 0x1200 is the viewer's away text (-0x14cc = none or no access). All z_aa_* at 0 means none, though 0x1200
       would happily invent "I am away from the keyboard". */
    for (k = 0; k < 8 && !set; k++) {
        pdb_item *a2 = pdb_get(b, s, aa[k], g_prof_rec);
        if (a2 && a2->v.bytes.p && *(BYTE *)a2->v.bytes.p) set = 1;
    }
    if (!set || !SVC(0x1200)) {
        PJ(",\"awayResult\":0");
    } else {
        memset(t, 0, sizeof t);
        memset(f, 0, sizeof f);
        r = ((int (__cdecl *)(void *, void *, void *, int, char *, int, char *, int))SVC(0x1200))(
                s, p, g_prof_rec, g_prof_row, t, sizeof t, f, sizeof f);
        if (r >= 0) {
            for (q = t; *q; q++) if (*q == 7) *q = '\n';
            pol_json(b, t, 0x600, js, sizeof js);
            PS(",\"away\":");
            PS(js);
            pol_json(b, f, 0x600, js, sizeof js);
            PJ(",\"awayFrom\":%s", js);
        } else PJ(",\"awayResult\":%d", r);
    }
#undef PJ
#undef PS
    g_prof_json[o] = 0;
}
static void profile_start(BYTE *b)
{
    int t = ((svc4b4_t)SVC(0x4b4))(g_fs_pib, &g_prof_cond, g_prof_rec);
    if (t == NO_SLOT) { g_prof_step = 2; return; }
    lg("profile: load row %d -> %d\r\n", g_prof_row, t);
    if (t < 0) { profile_done(failed(t)); return; }
    g_prof_job = t;
    g_prof_step = 1;
    g_prof_t0 = GetTickCount64();
}
static void profile_request(BYTE *b)
{
    char buf[128], *p, *tok, *rows, *key, k[17];
    BYTE fr[0x140];
    int row;
    pdb_schema *s;
    if (!db_free()) return;
    if (take_request("profile", buf, sizeof buf) < 0) return;
    g_db_last = GetTickCount64();
    p = buf;
    tok = req_line(&p);
    rows = req_line(&p);
    key = req_line(&p);
    lstrcpynA(g_prof_token, tok, sizeof g_prof_token);
    if (g_prof_json) g_prof_json[0] = 0;
    if (!parse_num(rows, 4, &row) || row >= 200) { profile_done("failed:bad request"); return; }
    if (notice_blocks(g_notice)) { profile_done("failed:account notice"); return; }
    if (!get_friend(b, row, fr)) { profile_done("failed:no such friend"); return; }
    friend_key(b, fr, k);
    if (lstrcmpA(k, key) != 0) { profile_done("failed:friend list changed"); return; }
    /* a row still waiting for an answer has no handle id yet, so no query that could find someone else */
    if (*(DWORD *)(fr + 0x08) & 0x10000000) { profile_done("failed:pending"); return; }
    if (!(s = fs_load_pib(b)) || !pf_load(b)) { profile_done("failed:no schema"); return; }
    unsigned __int64 hid = fr_hid(fr);
    g_prof_rec = (pdb_rec *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, s->rec_size + 8);
    if (!g_prof_rec) { profile_done("failed:alloc"); return; }
    memset(&g_prof_cond, 0, sizeof g_prof_cond);
    ((svc4a0_t)SVC(0x4a0))(s, &g_prof_cond.item, "z_hid", &hid, 1);
    if (!(g_prof_cond.item.flags & 1)) { profile_done("failed:schema"); return; }
    ((svc4a8_t)SVC(0x4a8))(&g_prof_cond, 1);
    ((svc4ac_t)SVC(0x4ac))(1, &g_prof_cond);
    g_prof_row = row;
    g_prof_hid = hid;
    lstrcpyA(g_prof_state, "loading");
    g_snap_dirty = 1;
    profile_start(b);
}
static void profile_pump(BYTE *b, int ctl)
{
    if (g_prof_step == 0) {
        if (ctl && !g_draining) profile_request(b);
        return;
    }
    if (g_prof_step == 2) { if (!g_draining) profile_start(b); else profile_done("cancelled"); return; }
    int r = ((svc4b8_t)SVC(0x4b8))(g_prof_job);
    if (r == 0) {
        if (GetTickCount64() - g_prof_t0 >= 120000) profile_abandon(b, "failed:timeout");
        return;
    }
    g_prof_job = -1;
    if (r < 0) { profile_done(failed(r)); return; }
    if (!g_prof_rec->items) { profile_done("failed:no profile"); return; }
    profile_build(b);
    profile_done("done");
}

/* a friend's FFXI character, the way the viewer's FFXI profile tab finds it: prof_001 (title 1) by z_ctsid and z_ctid
   together. Only for a character a friend's on right now */
static __declspec(align(8)) pdb_cond g_ch_cond[2];
static pdb_cond *g_ch_where;
static int g_ch_job = -1;
static DWORD g_ch_ctsid, g_ch_lo, g_ch_hi;
static char g_ch_token[48], g_ch_state[32] = "", g_ch_json[0x400];
static pdb_rec *g_ch_rec;
static ULONGLONG g_ch_t0;

static int parse_u32(const char *x, DWORD *out)
{
    unsigned __int64 v = 0;
    int d = 0;
    while (*x >= '0' && *x <= '9' && d < 10) { v = v * 10 + (unsigned)(*x++ - '0'); d++; }
    *out = (DWORD)v;
    return d > 0 && !*x && v <= 0xffffffffu;
}
/* the record's string field, empty when it isn't there */
static void ch_text(BYTE *b, pdb_rec *rec, const char *name, char *js, int cap, BYTE *level)
{
    pdb_item *it = pdb_get(b, g_ch_pib, name, rec);
    char raw[0x40] = "";
    if (level) *level = it ? it->level : 0;
    if (it && (it->flags & 1) && it->v.str && !IsBadStringPtrA(it->v.str, sizeof raw + 1))
        sjis_copy(raw, it->v.str, sizeof raw);
    pol_json(b, raw, 0x80, js, cap);
}
static void character_done(const char *state)
{
    lstrcpynA(g_ch_state, state, sizeof g_ch_state);
    if (g_ch_rec) { HeapFree(GetProcessHeap(), 0, g_ch_rec); g_ch_rec = NULL; }
    g_ch_step = 0;
    g_ch_job = -1;
    lg("character: %s\r\n", state);
    g_snap_dirty = 1;
}
static void character_abandon(BYTE *b, const char *state)
{
    if (g_ch_step == 1 && g_ch_job >= 0) ((svc4dc_t)SVC(0x4dc))(g_ch_job);
    g_ch_rec = NULL;   /* a cancelled read can still write, so never freed */
    character_done(state);
}
/* nameState 0 shown, 1 withheld, 2 empty. Like the viewer with a header field (and prof_pol's z_name), only the record
   level says whether the name's visible, z_name's own level just goes in the log */
static void character_build(BYTE *b)
{
    char nm[0x100], wn[0x100];
    BYTE lv = 0;
    int st;
    ch_text(b, g_ch_rec, "z_name", nm, sizeof nm, &lv);
    st = g_ch_rec->level < 1 ? 1 : strcmp(nm, "\"\"") == 0 ? 2 : 0;
    ch_text(b, g_ch_rec, "z_worldname", wn, sizeof wn, NULL);
    wsprintfA(g_ch_json, "\"name\":%s,\"nameState\":%d,\"level\":%u,\"worldName\":%s", st == 1 ? "\"\"" : nm, st,
              g_ch_rec->level, wn);
    lg("character: record level %u, name level %u, state %d\r\n", g_ch_rec->level, lv, st);
}
static void character_start(BYTE *b)
{
    int t = ((svc4b4_t)SVC(0x4b4))(g_ch_pib, g_ch_where, g_ch_rec);
    if (t == NO_SLOT) { g_ch_step = 2; return; }
    lg("character: load -> %d\r\n", t);
    if (t < 0) { character_done(failed(t)); return; }
    g_ch_job = t;
    g_ch_step = 1;
    g_ch_t0 = GetTickCount64();
}
static int ffxi_friend_on(BYTE *b, DWORD ctsid, DWORD lo, DWORD hi)
{
    DWORD cs, cl, chi;
    for (int i = 0; i < 200; i++) {
        BYTE *a = b + 0xb40d8 + i * 0xb0;
        if (IsBadReadPtr(a, 0xb0)) break;
        if ((*(unsigned *)(a + 0x98) & 1) && friend_ffxi(a, &cs, &cl, &chi) && cs == ctsid && cl == lo && chi == hi)
            return 1;
    }
    return 0;
}
static void character_request(BYTE *b)
{
    char buf[128], *p, *tok;
    unsigned __int64 ctid;
    if (!db_free()) return;
    if (take_request("character", buf, sizeof buf) < 0) return;
    g_db_last = GetTickCount64();
    p = buf;
    tok = req_line(&p);
    lstrcpynA(g_ch_token, tok, sizeof g_ch_token);
    g_ch_json[0] = 0;
    if (!parse_u32(req_line(&p), &g_ch_ctsid) || !parse_u32(req_line(&p), &g_ch_lo) || !parse_u32(req_line(&p), &g_ch_hi)) {
        character_done("failed:bad request");
        return;
    }
    if (notice_blocks(g_notice)) { character_done("failed:account notice"); return; }
    if (!ffxi_friend_on(b, g_ch_ctsid, g_ch_lo, g_ch_hi)) { character_done("failed:not playing"); return; }
    if (!ch_load_pib(b)) { character_done("failed:no schema"); return; }
    g_ch_rec = (pdb_rec *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, g_ch_pib->rec_size + 8);
    if (!g_ch_rec) { character_done("failed:alloc"); return; }
    ctid = (unsigned __int64)g_ch_hi << 32 | g_ch_lo;
    memset(g_ch_cond, 0, sizeof g_ch_cond);
    ((svc4a0_t)SVC(0x4a0))(g_ch_pib, &g_ch_cond[0].item, "z_ctsid", &g_ch_ctsid, 1);
    ((svc4a0_t)SVC(0x4a0))(g_ch_pib, &g_ch_cond[1].item, "z_ctid", &ctid, 1);
    if (!(g_ch_cond[0].item.flags & 1) || !(g_ch_cond[1].item.flags & 1)) { character_done("failed:schema"); return; }
    ((svc4a8_t)SVC(0x4a8))(&g_ch_cond[0], 1);
    ((svc4a8_t)SVC(0x4a8))(&g_ch_cond[1], 1);
    g_ch_where = ((svc4ac_t)SVC(0x4ac))(2, &g_ch_cond[0], &g_ch_cond[1]);
    if (!g_ch_where) { character_done("failed:schema"); return; }
    lstrcpyA(g_ch_state, "loading");
    g_snap_dirty = 1;
    character_start(b);
}
static void character_pump(BYTE *b, int ctl)
{
    if (g_ch_step == 0) {
        if (ctl && !g_draining) character_request(b);
        return;
    }
    if (g_ch_step == 2) { if (!g_draining) character_start(b); else character_done("cancelled"); return; }
    int r = ((svc4b8_t)SVC(0x4b8))(g_ch_job);
    if (r == 0) {
        if (GetTickCount64() - g_ch_t0 >= 120000) character_abandon(b, "failed:timeout");
        return;
    }
    g_ch_job = -1;
    if (r < 0) { character_done(failed(r)); return; }
    if (!g_ch_rec->items) { character_done("failed:no profile"); return; }
    character_build(b);
    character_done("done");
}

/* a friend's FFXI character for their profile's tabs, the viewer's prof_001 read. The app only says which slot, the
   ids come from the friend's own slots */
static __declspec(align(8)) pdb_cond g_fp_cond[2];
static pdb_cond *g_fp_where;
static int g_fp_job = -1, g_fp_slot = -1;
static DWORD g_fp_ctsid, g_fp_lo, g_fp_hi;
static char g_fp_token[48], g_fp_state[32] = "", g_fp_json[0x400];
static pdb_rec *g_fp_rec;
static ULONGLONG g_fp_t0;
static const char *const g_fp_nums[][2] = {
    { "z_countryid", "countryId" }, { "z_zoneid", "zoneId" }, { "z_jobid", "jobId" }, { "z_joblevel", "jobLevel" },
    { "z_raceid", "raceId" },
};

static int fp_check_schema(BYTE *b, pdb_schema *s)
{
    int i, idx;
    for (i = 0; i < (int)(sizeof g_fp_nums / sizeof g_fp_nums[0]); i++) {
        const pdb_field *f = fs_field(s, g_fp_nums[i][0], &idx);
        if (!f || f->type != 3 || ((svc4bc_t)SVC(0x4bc))(s, g_fp_nums[i][0]) != idx) return 0;
    }
    return 1;
}
static void ffxiprofile_done(const char *state)
{
    lstrcpynA(g_fp_state, state, sizeof g_fp_state);
    if (g_fp_rec) { HeapFree(GetProcessHeap(), 0, g_fp_rec); g_fp_rec = NULL; }
    g_fp_step = 0;
    g_fp_job = -1;
    lg("ffxiprofile: %s\r\n", state);
    g_snap_dirty = 1;
}
static void ffxiprofile_abandon(BYTE *b, const char *state)
{
    if (g_fp_step == 1 && g_fp_job >= 0) ((svc4dc_t)SVC(0x4dc))(g_fp_job);
    g_fp_rec = NULL;   /* a cancelled read can still write, so never freed */
    ffxiprofile_done(state);
}
/* states as the handle profile's, and prof_001 has no per-field levels so the record level decides them all. Type 3
   is a signed short */
static void ffxiprofile_build(BYTE *b)
{
    char nm[0x100], wn[0x100];
    int priv = g_fp_rec->level < 1, i, o, st;
    ch_text(b, g_fp_rec, "z_name", nm, sizeof nm, NULL);
    ch_text(b, g_fp_rec, "z_worldname", wn, sizeof wn, NULL);
    o = wsprintfA(g_fp_json, "\"level\":%u,\"name\":%s,\"nameState\":%d,\"worldName\":%s,\"worldNameState\":%d",
                  g_fp_rec->level, priv ? "\"\"" : nm, priv ? PV_PRIVATE : strcmp(nm, "\"\"") ? PV_OK : PV_NOTSET,
                  priv ? "\"\"" : wn, priv ? PV_PRIVATE : strcmp(wn, "\"\"") ? PV_OK : PV_NOTSET);
    for (i = 0; i < (int)(sizeof g_fp_nums / sizeof g_fp_nums[0]); i++) {
        pdb_item *it = pdb_get(b, g_ch_pib, g_fp_nums[i][0], g_fp_rec);
        st = priv ? PV_PRIVATE : it && (it->flags & 1) ? PV_OK : PV_NOTSET;
        o += wsprintfA(g_fp_json + o, ",\"%s\":%d,\"%sState\":%d", g_fp_nums[i][1],
                       st == PV_OK ? (int)(short)it->v.u16 : 0, g_fp_nums[i][1], st);
    }
    lg("ffxiprofile: slot %d, record level %u\r\n", g_fp_slot, g_fp_rec->level);
}
static void ffxiprofile_start(BYTE *b)
{
    int t = ((svc4b4_t)SVC(0x4b4))(g_ch_pib, g_fp_where, g_fp_rec);
    if (t == NO_SLOT) { g_fp_step = 2; return; }
    lg("ffxiprofile: load -> %d\r\n", t);
    if (t < 0) { ffxiprofile_done(failed(t)); return; }
    g_fp_job = t;
    g_fp_step = 1;
    g_fp_t0 = GetTickCount64();
}
static void ffxiprofile_request(BYTE *b)
{
    char buf[128], *p, *tok, *rows, *key, *sl, k[17];
    BYTE fr[0x140];
    const BYTE *slot;
    int row, n;
    unsigned __int64 ctid;
    if (!db_free()) return;
    if (take_request("ffxiprofile", buf, sizeof buf) < 0) return;
    g_db_last = GetTickCount64();
    p = buf;
    tok = req_line(&p);
    rows = req_line(&p);
    key = req_line(&p);
    sl = req_line(&p);
    lstrcpynA(g_fp_token, tok, sizeof g_fp_token);
    g_fp_json[0] = 0;
    g_fp_slot = -1;
    g_fp_ctsid = g_fp_lo = g_fp_hi = 0;
    if (!parse_num(rows, 4, &row) || row >= 200 || !parse_num(sl, 1, &n) || n > 7) {
        ffxiprofile_done("failed:bad request");
        return;
    }
    g_fp_slot = n;
    if (notice_blocks(g_notice)) { ffxiprofile_done("failed:account notice"); return; }
    if (!get_friend(b, row, fr)) { ffxiprofile_done("failed:no such friend"); return; }
    friend_key(b, fr, k);
    if (lstrcmpA(k, key) != 0) { ffxiprofile_done("failed:friend list changed"); return; }
    if (*(DWORD *)(fr + 0x08) & 0x10000000) { ffxiprofile_done("failed:pending"); return; }
    /* the slots the tabs came from when it's the same handle, like the viewer looks in z_phead first */
    slot = prof_slots_for(fr_hid(fr));
    slot = (slot ? slot : fr + 0x18) + n * 0x10;
    if (!(*(const WORD *)slot & 1) || *(const WORD *)(slot + 2) != 1) { ffxiprofile_done("failed:no character"); return; }
    g_fp_ctsid = *(const DWORD *)(slot + 4);
    g_fp_lo = *(const DWORD *)(slot + 8);
    g_fp_hi = *(const DWORD *)(slot + 0xc);
    if (!ch_load_pib(b) || !fp_check_schema(b, g_ch_pib)) { ffxiprofile_done("failed:no schema"); return; }
    g_fp_rec = (pdb_rec *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, g_ch_pib->rec_size + 8);
    if (!g_fp_rec) { ffxiprofile_done("failed:alloc"); return; }
    ctid = (unsigned __int64)g_fp_hi << 32 | g_fp_lo;
    memset(g_fp_cond, 0, sizeof g_fp_cond);
    ((svc4a0_t)SVC(0x4a0))(g_ch_pib, &g_fp_cond[0].item, "z_ctsid", &g_fp_ctsid, 1);
    ((svc4a0_t)SVC(0x4a0))(g_ch_pib, &g_fp_cond[1].item, "z_ctid", &ctid, 1);
    if (!(g_fp_cond[0].item.flags & 1) || !(g_fp_cond[1].item.flags & 1)) { ffxiprofile_done("failed:schema"); return; }
    ((svc4a8_t)SVC(0x4a8))(&g_fp_cond[0], 1);
    ((svc4a8_t)SVC(0x4a8))(&g_fp_cond[1], 1);
    g_fp_where = ((svc4ac_t)SVC(0x4ac))(2, &g_fp_cond[0], &g_fp_cond[1]);
    if (!g_fp_where) { ffxiprofile_done("failed:schema"); return; }
    lg("ffxiprofile: row %d slot %d\r\n", row, n);
    lstrcpyA(g_fp_state, "loading");
    g_snap_dirty = 1;
    ffxiprofile_start(b);
}
static void ffxiprofile_pump(BYTE *b, int ctl)
{
    if (g_fp_step == 0) {
        if (ctl && !g_draining) ffxiprofile_request(b);
        return;
    }
    if (g_fp_step == 2) { if (!g_draining) ffxiprofile_start(b); else ffxiprofile_done("cancelled"); return; }
    int r = ((svc4b8_t)SVC(0x4b8))(g_fp_job);
    if (r == 0) {
        if (GetTickCount64() - g_fp_t0 >= 120000) ffxiprofile_abandon(b, "failed:timeout");
        return;
    }
    g_fp_job = -1;
    /* "Selected profile is hidden." */
    if (r == -0x14cc) { ffxiprofile_done("failed:hidden"); return; }
    if (r < 0) { ffxiprofile_done(failed(r)); return; }
    if (!g_fp_rec->items) { ffxiprofile_done("failed:no profile"); return; }
    ffxiprofile_build(b);
    ffxiprofile_done("done");
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
static ULONGLONG g_me_t0, g_me_last;
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
    o += wsprintfA(out + o, "{\"token\":%s,\"handle\":%s,\"slot\":%d,\"level\":%u,\"fields\":{",
                   tk, js, g_my_slot, g_me_rec->level);
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
    lstrcpyA(out + o, "}}"); o += 2;
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
static void me_abandon(BYTE *b, const char *why)
{
    if (g_me_step == 2 && g_me_job >= 0) {
        ((svc4dc_t)SVC(0x4dc))(g_me_job);
        g_me_rec = NULL;                    /* a cancelled read can still write, so never freed */
    }
    if (g_me_step == 1 || g_me_step == 2) me_done(why);
}
static void me_load_start(BYTE *b)
{
    int t = ((svc4b4_t)SVC(0x4b4))(g_fs_pib, &g_me_cond, g_me_rec);
    if (t == NO_SLOT) { g_me_step = 1; return; }
    lg("profile edit: load -> %d\r\n", t);
    if (t < 0) { me_done(failed(t)); return; }
    g_me_job = t;
    g_me_step = 2;
    g_me_t0 = GetTickCount64();
}
static void me_save_start(BYTE *b)
{
    int t = pe_update_start(b, g_fs_pib, g_me_rec, g_my_slot, &g_me_req);
    if (t == NO_SLOT) { g_me_step = 3; return; }
    lg("profile edit: save %d field(s), level %d -> %d\r\n", g_me_req.n, g_me_req.level, t);
    if (t < 0) { me_done(failed(t)); return; }
    g_me_job = t;
    g_me_step = 4;
    g_me_t0 = GetTickCount64();
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
    if (GetTickCount64() - g_me_last < 3000 || !db_free()) return;
    kind = take_request("myprofile", buf, sizeof buf) >= 0 ? 1
         : take_request("saveprofile", buf, sizeof buf) >= 0 ? 2
         : take_request("rename", buf, sizeof buf) >= 0 ? 3 : 0;
    if (!kind) return;
    g_me_last = g_db_last = GetTickCount64();
    p = buf;
    tok = req_line(&p);
    lstrcpynA(g_me_token, tok, sizeof g_me_token);
    if (notice_blocks(g_notice)) { me_done("failed:account notice"); return; }
    if (g_my_slot < 0) { me_done("failed:no handle"); return; }
    if (!fs_load_pib(b) || !pf_load(b)) { me_done("failed:no schema"); return; }
    g_me_t0 = GetTickCount64();
    if (kind == 1) {
        ipc_file(jp, "myprofile.json");
        DeleteFileA(jp);
        memset(&h, 0, sizeof h);
        if (get_handle(b, g_my_slot, &h) < 0 || !(h.lo & 1)) { me_done("failed:no handle"); return; }
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
        name = req_line(&p);
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
    pol_handle h;
    pdb_item *it;
    int r;
    switch (g_me_step) {   /* 1 load waits for a slot, 2 load job, 3 save waits, 4 save job, 5 rename, 6 renamed */
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
            if (GetTickCount64() - g_me_t0 >= 120000 || g_draining) {
                ((svc4dc_t)SVC(0x4dc))(g_me_job);
                g_me_rec = NULL;   /* a cancelled read can still write, so never freed */
                me_done(g_draining ? "cancelled" : "failed:timeout");
            }
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
        if (r == 0) {
            if (GetTickCount64() - g_me_t0 >= 120000) { ((svc4dc_t)SVC(0x4dc))(g_me_job); me_done("failed:timeout"); }
            return;
        }
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
        else if (r == HR_E_REVERT) {
            /* right after the upload the slot can still show the old name for a moment, so look again in a bit */
            g_me_step = 6;
            g_me_t0 = GetTickCount64();
        } else me_done(r == HR_E_NAME ? "failed:bad name" : r == HR_E_NGWORD ? "failed:word" : failed(r));
        return;
    case 6:
        memset(&h, 0, sizeof h);
        get_handle(b, g_my_slot, &h);
        h.name[15] = 0;
        if (strcmp(h.name, g_hr.want) == 0) me_done("saved");
        else if (GetTickCount64() - g_me_t0 >= 30000 || g_draining) me_done("failed:reloaded");
        return;
    }
}

static int g_grp_job = -1, g_grp_poll;
static DWORD g_grp_new[2];
static char g_grp_token[48], g_grp_state[32] = "";
static ULONGLONG g_grp_last, g_grp_t0;

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
    g_grp_t0 = GetTickCount64();
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
    if (GetTickCount64() - g_grp_last < 2000) return;
    /* a group job's -0x100 is a failure, so wait for a free polcore slot */
    if (g_sq_n || g_answer_kind || slots_free(b) < 1) return;
    if (take_request("group", buf, sizeof buf) < 0) return;
    g_grp_last = GetTickCount64();
    p = buf;
    op = req_line(&p); tok = req_line(&p); gid = req_line(&p); a = req_line(&p); bb = req_line(&p); c = req_line(&p);
    d = req_line(&p);
    lstrcpynA(g_grp_token, tok, sizeof g_grp_token);
    if (pace_refusal() && (!lstrcmpA(op, "accept") || !lstrcmpA(op, "decline") || !lstrcmpA(op, "invite"))) {
        group_fail(pace_refusal());
        return;
    }
    if (notice_blocks(g_notice)) { group_fail("failed:account notice"); return; }
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
        if (slot < 0) { group_fail("failed:no handle"); return; }
        g_grp_new[0] = g_grp_new[1] = 0xffffffff;
        group_start(((int (__cdecl *)(const char *, int))SVC(0x608))(nm, slot), 0x60c, "create");
        return;
    }
    if (!haveGroup) { group_fail("failed:no such group"); return; }
    if (lstrcmpA(op, "disband") == 0) {
        if (myClass != 5) { group_fail("failed:not master"); return; }
        group_start(((int (__cdecl *)(DWORD, DWORD))SVC(0x610))(lo, hi), 0x614, "disband");
    } else if (lstrcmpA(op, "leave") == 0) {
        if (myClass == 5) { group_fail("failed:master cannot leave"); return; }
        if (myClass < 3) { group_fail("failed:not a member"); return; }
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
    if (r == 0) {
        if (GetTickCount64() - g_grp_t0 < 120000) return;
        CANCEL_JOB(g_grp_job);
        r = -0x104;
    }
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
    g_edit_last = GetTickCount64();
    p = buf;
    op = req_line(&p); tok = req_line(&p); mids = req_line(&p); subj = req_line(&p); arg = req_line(&p);
    hkey = req_line(&p);
    lstrcpynA(g_answer_token, tok, sizeof g_answer_token);
    /* every answer ends in a message, so refuse it before AddFriend or a list upload */
    if (pace_refusal()) { answer_fail(pace_refusal()); return; }
    if (!parse_num(mids, 6, &mid)) { answer_fail("failed:bad request"); return; }
    if (notice_blocks(g_notice)) { answer_fail("failed:account notice"); return; }
    if (lstrcmpA(op, "request") == 0) {
        if (mid >= g_srch_n || g_srch_step != 0) { answer_fail("failed:no such result"); return; }
        if (!g_friends_loaded) { answer_fail("failed:friends not loaded"); return; }
        if (g_my_slot < 0) { answer_fail("failed:no handle"); return; }
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
    if (!g_friends_loaded) { answer_fail("failed:friends not loaded"); return; }
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
    if (ctl && !g_draining && g_edit_job < 0 && !g_edit_upload_want && !g_comment_wanted &&
        !g_sq_n && GetTickCount64() - g_edit_last >= 2000)
        answer_request(b);
}

static int g_pic_want = -1;
static char g_pic_state[32] = "";
static char g_pic_token[48];
static ULONGLONG g_pic_last;
#define pic_fail(why) refuse(g_pic_state, sizeof g_pic_state, "pic: request refused - %s\r\n", why)
static void pic_pump(BYTE *b, int ctl)
{
    int r;
    if (g_pic_want < 0) {
        char buf[96], *p;
        int i, v = 0, d = 0;
        if (!ctl || g_draining || GetTickCount64() - g_pic_last < 5000 || !db_free()) return;
        if (take_request("pic", buf, sizeof buf) < 0) return;
        g_pic_last = g_db_last = GetTickCount64();
        for (i = 0; buf[i] >= '0' && buf[i] <= '9' && d < 5; i++, d++) v = v * 10 + (buf[i] - '0');
        p = buf + i;
        if (*p == '\r') p++;
        if (*p == '\n') p++;
        lstrcpynA(g_pic_token, p, sizeof g_pic_token);
        for (p = g_pic_token; *p; p++) if (*p == '\r' || *p == '\n') { *p = 0; break; }
        if (!d || v > 9999 || (buf[i] && buf[i] != '\r' && buf[i] != '\n')) { pic_fail("failed:bad request"); return; }
        if (notice_blocks(g_notice)) { pic_fail("failed:account notice"); return; }
        if (g_my_slot < 0) { pic_fail("failed:no handle"); return; }
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

/* Handles and Content IDs. Listing is GetHandle/GetChr (svc 0x2b4/0x2c4, memory only). A switch is the viewer's one
   ChangeMyStatus (app.dll 0x10040c9e), a link or unlink is local (0x2dc/0x2e0) and then one PutChrList (0x1038) */
#define CHR_LINKED 0x8000
#define CHR_HANDLE(f) (((f) >> 16) & 0x3f)
enum { HOP_NONE, HOP_SWITCH, HOP_LINK, HOP_UNLINK };
static int g_hop, g_hjob = -1, g_hwant, g_hslot = -1, g_hidx = -1, g_hfrom = -1, g_hreload;
static char g_htoken[48], g_hstate[48] = "";
static ULONGLONG g_hlast;
static int g_in_hold;
static int writes_pending(void);
static void wr_job(HANDLE h, const char *fmt, const char *token, const char *state);

static int get_chr(BYTE *b, int i, BYTE *rec)
{
    void *get = g_svc_2c4 ? g_svc_2c4 : SVC(0x2c4);
    memset(rec, 0, 0x68);
    return get && ((int (__cdecl *)(unsigned, void *))get)((unsigned)i, rec) == 0 && (rec[0] & 1);
}
static int handle_ok(BYTE *b, int slot, BYTE *rec)
{
    memset(rec, 0, 0x28);
    return slot >= 0 && slot < 64 && get_handle(b, slot, rec) == 0 && (rec[0] & 1);
}
/* the Handle ID is bits 1-44, the same 44 bits as a friend row's H-ID */
static void handle_key(const BYTE *rec, char k[17])
{
    unsigned __int64 h = (*(const unsigned __int64 *)rec >> 1) & 0xfffffffffffULL;
    wsprintfA(k, "%03x%08x", (unsigned)(h >> 32), (unsigned)h);
}
static void chr_key(const BYTE *rec, char k[17])
{
    wsprintfA(k, "%08x%08x", *(const DWORD *)(rec + 4), *(const DWORD *)(rec + 8));
}
static int handle_used(const BYTE *rec)
{
    int n = 0;
    for (int k = 0; k < 8; k++) n += rec[0x20 + k] & 1;
    return n;
}
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
/* a put that didn't go puts polcore back how the server has it, which costs nothing (no reload, the viewer doesn't) */
static void handle_undo(BYTE *b)
{
    if (g_hop == HOP_LINK) {
        ((int (__cdecl *)(int, int))SVC(0x2e0))(g_hidx, g_hslot);
        if (g_hfrom >= 0) ((int (__cdecl *)(int, int))SVC(0x2dc))(g_hidx, g_hfrom);
    } else {
        ((int (__cdecl *)(int, int))SVC(0x2dc))(g_hidx, g_hslot);
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
    if (g_hreload || linked != (g_hop == HOP_LINK)) handle_done("failed:not saved");
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

/* <op> switch|link|unlink, <token>, <handle slot>, <handle key>, then for link/unlink <content index>, <content key>.
   The keys are the snapshot's, so a request never lands on a record that changed */
static void handle_request(BYTE *b)
{
    char buf[512], *p, *op, *tok, *sl, *hk, *ix, *ck, k[17];
    BYTE h[0x28], c[0x68];
    int slot, idx = -1, kind, cur, old = -1, r;
    DWORD f;
    if (GetTickCount64() - g_hlast < 3000) return;
    if (take_request("handle", buf, sizeof buf) < 0) return;
    g_hlast = GetTickCount64();
    p = buf;
    op = req_line(&p); tok = req_line(&p); sl = req_line(&p); hk = req_line(&p); ix = req_line(&p); ck = req_line(&p);
    lstrcpynA(g_htoken, tok, sizeof g_htoken);
    kind = !lstrcmpA(op, "switch") ? HOP_SWITCH : !lstrcmpA(op, "link") ? HOP_LINK : !lstrcmpA(op, "unlink") ? HOP_UNLINK : 0;
    if (!kind || !tok[0] || !parse_num(sl, 2, &slot) || slot > 63 ||
        (kind != HOP_SWITCH && (!parse_num(ix, 2, &idx) || idx > 63))) {
        handle_fail("failed:bad request");
        return;
    }
    if (!g_in_hold) { handle_fail("failed:not in hold"); return; }
    if (g_draining) { handle_fail("failed:draining"); return; }
    if (notice_blocks(g_notice)) { handle_fail("failed:account notice"); return; }
    if (!handle_ok(b, slot, h)) { handle_fail("failed:list changed"); return; }
    handle_key(h, k);
    if (lstrcmpA(k, hk) != 0) { handle_fail("failed:list changed"); return; }
    if (kind == HOP_SWITCH) {
        cur = ((svc_v)SVC(0x2f8))();
        if (slot == cur) { handle_fail("failed:already active"); return; }
        /* they went under the old handle, some with its slot in them */
        if (writes_pending() || g_me_step || g_status_want >= 0) { handle_fail("failed:busy"); return; }
        g_hop = HOP_SWITCH;
        g_hslot = slot;
        lstrcpyA(g_hstate, "working");
        lg("handles: switch from slot %d to slot %d\r\n", cur, slot);
        g_snap_dirty = 1;
        handle_switch_start(b);
        return;
    }
    if (!get_chr(b, idx, c) || *(WORD *)(c + 2) != 1) { handle_fail("failed:list changed"); return; }
    chr_key(c, k);
    if (lstrcmpA(k, ck) != 0) { handle_fail("failed:list changed"); return; }
    f = *(DWORD *)(c + 0x10);
    if (kind == HOP_LINK) {
        if ((f & CHR_LINKED) && (int)CHR_HANDLE(f) == slot) { handle_fail("failed:already linked"); return; }
        if (handle_used(h) >= 8) { handle_fail("failed:handle full"); return; }
        /* polcore won't link one that's linked anywhere, so a move's an unlink first, like the viewer's */
        if (f & CHR_LINKED) {
            old = (int)CHR_HANDLE(f);
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
static void handles_json(BYTE *b, HANDLE out)
{
    char line[900], nm[64], tx[200], k[17], ids[48];
    BYTE rec[0x68];
    int cur = SVC(0x2f8) ? ((svc_v)SVC(0x2f8))() : -1, first = 1, i, j, o;
    DWORD f;
    wr_job(out, ",\"handleJob\":{\"token\":%s,\"state\":\"%s\"}", g_htoken, g_hstate);
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

/* a cheap look at who's on the friend list, so the hits' marks only get worked out again when it changes */
static DWORD friends_sig(BYTE *b)
{
    DWORD h = 2166136261u;
    for (int i = 0; i < 200; i++) {
        const DWORD *a = (const DWORD *)(b + 0xb40d8 + i * 0xb0);
        if (IsBadReadPtr(a, 0xb0)) break;
        h = (h ^ a[0]) * 16777619u;
        h = (h ^ a[1]) * 16777619u;
        h = (h ^ a[0x98 / 4]) * 16777619u;
        h = (h ^ a[0x9c / 4]) * 16777619u;
    }
    return h;
}
static void search_mark(BYTE *b)
{
    DWORD sig = friends_sig(b);
    int i;
    for (i = 0; i < g_srch_n; i++)
        if (!g_srch[i].key[0]) peer_key(b, g_srch[i].lo, g_srch[i].hi, (unsigned)g_srch[i].slot, g_srch[i].key);
    if (g_srch_marked && sig == g_srch_marked_for) return;
    for (i = 0; i < g_srch_n; i++) {
        g_srch[i].isfriend = find_friend_row(b, g_srch[i].lo, g_srch[i].hi, g_srch[i].slot) >= 0;
        g_srch[i].me = (BYTE)is_me(b, g_srch[i].lo, g_srch[i].hi);
    }
    g_srch_marked = 1;
    g_srch_marked_for = sig;
}
#include "pol_chat.c"

static void wr(HANDLE h, const char *s)
{
    DWORD n;
    WriteFile(h, s, lstrlenA(s), &n, NULL);
}
static void wr_job(HANDLE h, const char *fmt, const char *token, const char *state)
{
    char tk[100], line[400];
    json_str(tk, sizeof tk, token);
    wsprintfA(line, fmt, tk, state);
    wr(h, line);
}
static void snapshot_social(BYTE *b)
{
    static char one[900], mj[28000];
    char path[MAX_PATH + 64], tmp[MAX_PATH + 80], line[900], nm[64], cm[192], gm[112], tk[100], sj[110], sk[17];
    char gn[96], mt[96], mc[0x33 * 3 + 8], cu[0x33 * 3 + 1], u8[0x33 * 3 + 1], js[0x33 * 6 + 8], hn[64] = "\"\"";
    WCHAR wc[0x34];
    DWORD n, ids[8];
    int i, k, ng, st, first = 1, groups = 0, held = 0;
    unsigned pic = 0;
    unsigned __int64 me;
    const char *name;
    grp_hdr gh;
    grp_mem m;
    if (!b) return;
    ipc_file(path, "social.json");
    lstrcpyA(tmp, path); lstrcatA(tmp, ".tmp");
    HANDLE h = CreateFileA(tmp, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    if (SVC(0x604)) groups = ((svc_v)SVC(0x604))();
    wr(h, "{\"groups\":");
    wsprintfA(line, "%d", groups);
    wr(h, line);
    wsprintfA(line, ",\"ffxiService\":\"%s\",\"notice\":%u,\"noticeDays\":%u,\"presence\":\"%s\",\"play\":\"%s\","
                    "\"friendsLoaded\":%s,\"groupsLoaded\":%s,\"mail\":\"%s\",\"statusBusy\":%s,\"badPassword\":%s",
              g_ffxi_service == 1 ? "active" : g_ffxi_service == 0 ? "none" : "unknown", g_notice, g_notice_days,
              g_presence, g_play_state, g_friends_loaded ? "true" : "false", g_groups_loaded ? "true" : "false",
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
        wsprintfA(line, "%s{\"token\":%s,\"state\":%s,\"time\":%lu,\"seq\":%lu}", first ? "" : ",", tk, sj,
                  g_send_hist[k].time, g_send_hist[k].seq);
        wr(h, line);
        first = 0;
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
    if (SVC(0x648) && SVC(0x618) && SVC(0x61c)) {
        ng = ((int (__cdecl *)(DWORD *))SVC(0x648))(ids);
        wr(h, ",\"groupList\":[");
        for (int g = 0, gfirst = 1; g < ng && g < 4; g++) {
            if (!get_group(b, ids[2 * g], ids[2 * g + 1], &gh)) continue;
            sjis_json(gn, sizeof gn, (const BYTE *)gh.name, 0x14);
            wsprintfA(one, "%s{\"id\":\"%08x%08x\",\"name\":%s,\"myClass\":%u,\"mySlot\":%u,\"myStatus\":%u,"
                           "\"myIndex\":%u,\"members\":[",
                      gfirst ? "" : ",", gh.idHi, gh.idLo, gn, (gh.flags >> 1) & 7, (gh.flags >> 4) & 0x3f,
                      (gh.flags >> 10) & 7, gh.myIndex);
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
                wsprintfA(one, "%s{\"index\":%d,\"name\":%s,\"key\":\"%s\",\"pic\":%u,\"class\":%u,\"status\":%u,"
                               "\"me\":%s,\"ignored\":%s,\"text\":%s,\"comment\":%s}",
                          mfirst ? "" : ",", mi, nm, sk, m.pic, (m.flags >> 18) & 7, (m.flags >> 4) & 7,
                          ((m.flags >> 21) & 1) ? "true" : "false", ((m.flags >> 1) & 1) ? "true" : "false", mt, mc);
                wr(h, one);
                mfirst = 0;
            }
            wr(h, "]}");
        }
        wr(h, "]");
    }
    /* GetMyComment (svc 0x688) and GetMyStatus (0x338) are local reads, fine every 2 s */
    if (g_my_slot >= 0 && SVC(0x688)) {
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
        if (slot >= 0 && get_handle(b, slot, rec) == 0) {   /* unwrapped, the log budget's for FFXI's world login */
            sjis_json(hn, sizeof hn, rec + 0x08, 0x0f);
            pic = *(unsigned *)(rec + 0x1c);
        }
    }
    wr(h, ",\"myHandle\":"); wr(h, hn);
    me = my_id(b);
    sk[0] = 0;
    if (me && g_my_slot >= 0) peer_key(b, (unsigned)me, (unsigned)(me >> 32), (unsigned)g_my_slot, sk);
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
        /* location = A+0x0c bits 1-10 (1000/1001 = in the viewer or chat, where real-time messages reach) */
        wsprintfA(line, "%s{\"row\":%d,\"key\":\"%s\",\"name\":%s,\"comment\":%s,\"status\":\"%s\",\"online\":%s,"
                        "\"pending\":%s,\"idLow\":%u,\"idHigh\":%u,\"pic\":%u,\"location\":%u,\"a08\":%u,\"me\":%s%s}",
                  first ? "" : ",", i, sk, nm, cm, friend_status(fa),
                  friend_online(fa) ? "true" : "false", (fa & 0x10000000) ? "true" : "false", idLo, idHi,
                  *(unsigned *)(bn + 0x00), (*(unsigned *)(a + 0x0c) >> 1) & 0x3ff, fa,
                  is_me(b, idLo, idHi) ? "true" : "false", gm);
        wr(h, line);
        first = 0;
    }
    wr(h, "],\"messages\":[");
    /* states 0-3, oldest first since slots get reused */
    for (i = mail_next(0, 0xf), first = 1; i >= 0; i = mail_next(g_mail[i].mid, 0xf)) {
        const mailrec *ml = &g_mail[i];
        int o = 0, hold = mail_held(ml);
        held += hold;
        if (!first) mj[o++] = ',';
        o += msg_json(b, mj + o, (int)sizeof mj - o, ml->hdr, ml->mid,
                      ml->state == 3 ? "realtime" : ml->state == 2 ? "failed" : ml->state == 1 && !hold ? "read"
                                                                                                   : "pending",
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
/* Play is the one-shot POL_CRED_PLAY entry, user "play", wrapped so nothing else can forge it */
static int play_signalled(void)
{
    static WCHAR tbuf[256];
    static const WCHAR *t;
    static int from_env, resolved, warned;
    char user[64], go[64];
    if (!resolved) {
        t = cred_target("POL_CRED_PLAY", L"", tbuf, 256, &from_env);
        resolved = 1;
    }
    if (!t || !from_env) return 0;
    int rc = cred_read(t, user, sizeof user, go, sizeof go);
    SecureZeroMemory(go, sizeof go);
    if (rc == PC_OK && strcmp(user, "play") == 0) {
        if (launch_target(t)) CredDeleteW(t, CRED_TYPE_GENERIC, 0);
        return 1;
    }
    if (rc == PC_NOT_FOUND) {
        warned = 0;
    } else if (rc != PC_OK && !warned) {
        warned = 1;   /* once, Provenance takes this line as a failed start */
        lg("hold: Play entry unreadable (%s) - ignored\r\n", cred_why(rc));
    }
    return 0;
}
static int g_online_started;
static void go_online(BYTE *b)
{
    char nm[16], sv[8];
    int want = 0;
    if (g_online_started) return;
    if (notice_blocks(g_notice)) return;
    if (GetEnvironmentVariableA("POL_STATUS", sv, sizeof sv) > 0 &&
        (sv[0] == '0' || sv[0] == '1' || sv[0] == '4'))
        want = sv[0] - '0';
    g_my_slot = my_handle(b, nm);
    if (g_my_slot < 0) return;
    g_online_started = 1;
    status_start(b, g_my_slot, want, 1000);
}

static void db_reads_abandon(BYTE *b)
{
    if (g_prof_step != 0) profile_abandon(b, "cancelled");
    if (g_ch_step != 0) character_abandon(b, "cancelled");
    if (g_fp_step != 0) ffxiprofile_abandon(b, "cancelled");
    if (g_srch_step != 0) search_abandon(b, "cancelled");
}

static int writes_pending(void)
{
    return g_sq_n || g_status_job >= 0 || g_edit_job >= 0 || g_edit_upload_want ||
           g_comment_wanted || g_pic_want >= 0 || g_grp_job >= 0 || g_answer_kind || g_me_step >= 3 || g_hop;
}
/* writes that are due but haven't got a job slot yet, or an answer's message still to come */
static int writes_waiting(void)
{
    return send_built() || g_answer_kind || g_edit_upload_want || g_comment_wanted || g_me_step == 3 || g_hwant;
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
    handle_pump(b, 0);
    Sleep(16);
}

/* the viewer sets polcore's idle timeout from option.bin's GOPN1_AO at login and FFXI only ever switches the timer on,
   so with the hold's 0 it'd never fire in game. Off still till FFXI turns it on, same as the viewer leaves it */
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

/* writes under way get 5 s to finish, then the viewer's sign-out */
static void hold_sign_out(BYTE *b, const char *spath)
{
    ULONGLONG until = GetTickCount64() + 5000;
    DeleteFileA(spath);
    store_drop();
    g_draining = 1;
    if (!g_lean) mail_ack_request();
    me_abandon(b, "cancelled");
    while (writes_pending() && GetTickCount64() < until)
        drain_turn(b, !g_lean);
    viewer_sign_out(b);
    lg("hold: signed out\r\n");
    end_pol(0);
}

/* held until Play, a stop, Provenance ending or pol.exe ending. It's an IRC link so it never stops pumping, and never
   sends more than the viewer would. */
static void pol_hold(BYTE *b)
{
    static ULONGLONG lastCtl, lastStatusReq, lastMailReq;
    ULONGLONG lastSnap = 0, until;
    char spath[MAX_PATH + 64], path[MAX_PATH + 64], num[16], sv[8], rq[16];
    int ctl, wctl, idle, reason, why, want;
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
    /* the viewer's auto log-off option (GOPN1_AO), which polcore starts off: keep it that way */
    if (SVC(0x11d4)) ((void (__cdecl *)(int))SVC(0x11d4))(0);
    if (!g_lean && SVC(0xd9c)) {
        void *cb[6];
        push_callbacks(b, cb);
        ((void (__cdecl *)(void **))SVC(0xd9c))(cb);
        lg("mail: push callbacks registered (svc 0xd9c)\r\n");
    }
    if (!g_lean && !g_online_started) {
        go_online(b);
        if (!g_online_started) {
            struct pload hl = { "handles (hold)", 0x2ac, 0x2b0, 0, 0 };
            run_loads(b, &hl, 1);
            go_online(b);
        }
        if (!g_online_started) {
            lg("presence: no PlayOnline handle - cannot go online\r\n");
            lstrcpyA(g_presence, "no-handle");
        }
    }
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
            lg("hold: SESSION LOST reason=%d - signed out\r\n", why);
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
        if (ctl && SVC(0x13e4) && ((svc_v)SVC(0x13e4))()) {
            DeleteFileA(spath);
            store_drop();
            account_blocked(b, "jail");
        }
        status_pump(b);
        if (g_status_job < 0 && g_status_want >= 0 && g_my_slot >= 0)
            status_start(b, g_my_slot, g_status_want, g_status_loc);
        /* the new-mail flag (svc 0x1460, set by a push), cleared with 0x1464(0) like the viewer (app.dll 0x1019fef3) */
        if (!g_lean && SVC(0x1460) && ((svc_v)SVC(0x1460))()) {
            ((void (__cdecl *)(int))SVC(0x1464))(0);
            lg("mail: new-mail flag set - listing the inbox\r\n");
            g_mail_refresh = 1;
        }
        if (ctl && !g_lean) mail_ack_request();
        if (!g_lean) mail_pump(b);
        handle_pump(b, ctl);
        /* nothing new starts mid-switch, it'd go under whichever handle polcore has by then */
        wctl = ctl && !handle_switching();
        mail_send_pump(b);
        friend_edit_pump(b, wctl);
        search_pump(b, wctl);
        profile_pump(b, wctl);
        character_pump(b, wctl);
        ffxiprofile_pump(b, wctl);
        group_pump(b, wctl);
        me_pump(b, wctl);
        answer_pump(b, wctl);
        pic_pump(b, wctl);
        if (!g_lean) chat_pump(b, wctl);
        if (ctl && play_signalled()) {
            if (g_ffxi_service != 1 || notice_blocks(g_notice)) {
                const char *no = g_ffxi_service == 0 ? "refused:no-content-id"
                               : g_ffxi_service < 0 ? "refused:service-unknown" : "refused:account-notice";
                if (lstrcmpA(g_play_state, no) != 0) {
                    lstrcpyA(g_play_state, no);
                    lg("hold: Play %s\r\n", no);
                    g_snap_dirty = 1;
                }
            } else {
                until = GetTickCount64() + 15000;
                lg("hold: Play signalled - finishing PlayOnline writes, then booting FFXI\r\n");
                /* only writes hold Play, 15 s at most. An inbox read or delete is cancelled after the wait: a cancel
                   stalls job polls 10 s, which FFXI's start-up sits out */
                g_draining = 1;
                if (!g_lean) mail_ack_request();
                db_reads_abandon(b);
                me_abandon(b, "cancelled");
                chat_end(b);
                while (writes_pending() && GetTickCount64() < until)
                    drain_turn(b, !g_lean);
                for (int i = 0; i < MAIL_LANES; i++) if (g_rd[i].job >= 0) CANCEL_JOB(g_rd[i].job);
                if (g_del_job >= 0) CANCEL_JOB(g_del_job);
                for (int i = 0; i < g_sq_n; i++) if (g_sq[i].job >= 0) CANCEL_JOB(g_sq[i].job);
                if (g_status_job >= 0) CANCEL_JOB(g_status_job);
                if (g_hjob >= 0) CANCEL_JOB(g_hjob);
                g_in_hold = 0;
                if (g_edit_job >= 0) CANCEL_JOB(g_edit_job);
                if (g_pic_want >= 0) profile_pic_cancel(b);
                if (g_grp_job >= 0) CANCEL_JOB(g_grp_job);
                /* FFXI installs its own message handlers, so polcore gets its defaults back */
                if (SVC(0xda0)) ((void (__cdecl *)(void))SVC(0xda0))();
                /* the viewer always has notices on when a game starts, without them FFXI only sees new messages at its
                   next lobby login. A local flag, nothing goes on the wire */
                if (g_lean && SVC(0x1020)) ((svc_i)SVC(0x1020))(1);
                idle_timeout_for_game(b);
                DeleteFileA(spath);   /* the snapshot has friends' messages in it */
                store_drop();
                /* the game keeps going whatever happens to Provenance */
                if (g_owner) CloseHandle(g_owner);
                g_owner = NULL;
                lg("hold: booting FFXI\r\n");
                return;
            }
        }
        /* Only an admin can write the stop file in Provenance's folder, but in %TEMP% (manual runs) any user process
           can, so tools can end an elevated pol.exe cleanly */
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
        if (wctl && g_status_job < 0 && GetTickCount64() - lastStatusReq >= 2000 &&
            take_request("status", sv, sizeof sv) >= 0) {
            want = sv[0] - '0';
            lastStatusReq = GetTickCount64();
            /* the viewer's OK maps busy (2) to online and 3 to appear offline */
            if (want == 2) want = 0;
            if (want == 3) want = 4;
            if (want == 0 || want == 1 || want == 4) {
                if (g_my_slot >= 0) status_start(b, g_my_slot, want, -2);   /* -2 keeps the rest, never handle -1 */
                else lg("presence: status %d ignored - no handle selected\r\n", want);
            }
        }
        if (ctl && request_waiting("mail") && (!lastMailReq || GetTickCount64() - lastMailReq >= 30000) &&
            take_request("mail", rq, sizeof rq) >= 0) {
            lastMailReq = GetTickCount64();
            g_mail_refresh = 1;
            g_mail_fails = 0;
        }
        if ((g_snap_dirty && GetTickCount64() - lastSnap >= 250) || GetTickCount64() - lastSnap > 2000) {
            g_snap_dirty = 0;
            snapshot_social(b);
            lastSnap = GetTickCount64();
        }
        Sleep(16);
    }
}

static DWORD WINAPI fx_hook_once(void *unused)
{
    (void)unused;
    for (int i = 0; i < 1500 && !g_fx_iat_done; i++) {
        BYTE *fx = (BYTE *)GetModuleHandleA("FFXiMain.dll");
        if (fx) { patch_fx_sockets(fx); break; }
        Sleep(200);
    }
    return 0;
}

#define GAME_START_EXIT 15

/* add-ons and game plugins already unloaded inside FFXI's exit. Ashita hooks ExitProcess to unload its POL plugins
   and finish its log, so it gets a clean exit, anything else is just more places to crash on the way out */
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

#define WATCH_POLL_MS 500
#define WATCH_GRACE_MS 15000
enum { WATCH_WAIT, WATCH_GONE, WATCH_BACK, WATCH_END };
struct game_watch { int seen, gone; ULONGLONG gone_at; };
/* a window that's back before the grace runs out means it wasn't over */
static int watch_step(struct game_watch *w, int window, ULONGLONG now)
{
    if (window) {
        int back = w->gone;
        w->seen = 1;
        w->gone = 0;
        return back ? WATCH_BACK : WATCH_WAIT;
    }
    if (!w->seen) return WATCH_WAIT;
    if (!w->gone) {
        w->gone = 1;
        w->gone_at = now;
        return WATCH_GONE;
    }
    return now - w->gone_at >= WATCH_GRACE_MS ? WATCH_END : WATCH_WAIT;
}

/* every pol.exe's game window has this class, so only ours counts */
static int is_game_window(HWND h)
{
    char cls[16];
    DWORD pid;
    return GetClassNameA(h, cls, sizeof cls) && !lstrcmpiA(cls, "FFXiClass") && GetWindowThreadProcessId(h, &pid) &&
           pid == GetCurrentProcessId();
}
/* kept once found, FindWindowEx only sees top-level windows and a reparented one would look gone */
static int game_window_up(HWND *h)
{
    HWND w = NULL;
    if (*h && IsWindow(*h) && is_game_window(*h)) return 1;
    *h = NULL;
    while ((w = FindWindowExA(NULL, w, "FFXiClass", NULL)) != NULL)
        if (is_game_window(w)) {
            *h = w;
            return 1;
        }
    return 0;
}

#ifndef WER_FAULT_REPORTING_NO_UI
#define WER_FAULT_REPORTING_NO_UI 0x20
#endif
typedef HRESULT (WINAPI *wer_get_t)(HANDLE, PDWORD);
typedef HRESULT (WINAPI *wer_set_t)(DWORD);
static UINT g_err_mode;
static DWORD g_wer_flags;
/* nobody needs a "pol.exe has stopped working" box after they've quit the game */
static void quiet_crashes(void)
{
    HMODULE k = GetModuleHandleW(L"kernel32.dll");
    wer_get_t get = (wer_get_t)GetProcAddress(k, "WerGetFlags");
    wer_set_t set = (wer_set_t)GetProcAddress(k, "WerSetFlags");
    g_wer_flags = 0;
    if (get && get(GetCurrentProcess(), &g_wer_flags) != S_OK) g_wer_flags = 0;
    if (set) set(g_wer_flags | WER_FAULT_REPORTING_NO_UI);
    g_err_mode = GetErrorMode();
    SetErrorMode(g_err_mode | SEM_NOGPFAULTERRORBOX | SEM_FAILCRITICALERRORS);
}
static void loud_crashes(void)
{
    wer_set_t set = (wer_set_t)GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "WerSetFlags");
    if (set) set(g_wer_flags);
    SetErrorMode(g_err_mode);
}

/* for anything still going once the window's gone, or a missing content launcher hook. Not a hung add-on unload
   though, that's in FFXI's D3D cleanup, before the window goes */
static DWORD WINAPI game_watch(void *unused)
{
    struct game_watch w = { 0 };
    HWND h = NULL;
    (void)unused;
    for (;;) {
        switch (watch_step(&w, game_window_up(&h), GetTickCount64())) {
        case WATCH_GONE:
            quiet_crashes();
            lg("pol_hook: game window gone - crash boxes off, ending pol.exe in %d s if it's still here\r\n",
               WATCH_GRACE_MS / 1000);
            break;
        case WATCH_BACK:
            loud_crashes();
            lg("pol_hook: game window back - not over yet, crash boxes back on\r\n");
            break;
        case WATCH_END:
            lg("pol_hook: still here %d s after the game window went - ending pol.exe\r\n", WATCH_GRACE_MS / 1000);
            end_pol(0);   /* even under Ashita, something's stuck and a clean exit could get stuck behind it */
            break;
        }
        Sleep(WATCH_POLL_MS);
    }
}

/* The menu session detour, thiscall (this in ecx) + 4 args, callee pops 16 bytes. Returning 1 takes pol.exe's
   dispatch (0x0041406e) down its FFXI case: 0x00413140 CoCreates FFXiEntry {989D790D}, which boots FFXiMain. */
static int g_menu_calls;
static unsigned __stdcall menu_stub(unsigned a, unsigned b, unsigned c, unsigned d)
{
    (void)a; (void)b; (void)c; (void)d;
    if (++g_menu_calls == 1) {
        prime_session();
        set_cwd_ffxi();
        /* pol.exe 0x00406a10 destroys the hidden splash on the thread that made it */
        ((void (__cdecl *)(void))((BYTE *)GetModuleHandleW(NULL) + 0x6a10))();
        if (getenv_set("POL_HOLD")) {
            sweep_stale_social();
            pol_hold((BYTE *)polcore_module());
        }
        lg("pol_hook: menu call 1 -> content code 1 (FFXiEntry)\r\n");
        g_game_started = 1;
        CloseHandle(CreateThread(NULL, 0, fx_hook_once, NULL, 0, NULL));
        CloseHandle(CreateThread(NULL, 0, game_watch, NULL, 0, NULL));
        return 1;
    }
    /* the game came back to the menu (closed, or it failed): end it here, never relaunch */
    lg("pol_hook: menu call %d -> content exited, staying exited\r\n", g_menu_calls);
    game_over(0);
    return 1000;
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
    /* An E9 jmp over the menu session (pol.exe 0x00413710), only in the 1.18.15e pol.exe I wrote this against.
       Other builds (1.18.00n for one) have other code here. */
    BYTE *p = (BYTE *)exe + 0x13710;
    if (memcmp(p, MENU_PROLOGUE, sizeof MENU_PROLOGUE) != 0) {
        lg("pol_hook: pol.exe is not the 1.18.15e build (menu session %02x %02x %02x) - not hooked\r\n",
           p[0], p[1], p[2]);
        return;
    }
    if (!VirtualProtect(p, 5, PAGE_EXECUTE_READWRITE, &old)) return;
    rel = (DWORD)((BYTE *)menu_stub - (p + 5));
    p[0] = 0xE9;
    *(DWORD *)(p + 1) = rel;
    VirtualProtect(p, 5, old, &old);
    lg("pol_hook: menu session detoured -> %08x\r\n", (unsigned)(ULONG_PTR)menu_stub);
    hook_game_end((BYTE *)exe);
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
