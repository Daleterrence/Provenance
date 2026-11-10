
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
static char g_srch_clause[64];
static pdb_schema *g_srch_pib;
static DWORD g_srch_set, g_srch_want;
static int g_srch_marked;
static DWORD g_srch_marked_for;

static void profiles_search_done(const char *state);
static void profiles_search_start(BYTE *b, db_job *j)
{
    int t = pc_pdb_search_start(b, g_srch_pib, &j->cond[0], NULL);
    if (t != NO_SLOT) base_log("friends: search start -> %d\r\n", t);
    if (t < 0 && t != NO_SLOT) { j->done(requests_failed(t)); return; }
    db_job_started(j, 0, t);
}
static void profiles_search_fetch(BYTE *b, db_job *j)
{
    int r = pc_pdb_search_fetch(b, (int)g_srch_set, g_srch_pib, 0,
                                                                        (int)g_srch_want, j->rec);
    if (r != NO_SLOT) base_log("friends: search fetching %u -> %d\r\n", g_srch_want, r);
    if (r < 0 && r != NO_SLOT) { j->done(requests_failed(r)); return; }
    db_job_started(j, 1, r);
}
static void profiles_search_go(BYTE *b, db_job *j)
{
    if (j->step == 3) profiles_search_fetch(b, j); else profiles_search_start(b, j);
}
static db_job g_search = { .job = -1, .state = g_s.search_state, .cap = sizeof g_s.search_state, .done = profiles_search_done,
                           .start = profiles_search_go, .last_read = 4 };
static void profiles_search_done(const char *state)
{
    db_job_done(&g_search, state);
    g_srch_marked = 0;
    base_log("friends: search %s (%d hits)\r\n", state, g_s.search_n);
    g_snap_dirty = 1;
}
static void profiles_search_abandon(BYTE *b) { db_job_abandon(b, &g_search); }
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
    lstrcpynA(g_s.search_token, tok, sizeof g_s.search_token);
    g_s.search_n = 0;
    g_s.search_total = 0;
    g_s.search_many = 0;
    while (k < 3 && lstrcmpA(kind, g_srch_kinds[k]) != 0) k++;
    g_s.search_kind = k < 3 ? k : SK_HANDLE;
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
    memset(&g_search.cond[0], 0, sizeof g_search.cond[0]);
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
    pc_pdb_cond_text(b, &g_search.cond[0], 7, g_srch_clause);
    base_log("friends: search %s \"%s\"\r\n", g_srch_kinds[k], val);
    lstrcpyA(g_s.search_state, "searching");
    g_snap_dirty = 1;
    profiles_search_start(b, &g_search);
}
static void profiles_search_pump(BYTE *b, int ctl)
{
    db_job *j = &g_search;
    int r, guard = 0;
    if (j->step == 0) {
        if (ctl && !g_s.draining) profiles_search_request(b);
        return;
    }
    if (j->step == 1 || j->step == 3) { db_job_waiting(b, j, 1); return; }
    if (j->step == 2) {
        DWORD out[3] = { 0, 0, 0 };
        r = pc_pdb_search_hits(b, j->job, out);
        if (db_job_pending(b, j, r, 0)) return;
        j->job = -1;
        if (r == NOTHING_FOUND) { profiles_search_done("done"); return; }   /* "No matching results were found." */
        if (r < 0) { profiles_search_done(requests_failed(r)); return; }
        if ((int)out[0] < 0) { profiles_search_done("done"); return; }
        base_log("friends: search %d hits%s\r\n", (int)out[1], out[2] ? " (too many)" : "");
        g_s.search_total = (int)out[1] > 0 ? (int)out[1] : 0;
        /* the viewer goes by the too-many flag before it looks at the count */
        if (out[2] || (int)out[1] >= SEARCH_TOO_MANY) { g_s.search_many = 1; profiles_search_done("done"); return; }
        if ((int)out[1] <= 0) { profiles_search_done("done"); return; }
        g_srch_set = out[0];
        g_srch_want = out[1] > SEARCH_MAX ? SEARCH_MAX : out[1];
        j->rec = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, (SIZE_T)g_srch_pib->rec_size * g_srch_want + 64);
        if (!j->rec) { profiles_search_done("failed:alloc"); return; }
        profiles_search_fetch(b, j);
        return;
    }
    r = pc_pdb_search_fetch_check(b, j->job);
    if (db_job_pending(b, j, r, 0)) return;
    j->job = -1;
    if (r < 0) { profiles_search_done(requests_failed(r)); return; }
    for (BYTE *rec = (BYTE *)j->rec; rec
 && g_s.search_n < SEARCH_MAX && guard++ < SEARCH_MAX * 2; rec = *(BYTE **)rec) {
        pdb_item *ph, *it;
        const pol_friend *fr;
        srchrec *h = &g_srch[g_s.search_n];
        if (!*(DWORD *)(rec + 0xc)) continue;
        ph = profile_db_get(b, g_srch_pib, "z_phead", (pdb_rec *)rec);
        if (!ph || !(ph->flags & 1) || !(fr = (const pol_friend *)ph->v.bytes.p) || IsBadReadPtr(fr, sizeof *fr)) continue;
        if (!pc_friend_valid(fr)) continue;
        memset(h, 0, sizeof *h);
        h->lo = fr->id_lo;
        h->hi = fr->id_hi;
        h->slot = pc_friend_slot(fr);
        memcpy(h->name, fr->name, 15);
        if (g_s.search_kind == SK_CHARACTER) {
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
        g_s.search_n++;
    }
    profiles_search_done("done");
}

