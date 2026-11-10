static void message_store_msg_json(BYTE *b, jw *w, const BYTE *e, int mid, const char *state, int read,
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
    jw_sjis(w, "from", (const BYTE *)MAIL(e)->name, 0x0f, 64);
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
    /* removed from a group (0x12) or it was disbanded (0x13): the name's in the type-10 attachment, the text's empty */
    if (att && (kind == 0x12 || kind == 0x13)) jw_sjis(w, "group", att + 8, 0x14, 71);
    jw_text(w, "subject", subj);
    jw_text(w, "text", text);
    jw_end(w);
}

/* pub\homeNN\msg: r\b unread, r\a read (FFXI moves them there), s\b the game's outbox, s\a sent. A file's name is
   its header in polcore's base64 */
#define STORE_KEEP 200   /* per folder, the viewer's own cap */
#define STORE_SCAN_MAX 20000
#define STORE_BODY_MAX 0x1100
#define STORE_JSON_MAX (3 * 1024 * 1024)
static const char *const g_store_sub[4] = { "r\\a", "r\\b", "s\\a", "s\\b" };
typedef struct { BYTE hdr[MAIL_KEY_SIZE]; char name[100]; int home, sub, alt_home, alt_sub; char alt[100]; } storefile;

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
static HANDLE message_store_open_plain(const char *path)
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
static int message_store_owned(const BYTE *h, int sub, unsigned __int64 me)
{
    return memcmp(sub >= 2 ? &MAIL(h)->from_lo : &MAIL(h)->to_lo, &me, 8) == 0;
}
/* requests and invitations can't be answered from a copy, the server's is gone */
static int message_store_wanted(const BYTE *h, int sub)
{
    unsigned kd = pc_mail_kind(h);
    if (pc_mail_receipt(h)) return 0;
    if (sub >= 2) return kd == 0;
    return kd == 0 || kd == 9 || kd == 10 || kd == 0xf || kd == 0x10 || kd == 0x12 || kd == 0x13;
}
/* my newest STORE_KEEP in one folder, headers decoded by polcore (svc 0x540) so the ids are in this session's form */
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
        if (!(MAIL(v[n].hdr)->flags & MAIL_VALID)) continue;   /* the viewer's loader skips those too */
        if (!message_store_owned(v[n].hdr, sub, me) || (wanted && !message_store_wanted(v[n].hdr, sub))) continue;
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
/* the other home's copy of the same message if this one won't read */
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
static int message_store_legacy_home_there(void)
{
    char p[MAX_PATH + 32];
    wsprintfA(p, "%s\\pub\\home%02d", g_viewer, LEGACY_HOME);
    return g_viewer[0] && message_store_dir_exists(p) && message_store_plain_path(p);
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
    if (row >= 0 && peers_get_friend(b, row, &fr)) jw_sjis(w, "to", (const BYTE *)fr.f.name, 0x0f, 64);
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

static void message_store_drop(void)
{
    char p[MAX_PATH + 64];
    base_ipc_file(p, IPC_STORE);
    DeleteFileA(p);
    base_ipc_file(p, IPC_CHATROOM);   /* the chat room's lines, other people's words like the messages */
    DeleteFileA(p);
    base_ipc_file(p, IPC_GROUPCHAT);
    DeleteFileA(p);
    /* and an enter, a create, a master's change or a group chat request that never got taken */
    requests_sweep();

}
/* FFXI deletes what it fetches from the server, so the store's the only copy of what was handled in game. Read
   once, nothing on the wire */
static void message_store_import(BYTE *b)
{
    static mailrec scratch;
    static char body[STORE_BODY_MAX];
    ipc_json file;
    unsigned __int64 me = pc_my_id(b);
    storefile *all = NULL, *v, *g;
    jw in = {0}, out = {0};
    int homes[2], nh = 0, n = 0, cap = 0, k, i, j, h, s, got, nin = 0, nout = 0, left = 0;
    message_store_drop();
    if (!me || g_home < 0) return;
    homes[nh++] = g_home;
    if (g_home != LEGACY_HOME && message_store_legacy_home_there()) homes[nh++] = LEGACY_HOME;
    for (h = 0; h < nh; h++)
        for (s = 0; s < 4; s++) {   /* s\a before s\b, so a sent copy beats the outbox's */
            k = message_store_list(b, homes[h], s, me, 1, &v);
            for (i = 0; i < k; i++) {
                for (j = 0; j < n; j++)
                    if ((all[j].sub >= 2) == (s >= 2) && mail_inbox_same(all[j].hdr, v[i].hdr)) break;
                if (j < n) {
                    /* the same message twice: read if either copy is, sent if either went, the other's the spare */
                    if (s < 2 && (s == 0 || (MAIL(v[i].hdr)->seen & 0x100)))
                        MAIL(all[j].hdr)->seen |= 0x100;
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
                if (s == 0) MAIL(all[n].hdr)->seen |= 0x100;
                n++;
            }
            if (v) HeapFree(GetProcessHeap(), 0, v);
        }
    if (n > 1) qsort(all, n, sizeof *all, message_store_newer);
    for (i = 0; i < n; i++) {
        int sent = all[i].sub >= 2;
        jw one = {0}, *dst = sent ? &out : &in;
        if ((got = message_store_body(&all[i], body, sizeof body)) <= 0) continue;
        memset(&scratch, 0, sizeof scratch);
        memcpy(scratch.hdr, all[i].hdr, MAIL_KEY_SIZE);
        mail_inbox_parse(&scratch, body, got);
        if (sent) message_store_sent_json(b, &one, all[i].hdr, 0, all[i].sub == 2 ? "sent" : "unsent",
                                  scratch.subj, scratch.text);
        else message_store_msg_json(b, &one, all[i].hdr, 0, "local", (MAIL(all[i].hdr)->seen >> 8) & 1,
                            scratch.subj, scratch.text, scratch.has_att ? scratch.att : NULL);
        if (one.fail) { jw_free(&one); continue; }
        /* newest first, so a store too big for the file loses its oldest lines */
        if (in.n + out.n + one.n + 2 > STORE_JSON_MAX || (jw_splice(dst, one.p, one.n), dst->fail)) {
            left = n - i;
            jw_free(&one);
            break;
        }
        jw_free(&one);
        if (sent) nout++; else nin++;
    }
    if (all) HeapFree(GetProcessHeap(), 0, all);
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
        base_ipc_json_commit(&file, !doc.fail && base_ipc_json_write(&file, doc.p, doc.n));
        jw_free(&doc);
    }
    jw_free(&in);
    jw_free(&out);
    base_log("store: %d received and %d sent line(s) from the message store (home%02d%s)%s\r\n", nin, nout, g_home,
       nh > 1 ? " and home84" : "", left ? ", the oldest left out (too big)" : "");
}
