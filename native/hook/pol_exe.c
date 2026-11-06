static FARPROC g_pCreateMutexW, g_pOpenMutexW;

/* WinMain's single-instance gates are mutexes. Each name gets "_X" + 16 bytes of HMAC-SHA256(per-process secret,
   name) in hex, so nothing without admin can guess it and squat it first. */
#define MUTEX_NAME_KEEP 200
#define MUTEX_SUFFIX_LEN 34
static BYTE g_mutex_secret[32];
static int g_mutex_secret_ok;
static INIT_ONCE g_mutex_secret_once = INIT_ONCE_STATIC_INIT;
/* pol.exe's first mutex call runs this, so never under the loader lock */
static BOOL CALLBACK pol_exe_mutex_secret_init(PINIT_ONCE once, PVOID param, PVOID *ctx)
{
    (void)once; (void)param; (void)ctx;
    g_mutex_secret_ok =
        BCryptGenRandom(NULL, g_mutex_secret, sizeof g_mutex_secret, BCRYPT_USE_SYSTEM_PREFERRED_RNG) == 0;
    return TRUE;
}
static void pol_exe_mutex_name(LPCWSTR name, WCHAR *out)
{
    BYTE mac[32];
    int i, n = lstrlenW(name);
    InitOnceExecuteOnce(&g_mutex_secret_once, pol_exe_mutex_secret_init, NULL, NULL);
    if (!g_mutex_secret_ok
        || BCryptHash(BCRYPT_HMAC_SHA256_ALG_HANDLE, g_mutex_secret, sizeof g_mutex_secret,
                      (PUCHAR)name, (ULONG)n * sizeof(WCHAR), mac, sizeof mac) != 0) {
        base_log("pol_hook: no safe mutex name (%s) - ending pol.exe (exit %d)\r\n",
           g_mutex_secret_ok ? "HMAC failed" : "BCryptGenRandom failed", MUTEX_EXIT);
        base_end_pol(MUTEX_EXIT);
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
static HANDLE WINAPI pol_exe_my_CreateMutexW(void *sa, BOOL own, LPCWSTR name)
{
    WCHAR buf[MUTEX_NAME_KEEP + MUTEX_SUFFIX_LEN + 1];
    if (!name || !name[0])
        return ((HANDLE(WINAPI *)(void *, BOOL, LPCWSTR))g_pCreateMutexW)(sa, own, name);
    pol_exe_mutex_name(name, buf);
    return ((HANDLE(WINAPI *)(void *, BOOL, LPCWSTR))g_pCreateMutexW)(sa, own, buf);
}
static HANDLE WINAPI pol_exe_my_OpenMutexW(DWORD acc, BOOL inh, LPCWSTR name)
{
    WCHAR buf[MUTEX_NAME_KEEP + MUTEX_SUFFIX_LEN + 1];
    if (!name || !name[0])
        return ((HANDLE(WINAPI *)(DWORD, BOOL, LPCWSTR))g_pOpenMutexW)(acc, inh, name);
    pol_exe_mutex_name(name, buf);
    return ((HANDLE(WINAPI *)(DWORD, BOOL, LPCWSTR))g_pOpenMutexW)(acc, inh, buf);
}

static FARPROC pol_exe_user32_proc(const char *name)
{
    HMODULE u = GetModuleHandleW(L"user32.dll");
    if (!u) u = LoadLibraryW(L"user32.dll");
    return u ? GetProcAddress(u, name) : NULL;
}
/* pol.exe's splash (dialog 0x67, proc exe+0x6800), made hidden so pol.exe 0x00406a10 skips its 5s dwell. Took a
   fair few attempts to scrap it */
typedef HWND (WINAPI *cdip_t)(HINSTANCE, LPCDLGTEMPLATEW, HWND, DLGPROC, LPARAM);
static DWORD g_splash_tmpl[64];   /* DWORDs keep the DLGTEMPLATE copy aligned */
static HWND pol_exe_hidden_splash(HINSTANCE hInst, LPCWSTR tmpl, HWND parent, DLGPROC proc, LPARAM init)
{
    cdip_t cdip = (cdip_t)pol_exe_user32_proc("CreateDialogIndirectParamW");
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
static HWND WINAPI pol_exe_my_CreateDialogParamW(HINSTANCE hInst, LPCWSTR tmpl, HWND parent, DLGPROC proc, LPARAM init)
{
    BYTE *exe = (BYTE *)GetModuleHandleW(NULL);
    if (tmpl == (LPCWSTR)(ULONG_PTR)0x67 && (void *)proc == (void *)(exe + 0x6800)) {
        HWND h = pol_exe_hidden_splash(hInst, tmpl, parent, proc, init);
        base_log("pol_hook: startup splash created hidden hwnd=%08x%s\r\n",
           (unsigned)(ULONG_PTR)h, h ? "" : " (NULL fallback)");
        return h;   /* NULL is pol.exe's own no-splash path, never the real call (it'd flash) */
    }
    if (!g_realCreateDialogParamW)
        g_realCreateDialogParamW = (HWND (WINAPI *)(HINSTANCE, LPCWSTR, HWND, DLGPROC, LPARAM))
            pol_exe_user32_proc("CreateDialogParamW");
    return g_realCreateDialogParamW ? g_realCreateDialogParamW(hInst, tmpl, parent, proc, init) : NULL;
}

/* the real target comes from GetProcAddress, never *slot (the thunk would overwrite the hook) */
static int pol_exe_hook_slot(HMODULE exe, DWORD rva, DWORD thunk_rva, void *hook, const char *what)
{
    void **slot = (void **)((BYTE *)exe + rva);
    DWORD o;
    if (*slot != (void *)((BYTE *)exe + thunk_rva)) {
        base_log("pol_hook: %s slot +%05x = %08x, expected thunk +%05x - skipped\r\n",
           what, (unsigned)rva, (unsigned)(ULONG_PTR)*slot, (unsigned)thunk_rva);
        return 0;
    }
    if (!VirtualProtect(slot, sizeof(void *), PAGE_READWRITE, &o)) {
        base_log("pol_hook: %s slot protect failed\r\n", what);
        return 0;
    }
    *slot = hook;
    VirtualProtect(slot, sizeof(void *), o, &o);
    base_log("pol_hook: %s slot +%05x hooked\r\n", what, (unsigned)rva);
    return 1;
}

static LONG WINAPI crash_filter(EXCEPTION_POINTERS *ep)
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
        base_log(LOG_CRASH "%08x at %08x = %s+%05x\r\n", code, eip, name, eip - base);
    }
    return EXCEPTION_CONTINUE_SEARCH;
}