static void profiles_profile_done(const char *state);
static void profiles_profile_start(BYTE *b, db_job *j);
static db_job g_prof = { .job = -1, .state = g_s.prof_state, .cap = sizeof g_s.prof_state, .done = profiles_profile_done,
                         .start = profiles_profile_start, .last_read = 2 };
static int g_prof_row;
static unsigned __int64 g_prof_hid;
static char *g_prof_json;
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
    db_job_done(&g_prof, state);
    base_log(LOG_PROFILE "%s\r\n", state);
    g_snap_dirty = 1;
}
static void profiles_profile_abandon(BYTE *b) { db_job_abandon(b, &g_prof); }
static void profiles_profile_build(BYTE *b)
{
    static const char *aa[] = { "z_aa_mt", "z_aa_dt", "z_aa_t", "z_aa_m", "z_aa_c", "z_aa_w", "z_aa_d", "z_aa_e" };
    static char lines[3][0x80], t[0x400], f[0x400];
    pdb_rec *rec = g_prof.rec;
    pdb_schema *s = g_profile_db_pib;
    pfb_hdr *p = g_pfb;
    char hid[0x24], *q;
    int i, k, n, st, set = 0, r;
    pdb_item *it;
    jw w = {0};
    st = profile_db_pfb_value(b, s, p, rec, "z_name", 1, lines, &n);
    jw_pol(&w, "name", b, lines[0], 0x600, 0xc00);
    jw_int(&w, "nameState", st);
    st = profile_db_pfb_value(b, s, p, rec, "z_mail", 1, lines, &n);
    jw_pol(&w, "mail", b, lines[0], 0x600, 0xc00);
    jw_int(&w, "mailState", st);
    it = profile_db_get(b, s, "z_hid", rec);
    memset(hid, 0, sizeof hid);
    /* the viewer prints the record's low 32 bits, or the H-ID it asked for when that's 0 (a hidden one) */
    if (pc_has(b, SV_FORMAT_HID)) {
        DWORD lo = it ? (DWORD)it->v.u64 : 0, hi = 0;
        if (!lo) { lo = (DWORD)g_prof_hid; hi = (DWORD)(g_prof_hid >> 32); }
        pc_format_hid(b, hid, lo, hi);
    }
    hid[0x20] = 0;
    jw_str(&w, "hid", hid, 0xc00);
    it = profile_db_get(b, s, "z_utime", rec);
    jw_uint(&w, "updated", it ? it->v.u32 : 0);
    jw_uint(&w, "level", rec->level);
    jw_arr(&w, "rows");
    for (i = 0; p && i < p->nItems; i++) {
        const pfb_item *pi = &p->items[i];
        const char *fname;
        if (pi->field >= s->nfields || pi->ui == 6) continue;
        fname = s->fields[pi->field].name;
        if (!strcmp(fname, "z_age_f") || !strcmp(fname, "z_name") || !strcmp(fname, "z_hid") ||
            !strcmp(fname, "z_mail") || !strcmp(fname, "z_utime")) continue;
        jw_obj(&w, NULL);
        jw_pol(&w, "label", b, pi->label, 0x100, 0xc00);
        if (!strcmp(fname, "z_ficon")) {
            pdb_item *fi = profile_db_get(b, s, "z_ficon", rec);
            st = (!fi || rec->level < 1 || fi->level == 0 ||
                  ((s->fields[pi->field].flags & 0x40) && fi->level < 1)) ? PV_PRIVATE
               : (int)fi->v.u32 <= 0 ? PV_NOTSET : PV_OK;
            jw_lit(&w, "field", "z_ficon");
            jw_int(&w, "state", st);
            jw_int(&w, "pic", st == PV_OK ? (int)fi->v.u32 : 0);
            jw_end(&w);
            continue;
        }
        st = profile_db_pfb_value(b, s, p, rec, fname, 1, lines, &n);
        jw_lit(&w, "field", fname);
        jw_int(&w, "state", st);
        jw_arr(&w, "lines");
        for (k = 0; k < n; k++) jw_pol(&w, NULL, b, lines[k], 0x600, 0xc00);
        jw_end(&w);
        jw_end(&w);
    }
    jw_end(&w);
    /* the FFXI tabs: z_phead's slots like the viewer (readable whatever the level), else the friend row's. Only title
       1, other games' profiles never get a tab here */
    {
        pol_friend_copy fr;
        pdb_item *ph = profile_db_get(b, s, "z_phead", rec);
        const pol_friend *h = ph && (ph->flags & 1) ? (const pol_friend *)ph->v.bytes.p : NULL;
        const pol_game *src = NULL;
        const char *from = "nowhere";
        if (h && !IsBadReadPtr(h, sizeof *h) && pc_friend_valid(h) && pc_friend_hid(h) == g_prof_hid) {
            src = h->games;
            from = "z_phead";
        } else if (peers_get_friend(b, g_prof_row, &fr) && pc_friend_hid(&fr.f) == g_prof_hid) {
            src = fr.f.games;
            from = "the friend row";
        }
        profiles_slots_keep(g_prof_hid, src);
        jw_arr(&w, "characters");
        for (i = 0, k = 0; src && i < 8; i++) {
            const pol_game *sl = &src[i];
            DWORD cs = sl->ctsid;
            if (!(sl->flags & 1) || sl->title != 1) continue;
            jw_obj(&w, NULL);
            jw_int(&w, "slot", i);
            jw_uint(&w, "ctsid", cs);
            jw_uint(&w, "ctidLow", sl->ctid_lo);
            jw_uint(&w, "ctidHigh", sl->ctid_hi);
            jw_uint(&w, "world", (cs >> 16) & 0xff);
            jw_end(&w);
            k++;
        }
        jw_end(&w);
        base_log(LOG_PROFILE "%d FFXI characters (from %s)\r\n", k, from);
    }
    /* svc 0x1200 is the viewer's away text (-0x14cc = none or no access). All z_aa_* at 0 means none, though 0x1200
       would happily invent "I am away from the keyboard". */
    for (k = 0; k < 8 && !set; k++) {
        pdb_item *a2 = profile_db_get(b, s, aa[k], rec);
        if (a2 && a2->v.bytes.p && *(BYTE *)a2->v.bytes.p) set = 1;
    }
    if (set && pc_has(b, SV_AWAY_TEXT)) {
        memset(t, 0, sizeof t);
        memset(f, 0, sizeof f);
        r = pc_away_text(b, s, p, rec, g_prof_row, t, sizeof t, f, sizeof f);
        if (r >= 0) {
            for (q = t; *q; q++) if (*q == 7) *q = '\n';
            jw_pol(&w, "away", b, t, 0x600, 0xc00);
            jw_pol(&w, "awayFrom", b, f, 0x600, 0xc00);
        }
    }
    if (!g_prof_json) g_prof_json = (char *)HeapAlloc(GetProcessHeap(), 0, 0x8000);
    if (g_prof_json) jw_fit(&w, g_prof_json, 0x8000, "profile");
    jw_free(&w);
}
static void profiles_profile_start(BYTE *b, db_job *j)
{
    int t = pc_pdb_load_start(b, g_profile_db_pib, j->where, (pdb_rec *)j->rec);
    if (t != NO_SLOT) base_log(LOG_PROFILE "load row %d -> %d\r\n", g_prof_row, t);
    if (t < 0 && t != NO_SLOT) { j->done(requests_failed(t)); return; }
    db_job_started(j, 0, t);
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
    if (!(s = profile_db_load_pib(b)) || !profile_db_load_pfb(b)) { profiles_profile_done("failed:no schema"); return; }
    unsigned __int64 hid = pc_friend_hid(&fr.f);
    g_prof.rec = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, s->rec_size + 8);
    if (!g_prof.rec) { profiles_profile_done("failed:alloc"); return; }
    g_prof.where = db_where_hid(b, s, &hid, &g_prof.cond[0]);
    if (!g_prof.where) { profiles_profile_done("failed:schema"); return; }
    g_prof_row = row;
    g_prof_hid = hid;
    lstrcpyA(g_s.prof_state, "loading");
    g_snap_dirty = 1;
    profiles_profile_start(b, &g_prof);
}
static void profiles_profile_pump(BYTE *b, int ctl)
{
    db_job *j = &g_prof;
    int r;
    if (j->step == 0) {
        if (ctl && !g_s.draining) profiles_profile_request(b);
        return;
    }
    if (j->step == 1) { db_job_waiting(b, j, 1); return; }
    r = pc_pdb_load_check(b, j->job);
    if (db_job_pending(b, j, r, 0)) return;
    j->job = -1;
    if (r < 0) { profiles_profile_done(requests_failed(r)); return; }
    if (!((pdb_rec *)j->rec)->items) { profiles_profile_done("failed:no profile"); return; }

    profiles_profile_build(b);
    profiles_profile_done("done");
}

