static CRITICAL_SECTION g_lgcs;
static int g_lgcs_ready;

static int base_getenv_set(const char *name) { return GetEnvironmentVariableA(name, NULL, 0) > 0; }

/* pol.exe turns the screen saver off at start and only puts it back in its own cleanup, which base_end_pol skips */
static int g_saver = -1;
static void base_saver_save(void)
{
    char v[4];
    BOOL on;
    if (GetEnvironmentVariableA(ENV_SCREENSAVER, v, sizeof v) == 1 && (v[0] == '0' || v[0] == '1'))
        g_saver = v[0] - '0';
    else if (SystemParametersInfoW(SPI_GETSCREENSAVEACTIVE, 0, &on, 0))
        g_saver = on != 0;
}
static int base_others_running(void)
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
static void base_saver_restore(void)
{
    if (g_saver >= 0 && !base_others_running()) SystemParametersInfoW(SPI_SETSCREENSAVEACTIVE, (UINT)g_saver, NULL, 0);
}

/* Sleep(1) lasts a whole 15.6ms tick in pol.exe unless something asks for a finer one */
static UINT (WINAPI *g_time_begin)(UINT), (WINAPI *g_time_end)(UINT);
static int g_timer_on;
static void base_timer_find(void)
{
    HMODULE mm;
    if (g_time_begin && g_time_end) return;
    mm = GetModuleHandleW(L"winmm.dll");
    if (!mm) mm = LoadLibraryExW(L"winmm.dll", NULL, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!mm) return;
    g_time_begin = (UINT (WINAPI *)(UINT))GetProcAddress(mm, "timeBeginPeriod");
    g_time_end = (UINT (WINAPI *)(UINT))GetProcAddress(mm, "timeEndPeriod");
}
static void base_timer_end(void)
{
    if (!g_timer_on) return;
    g_timer_on = 0;
    g_time_end(1);
}

/* Windower 4's Hook.dll fails fast (0xc0000409) in its detach code when the game never started, so TerminateProcess */
NORETURN static void base_end_pol(UINT code)
{
    base_timer_end();
    base_saver_restore();
    TerminateProcess(GetCurrentProcess(), code);
    ExitProcess(code);
}

static void base_ipc_dir(char out[MAX_PATH])
{
    DWORD n = GetEnvironmentVariableA(ENV_IPC_DIR, out, MAX_PATH), a;
    if (n == 0) base_end_pol(NO_ENV_EXIT);
    a = n < MAX_PATH - 64 ? GetFileAttributesA(out) : INVALID_FILE_ATTRIBUTES;
    if (a == INVALID_FILE_ATTRIBUTES || !(a & FILE_ATTRIBUTE_DIRECTORY)) base_end_pol(IPC_DIR_EXIT);
    if (out[n - 1] != '\\' && out[n - 1] != '/') { out[n] = '\\'; out[n + 1] = 0; }
}

static void base_ipc_file(char *out, const char *ext)
{
    base_ipc_dir(out);
    wsprintfA(out + lstrlenA(out), IPC_NAME, GetCurrentProcessId(), ext);
}

