#define NORETURN __declspec(noreturn)
static CRITICAL_SECTION g_lgcs;
static int g_lgcs_ready;


static int getenv_set(const char *name) { return GetEnvironmentVariableA(name, NULL, 0) > 0; }

/* pol.exe turns the screen saver off at start and only puts it back in its own cleanup, which end_pol skips. The app
   reads POL_SCREENSAVER first, by DllMain it may already be off */
static int g_saver = -1;
static void saver_save(void)
{
    char v[4];
    BOOL on;
    if (GetEnvironmentVariableA(ENV_SCREENSAVER, v, sizeof v) == 1 && (v[0] == '0' || v[0] == '1'))
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
NORETURN static void end_pol(UINT code)
{
    saver_restore();
    TerminateProcess(GetCurrentProcess(), code);
    ExitProcess(code);   /* only if TerminateProcess somehow comes back */
}

/* Provenance's admin-only POL_IPC_DIR. Missing, too long or not a folder ends pol.exe so nothing lands anywhere
   readable. No lg() here, it'd recurse */
static void ipc_dir(char out[MAX_PATH])
{
    DWORD n = GetEnvironmentVariableA(ENV_IPC_DIR, out, MAX_PATH), a;
    if (n == 0) end_pol(NO_ENV_EXIT);
    a = n < MAX_PATH - 64 ? GetFileAttributesA(out) : INVALID_FILE_ATTRIBUTES;
    if (a == INVALID_FILE_ATTRIBUTES || !(a & FILE_ATTRIBUTE_DIRECTORY)) end_pol(IPC_DIR_EXIT);
    if (out[n - 1] != '\\' && out[n - 1] != '/') { out[n] = '\\'; out[n + 1] = 0; }
}

static void ipc_file(char *out, const char *ext)
{
    ipc_dir(out);
    wsprintfA(out + lstrlenA(out), IPC_NAME, GetCurrentProcessId(), ext);
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
    ipc_file(path, IPC_LOG);
    HANDLE h = CreateFileA(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h != INVALID_HANDLE_VALUE) {
        WriteFile(h, buf, lstrlenA(buf), &n, NULL);
        CloseHandle(h);
    }
    if (g_lgcs_ready) LeaveCriticalSection(&g_lgcs);
}

/* every Provenance start sets these (and POL_IPC_DIR, which ipc_dir checks) */
static void env_check(void)
{
    if (getenv_set(ENV_HOLD) && getenv_set(ENV_CRED_KEYFILE)) return;
    lg("pol_hook: no POL_HOLD or POL_CRED_KEYFILE, so not started by Provenance - ending pol.exe (exit %d)\r\n",
       NO_ENV_EXIT);
    end_pol(NO_ENV_EXIT);
}

static int stop_requested(void)
{
    char p[MAX_PATH + 64];
    ipc_file(p, IPC_STOP);
    return GetFileAttributesA(p) != INVALID_FILE_ATTRIBUTES;
}

/* POL_OWNER is the Provenance that started this pol.exe. Held by handle, so a reused pid can't stand in for it */
static HANDLE g_owner;
static int g_owner_gone;
static ULONGLONG g_owner_checked;
static void owner_open(void)
{
    char v[16];
    DWORD n = GetEnvironmentVariableA(ENV_OWNER, v, sizeof v), pid, err;
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