/* prof_001 (title 1) by z_ctsid and z_ctid together: a friend's FFXI character, the way the viewer's profile tab finds it,
   for one that's on right now, and for one of a friend's tabs. Same where, same load, a builder each */
typedef struct ffxi_read {
    db_job j;
    const char *prefix;
    int hidden;
    DWORD *ctsid, *lo, *hi;
    unsigned __int64 ctid;
    void (*build)(BYTE *b, pdb_rec *rec);
} ffxi_read;
static DWORD g_ch_ctsid, g_ch_lo, g_ch_hi;
static char g_ch_token[48], g_ch_state[32] = "", g_ch_json[0x400];
static DWORD g_fp_ctsid, g_fp_lo, g_fp_hi;
static int g_fp_slot = -1;
static char g_fp_token[48], g_fp_state[32] = "", g_fp_json[0x400];

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
/* nameState 0 shown, 1 withheld, 2 empty. Like the viewer with a header field (and prof_pol's z_name), only the record
   level says whether the name's visible, z_name's own level just goes in the log */
static void profiles_character_build(BYTE *b, pdb_rec *rec)
{
    char nm[0x100], wn[0x100];
    BYTE lv = 0;
    int st;
    profiles_character_text(b, rec, "z_name", nm, sizeof nm, &lv);
    st = rec->level < 1 ? 1 : strcmp(nm, "\"\"") == 0 ? 2 : 0;
    profiles_character_text(b, rec, "z_worldname", wn, sizeof wn, NULL);
    jw w = {0};
    jw_raw(&w, "name", st == 1 ? "\"\"" : nm);
    jw_int(&w, "nameState", st);
    jw_uint(&w, "level", rec->level);
    jw_raw(&w, "worldName", wn);
    jw_fit(&w, g_ch_json, sizeof g_ch_json, "character");
    jw_free(&w);
    base_log("character: record level %u, name level %u, state %d\r\n", rec->level, lv, st);
}
static int profiles_ffxi_friend_on(BYTE *b, DWORD ctsid, DWORD lo, DWORD hi)
{
    DWORD cs, cl, chi;
    for (int i = 0; i < 200; i++) {
        const pol_friend *a = pc_friend_row(b, i);
        if (IsBadReadPtr(a, sizeof *a)) break;
        if (pc_friend_valid(a) && peers_friend_ffxi(a, &cs, &cl, &chi) && cs == ctsid && cl == lo && chi == hi)
            return 1;
    }
    return 0;
}

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
/* states as the handle profile's, and prof_001 has no per-field levels so the record level decides them all. Type 3
   is a signed short */
