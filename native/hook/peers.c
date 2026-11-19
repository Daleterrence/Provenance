static int g_my_slot = -1;

static int peers_get_chr(BYTE *b, int i, pol_chr *rec)
{
    memset(rec, 0, sizeof *rec);
    return pc_has(b, SV_GET_CONTENT_ID) && pc_get_content_id(b, (unsigned)i, rec) == 0 && pc_chr_valid(rec);
}
static void peers_hid_key(unsigned __int64 hid, char k[17])
{
    wsprintfA(k, "%03x%08x", (unsigned)(hid >> 32), (unsigned)hid);
}
static void peers_chr_key(const pol_chr *c, char k[17])
{
    wsprintfA(k, "%08x%08x", c->ctsid, c->ctid_lo);
}
static int peers_handle_used(const pol_handle *h)
{
    int n = 0;
    for (int k = 0; k < 8; k++) n += h->chars[k] & 1;
    return n;
}

static int peers_ascii_name_ok(const char *s)
{
    int n = 0, visible = 0;
    for (; *s; s++, n++) {
        if ((BYTE)*s < 0x20 || (BYTE)*s > 0x7e) return 0;
        if (*s != ' ') visible = 1;
    }
    return n >= 1 && n <= 15 && visible;
}

/* a row's flags >> 13 & 7 is status + 1 (1 online, 2 away, 3 busy), 0 offline. Online also needs 0x800. */
static int peers_friend_online(unsigned fa)
{
    unsigned s = (fa >> 13) & 7;
    return s >= 1 && s <= 3 && ((fa & 0x800) || s != 1);
}
static const char *peers_friend_status(unsigned fa)
{
    if (!peers_friend_online(fa)) return "offline";
    switch ((fa >> 13) & 7) {
        case 2: return "away";
        case 3: return "busy";
        default: return "online";
    }
}
static int peers_friend_ffxi(const pol_friend *f, DWORD *ctsid, DWORD *ctLo, DWORD *ctHi)
{
    unsigned fa = f->flags;
    const pol_game *s = &f->games[(fa >> 17) & 7];
    if (!(fa & 0x800) || !(fa & 0x10000) || (fa & FRIEND_PENDING) || !peers_friend_online(fa)) return 0;
    if (!(s->flags & 1) || s->title != 1) return 0;
    *ctsid = s->ctsid;
    *ctLo = s->ctid_lo;
    *ctHi = s->ctid_hi;
    return 1;
}

/* ids are masked per session and names change, so conversations are keyed on HMAC-SHA256("<my POL ID>/<peer POL ID>/<slot>"), 8 bytes in hex */
static char g_my_polid[64];
static BYTE g_peer_secret[32];
static int g_peer_secret_ok;
static void peers_fmt_polid(BYTE *b, unsigned lo, unsigned hi, char out[64])
{
    memset(out, 0, 64);
    pc_format_polid(b, lo, hi, out);
    out[8] = 0;
}
static int peers_secret_read(const WCHAR *path)
{
    DWORD n = 0;
    HANDLE f = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, NULL);
    if (f == INVALID_HANDLE_VALUE) return -1;
    g_peer_secret_ok = ReadFile(f, g_peer_secret, sizeof g_peer_secret, &n, NULL) && n == sizeof g_peer_secret;
    CloseHandle(f);
    return g_peer_secret_ok;
}
static int peers_secret_name(WCHAR *out, int cap)
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
static int peers_secret(void)
{
    WCHAR path[MAX_PATH + 256], name[256];
    HANDLE f;
    DWORD n = 0;
    int r;
    if (g_peer_secret_ok) return 1;
    n = GetEnvironmentVariableW(WIDE(ENV_IPC_DIR), path, MAX_PATH);
    if (!n || n >= MAX_PATH || !peers_secret_name(name, 256)) return 0;
    if (path[n - 1] != L'\\' && path[n - 1] != L'/') lstrcatW(path, L"\\");
    lstrcatW(path, name);
    if ((r = peers_secret_read(path)) >= 0) return r;
    if (BCryptGenRandom(NULL, g_peer_secret, sizeof g_peer_secret, BCRYPT_USE_SYSTEM_PREFERRED_RNG) != 0) return 0;
    f = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) r = peers_secret_read(path) > 0;
    else {
        g_peer_secret_ok = WriteFile(f, g_peer_secret, sizeof g_peer_secret, &n, NULL) && n == sizeof g_peer_secret;
        CloseHandle(f);
        r = g_peer_secret_ok;
    }
    return r;
}
static void peers_key(BYTE *b, unsigned lo, unsigned hi, unsigned slot, char out[17])
{
    char id[64], msg[40];
    BYTE mac[32];
    out[0] = 0;
    if (!pc_has(b, SV_FORMAT_POLID) || !peers_secret()) return;
    if (!g_my_polid[0]) {
        unsigned __int64 me = pc_my_id(b);
        if (!me) return;
        peers_fmt_polid(b, (unsigned)me, (unsigned)(me >> 32), g_my_polid);
    }
    peers_fmt_polid(b, lo, hi, id);
    wsprintfA(msg, "%s/%s/%u", g_my_polid, id, slot & 0x3f);
    if (BCryptHash(BCRYPT_HMAC_SHA256_ALG_HANDLE, g_peer_secret, sizeof g_peer_secret,
                   (PUCHAR)msg, (ULONG)lstrlenA(msg), mac, sizeof mac) != 0) return;
    for (int i = 0; i < 8; i++) {
        out[2 * i] = "0123456789abcdef"[mac[i] >> 4];
        out[2 * i + 1] = "0123456789abcdef"[mac[i] & 15];
    }
    out[16] = 0;
}
static int peers_is_me(BYTE *b, unsigned lo, unsigned hi)
{
    char id[64], mine[64];
    if (!pc_has(b, SV_FORMAT_POLID)) return 0;
    unsigned __int64 me = pc_my_id(b);
    if (!me) return 0;
    peers_fmt_polid(b, (unsigned)me, (unsigned)(me >> 32), mine);
    peers_fmt_polid(b, lo, hi, id);
    return mine[0] && lstrcmpA(id, mine) == 0;
}
static int peers_get_friend(BYTE *b, int row, pol_friend_copy *fr)
{
    memset(fr, 0, sizeof *fr);
    return pc_friend_copy(b, row, fr) >= 0 && pc_friend_valid(&fr->f);
}
static void peers_friend_key(BYTE *b, const pol_friend *f, char k[17])
{
    peers_key(b, f->id_lo, f->id_hi, (unsigned)pc_friend_slot(f), k);
}
static int peers_find_friend_row(BYTE *b, DWORD lo, DWORD hi, int slot)
{
    pol_friend_copy r;
    for (int i = 0; i < 200; i++)
        if (peers_get_friend(b, i, &r) && r.f.id_lo == lo && r.f.id_hi == hi && pc_friend_slot(&r.f) == slot) return i;
    return -1;
}

static int peers_my_handle(BYTE *b, char *name16)
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
static int peers_slot_name(BYTE *b, int slot, char *name16)
{
    pol_handle h;
    if (!handle_get(b, slot, &h)) return 0;
    memcpy(name16, h.name, 15);
    name16[15] = 0;
    return 1;
}