typedef struct { HANDLE h; char path[MAX_PATH + 64], tmp[MAX_PATH + 80]; } ipc_json;
static int base_ipc_json_open(const char *ext, ipc_json *f)
{
    base_ipc_file(f->path, ext);
    lstrcpyA(f->tmp, f->path);
    lstrcatA(f->tmp, ".tmp");
    f->h = CreateFileA(f->tmp, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    return f->h != INVALID_HANDLE_VALUE;
}
static int base_ipc_json_write(ipc_json *f, const char *p, int n)
{
    DWORD wn = 0;
    return WriteFile(f->h, p, (DWORD)n, &wn, NULL) && wn == (DWORD)n;
}
static int base_ipc_json_commit(ipc_json *f, int ok)
{
    CloseHandle(f->h);
    ok = ok && MoveFileExA(f->tmp, f->path, MOVEFILE_REPLACE_EXISTING);
    if (!ok) DeleteFileA(f->tmp);
    return ok;
}

enum { LOG_LINE = 1100, LOG_RING = 64 };
/* the game's thread does no file work, its lines wait here for the worker */
static char g_log_ring[LOG_RING][LOG_LINE];
static volatile LONG g_log_head, g_log_tail, g_log_lost, g_log_queue;
static volatile DWORD g_log_thread;

static void base_log_write(const char *buf)
{
    char path[MAX_PATH + 64];
    DWORD n;
    base_ipc_file(path, IPC_LOG);
    HANDLE h = CreateFileA(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h != INVALID_HANDLE_VALUE) {
        WriteFile(h, buf, lstrlenA(buf), &n, NULL);
        CloseHandle(h);
    }
}

static void base_log_drain(void)
{
    static volatile LONG draining;
    char buf[LOG_LINE];
    LONG lost;
    /* never the game's thread, so waiting on another drain is fine */
    while (InterlockedCompareExchange(&draining, 1, 0)) Sleep(0);
    if (g_lgcs_ready) EnterCriticalSection(&g_lgcs);
    while (g_log_tail - g_log_head > 0) {
        base_log_write(g_log_ring[g_log_head % LOG_RING]);
        InterlockedIncrement(&g_log_head);
    }
    if ((lost = InterlockedExchange(&g_log_lost, 0)) != 0) {
        wsprintfA(buf, "%ld log line(s) from the game's thread were dropped, the queue was full\r\n", lost);
        base_log_write(buf);
    }
    if (g_lgcs_ready) LeaveCriticalSection(&g_lgcs);
    InterlockedExchange(&draining, 0);
}

static void base_log(const char *fmt, ...)
{
    char buf[LOG_LINE];
    SYSTEMTIME st;
    va_list ap;
    GetLocalTime(&st);
    wsprintfA(buf, "[%02u:%02u:%02u.%03u] ", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
    va_start(ap, fmt);
    wvsprintfA(buf + lstrlenA(buf), fmt, ap);
    va_end(ap);
    if (g_log_queue && GetCurrentThreadId() == g_log_thread) {
        if (g_log_tail - g_log_head >= LOG_RING) {
            InterlockedIncrement(&g_log_lost);
            return;
        }
        lstrcpyA(g_log_ring[g_log_tail % LOG_RING], buf);
        InterlockedIncrement(&g_log_tail);
        return;
    }
    if (g_lgcs_ready) EnterCriticalSection(&g_lgcs);
    base_log_write(buf);
    if (g_lgcs_ready) LeaveCriticalSection(&g_lgcs);
}

static void base_timer_begin(void)
{
    if (g_timer_on) return;
    base_timer_find();
    if (!g_time_begin || !g_time_end || g_time_begin(1) != 0) {
        base_log("login: no 1ms timer, the pump sleeps at the system's pace\r\n");
        return;
    }
    g_timer_on = 1;
    base_log("login: 1ms timer on until the session's ready\r\n");
}

static void base_env_check(void)
{
    if (base_getenv_set(ENV_HOLD) && base_getenv_set(ENV_CRED_KEYFILE)) return;
    base_log("pol_hook: no POL_HOLD or POL_CRED_KEYFILE, so not started by Provenance - ending pol.exe (exit %d)\r\n",
       NO_ENV_EXIT);
    base_end_pol(NO_ENV_EXIT);
}

static int base_stop_requested(void)
{
    char p[MAX_PATH + 64];
    base_ipc_file(p, IPC_STOP);
    return GetFileAttributesA(p) != INVALID_FILE_ATTRIBUTES;
}

/* held by handle, so a reused pid can't stand in for it */
static HANDLE g_owner;
static int g_owner_gone;
static ULONGLONG g_owner_checked;
static void base_owner_open(void)
{
    char v[16];
    DWORD n = GetEnvironmentVariableA(ENV_OWNER, v, sizeof v), pid, err;
    if (n == 0 || n >= sizeof v || (pid = (DWORD)atoi(v)) == 0) return;
    g_owner = OpenProcess(SYNCHRONIZE, FALSE, pid);
    if (g_owner) { base_log("pol_hook: owned by Provenance pid %u\r\n", pid); return; }
    err = GetLastError();
    g_owner_gone = err == ERROR_INVALID_PARAMETER;
    base_log("pol_hook: Provenance pid %u can't be watched (error %u)\r\n", pid, err);
}
static int base_owner_gone(void)
{
    if (g_owner && !g_owner_gone && GetTickCount64() - g_owner_checked >= 1000) {
        g_owner_checked = GetTickCount64();
        g_owner_gone = WaitForSingleObject(g_owner, 0) == WAIT_OBJECT_0;
    }
    return g_owner_gone;
}
