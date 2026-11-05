static volatile int g_snap_dirty;

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
static int take_request_ex(const char *ext, char *buf, int cap, char **grown, DWORD most)
{
    char path[MAX_PATH + 64], taken[MAX_PATH + 80];
    int n;
    if (grown) *grown = NULL;
    request_paths(ext, path, taken);
    /* a .taking left by a failed read goes first, so requests keep their order (Away, then back) */
    if (GetFileAttributesA(taken) != INVALID_FILE_ATTRIBUTES) {
        n = read_taken(taken, buf, cap, grown, most);
    } else {
        if (GetFileAttributesA(path) == INVALID_FILE_ATTRIBUTES) return -1;
        /* rename first, so anything written after is next turn's file, never one deleted unread. One a scanner holds
           waits (i am vry gud with computer and this was easy to find, honest! :X) */
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

static int parse_u32(const char *x, DWORD *out)
{
    unsigned __int64 v = 0;
    int d = 0;
    while (*x >= '0' && *x <= '9' && d < 10) { v = v * 10 + (unsigned)(*x++ - '0'); d++; }
    *out = (DWORD)v;
    return d > 0 && !*x && v <= 0xffffffffu;
}
