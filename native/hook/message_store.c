static void message_store_msg_json(BYTE *b, jw *w, const BYTE *e, int mid, const char *state, int read, int ignored,
                    const char *subj, const char *text, const BYTE *att)
{
    char sk[17], tk[17];
    WORD f = MAIL(e)->flags;
    unsigned kind = (f >> 7) & 0x1f;
    DWORD tlo = MAIL(e)->to_lo, thi = MAIL(e)->to_hi;
    int tome = mail_inbox_polid_ok(b, tlo, thi) && peers_is_me(b, tlo, thi);
    peers_key(b, MAIL(e)->from_lo, MAIL(e)->from_hi, MAIL(e)->from_slot, sk);
    peers_key(b, tlo, thi, MAIL(e)->to_slot, tk);
    jw_obj(w, NULL);
    jw_int(w, "mid", mid);
    jw_sjis(w, "from", b, (const BYTE *)MAIL(e)->name, 0x0f, 64);
    jw_lit(w, "fromKey", sk);
    jw_uint(w, "fromSlot", MAIL(e)->from_slot);
    jw_bool(w, "me", peers_is_me(b, MAIL(e)->from_lo, MAIL(e)->from_hi));
    jw_lit(w, "toKey", tk);
    jw_bool(w, "toMe", tome);
    jw_uint(w, "kind", kind);
    jw_uint(w, "time", MAIL(e)->time);
    jw_uint(w, "seq", MAIL(e)->seq);
    jw_lit(w, "state", state);
    if (read >= 0) jw_bool(w, "read", read);
    if (ignored >= 0) jw_bool(w, "ignored", ignored);
    /* removed from a group (0x12) or it was disbanded (0x13): the name's in the type-10 attachment, the text's empty */
    if (att && (kind == 0x12 || kind == 0x13)) jw_sjis(w, "group", b, att + 8, 0x14, 71);
    jw_text(w, "subject", subj);
    jw_text(w, "text", text);
    jw_end(w);
}

/* r\b unread, r\a read (FFXI moves them there), s\b the game's outbox, s\a sent. A file's name is its header in polcore's base64 */
#define STORE_KEEP 200
#define STORE_SCAN_MAX 20000
#define STORE_BODY_MAX 0x1100
#define STORE_JSON_MAX (3 * 1024 * 1024)
static const char *const g_store_sub[4] = { "r\\a", "r\\b", "s\\a", "s\\b" };
typedef struct { BYTE hdr[MAIL_KEY_SIZE]; char name[100]; int home, sub, alt_home, alt_sub; char alt[100]; int out, read; } storefile;

