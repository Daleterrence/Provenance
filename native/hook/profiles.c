
/* prof_001, the FFXI table (title 1). z_phead's what a search hit gets added from */
static pdb_schema *g_ch_pib;
static int profiles_character_check_schema(BYTE *b, pdb_schema *s)
{
    static const struct { const char *n; BYTE type; WORD size; } want[] = {
        { "z_ctsid", 6, 4 }, { "z_ctid", 8, 8 }, { "z_name", 1, 0 }, { "z_worldname", 1, 0 }, { "z_phead", 9, 0 },
    };
    int i, idx;
    if (s->table_id != 1) return 0;
    for (i = 0; i < (int)(sizeof want / sizeof want[0]); i++) {
        const pdb_field *f = profile_db_field(s, want[i].n, &idx);
        if (!f || f->type != want[i].type || (want[i].size && f->size != want[i].size)) return 0;
        if (pc_pdb_field_index(b, s, want[i].n) != idx) return 0;
    }
    if (0x17 + (DWORD)s->nfields * 0x10 + s->rec_raw > s->rec_size + 8) return 0;
    return 1;
}
static pdb_schema *profiles_character_load_pib(BYTE *b)
{
    if (!g_ch_pib) g_ch_pib = profile_db_load_file(b, L"prof_001.pib", "prof_001", profiles_character_check_schema);
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
static int g_srch_job = -1;
static __declspec(align(8)) pdb_cond g_srch_cond;
static char g_srch_clause[64];
static BYTE *g_srch_buf;
static pdb_schema *g_srch_pib;
static DWORD g_srch_set, g_srch_want;
static int g_srch_marked;
static DWORD g_srch_marked_for;

static void profiles_search_done(const char *state)
{
    lstrcpynA(g_s.srch_state, state, sizeof g_s.srch_state);
    if (g_srch_buf) { HeapFree(GetProcessHeap(), 0, g_srch_buf); g_srch_buf = NULL; }
    g_s.srch_step = 0;
    g_srch_job = -1;
    g_srch_marked = 0;
    base_log("friends: search %s (%d hits)\r\n", state, g_s.srch_n);
    g_snap_dirty = 1;
}
/* polcore can still write into a cancelled read, so its buffer's never freed (the mail reads too). A small leak
   beats polcore going ham over freed memory */
static void profiles_search_abandon(BYTE *b)
{
    if (g_srch_job >= 0 && (g_s.srch_step == 2 || g_s.srch_step == 4))
        pc_free_job(b, g_srch_job);
    g_srch_buf = NULL;
    profiles_search_done("cancelled");
}
static void profiles_search_start(BYTE *b)
{
    int t = pc_pdb_search_start(b, g_srch_pib, &g_srch_cond, NULL);
    if (t == NO_SLOT) { g_s.srch_step = 1; return; }
    base_log("friends: search start -> %d\r\n", t);
    if (t < 0) { profiles_search_done(requests_failed(t)); return; }
    g_srch_job = t;
    g_s.srch_step = 2;
}
static void profiles_search_fetch(BYTE *b)
{
    int r = pc_pdb_search_fetch(b, (int)g_srch_set, g_srch_pib, 0,
                                                                        (int)g_srch_want, g_srch_buf);
    if (r == NO_SLOT) { g_s.srch_step = 3; return; }
    base_log("friends: search fetching %u -> %d\r\n", g_srch_want, r);
    if (r < 0) { profiles_search_done(requests_failed(r)); return; }
    g_srch_job = r;
    g_s.srch_step = 4;
}
/* an H-ID as the viewer shows it (00-1234567) or just the number: '-' and spaces go, then 1-13 digits */
static int profiles_hid_parse(const char *s, unsigned __int64 *out)
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
static int profiles_ffxi_name_ok(const char *s)
{
    int n = 0;
    for (; *s; s++, n++)
        if (!((*s >= 'A' && *s <= 'Z') || (*s >= 'a' && *s <= 'z'))) return 0;
    return n >= 1 && n <= 15;
}
static void profiles_search_request(BYTE *b)
{
    char buf[256], *p, *tok, *kind, *val, dec[24];
    unsigned __int64 hid = 0;
    int k = 0;
    if (!hold_db_free()) return;
    if (requests_take(REQ_SEARCH, buf, sizeof buf) < 0) return;
    g_s.db_last = GetTickCount64();
    p = buf;
    tok = requests_line(&p);
    kind = requests_line(&p);
    val = requests_line(&p);
    lstrcpynA(g_s.srch_token, tok, sizeof g_s.srch_token);
    g_s.srch_n = 0;
    g_s.srch_total = 0;
    g_s.srch_many = 0;
    while (k < 3 && lstrcmpA(kind, g_srch_kinds[k]) != 0) k++;
    g_s.srch_kind = k < 3 ? k : SK_HANDLE;
    if (k == 3) { profiles_search_done("failed:bad request"); return; }
    if (k == SK_HANDLE) {
        if (!peers_ascii_name_ok(val)) { profiles_search_done("failed:bad name"); return; }
        /* a name with a ' (or 0x04) never goes out in a handle search */
        if (strchr(val, '\'')) { profiles_search_done("failed:quote"); return; }
    } else if (k == SK_HID) {
        if (!profiles_hid_parse(val, &hid)) { profiles_search_done("failed:bad hid"); return; }
    } else if (!profiles_ffxi_name_ok(val)) {
        profiles_search_done("failed:bad character");
        return;
    }
    g_srch_pib = k == SK_CHARACTER ? profiles_character_load_pib(b) : profile_db_load_pib(b);
    if (!g_srch_pib) { profiles_search_done("failed:no schema"); return; }
    memset(&g_srch_cond, 0, sizeof g_srch_cond);
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
    pc_pdb_cond_text(b, &g_srch_cond, 7, g_srch_clause);
    base_log("friends: search %s \"%s\"\r\n", g_srch_kinds[k], val);
    lstrcpyA(g_s.srch_state, "searching");
    g_snap_dirty = 1;
    profiles_search_start(b);
}
static void profiles_search_pump(BYTE *b, int ctl)
{
    int r, guard = 0;
    if (g_s.srch_step == 0) {
        if (ctl && !g_s.draining) profiles_search_request(b);
        return;
    }
    if (g_s.srch_step == 1) {
        if (!g_s.draining) profiles_search_start(b); else profiles_search_done("cancelled"); return;
    }
    if (g_s.srch_step == 3) {
        if (!g_s.draining) profiles_search_fetch(b); else profiles_search_done("cancelled"); return;
    }
    if (g_s.srch_step == 2) {
        DWORD out[3] = { 0, 0, 0 };
        r = pc_pdb_search_hits(b, g_srch_job, out);
        if (r == 0) return;
        g_srch_job = -1;
        if (r == NOTHING_FOUND) { profiles_search_done("done"); return; }   /* "No matching results were found." */
        if (r < 0) { profiles_search_done(requests_failed(r)); return; }
        if ((int)out[0] < 0) { profiles_search_done("done"); return; }
        base_log("friends: search %d hits%s\r\n", (int)out[1], out[2] ? " (too many)" : "");
        g_s.srch_total = (int)out[1] > 0 ? (int)out[1] : 0;
        /* the viewer goes by the too-many flag before it looks at the count */
        if (out[2] || (int)out[1] >= SEARCH_TOO_MANY) { g_s.srch_many = 1; profiles_search_done("done"); return; }
        if ((int)out[1] <= 0) { profiles_search_done("done"); return; }
        g_srch_set = out[0];
        g_srch_want = out[1] > SEARCH_MAX ? SEARCH_MAX : out[1];
        g_srch_buf = (BYTE *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY,
                                       (SIZE_T)g_srch_pib->rec_size * g_srch_want + 64);
        if (!g_srch_buf) { profiles_search_done("failed:alloc"); return; }
        profiles_search_fetch(b);
        return;
    }
    r = pc_pdb_search_fetch_check(b, g_srch_job);
    if (r == 0) return;
    g_srch_job = -1;
    if (r < 0) { profiles_search_done(requests_failed(r)); return; }
    for (BYTE *rec = g_srch_buf; rec && g_s.srch_n < SEARCH_MAX && guard++ < SEARCH_MAX * 2; rec = *(BYTE **)rec) {
        pdb_item *ph, *it;
        const pol_friend *fr;
        srchrec *h = &g_srch[g_s.srch_n];
        if (!*(DWORD *)(rec + 0xc)) continue;
        ph = profile_db_get(b, g_srch_pib, "z_phead", (pdb_rec *)rec);
        if (!ph || !(ph->flags & 1) || !(fr = (const pol_friend *)ph->v.bytes.p) || IsBadReadPtr(fr, sizeof *fr)) continue;
        if (!friend_valid(fr)) continue;
        memset(h, 0, sizeof *h);
        h->lo = fr->id_lo;
        h->hi = fr->id_hi;
        h->slot = friend_slot(fr);
        memcpy(h->name, fr->name, 15);
        if (g_s.srch_kind == SK_CHARACTER) {
            /* prof_001 has no profile picture. The world stays out of a Private record */
            it = profile_db_get(b, g_srch_pib, "z_name", (pdb_rec *)rec);
            if (it && (it->flags & 1) && it->v.str && !IsBadStringPtrA(it->v.str, 0x40))
                json_sjis_copy(h->chname, it->v.str, sizeof h->chname);
            it = profile_db_get(b, g_srch_pib, "z_worldname", (pdb_rec *)rec);
            if (((pdb_rec *)rec)->level >= 1 && it && (it->flags & 1) && it->v.str && !IsBadStringPtrA(it->v.str, 0x40))
                json_sjis_copy(h->world, it->v.str, sizeof h->world);
        } else {
            it = profile_db_get(b, g_srch_pib, "z_ficon", (pdb_rec *)rec);
            h->pic = it && (it->flags & 1) ? it->v.u32 : 0;
        }
        g_s.srch_n++;
    }
    profiles_search_done("done");
}

static pdb_cond g_prof_cond;
static int g_prof_job = -1, g_prof_row;
static unsigned __int64 g_prof_hid;
static char *g_prof_json;
static pdb_rec *g_prof_rec;
/* game slots (z_phead, or the friend row) from the last 16 profiles read, by H-ID. The app shows a profile again from
   its own copy and the tabs still need them */
#define PROF_SLOTS_KEPT 16
static struct { unsigned __int64 hid; pol_game slots[8]; } g_prof_slots[PROF_SLOTS_KEPT];
static int g_prof_slots_next;
static const pol_game *profiles_slots_for(unsigned __int64 hid)
{
    for (int i = 0; hid && i < PROF_SLOTS_KEPT; i++) if (g_prof_slots[i].hid == hid) return g_prof_slots[i].slots;
    return NULL;
}
/* NULL forgets the handle's */
static void profiles_slots_keep(unsigned __int64 hid, const pol_game *src)
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

static void profiles_profile_done(const char *state)
{
    lstrcpynA(g_s.prof_state, state, sizeof g_s.prof_state);
    if (g_prof_rec) { HeapFree(GetProcessHeap(), 0, g_prof_rec); g_prof_rec = NULL; }
    g_s.prof_step = 0;
    g_prof_job = -1;
    base_log(LOG_PROFILE "%s\r\n", state);
    g_snap_dirty = 1;
}
static void profiles_profile_abandon(BYTE *b)
{
    if (g_s.prof_step == 1 && g_prof_job >= 0) pc_free_job(b, g_prof_job);
    g_prof_rec = NULL;   /* a cancelled read can still write, so never freed */
    profiles_profile_done("cancelled");
}
static void profiles_profile_build(BYTE *b)
{
    static const char *aa[] = { "z_aa_mt", "z_aa_dt", "z_aa_t", "z_aa_m", "z_aa_c", "z_aa_w", "z_aa_d", "z_aa_e" };
    static char lines[3][0x80], js[0xc00], t[0x400], f[0x400];
    pdb_schema *s = g_profile_db_pib;
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
    st = profile_db_pfb_value(b, s, p, g_prof_rec, "z_name", 1, lines, &n);
    json_pol(b, lines[0], 0x600, js, sizeof js);
    PJ("\"name\":%s,\"nameState\":%d", js, st);
    st = profile_db_pfb_value(b, s, p, g_prof_rec, "z_mail", 1, lines, &n);
    json_pol(b, lines[0], 0x600, js, sizeof js);
    PJ(",\"mail\":%s,\"mailState\":%d", js, st);
    it = profile_db_get(b, s, "z_hid", g_prof_rec);
    memset(hid, 0, sizeof hid);
    /* the viewer prints the record's low 32 bits, or the H-ID it asked for when that's 0 (a hidden one) */
    if (pc_has(b, SV_FORMAT_HID)) {
        DWORD lo = it ? (DWORD)it->v.u64 : 0, hi = 0;
        if (!lo) { lo = (DWORD)g_prof_hid; hi = (DWORD)(g_prof_hid >> 32); }
        pc_format_hid(b, hid, lo, hi);
    }
    hid[0x20] = 0;
    json_str(js, sizeof js, hid);
    PJ(",\"hid\":%s", js);
    it = profile_db_get(b, s, "z_utime", g_prof_rec);
    PJ(",\"updated\":%u,\"level\":%u", it ? it->v.u32 : 0, g_prof_rec->level);
    PJ(",\"rows\":[");
    for (i = 0; p && i < p->nItems; i++) {
        const pfb_item *pi = &p->items[i];
        const char *fname;
        if (pi->field >= s->nfields || pi->ui == 6) continue;
        fname = s->fields[pi->field].name;
        if (!strcmp(fname, "z_age_f") || !strcmp(fname, "z_name") || !strcmp(fname, "z_hid") ||
            !strcmp(fname, "z_mail") || !strcmp(fname, "z_utime")) continue;
        json_pol(b, pi->label, 0x100, js, sizeof js);
        if (!strcmp(fname, "z_ficon")) {
            pdb_item *fi = profile_db_get(b, s, "z_ficon", g_prof_rec);
            st = (!fi || g_prof_rec->level < 1 || fi->level == 0 ||
                  ((s->fields[pi->field].flags & 0x40) && fi->level < 1)) ? PV_PRIVATE
               : (int)fi->v.u32 <= 0 ? PV_NOTSET : PV_OK;
            PJ("%s{\"label\":%s,\"field\":\"z_ficon\",\"state\":%d,\"pic\":%d}", first ? "" : ",", js, st,
               st == PV_OK ? (int)fi->v.u32 : 0);
            first = 0;
            continue;
        }
        st = profile_db_pfb_value(b, s, p, g_prof_rec, fname, 1, lines, &n);
        PJ("%s{\"label\":%s,\"field\":\"%s\",\"state\":%d,\"lines\":[", first ? "" : ",", js, fname, st);
        for (k = 0; k < n; k++) {
            json_pol(b, lines[k], 0x600, js, sizeof js);
            PJ("%s%s", k ? "," : "", js);
        }
        PJ("]}");
        first = 0;
    }
    PJ("]");
    /* the FFXI tabs: z_phead's slots like the viewer (readable whatever the level), else the friend row's. Only title
       1, other games' profiles never get a tab here */
    {
        pol_friend_copy fr;
        pdb_item *ph = profile_db_get(b, s, "z_phead", g_prof_rec);
        const pol_friend *h = ph && (ph->flags & 1) ? (const pol_friend *)ph->v.bytes.p : NULL;
        const pol_game *src = NULL;
        const char *from = "nowhere";
        if (h && !IsBadReadPtr(h, sizeof *h) && friend_valid(h) && friend_hid(h) == g_prof_hid) {
            src = h->games;
            from = "z_phead";
        } else if (peers_get_friend(b, g_prof_row, &fr) && friend_hid(&fr.f) == g_prof_hid) {
            src = fr.f.games;
            from = "the friend row";
        }
        profiles_slots_keep(g_prof_hid, src);
        PJ(",\"characters\":[");
        for (i = 0, k = 0; src && i < 8; i++) {
            const pol_game *sl = &src[i];
            DWORD cs = sl->ctsid;
            if (!(sl->flags & 1) || sl->title != 1) continue;
            PJ("%s{\"slot\":%d,\"ctsid\":%u,\"ctidLow\":%u,\"ctidHigh\":%u,\"world\":%u}", k++ ? "," : "", i, cs,
               sl->ctid_lo, sl->ctid_hi, (cs >> 16) & 0xff);
        }
        PJ("]");
        base_log(LOG_PROFILE "%d FFXI characters (from %s)\r\n", k, from);
    }
    /* svc 0x1200 is the viewer's away text (-0x14cc = none or no access). All z_aa_* at 0 means none, though 0x1200
       would happily invent "I am away from the keyboard". */
    for (k = 0; k < 8 && !set; k++) {
        pdb_item *a2 = profile_db_get(b, s, aa[k], g_prof_rec);
        if (a2 && a2->v.bytes.p && *(BYTE *)a2->v.bytes.p) set = 1;
    }
    if (set && pc_has(b, SV_AWAY_TEXT)) {
        memset(t, 0, sizeof t);
        memset(f, 0, sizeof f);
        r = pc_away_text(b, s, p, g_prof_rec, g_prof_row, t, sizeof t, f, sizeof f);
        if (r >= 0) {
            for (q = t; *q; q++) if (*q == 7) *q = '\n';
            json_pol(b, t, 0x600, js, sizeof js);
            PS(",\"away\":");
            PS(js);
            json_pol(b, f, 0x600, js, sizeof js);
            PJ(",\"awayFrom\":%s", js);
        }
    }
#undef PJ
#undef PS
    g_prof_json[o] = 0;
}
static void profiles_profile_start(BYTE *b)
{
    int t = pc_pdb_load_start(b, g_profile_db_pib, &g_prof_cond, g_prof_rec);
    if (t == NO_SLOT) { g_s.prof_step = 2; return; }
    base_log(LOG_PROFILE "load row %d -> %d\r\n", g_prof_row, t);
    if (t < 0) { profiles_profile_done(requests_failed(t)); return; }
    g_prof_job = t;
    g_s.prof_step = 1;
}
static void profiles_profile_request(BYTE *b)
{
    char buf[128], *p, *tok, *rows, *key, k[17];
    pol_friend_copy fr;
    int row;
    pdb_schema *s;
    if (!hold_db_free()) return;
    if (requests_take(REQ_PROFILE, buf, sizeof buf) < 0) return;
    g_s.db_last = GetTickCount64();
    p = buf;
    tok = requests_line(&p);
    rows = requests_line(&p);
    key = requests_line(&p);
    lstrcpynA(g_s.prof_token, tok, sizeof g_s.prof_token);
    if (g_prof_json) g_prof_json[0] = 0;
    if (!requests_parse_num(rows, 4, &row) || row >= 200) { profiles_profile_done("failed:bad request"); return; }
    if (!peers_get_friend(b, row, &fr)) { profiles_profile_done("failed:no such friend"); return; }
    peers_friend_key(b, &fr.f, k);
    if (lstrcmpA(k, key) != 0) { profiles_profile_done("failed:friend list changed"); return; }
    /* a row still waiting for an answer has no handle id yet, so no query that could find someone else */
    if (fr.f.flags & FRIEND_PENDING) { profiles_profile_done("failed:pending"); return; }
    if (!(s = profile_db_load_pib(b)) || !profile_db_pfb_load(b)) { profiles_profile_done("failed:no schema"); return; }
    unsigned __int64 hid = friend_hid(&fr.f);
    g_prof_rec = (pdb_rec *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, s->rec_size + 8);
    if (!g_prof_rec) { profiles_profile_done("failed:alloc"); return; }
    memset(&g_prof_cond, 0, sizeof g_prof_cond);
    pc_pdb_item_set(b, s, &g_prof_cond.item, "z_hid", &hid, 1);
    if (!(g_prof_cond.item.flags & 1)) { profiles_profile_done("failed:schema"); return; }
    pc_pdb_cond_op(b, &g_prof_cond, 1);
    pc_pdb_where(b, 1, &g_prof_cond);
    g_prof_row = row;
    g_prof_hid = hid;
    lstrcpyA(g_s.prof_state, "loading");
    g_snap_dirty = 1;
    profiles_profile_start(b);
}
static void profiles_profile_pump(BYTE *b, int ctl)
{
    if (g_s.prof_step == 0) {
        if (ctl && !g_s.draining) profiles_profile_request(b);
        return;
    }
    if (g_s.prof_step == 2) {
        if (!g_s.draining) profiles_profile_start(b); else profiles_profile_done("cancelled"); return;
    }
    int r = pc_pdb_load_check(b, g_prof_job);
    if (r == 0) return;
    g_prof_job = -1;
    if (r < 0) { profiles_profile_done(requests_failed(r)); return; }
    if (!g_prof_rec->items) { profiles_profile_done("failed:no profile"); return; }
    profiles_profile_build(b);
    profiles_profile_done("done");
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
static void profiles_character_text(BYTE *b, pdb_rec *rec, const char *name, char *js, int cap, BYTE *level)
{
    pdb_item *it = profile_db_get(b, g_ch_pib, name, rec);
    char raw[0x40] = "";
    if (level) *level = it ? it->level : 0;
    if (it && (it->flags & 1) && it->v.str && !IsBadStringPtrA(it->v.str, sizeof raw + 1))
        json_sjis_copy(raw, it->v.str, sizeof raw);
    json_pol(b, raw, 0x80, js, cap);
}
static void profiles_character_done(const char *state)
{
    lstrcpynA(g_ch_state, state, sizeof g_ch_state);
    if (g_ch_rec) { HeapFree(GetProcessHeap(), 0, g_ch_rec); g_ch_rec = NULL; }
    g_s.ch_step = 0;
    g_ch_job = -1;
    base_log("character: %s\r\n", state);
    g_snap_dirty = 1;
}
static void profiles_character_abandon(BYTE *b)
{
    if (g_s.ch_step == 1 && g_ch_job >= 0) pc_free_job(b, g_ch_job);
    g_ch_rec = NULL;   /* a cancelled read can still write, so never freed */
    profiles_character_done("cancelled");
}
/* nameState 0 shown, 1 withheld, 2 empty. Like the viewer with a header field (and prof_pol's z_name), only the record
   level says whether the name's visible, z_name's own level just goes in the log */
static void profiles_character_build(BYTE *b)
{
    char nm[0x100], wn[0x100];
    BYTE lv = 0;
    int st;
    profiles_character_text(b, g_ch_rec, "z_name", nm, sizeof nm, &lv);
    st = g_ch_rec->level < 1 ? 1 : strcmp(nm, "\"\"") == 0 ? 2 : 0;
    profiles_character_text(b, g_ch_rec, "z_worldname", wn, sizeof wn, NULL);
    wsprintfA(g_ch_json, "\"name\":%s,\"nameState\":%d,\"level\":%u,\"worldName\":%s", st == 1 ? "\"\"" : nm, st,
              g_ch_rec->level, wn);
    base_log("character: record level %u, name level %u, state %d\r\n", g_ch_rec->level, lv, st);
}
static void profiles_character_start(BYTE *b)
{
    int t = pc_pdb_load_start(b, g_ch_pib, g_ch_where, g_ch_rec);
    if (t == NO_SLOT) { g_s.ch_step = 2; return; }
    base_log("character: load -> %d\r\n", t);
    if (t < 0) { profiles_character_done(requests_failed(t)); return; }
    g_ch_job = t;
    g_s.ch_step = 1;
}
static int profiles_ffxi_friend_on(BYTE *b, DWORD ctsid, DWORD lo, DWORD hi)
{
    DWORD cs, cl, chi;
    for (int i = 0; i < 200; i++) {
        const pol_friend *a = friend_row(b, i);
        if (IsBadReadPtr(a, sizeof *a)) break;
        if (friend_valid(a) && peers_friend_ffxi(a, &cs, &cl, &chi) && cs == ctsid && cl == lo && chi == hi)
            return 1;
    }
    return 0;
}

static void profiles_character_request(BYTE *b)
{
    char buf[128], *p, *tok;
    unsigned __int64 ctid;
    if (!hold_db_free() || profile_db_user_waiting()) return;
    if (requests_take(REQ_CHARACTER, buf, sizeof buf) < 0) return;
    g_s.db_last = GetTickCount64();
    p = buf;
    tok = requests_line(&p);
    lstrcpynA(g_ch_token, tok, sizeof g_ch_token);
    g_ch_json[0] = 0;
    if (!requests_parse_u32(requests_line(&p), &g_ch_ctsid) || !requests_parse_u32(requests_line(&p), &g_ch_lo) || !requests_parse_u32(requests_line(&p), &g_ch_hi)) {
        profiles_character_done("failed:bad request");
        return;
    }
    if (!profiles_ffxi_friend_on(b, g_ch_ctsid, g_ch_lo, g_ch_hi)) {
        profiles_character_done("failed:not playing"); return;
    }
    if (!profiles_character_load_pib(b)) { profiles_character_done("failed:no schema"); return; }
    g_ch_rec = (pdb_rec *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, g_ch_pib->rec_size + 8);
    if (!g_ch_rec) { profiles_character_done("failed:alloc"); return; }
    ctid = (unsigned __int64)g_ch_hi << 32 | g_ch_lo;
    memset(g_ch_cond, 0, sizeof g_ch_cond);
    pc_pdb_item_set(b, g_ch_pib, &g_ch_cond[0].item, "z_ctsid", &g_ch_ctsid, 1);
    pc_pdb_item_set(b, g_ch_pib, &g_ch_cond[1].item, "z_ctid", &ctid, 1);
    if (!(g_ch_cond[0].item.flags & 1) || !(g_ch_cond[1].item.flags & 1)) {
        profiles_character_done("failed:schema"); return;
    }
    pc_pdb_cond_op(b, &g_ch_cond[0], 1);
    pc_pdb_cond_op(b, &g_ch_cond[1], 1);
    g_ch_where = pc_pdb_where(b, 2, &g_ch_cond[0], &g_ch_cond[1]);
    if (!g_ch_where) { profiles_character_done("failed:schema"); return; }
    lstrcpyA(g_ch_state, "loading");
    g_snap_dirty = 1;
    profiles_character_start(b);
}
static void profiles_character_pump(BYTE *b, int ctl)
{
    if (g_s.ch_step == 0) {
        if (ctl && !g_s.draining) profiles_character_request(b);
        return;
    }
    if (g_s.ch_step == 2) {
        if (!g_s.draining) profiles_character_start(b); else profiles_character_done("cancelled"); return;
    }
    int r = pc_pdb_load_check(b, g_ch_job);
    if (r == 0) return;
    g_ch_job = -1;
    if (r < 0) { profiles_character_done(requests_failed(r)); return; }
    if (!g_ch_rec->items) { profiles_character_done("failed:no profile"); return; }
    profiles_character_build(b);
    profiles_character_done("done");
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

static int profiles_ffxiprofile_check_schema(BYTE *b, pdb_schema *s)
{
    int i, idx;
    for (i = 0; i < (int)(sizeof g_fp_nums / sizeof g_fp_nums[0]); i++) {
        const pdb_field *f = profile_db_field(s, g_fp_nums[i][0], &idx);
        if (!f || f->type != 3 || pc_pdb_field_index(b, s, g_fp_nums[i][0]) != idx) return 0;
    }
    return 1;
}
static void profiles_ffxiprofile_done(const char *state)
{
    lstrcpynA(g_fp_state, state, sizeof g_fp_state);
    if (g_fp_rec) { HeapFree(GetProcessHeap(), 0, g_fp_rec); g_fp_rec = NULL; }
    g_s.fp_step = 0;
    g_fp_job = -1;
    base_log("ffxiprofile: %s\r\n", state);
    g_snap_dirty = 1;
}
static void profiles_ffxiprofile_abandon(BYTE *b)
{
    if (g_s.fp_step == 1 && g_fp_job >= 0) pc_free_job(b, g_fp_job);
    g_fp_rec = NULL;   /* a cancelled read can still write, so never freed */
    profiles_ffxiprofile_done("cancelled");
}
/* states as the handle profile's, and prof_001 has no per-field levels so the record level decides them all. Type 3
   is a signed short */
static void profiles_ffxiprofile_build(BYTE *b)
{
    char nm[0x100], wn[0x100];
    int priv = g_fp_rec->level < 1, i, o, st;
    profiles_character_text(b, g_fp_rec, "z_name", nm, sizeof nm, NULL);
    profiles_character_text(b, g_fp_rec, "z_worldname", wn, sizeof wn, NULL);
    o = wsprintfA(g_fp_json, "\"level\":%u,\"name\":%s,\"nameState\":%d,\"worldName\":%s,\"worldNameState\":%d",
                  g_fp_rec->level, priv ? "\"\"" : nm, priv ? PV_PRIVATE : strcmp(nm, "\"\"") ? PV_OK : PV_NOTSET,
                  priv ? "\"\"" : wn, priv ? PV_PRIVATE : strcmp(wn, "\"\"") ? PV_OK : PV_NOTSET);
    for (i = 0; i < (int)(sizeof g_fp_nums / sizeof g_fp_nums[0]); i++) {
        pdb_item *it = profile_db_get(b, g_ch_pib, g_fp_nums[i][0], g_fp_rec);
        st = priv ? PV_PRIVATE : it && (it->flags & 1) ? PV_OK : PV_NOTSET;
        o += wsprintfA(g_fp_json + o, ",\"%s\":%d,\"%sState\":%d", g_fp_nums[i][1],
                       st == PV_OK ? (int)(short)it->v.u16 : 0, g_fp_nums[i][1], st);
    }
    base_log("ffxiprofile: slot %d, record level %u\r\n", g_fp_slot, g_fp_rec->level);
}
static void profiles_ffxiprofile_start(BYTE *b)
{
    int t = pc_pdb_load_start(b, g_ch_pib, g_fp_where, g_fp_rec);
    if (t == NO_SLOT) { g_s.fp_step = 2; return; }
    base_log("ffxiprofile: load -> %d\r\n", t);
    if (t < 0) { profiles_ffxiprofile_done(requests_failed(t)); return; }
    g_fp_job = t;
    g_s.fp_step = 1;
}
static void profiles_ffxiprofile_request(BYTE *b)
{
    char buf[128], *p, *tok, *rows, *key, *sl, k[17];
    pol_friend_copy fr;
    const pol_game *slot;
    int row, n;
    unsigned __int64 ctid;
    if (!hold_db_free()) return;
    if (requests_take(REQ_FFXIPROFILE, buf, sizeof buf) < 0) return;
    g_s.db_last = GetTickCount64();
    p = buf;
    tok = requests_line(&p);
    rows = requests_line(&p);
    key = requests_line(&p);
    sl = requests_line(&p);
    lstrcpynA(g_fp_token, tok, sizeof g_fp_token);
    g_fp_json[0] = 0;
    g_fp_slot = -1;
    g_fp_ctsid = g_fp_lo = g_fp_hi = 0;
    if (!requests_parse_num(rows, 4, &row) || row >= 200 || !requests_parse_num(sl, 1, &n) || n > 7) {
        profiles_ffxiprofile_done("failed:bad request");
        return;
    }
    g_fp_slot = n;
    if (!peers_get_friend(b, row, &fr)) { profiles_ffxiprofile_done("failed:no such friend"); return; }
    peers_friend_key(b, &fr.f, k);
    if (lstrcmpA(k, key) != 0) { profiles_ffxiprofile_done("failed:friend list changed"); return; }
    if (fr.f.flags & FRIEND_PENDING) { profiles_ffxiprofile_done("failed:pending"); return; }
    /* the slots the tabs came from when it's the same handle, like the viewer looks in z_phead first */
    slot = profiles_slots_for(friend_hid(&fr.f));
    slot = (slot ? slot : fr.f.games) + n;
    if (!(slot->flags & 1) || slot->title != 1) { profiles_ffxiprofile_done("failed:no character"); return; }
    g_fp_ctsid = slot->ctsid;
    g_fp_lo = slot->ctid_lo;
    g_fp_hi = slot->ctid_hi;
    if (!profiles_character_load_pib(b) || !profiles_ffxiprofile_check_schema(b, g_ch_pib)) {
        profiles_ffxiprofile_done("failed:no schema"); return;
    }
    g_fp_rec = (pdb_rec *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, g_ch_pib->rec_size + 8);
    if (!g_fp_rec) { profiles_ffxiprofile_done("failed:alloc"); return; }
    ctid = (unsigned __int64)g_fp_hi << 32 | g_fp_lo;
    memset(g_fp_cond, 0, sizeof g_fp_cond);
    pc_pdb_item_set(b, g_ch_pib, &g_fp_cond[0].item, "z_ctsid", &g_fp_ctsid, 1);
    pc_pdb_item_set(b, g_ch_pib, &g_fp_cond[1].item, "z_ctid", &ctid, 1);
    if (!(g_fp_cond[0].item.flags & 1) || !(g_fp_cond[1].item.flags & 1)) {
        profiles_ffxiprofile_done("failed:schema"); return;
    }
    pc_pdb_cond_op(b, &g_fp_cond[0], 1);
    pc_pdb_cond_op(b, &g_fp_cond[1], 1);
    g_fp_where = pc_pdb_where(b, 2, &g_fp_cond[0], &g_fp_cond[1]);
    if (!g_fp_where) { profiles_ffxiprofile_done("failed:schema"); return; }
    base_log("ffxiprofile: row %d slot %d\r\n", row, n);
    lstrcpyA(g_fp_state, "loading");
    g_snap_dirty = 1;
    profiles_ffxiprofile_start(b);
}
static void profiles_ffxiprofile_pump(BYTE *b, int ctl)
{
    if (g_s.fp_step == 0) {
        if (ctl && !g_s.draining) profiles_ffxiprofile_request(b);
        return;
    }
    if (g_s.fp_step == 2) {
        if (!g_s.draining) profiles_ffxiprofile_start(b); else profiles_ffxiprofile_done("cancelled"); return;
    }
    int r = pc_pdb_load_check(b, g_fp_job);
    if (r == 0) return;
    g_fp_job = -1;
    /* "Selected profile is hidden." */
    if (r == NOTHING_FOUND) { profiles_ffxiprofile_done("failed:hidden"); return; }
    if (r < 0) { profiles_ffxiprofile_done(requests_failed(r)); return; }
    if (!g_fp_rec->items) { profiles_ffxiprofile_done("failed:no profile"); return; }
    profiles_ffxiprofile_build(b);
    profiles_ffxiprofile_done("done");
}

/* a cheap look at who's on the friend list, so the hits' marks only get worked out again when it changes */
static DWORD profiles_friends_sig(BYTE *b)
{
    DWORD h = 2166136261u;
    for (int i = 0; i < 200; i++) {
        const pol_friend *a = friend_row(b, i);
        if (IsBadReadPtr(a, sizeof *a)) break;
        h = (h ^ a->id_lo) * 16777619u;
        h = (h ^ a->id_hi) * 16777619u;
        h = (h ^ a->hid_lo) * 16777619u;
        h = (h ^ a->hid_hi) * 16777619u;
    }
    return h;
}
static void profiles_search_mark(BYTE *b)
{
    DWORD sig = profiles_friends_sig(b);
    int i;
    for (i = 0; i < g_s.srch_n; i++)
        if (!g_srch[i].key[0]) peers_key(b, g_srch[i].lo, g_srch[i].hi, (unsigned)g_srch[i].slot, g_srch[i].key);
    if (g_srch_marked && sig == g_srch_marked_for) return;
    for (i = 0; i < g_s.srch_n; i++) {
        g_srch[i].isfriend = peers_find_friend_row(b, g_srch[i].lo, g_srch[i].hi, g_srch[i].slot) >= 0;
        g_srch[i].me = (BYTE)peers_is_me(b, g_srch[i].lo, g_srch[i].hi);
    }
    g_srch_marked = 1;
    g_srch_marked_for = sig;
}