static void profiles_ffxiprofile_build(BYTE *b, pdb_rec *rec)
{
    char nm[0x100], wn[0x100];
    int priv = rec->level < 1, i, st;
    char kn[40];
    jw w = {0};
    profiles_character_text(b, rec, "z_name", nm, sizeof nm, NULL);
    profiles_character_text(b, rec, "z_worldname", wn, sizeof wn, NULL);
    jw_uint(&w, "level", rec->level);
    jw_raw(&w, "name", priv ? "\"\"" : nm);
    jw_int(&w, "nameState", priv ? PV_PRIVATE : strcmp(nm, "\"\"") ? PV_OK : PV_NOTSET);
    jw_raw(&w, "worldName", priv ? "\"\"" : wn);
    jw_int(&w, "worldNameState", priv ? PV_PRIVATE : strcmp(wn, "\"\"") ? PV_OK : PV_NOTSET);
    for (i = 0; i < (int)(sizeof g_fp_nums / sizeof g_fp_nums[0]); i++) {
        pdb_item *it = profile_db_get(b, g_ch_pib, g_fp_nums[i][0], rec);
        st = priv ? PV_PRIVATE : it && (it->flags & 1) ? PV_OK : PV_NOTSET;
        jw_int(&w, g_fp_nums[i][1], st == PV_OK ? (int)(short)it->v.u16 : 0);
        wsprintfA(kn, "%sState", g_fp_nums[i][1]);
        jw_int(&w, kn, st);
    }
    jw_fit(&w, g_fp_json, sizeof g_fp_json, "FFXI profile");
    jw_free(&w);
    base_log("ffxiprofile: slot %d, record level %u\r\n", g_fp_slot, rec->level);
}
static void ffxi_read_done(ffxi_read *r, const char *state)
{
    db_job_done(&r->j, state);
    base_log("%s: %s\r\n", r->prefix, state);
    g_snap_dirty = 1;
}
static void ffxi_read_start(BYTE *b, db_job *j)
{
    ffxi_read *r = (ffxi_read *)j;
    int t = pc_pdb_load_start(b, g_ch_pib, j->where, (pdb_rec *)j->rec);
    if (t != NO_SLOT) base_log("%s: load -> %d\r\n", r->prefix, t);
    if (t < 0 && t != NO_SLOT) { j->done(requests_failed(t)); return; }
    db_job_started(j, 0, t);
}
/* note is a line for the log once the where's built, or NULL */
static void ffxi_read_go(BYTE *b, ffxi_read *r, const char *note)
{
    db_job *j = &r->j;
    j->rec = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, g_ch_pib->rec_size + 8);
    if (!j->rec) { j->done("failed:alloc"); return; }
    r->ctid = (unsigned __int64)*r->hi << 32 | *r->lo;
    j->where = db_where_character(b, g_ch_pib, r->ctsid, &r->ctid, j->cond);
    if (!j->where) { j->done("failed:schema"); return; }
    if (note) base_log("%s", note);
    lstrcpyA(j->state, "loading");
    g_snap_dirty = 1;
    ffxi_read_start(b, j);
}
static void ffxi_read_pump(BYTE *b, ffxi_read *r, int ctl, void (*request)(BYTE *))
{
    db_job *j = &r->j;
    int rc;
    if (j->step == 0) {
        if (ctl && !g_s.draining) request(b);
        return;
    }
    if (j->step == 1) { db_job_waiting(b, j, 1); return; }
    rc = pc_pdb_load_check(b, j->job);
    if (db_job_pending(b, j, rc, 0)) return;
    j->job = -1;
    /* "Selected profile is hidden." */
    if (rc == NOTHING_FOUND && r->hidden) { j->done("failed:hidden"); return; }
    if (rc < 0) { j->done(requests_failed(rc)); return; }
    if (!((pdb_rec *)j->rec)->items) { j->done("failed:no profile"); return; }
    r->build(b, (pdb_rec *)j->rec);
    j->done("done");
}
static void profiles_character_done(const char *state);
static void profiles_ffxiprofile_done(const char *state);
static ffxi_read g_ch_read = { { .job = -1, .state = g_ch_state, .cap = sizeof g_ch_state, .done = profiles_character_done,
                                 .start = ffxi_read_start, .last_read = 2 },
                               "character", 0, &g_ch_ctsid, &g_ch_lo, &g_ch_hi, 0, profiles_character_build };