static int message_store_name_ok(const char *n)
{
    static const char abc[] = "TSG8IncW3HFKokOg79qzeCmZs2yBYEQVAUxR5rbwi4P@jMDLtpvad0f_J1hlN6uX";
    int i;
    for (i = 0; n[i]; i++) if (i >= 96 || !strchr(abc, n[i])) return 0;
    return i == 96;
}
static int message_store_newer(const void *x, const void *y)
{
    const BYTE *a = ((const storefile *)x)->hdr, *c = ((const storefile *)y)->hdr;
    DWORD ta = MAIL(a)->time, tc = MAIL(c)->time;
    DWORD sa = MAIL(a)->seq, sc = MAIL(c)->seq;
    if (ta != tc) return ta < tc ? 1 : -1;
    return sa == sc ? 0 : sa < sc ? 1 : -1;
}
static void message_store_dir(char *out, int home, int sub)
{
    wsprintfA(out, "%s\\pub\\home%02d\\msg\\%s", g_viewer, home, g_store_sub[sub]);
}
/* pol.exe runs elevated, so it never goes through a junction or link someone else put in the viewer folder */
static int message_store_plain_path(const char *path)
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
static HANDLE message_store_open_plain_as(const char *path, DWORD access)
{
    BY_HANDLE_FILE_INFORMATION fi;
    HANDLE h = CreateFileA(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL,
                           OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, NULL);
    if (h == INVALID_HANDLE_VALUE) return h;
    if (!GetFileInformationByHandle(h, &fi) || fi.nNumberOfLinks != 1 ||
        (fi.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY))) {
        CloseHandle(h);
        return INVALID_HANDLE_VALUE;
    }
    return h;
}
static HANDLE message_store_open_plain(const char *path) { return message_store_open_plain_as(path, GENERIC_READ); }
static HANDLE message_store_open_folder(const char *path)
{
    BY_HANDLE_FILE_INFORMATION fi;
    HANDLE h = CreateFileA(path, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING,
                           FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, NULL);
    if (h == INVALID_HANDLE_VALUE) return h;
    if (!GetFileInformationByHandle(h, &fi) || !(fi.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) ||
        (fi.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) {
        CloseHandle(h);
        return INVALID_HANDLE_VALUE;
    }
    return h;
}
static int message_store_final_ok(HANDLE h, const char *expect)
{
    char got[MAX_PATH + 160], full[MAX_PATH + 160], want[MAX_PATH + 160], *p = got;
    DWORD n = GetFinalPathNameByHandleA(h, got, sizeof got, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    if (!n || n >= sizeof got) return 0;
    if (!strncmp(got, "\\\\?\\UNC\\", 8)) { p = got + 6; p[0] = '\\'; }
    else if (!strncmp(got, "\\\\?\\", 4)) p += 4;
    n = GetFullPathNameA(expect, sizeof full, full, NULL);
    if (!n || n >= sizeof full) return 0;
    if (!GetLongPathNameA(full, want, sizeof want)) lstrcpyA(want, full);
    return lstrcmpiA(p, want) == 0;
}
static int message_store_rename_open(HANDLE f, const char *to)
{
    WCHAR w[MAX_PATH + 160];
    FILE_RENAME_INFO *ri;
    DWORD size;
    int n, ok;
    if ((n = MultiByteToWideChar(CP_ACP, 0, to, -1, w, (int)(sizeof w / sizeof w[0]))) <= 0) return 0;
    size = (DWORD)(sizeof *ri + n * sizeof(WCHAR));
    if (!(ri = (FILE_RENAME_INFO *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, size))) return 0;
    ri->ReplaceIfExists = FALSE;
    ri->RootDirectory = NULL;
    ri->FileNameLength = (DWORD)((n - 1) * sizeof(WCHAR));
    memcpy(ri->FileName, w, n * sizeof(WCHAR));
    ok = SetFileInformationByHandle(f, FileRenameInfo, ri, size);
    HeapFree(GetProcessHeap(), 0, ri);
    return ok;
}
static int message_store_owned(const BYTE *h, int sub, unsigned __int64 me)
{
    return memcmp(sub >= 2 ? &MAIL(h)->from_lo : &MAIL(h)->to_lo, &me, 8) == 0;
}
static int message_store_wanted(const BYTE *h, int sub, int mode)
{
    unsigned kd = pc_mail_kind(h);
    if (pc_mail_receipt(h)) return 0;
    if (mode == 2) return sub < 2 && (kd == 1 || kd == 0xe);
    if (sub >= 2) return kd == 0;
    return kd == 0 || kd == 9 || kd == 10 || kd == 0xf || kd == 0x10 || kd == 0x12 || kd == 0x13;
}
static int message_store_list(BYTE *b, int home, int sub, unsigned __int64 me, int wanted, storefile **out)
{
    char glob[MAX_PATH + 64];
    WIN32_FIND_DATAA fd;
    storefile *v = NULL;
    int n = 0, looked = 0, cap = STORE_KEEP * 4;
    HANDLE h;
    *out = NULL;
    if (!pc_has(b, SV_MAIL_DECODE_HEADER) || !g_viewer[0]) return 0;
    message_store_dir(glob, home, sub);
    if (!message_store_plain_path(glob)) return 0;
    lstrcatA(glob, "\\*");
    if ((h = FindFirstFileA(glob, &fd)) == INVALID_HANDLE_VALUE) return 0;
    if (!(v = (storefile *)HeapAlloc(GetProcessHeap(), 0, (cap + 1) * sizeof *v))) { FindClose(h); return 0; }
    do {
        if ((fd.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) ||
            !message_store_name_ok(fd.cFileName))
            continue;
        if (++looked > STORE_SCAN_MAX) {
            base_log("store: over %d files in home%02d\\msg\\%s, the rest aren't read\r\n", STORE_SCAN_MAX, home,
               g_store_sub[sub]);
            break;
        }
        memset(&v[n], 0, sizeof v[n]);
        if (pc_mail_decode_header(b, v[n].hdr, fd.cFileName) != 0) continue;
        if (!(MAIL(v[n].hdr)->flags & MAIL_VALID)) continue;
        if (!message_store_owned(v[n].hdr, sub, me) || (wanted && !message_store_wanted(v[n].hdr, sub, wanted))) continue;
        lstrcpyA(v[n].name, fd.cFileName);
        v[n].home = home;
        v[n].sub = sub;
        v[n].alt_home = -1;
        if (++n == cap) {
            qsort(v, n, sizeof *v, message_store_newer);
            n = STORE_KEEP;
        }
    } while (FindNextFileA(h, &fd));
    FindClose(h);
    if (n > 1) qsort(v, n, sizeof *v, message_store_newer);
    *out = v;
    return n > STORE_KEEP ? STORE_KEEP : n;
}
static int message_store_read(const char *path, char *buf, int cap)
{
    DWORD got = 0;
    BOOL ok;
    HANDLE h = message_store_open_plain(path);
    if (h == INVALID_HANDLE_VALUE) return -1;
    ok = ReadFile(h, buf, (DWORD)cap, &got, NULL);
    CloseHandle(h);
    return ok ? (int)got : -1;
}
static int message_store_body(const storefile *f, char *buf, int cap)
{
    char path[MAX_PATH + 160];
    DWORD len = MAIL(f->hdr)->len;
    int got;
    if (len == 0 || len > 0x101100) return -1;
    if (cap > (int)len) cap = (int)len;
    message_store_dir(path, f->home, f->sub);
    lstrcatA(path, "\\");
    lstrcatA(path, f->name);
    if ((got = message_store_read(path, buf, cap)) > 0 || f->alt_home < 0) return got;
    message_store_dir(path, f->alt_home, f->alt_sub);
    lstrcatA(path, "\\");
    lstrcatA(path, f->alt);
    return message_store_read(path, buf, cap);
}
static int message_store_dir_exists(const char *p)
{
    DWORD a = GetFileAttributesA(p);
    return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY);
}
static void message_store_sent_json(BYTE *b, jw *w, const BYTE *e, int mid, const char *state, const char *subj,
                     const char *text)
{
    char sk[17];
    pol_friend_copy fr;
    DWORD lo = MAIL(e)->to_lo, hi = MAIL(e)->to_hi;
    int row = peers_find_friend_row(b, lo, hi, MAIL(e)->to_slot);
    peers_key(b, lo, hi, MAIL(e)->to_slot, sk);
    jw_obj(w, NULL);
    jw_int(w, "mid", mid);
    if (row >= 0 && peers_get_friend(b, row, &fr)) jw_sjis(w, "to", b, (const BYTE *)fr.f.name, 0x0f, 64);
    else jw_raw(w, "to", "\"\"");
    jw_lit(w, "toKey", sk);
    jw_uint(w, "toSlot", MAIL(e)->to_slot);
    jw_uint(w, "fromSlot", MAIL(e)->from_slot);
    jw_bool(w, "toOther", mail_inbox_polid_ok(b, lo, hi) && !peers_is_me(b, lo, hi));
    jw_uint(w, "time", MAIL(e)->time);
    jw_uint(w, "seq", MAIL(e)->seq);
    jw_lit(w, "state", state);
    jw_text(w, "subject", subj);
    jw_text(w, "text", text);
    jw_end(w);
}

static void message_store_forget_import(void);
static void message_store_drop(void)
{
    char p[MAX_PATH + 64];
    message_store_forget_import();
    base_ipc_file(p, IPC_STORE);
    DeleteFileA(p);
    base_ipc_file(p, IPC_CHATROOM);
    DeleteFileA(p);
    base_ipc_file(p, IPC_GROUPCHAT);
    DeleteFileA(p);
    requests_sweep();
}
static int message_store_file_name(BYTE *b, int which, void *hdr, char *name)
{
    char out[0x200];
    int n;
    memset(out, 0, sizeof out);
    if (which == 2) pc_mail_path_sent(b, hdr, out);
    else if (which) pc_mail_path_read(b, hdr, out);
    else pc_mail_path_unread(b, hdr, out);
    out[sizeof out - 1] = 0;
    n = lstrlenA(out);
    if (n < 96) return 0;
    lstrcpyA(name, out + n - 96);
    return message_store_name_ok(name);
}
/* state bits to 1, 0x4000 off, rename r\b to r\a. The old name has to build back from the header or nothing moves */
static int message_store_delete_file(const char *dir, const char *name);
static int message_store_move_read_ex(BYTE *b, const BYTE *hdr, const char *name, int home, int read)
{
    __declspec(align(8)) BYTE h[sizeof(pol_mail)];
    char was[100], now[100], from[MAX_PATH + 160], to[MAX_PATH + 160], dir[MAX_PATH + 160], fdir[MAX_PATH + 160];
    const char *what = read ? "a read message" : "the answered request";
    HANDLE f, d;
    int ok;
    if (!g_viewer[0] || home < 0 || !message_store_name_ok(name) || !pc_has(b, SV_MAIL_PATH_UNREAD) ||
        !pc_has(b, SV_MAIL_PATH_READ)) {
        base_log("store: %s isn't moved, no way to build its new name\r\n", what);
        return 0;
    }
    memcpy(h, hdr, sizeof h);
    MAIL(h)->seen &= 0xf3ff;
    if (!message_store_file_name(b, 0, h, was) || lstrcmpA(was, name) != 0) {
        base_log("store: %s isn't moved, its name doesn't build back from its header\r\n", what);
        return 0;
    }
    MAIL(h)->flags &= 0xbfff;
    MAIL(h)->seen = (WORD)((MAIL(h)->seen & 0xf3ff) | 0x400);
    if (!message_store_file_name(b, 1, h, now)) {
        base_log("store: %s isn't moved, no name for r\\a\r\n", what);
        return 0;
    }
    message_store_dir(from, home, 1);
    message_store_dir(to, home, 0);
    if (!message_store_plain_path(from) || !message_store_plain_path(to) || !message_store_dir_exists(to)) {
        base_log("store: %s isn't moved, r\\b or r\\a isn't a plain folder\r\n", what);
        return 0;
    }
    if (lstrlenA(from) + 1 + 96 >= (int)sizeof from || lstrlenA(to) + 1 + 96 >= (int)sizeof to) {
        base_log("store: %s isn't moved, the path's too long\r\n", what);
        return 0;
    }
    lstrcpyA(dir, to);
    lstrcpyA(fdir, from);
    lstrcatA(from, "\\");
    lstrcatA(from, name);
    lstrcatA(to, "\\");
    lstrcatA(to, now);
    if (read && GetFileAttributesA(to) != INVALID_FILE_ATTRIBUTES) {
        ok = GetFileAttributesA(from) == INVALID_FILE_ATTRIBUTES || message_store_delete_file(fdir, name);
        if (ok) message_store_secure_add(now);
        return ok;
    }
    if ((f = message_store_open_plain_as(from, DELETE | SYNCHRONIZE)) == INVALID_HANDLE_VALUE) {
        base_log("store: %s isn't moved, its file won't open as a plain file\r\n", what);
        return 0;
    }
    if ((d = message_store_open_folder(dir)) == INVALID_HANDLE_VALUE) {
        CloseHandle(f);
        base_log("store: %s isn't moved, r\\a isn't a plain folder\r\n", what);
        return 0;
    }
    if (!message_store_final_ok(d, dir)) {
        CloseHandle(d);
        CloseHandle(f);
        base_log("store: %s isn't moved, r\\a isn't where it should be\r\n", what);
        return 0;
    }
    ok = message_store_rename_open(f, to);
    if (!ok) base_log("store: moving %s to r\\a failed (%lu), the file's left in r\\b\r\n", what, GetLastError());
    CloseHandle(d);
    CloseHandle(f);
    if (ok && read) message_store_secure_add(now);
    return ok;
}
static int message_store_move_read(BYTE *b, const BYTE *hdr, const char *name, int home)
{
    return message_store_move_read_ex(b, hdr, name, home, 0);
}
static int message_store_delete_file(const char *dir, const char *name)
{
    char path[MAX_PATH + 160];
    FILE_DISPOSITION_INFO del = { TRUE };
    HANDLE h;
    int ok;
    if (!message_store_name_ok(name) || lstrlenA(dir) + 1 + 96 >= (int)sizeof path) return 0;
    wsprintfA(path, "%s\\%s", dir, name);
    if (!message_store_plain_path(dir)) return 0;
    if ((h = message_store_open_plain_as(path, DELETE | SYNCHRONIZE)) == INVALID_HANDLE_VALUE) {
        base_log("store: a file wouldn't open as a plain file to be deleted (%lu)\r\n", GetLastError());
        return 0;
    }
    ok = SetFileInformationByHandle(h, FileDispositionInfo, &del, sizeof del);
    if (!ok) base_log("store: a file wouldn't delete (%lu)\r\n", GetLastError());
    CloseHandle(h);
    return ok;
}

#define SECURE_MAX 2048
static struct { char name[100]; char how; } g_secure[SECURE_MAX];
static int g_secure_n, g_secure_next, g_import_pending;
static void message_store_secure_put(const char *name, char how)
{
    int i;
    for (i = 0; i < g_secure_n; i++)
        if (!lstrcmpA(g_secure[i].name, name)) {
            if (how == 'a') g_secure[i].how = 'a';
            return;
        }
    lstrcpyA(g_secure[g_secure_next].name, name);
    g_secure[g_secure_next].how = how;
    g_secure_next = (g_secure_next + 1) % SECURE_MAX;
    if (g_secure_n < SECURE_MAX) g_secure_n++;
}
static void message_store_secure_add(const char *name) { message_store_secure_put(name, 'a'); }
static int message_store_import_taken(void)
{
    char p[MAX_PATH + 64];
    if (!g_import_pending) return 1;
    base_ipc_file(p, IPC_STORE);
    if (GetFileAttributesA(p) != INVALID_FILE_ATTRIBUTES) return 0;
    g_import_pending = 0;
    for (int i = 0; i < g_secure_n; i++) if (g_secure[i].how == 'i') g_secure[i].how = 'a';
    return 1;
}
static int message_store_secured(const char *name)
{
    for (int i = 0; i < g_secure_n; i++)
        if (!lstrcmpA(g_secure[i].name, name)) return g_secure[i].how == 'a' || (g_secure[i].how == 'i' && message_store_import_taken());
    return 0;
}
static void message_store_imp_forget(void);
static void message_store_forget_import(void)
{
    if (!message_store_import_taken())
        for (int i = 0; i < g_secure_n; i++)
            if (g_secure[i].how == 'i') {
                g_secure[i].name[0] = 0;
                g_secure[i].how = 0;
            }
    g_import_pending = 0;
    message_store_imp_forget();
}

/* 1 written, 2 already there at the right size, 0 not written (the message is then only in the log) */
static int message_store_put(BYTE *b, int sub, const BYTE *hdr, const char *body, char *name)
{
    __declspec(align(8)) BYTE h[sizeof(pol_mail)];
    char dir[MAX_PATH + 160], path[MAX_PATH + 160], tmp[MAX_PATH + 170];
    FILE_DISPOSITION_INFO gone = { TRUE };
    DWORD len, put = 0, wrote = 0;
    HANDLE f, d;
    int which = sub == 2 ? 2 : 0, ok;
    if (!g_store_open || !g_viewer[0] || g_home < 0 || !body || (sub != 1 && sub != 2)) return 0;
    len = MAIL(hdr)->len;
    if (len == 0 || len > STORE_BODY_MAX || !(MAIL(hdr)->flags & MAIL_VALID)) return 0;
    if (!pc_has(b, sub == 2 ? SV_MAIL_PATH_SENT : SV_MAIL_PATH_UNREAD)) return 0;
    memcpy(h, hdr, sizeof h);
    MAIL(h)->body = NULL;
    MAIL(h)->seen = (WORD)((MAIL(h)->seen & 0xf3ff) | (sub == 2 ? 0xc00 : 0));
    if (!message_store_file_name(b, which, h, name)) return 0;
    message_store_dir(dir, g_home, sub);
    if (!message_store_plain_path(dir) || !message_store_dir_exists(dir) || lstrlenA(dir) + 1 + 96 >= (int)sizeof path) {
        base_log("store: a message isn't filed, %s isn't a plain folder\r\n", g_store_sub[sub]);
        return 0;
    }
    wsprintfA(path, "%s\\%s", dir, name);
    if (GetFileAttributesA(path) != INVALID_FILE_ATTRIBUTES) {
        BY_HANDLE_FILE_INFORMATION fi;
        f = message_store_open_plain(path);
        ok = f != INVALID_HANDLE_VALUE && GetFileInformationByHandle(f, &fi) && !fi.nFileSizeHigh && fi.nFileSizeLow == len;
        if (f != INVALID_HANDLE_VALUE) CloseHandle(f);
        if (!ok) base_log("store: a message isn't filed, a file with its name is there and it isn't the right size\r\n");
        return ok ? 2 : 0;
    }
    if ((d = message_store_open_folder(dir)) == INVALID_HANDLE_VALUE || !message_store_final_ok(d, dir)) {
        if (d != INVALID_HANDLE_VALUE) CloseHandle(d);
        base_log("store: a message isn't filed, %s isn't where it should be\r\n", g_store_sub[sub]);
        return 0;
    }
    lstrcpyA(tmp, path);
    lstrcatA(tmp, ".tmp");
    DeleteFileA(tmp);
    f = CreateFileA(tmp, GENERIC_WRITE | DELETE, FILE_SHARE_READ | FILE_SHARE_DELETE, NULL, CREATE_NEW,
                    FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, NULL);
    if (f == INVALID_HANDLE_VALUE) {
        CloseHandle(d);
        base_log("store: a message isn't filed, its file wouldn't open (%lu)\r\n", GetLastError());
        return 0;
    }
    ok = 1;
    while (ok && put < len) {
        wrote = 0;
        ok = WriteFile(f, body + put, len - put, &wrote, NULL) && wrote > 0;
        put += wrote;
    }
    ok = ok && FlushFileBuffers(f) && message_store_rename_open(f, path);
    if (!ok) SetFileInformationByHandle(f, FileDispositionInfo, &gone, sizeof gone);
    CloseHandle(f);
    CloseHandle(d);
    if (!ok) base_log("store: a message isn't filed, writing it failed (%lu)\r\n", GetLastError());
    return ok;
}
static void message_store_sent(BYTE *b, const BYTE *msg)
{
    char name[100];
    if (!message_store_put(b, 2, msg, (const char *)msg + MAIL_BUILT_SIZE, name)) return;
    message_store_secure_add(name);
    message_store_trim(b, 2);
}

static int message_store_trim_order(const void *x, const void *y)
{
    const BYTE *a = ((const storefile *)x)->hdr, *c = ((const storefile *)y)->hdr;
    int la = (MAIL(a)->seen >> 8) & 1, lc = (MAIL(c)->seen >> 8) & 1;
    if (la != lc) return la - lc;
    if (MAIL(a)->time != MAIL(c)->time) return MAIL(a)->time < MAIL(c)->time ? -1 : 1;
    return MAIL(a)->seq == MAIL(c)->seq ? 0 : MAIL(a)->seq < MAIL(c)->seq ? -1 : 1;
}
static int message_store_trim(BYTE *b, int sub)
{
    char dir[MAX_PATH + 160], glob[MAX_PATH + 170];
    WIN32_FIND_DATAA fd;
    storefile *v;
    HANDLE h;
    int raw = 0, n = 0, over, gone = 0, i;
    if (!g_store_open || !g_viewer[0] || g_home < 0 || !pc_has(b, SV_MAIL_DECODE_HEADER)) return 0;
    message_store_dir(dir, g_home, sub);
    if (!message_store_plain_path(dir)) return 0;
    wsprintfA(glob, "%s\\*", dir);
    if ((h = FindFirstFileA(glob, &fd)) == INVALID_HANDLE_VALUE) return 0;
    do raw++; while (raw <= STORE_SCAN_MAX && FindNextFileA(h, &fd));
    FindClose(h);
    if (raw <= STORE_KEEP + 2) return 0;
    if (!(v = (storefile *)HeapAlloc(GetProcessHeap(), 0, (raw + 1) * sizeof *v))) return 0;
    if ((h = FindFirstFileA(glob, &fd)) != INVALID_HANDLE_VALUE) {
        do {
            if (n >= raw) break;
            if ((fd.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) ||
                !message_store_name_ok(fd.cFileName))
                continue;
            memset(&v[n], 0, sizeof v[n]);
            if (pc_mail_decode_header(b, v[n].hdr, fd.cFileName) != 0 || !(MAIL(v[n].hdr)->flags & MAIL_VALID)) continue;
            lstrcpyA(v[n].name, fd.cFileName);
            n++;
        } while (FindNextFileA(h, &fd));
        FindClose(h);
    }
    over = n - STORE_KEEP;
    if (over > 0) {
        qsort(v, n, sizeof *v, message_store_trim_order);
        for (i = 0; i < n && over > 0; i++) {
            if (!message_store_wanted(v[i].hdr, sub, 1) || !message_store_secured(v[i].name)) continue;
            if (message_store_delete_file(dir, v[i].name)) {
                over--;
                gone++;
            }
        }
        if (gone) base_log("store: %d old file(s) trimmed from %s to keep it at %d\r\n", gone, g_store_sub[sub], STORE_KEEP);
        if (over > 0) base_log("store: %s is %d over %d, the files that could go aren't saved in Provenance yet\r\n",
                               g_store_sub[sub], over, STORE_KEEP);
    }
    HeapFree(GetProcessHeap(), 0, v);
    return gone;
}

typedef struct { BYTE hdr[sizeof(pol_mail)]; char name[100]; int mid, home, want, fails; ULONGLONG at; } imprec;
#define IMP_MAX 200
static imprec g_imp[IMP_MAX];
static int g_imp_n;
static void message_store_imp_forget(void)
{
    g_imp_n = 0;
}
static void message_store_imp_want(int mid)
{
    for (int i = 0; i < g_imp_n; i++)
        if (g_imp[i].mid == mid && g_imp[i].name[0]) {
            g_imp[i].want = 1;
            g_imp[i].fails = 0;
            g_imp[i].at = 0;
        }
}
static int message_store_imp_waiting(const imprec *p)
{
    for (int i = 0; i < g_s.mail_n; i++) {
        const mailrec *m = &g_mail[i];
        if (!m->srv_gone && (m->state <= 2 || m->state == 4) && mail_inbox_same(m->hdr, p->hdr)) return 1;
    }
    return 0;
}
static int message_store_imp_pending(void)
{
    for (int i = 0; i < g_imp_n; i++) if (g_imp[i].want && !message_store_imp_waiting(&g_imp[i])) return 1;
    return 0;
}
static int message_store_imp_pump(BYTE *b)
{
    ULONGLONG now = GetTickCount64();
    int moved = 0;
    if (!g_mail_listed) return 0;
    for (int i = 0; i < g_imp_n; i++) {
        imprec *p = &g_imp[i];
        if (!p->want || now < p->at || message_store_imp_waiting(p)) continue;
        if (message_store_move_read_ex(b, p->hdr, p->name, p->home, 1)) {
            p->want = 0;
            moved++;
            message_store_trim(b, 0);
        } else if (++p->fails >= STORED_TRIES) {
            p->want = 0;
            base_log("store: a read message stays in r\\b, the move failed %d times\r\n", p->fails);
        } else {
            p->at = now + STORED_MOVE_GAP_MS;
        }
    }
    if (moved) base_log("store: %d read message(s) moved to r\\a\r\n", moved);
    return moved;
}

static void message_store_file_gone(const char *name)
{
    for (int i = 0; i < g_s.mail_n; i++)
        if (g_mail[i].filed && !lstrcmpA(g_mail[i].sname, name)) {
            g_mail[i].filed = 0;
            g_mail[i].read_want = 0;
        }
    for (int i = 0; i < g_imp_n; i++)
        if (!lstrcmpA(g_imp[i].name, name)) {
            g_imp[i].want = 0;
            g_imp[i].name[0] = 0;
        }
}
#define DEL_MAX 1024
typedef struct { char sent; DWORD time, seq; char key[20]; } delline;
static delline g_del[DEL_MAX];
static int message_store_del_line(char **pp, delline *d)
{
    char *p = *pp;
    DWORD v;
    int k = 0;
    if ((*p != 'r' && *p != 's') || p[1] != '|') return 0;
    d->sent = *p == 's';
    p += 2;
    for (v = 0; *p >= '0' && *p <= '9' && k < 10; k++) v = v * 10 + (DWORD)(*p++ - '0');
    if (!k || *p++ != '|') return 0;
    d->time = v;
    for (v = 0, k = 0; *p >= '0' && *p <= '9' && k < 10; k++) v = v * 10 + (DWORD)(*p++ - '0');
    if (!k || *p++ != '|') return 0;
    d->seq = v;
    for (k = 0; k < 16 && ((*p >= '0' && *p <= '9') || (*p >= 'a' && *p <= 'f') || (*p >= 'A' && *p <= 'F')); k++)
        d->key[k] = *p++;
    d->key[k] = 0;
    *pp = p;
    return k > 0;
}
static int message_store_delete_scan(BYTE *b, unsigned __int64 me, const delline *want, int n)
{
    int sub, gone = 0, j;
    for (sub = 0; sub < 4; sub++) {
        char dir[MAX_PATH + 160], glob[MAX_PATH + 170], sk[17];
        WIN32_FIND_DATAA fd;
        BYTE hd[MAIL_KEY_SIZE];
        HANDLE h;
        message_store_dir(dir, g_home, sub);
        if (!message_store_plain_path(dir)) continue;
        wsprintfA(glob, "%s\\*", dir);
        if ((h = FindFirstFileA(glob, &fd)) == INVALID_HANDLE_VALUE) continue;
        do {
            if ((fd.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) ||
                !message_store_name_ok(fd.cFileName))
                continue;
            memset(hd, 0, sizeof hd);
            if (pc_mail_decode_header(b, hd, fd.cFileName) != 0 || !(MAIL(hd)->flags & MAIL_VALID) ||
                !message_store_owned(hd, sub, me) || !message_store_wanted(hd, sub, 1))
                continue;
            if (sub < 2) peers_key(b, MAIL(hd)->from_lo, MAIL(hd)->from_hi, MAIL(hd)->from_slot, sk);
            else peers_key(b, MAIL(hd)->to_lo, MAIL(hd)->to_hi, MAIL(hd)->to_slot, sk);
            for (j = 0; j < n; j++)
                if (want[j].sent == (sub >= 2) && want[j].time == MAIL(hd)->time && want[j].seq == MAIL(hd)->seq &&
                    !lstrcmpiA(want[j].key, sk)) {
                    if (message_store_delete_file(dir, fd.cFileName)) {
                        gone++;
                        message_store_file_gone(fd.cFileName);
                    }
                    break;
                }
        } while (FindNextFileA(h, &fd));
        FindClose(h);
    }
    return gone;
}
static void message_store_delete_request(BYTE *b)
{
    char *buf, *p;
    unsigned __int64 me;
    int got, n = 0, gone = 0;
    if (!requests_waiting(REQ_DELETE_FILES)) return;
    if ((got = requests_take_ex(REQ_DELETE_FILES, NULL, 0, &buf, ACK_MAX)) < 0) return;
    me = pc_my_id(b);
    if (g_store_open && me && g_home >= 0 && g_viewer[0] && pc_has(b, SV_MAIL_DECODE_HEADER)) {
        for (p = buf; *p;) {
            if (message_store_del_line(&p, &g_del[n])) n++;
            while (*p && *p != '\n') p++;
            if (*p) p++;
            if (n == DEL_MAX || (!*p && n)) {
                gone += message_store_delete_scan(b, me, g_del, n);
                n = 0;
            }
        }
        base_log("store: %d file(s) deleted along with lines deleted in Provenance (home%02d)\r\n", gone, g_home);
    }
    HeapFree(GetProcessHeap(), 0, buf);
}
#define STORED_MAX 100
static void message_store_requests(BYTE *b, unsigned __int64 me)
{
    static char body[STORE_BODY_MAX];
    static mailrec probe;
    storefile *v = NULL, *ans = NULL;
    int k, na, i, j, got, held = 0, skipped = 0, dups = 0, dropped = 0;
    char tail[96];
    k = message_store_list(b, g_home, 1, me, 2, &v);
    na = message_store_list(b, g_home, 0, me, 2, &ans);
    if (k > STORED_MAX) k = STORED_MAX;
    for (i = k - 1; i >= 0; i--) {
        mailrec *m;
        for (j = 0; j < na; j++) if (mail_inbox_same(ans[j].hdr, v[i].hdr)) break;
        if (j < na) continue;
        for (j = 0; j < g_s.mail_n; j++) if (mail_inbox_same(g_mail[j].hdr, v[i].hdr)) break;
        if (j < g_s.mail_n) { dups++; continue; }
        if ((got = message_store_body(&v[i], body, sizeof body)) <= 0) { skipped++; continue; }
        memset(&probe, 0, sizeof probe);
        memcpy(probe.hdr, v[i].hdr, MAIL_KEY_SIZE);
        mail_inbox_parse(b, &probe, body, got);
        if (pc_mail_kind(probe.hdr) == 0xe && !probe.has_att) { skipped++; continue; }
        if (!(m = mail_inbox_add(v[i].hdr))) { dropped++; continue; }
        memcpy(m->subj, probe.subj, sizeof m->subj);
        memcpy(m->text, probe.text, sizeof m->text);
        memcpy(m->att, probe.att, sizeof m->att);
        m->has_att = probe.has_att;
        m->state = 7;
        m->stored = 1;
        m->shome = g_home;
        lstrcpyA(m->sname, v[i].name);
        held++;
    }
    if (v) HeapFree(GetProcessHeap(), 0, v);
    if (ans) HeapFree(GetProcessHeap(), 0, ans);
    SecureZeroMemory(body, sizeof body);
    SecureZeroMemory(&probe, sizeof probe);
    if (held) g_snap_dirty = 1;
    tail[0] = 0;
    if (dups) wsprintfA(tail + lstrlenA(tail), ", %d the same message twice (kept once)", dups);
    if (skipped) wsprintfA(tail + lstrlenA(tail), ", %d not readable", skipped);
    if (dropped) wsprintfA(tail + lstrlenA(tail), ", %d not kept (table full or seen before)", dropped);
    base_log("store: %d friend request(s) and group invitation(s) held from r\\b (home%02d)%s\r\n", held, g_home, tail);
}
static void message_store_import(BYTE *b)
{
    static mailrec scratch;
    static char body[STORE_BODY_MAX];
    ipc_json file;
    unsigned __int64 me = pc_my_id(b);
    storefile *all = NULL, *v, *g;
    jw in = {0}, out = {0};
    int n = 0, cap = 0, k, i, j, s, got, nin = 0, nout = 0, left = 0, mid;
    message_store_drop();
    if (!me || g_home < 0) return;
    message_store_requests(b, me);
    for (s = 0; s < 4; s++) {
        k = message_store_list(b, g_home, s, me, 1, &v);
        for (i = 0; i < k; i++) {
            for (j = 0; j < n; j++)
                if ((all[j].sub >= 2) == (s >= 2) && mail_inbox_same(all[j].hdr, v[i].hdr)) break;
            if (j < n) {
                if (s == 0) all[j].read = 1;
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
            if (s == 0) all[n].read = 1;
            n++;
        }
        if (v) HeapFree(GetProcessHeap(), 0, v);
    }
    if (n > 1) qsort(all, n, sizeof *all, message_store_newer);
    for (i = 0; i < n; i++) {
        int sent = all[i].sub >= 2;
        jw one = {0}, *dst = sent ? &out : &in;
        imprec *ip = NULL;
        if ((got = message_store_body(&all[i], body, sizeof body)) <= 0) continue;
        mid = 0;
        if (!sent && all[i].sub == 1 && g_imp_n < IMP_MAX) {
            ip = &g_imp[g_imp_n];
            mid = ++g_s.mail_mid;
        }
        memset(&scratch, 0, sizeof scratch);
        memcpy(scratch.hdr, all[i].hdr, MAIL_KEY_SIZE);
        mail_inbox_parse(b, &scratch, body, got);
        if (sent) message_store_sent_json(b, &one, all[i].hdr, 0, all[i].sub == 2 ? "sent" : "unsent",
                                  scratch.subj, scratch.text);
        else message_store_msg_json(b, &one, all[i].hdr, mid, "local", all[i].read, -1,
                            scratch.subj, scratch.text, scratch.has_att ? scratch.att : NULL);
        if (one.fail) { jw_free(&one); continue; }
        if (in.n + out.n + one.n + 2 > STORE_JSON_MAX || (jw_splice(dst, one.p, one.n), dst->fail)) {
            left = n - i;
            jw_free(&one);
            break;
        }
        jw_free(&one);
        if (sent) nout++; else nin++;
        all[i].out = 1;
        if (ip) {
            memset(ip, 0, sizeof *ip);
            memcpy(ip->hdr, all[i].hdr, MAIL_KEY_SIZE);
            lstrcpyA(ip->name, all[i].name);
            ip->mid = mid;
            ip->home = g_home;
            g_imp_n++;
        }
    }
    SecureZeroMemory(body, sizeof body);
    SecureZeroMemory(&scratch, sizeof scratch);
    if ((nin || nout) && base_ipc_json_open(IPC_STORE, &file)) {
        jw doc = {0};
        jw_obj(&doc, NULL);
        jw_arr(&doc, "messages");
        jw_splice(&doc, in.p, in.n);
        jw_end(&doc);
        jw_arr(&doc, "gameSent");
        jw_splice(&doc, out.p, out.n);
        jw_end(&doc);
        jw_end(&doc);
        if (base_ipc_json_commit(&file, !doc.fail && base_ipc_json_write(&file, doc.p, doc.n))) {
            for (i = 0; i < n; i++)
                if (all[i].out && all[i].sub != 3) message_store_secure_put(all[i].name, 'i');
            g_import_pending = 1;
        } else {
            g_imp_n = 0;
        }
        jw_free(&doc);
    }
    if (all) HeapFree(GetProcessHeap(), 0, all);
    jw_free(&in);
    jw_free(&out);
    base_log("store: %d received and %d sent line(s) from the message store (home%02d)%s\r\n", nin, nout, g_home,
       left ? ", the oldest left out (too big)" : "");
}
