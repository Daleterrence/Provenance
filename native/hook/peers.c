static int g_my_slot = -1;

static int get_chr(BYTE *b, int i, pol_chr *rec)
{
    memset(rec, 0, sizeof *rec);
    return pc_has(b, SV_GET_CONTENT_ID) && pc_get_content_id(b, (unsigned)i, rec) == 0 && chr_valid(rec);
}
/* a Handle ID as the key the app knows it by */
static void hid_key(unsigned __int64 hid, char k[17])
{
    wsprintfA(k, "%03x%08x", (unsigned)(hid >> 32), (unsigned)hid);
}
static void chr_key(const pol_chr *c, char k[17])
{
    wsprintfA(k, "%08x%08x", c->ctsid, c->ctid_lo);
}
static int handle_used(const pol_handle *h)
{
    int n = 0;
    for (int k = 0; k < 8; k++) n += h->chars[k] & 1;
    return n;
}

/* one rule for every handle name: 1-15 printable ASCII bytes (0x7f is out) and not all blanks */
static int ascii_name_ok(const char *s)
{
    int n = 0, visible = 0;
    for (; *s; s++, n++) {
        if ((BYTE)*s < 0x20 || (BYTE)*s > 0x7e) return 0;
        if (*s != ' ') visible = 1;
    }
    return n >= 1 && n <= 15 && visible;
}

/* a row's flags >> 13 & 7 is status + 1 (1 online, 2 away, 3 busy), 0 offline. Online also needs 0x800. */
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
/* the game slot the row is in, when it's FFXI. FFXiMain checks the world id (the ctsid's byte 2) against your own the
   same way */
static int friend_ffxi(const pol_friend *f, DWORD *ctsid, DWORD *ctLo, DWORD *ctHi)
{
    unsigned fa = f->flags;
    const pol_game *s = &f->games[(fa >> 17) & 7];
    if (!(fa & 0x800) || !(fa & 0x10000) || (fa & FRIEND_PENDING) || !friend_online(fa)) return 0;
    if (!(s->flags & 1) || s->title != 1) return 0;
    *ctsid = s->ctsid;
    *ctLo = s->ctid_lo;
    *ctHi = s->ctid_hi;
    return 1;
}

/* ids are masked per session and names change, so conversations are keyed on HMAC-SHA256("<my POL ID>/<peer POL
   ID>/<slot>"), 8 bytes in hex. No POL ID ever leaves the hook. */
static char g_my_polid[12];
static BYTE g_peer_secret[32];
static int g_peer_secret_ok;
static void fmt_polid(BYTE *b, unsigned lo, unsigned hi, char out[12])
{
    memset(out, 0, 12);
    pc_format_polid(b, lo, hi, out);
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
/* an old %APPDATA%\Provenance\peerkey.bin is taken once so chat history keeps its keys. Links can be planted there, so
   it's read and deleted through a handle that really is that file */
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
/* kept in the admin-only POL_IPC_DIR */
static int peer_secret(void)
{
    WCHAR path[MAX_PATH + 256], name[256];
    FILE_DISPOSITION_INFO gone = { TRUE };
    HANDLE f, old;
    DWORD n = 0;
    int r;
    if (g_peer_secret_ok) return 1;
    n = GetEnvironmentVariableW(L_(ENV_IPC_DIR), path, MAX_PATH);
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
    if (!pc_has(b, SV_FORMAT_POLID) || !peer_secret()) return;
    if (!g_my_polid[0]) {
        unsigned __int64 me = pc_my_id(b);
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
    if (!pc_has(b, SV_FORMAT_POLID)) return 0;
    unsigned __int64 me = pc_my_id(b);
    if (!me) return 0;
    fmt_polid(b, (unsigned)me, (unsigned)(me >> 32), mine);
    fmt_polid(b, lo, hi, id);
    return mine[0] && lstrcmpA(id, mine) == 0;
}
static int get_friend(BYTE *b, int row, pol_friend_copy *fr)
{
    memset(fr, 0, sizeof *fr);
    return pc_friend_copy(b, row, fr) >= 0 && friend_valid(&fr->f);
}
static void friend_key(BYTE *b, const pol_friend *f, char k[17])
{
    peer_key(b, f->id_lo, f->id_hi, (unsigned)friend_slot(f), k);
}
/* the same test as polcore.dll 0x100254d0 */
static int find_friend_row(BYTE *b, DWORD lo, DWORD hi, int slot)
{
    pol_friend_copy r;
    for (int i = 0; i < 200; i++)
        if (get_friend(b, i, &r) && r.f.id_lo == lo && r.f.id_hi == hi && friend_slot(&r.f) == slot) return i;
    return -1;
}

static int my_handle(BYTE *b, char *name16)
{
    pol_handle h;
    int slot = pc_current_handle(b);
    name16[0] = 0;
    if (slot < 0)
        for (int i = 0; i < 64; i++)
            if (handle_get(b, i, &h)) { slot = i; break; }
    if (pc_get_handle(b, (unsigned)slot, &h) == 0) {
        memcpy(name16, h.name, 15);
        name16[15] = 0;
    }
    return slot;
}
static int slot_name(BYTE *b, int slot, char *name16)
{
    pol_handle h;
    if (!handle_get(b, slot, &h)) return 0;
    memcpy(name16, h.name, 15);
    name16[15] = 0;
    return 1;
}