static ffxi_read g_fp_read = { { .job = -1, .state = g_fp_state, .cap = sizeof g_fp_state, .done = profiles_ffxiprofile_done,
                                 .start = ffxi_read_start, .last_read = 2 },
                               "ffxiprofile", 1, &g_fp_ctsid, &g_fp_lo, &g_fp_hi, 0, profiles_ffxiprofile_build };
static void profiles_character_done(const char *state) { ffxi_read_done(&g_ch_read, state); }
static void profiles_ffxiprofile_done(const char *state) { ffxi_read_done(&g_fp_read, state); }
static void profiles_character_abandon(BYTE *b) { db_job_abandon(b, &g_ch_read.j); }
static void profiles_ffxiprofile_abandon(BYTE *b) { db_job_abandon(b, &g_fp_read.j); }

static void profiles_character_request(BYTE *b)
{
    char buf[128], *p, *tok;
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
    ffxi_read_go(b, &g_ch_read, NULL);
}
static void profiles_character_pump(BYTE *b, int ctl) { ffxi_read_pump(b, &g_ch_read, ctl, profiles_character_request); }

/* a friend's FFXI character for their profile's tabs, the viewer's prof_001 read. The app only says which slot, the
   ids come from the friend's own slots */
static void profiles_ffxiprofile_request(BYTE *b)
{
    char buf[128], *p, *tok, *rows, *key, *sl, k[17];
    pol_friend_copy fr;
    const pol_game *slot;
    int row, n;
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
    slot = profiles_slots_for(pc_friend_hid(&fr.f));
    slot = (slot ? slot : fr.f.games) + n;
    if (!(slot->flags & 1) || slot->title != 1) { profiles_ffxiprofile_done("failed:no character"); return; }
    g_fp_ctsid = slot->ctsid;
    g_fp_lo = slot->ctid_lo;
    g_fp_hi = slot->ctid_hi;
    if (!profiles_character_load_pib(b) || !profiles_ffxiprofile_check_schema(b, g_ch_pib)) {
        profiles_ffxiprofile_done("failed:no schema"); return;
    }
    {
        char note[48];
        wsprintfA(note, "ffxiprofile: row %d slot %d\r\n", row, n);
        ffxi_read_go(b, &g_fp_read, note);
    }
}
static void profiles_ffxiprofile_pump(BYTE *b, int ctl) { ffxi_read_pump(b, &g_fp_read, ctl, profiles_ffxiprofile_request); }

/* a cheap look at who's on the friend list, so the hits' marks only get worked out again when it changes */
static DWORD profiles_friends_sig(BYTE *b)
{
    DWORD h = 2166136261u;
    for (int i = 0; i < 200; i++) {
        const pol_friend *a = pc_friend_row(b, i);
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
    for (i = 0; i < g_s.search_n; i++)
        if (!g_srch[i].key[0]) peers_key(b, g_srch[i].lo, g_srch[i].hi, (unsigned)g_srch[i].slot, g_srch[i].key);
    if (g_srch_marked && sig == g_srch_marked_for) return;
    for (i = 0; i < g_s.search_n; i++) {
        g_srch[i].isfriend = peers_find_friend_row(b, g_srch[i].lo, g_srch[i].hi, g_srch[i].slot) >= 0;
        g_srch[i].me = (BYTE)peers_is_me(b, g_srch[i].lo, g_srch[i].hi);
    }
    g_srch_marked = 1;
    g_srch_marked_for = sig;
}
