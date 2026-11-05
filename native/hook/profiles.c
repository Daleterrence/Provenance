static int g_srch_step;   /* 0 idle, 1 start waits, 2 search job, 3 fetch waits, 4 fetch job */
static int g_prof_step, g_ch_step, g_fp_step;   /* 0 idle, 1 load running, 2 start waits for a slot */

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
static void search_abandon(BYTE *b)
{
    if (g_srch_job >= 0 && (g_srch_step == 2 || g_srch_step == 4))
        ((svc_i)SVC(0x4dc))(g_srch_job);
    g_srch_buf = NULL;
    search_done("cancelled");
}
static void search_start(BYTE *b)
{
    int t = ((int (__cdecl *)(void *, void *, void *))SVC(0x4c0))(g_srch_pib, g_srch_cond, NULL);
    if (t == NO_SLOT) { g_srch_step = 1; return; }
    lg("friends: search start -> %d\r\n", t);
    if (t < 0) { search_done(failed(t)); return; }
    g_srch_job = t;
    g_srch_step = 2;
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
    if (!db_free()) return;
    if (take_request("search", buf, sizeof buf) < 0) return;
    g_db_last = GetTickCount64();
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
    if (k == SK_HANDLE) {
        if (!ascii_name_ok(val)) { search_done("failed:bad name"); return; }
        /* a name with a ' (or 0x04) never goes out in a handle search */
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
        if (r == 0) return;
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
    if (r == 0) return;
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

static pdb_cond g_prof_cond;
static int g_prof_job = -1, g_prof_row;
static unsigned __int64 g_prof_hid;
static char g_prof_token[48], g_prof_state[32] = "";
static char *g_prof_json;
static pdb_rec *g_prof_rec;
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
        for (i = 0; i < PROF_SLOTS_KEPT && g_prof_slots[i].hid; i++) ;   /* a forgotten one's free first */
        if (i == PROF_SLOTS_KEPT) {
            i = g_prof_slots_next;
            g_prof_slots_next = (g_prof_slots_next + 1) % PROF_SLOTS_KEPT;
        }
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
static void profile_abandon(BYTE *b)
{
    if (g_prof_step == 1 && g_prof_job >= 0) ((svc4dc_t)SVC(0x4dc))(g_prof_job);
    g_prof_rec = NULL;   /* a cancelled read can still write, so never freed */
    profile_done("cancelled");
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
    if (set && SVC(0x1200)) {
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
        }
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
    if (r == 0) return;
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
static void character_abandon(BYTE *b)
{
    if (g_ch_step == 1 && g_ch_job >= 0) ((svc4dc_t)SVC(0x4dc))(g_ch_job);
    g_ch_rec = NULL;   /* a cancelled read can still write, so never freed */
    character_done("cancelled");
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
    if (!db_free() || user_db_waiting()) return;
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
    if (r == 0) return;
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
static void ffxiprofile_abandon(BYTE *b)
{
    if (g_fp_step == 1 && g_fp_job >= 0) ((svc4dc_t)SVC(0x4dc))(g_fp_job);
    g_fp_rec = NULL;   /* a cancelled read can still write, so never freed */
    ffxiprofile_done("cancelled");
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
    if (r == 0) return;
    g_fp_job = -1;
    /* "Selected profile is hidden." */
    if (r == -0x14cc) { ffxiprofile_done("failed:hidden"); return; }
    if (r < 0) { ffxiprofile_done(failed(r)); return; }
    if (!g_fp_rec->items) { ffxiprofile_done("failed:no profile"); return; }
    ffxiprofile_build(b);
    ffxiprofile_done("done");
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
