#define CR_ZONES 32
#define CR_ROOMS 100

typedef struct { char irc[64]; char topic[80]; int locked; DWORD people, capa, ctime; WORD cate[3]; } cr_room;
typedef struct { BYTE order, field; WORD pad; void *next; } cr_sort;

static pdb_schema *g_cr_cpib, *g_cr_rpib;
static pfb_hdr *g_cr_cpfb, *g_cr_rpfb;
static int g_cr_job = -1;
static int g_cr_set, g_cr_count;
static BYTE *g_cr_buf;
static char g_cr_ztok[48], g_cr_zstate[32] = "";
static char g_cr_rtok[48], g_cr_rstate[32] = "";
static struct { WORD code; DWORD rooms, people; } g_cr_zone[CR_ZONES];
static int g_cr_zone_n;
static cr_room g_cr_rooms[CR_ROOMS];
static int g_cr_rooms_n, g_cr_rzone, g_cr_roff, g_cr_rtotal;
static __declspec(align(8)) pdb_cond g_cr_cond[2];
static pdb_cond *g_cr_where;
static cr_sort g_cr_sortby[2];
static DWORD g_cr_vzone, g_cr_vcapa;

static int g_cr_db;   /* chat's zone and room lists, 0 idle, the steps are cr_db_pump's */

/* an IRC channel name as the room list builds it, nothing that'd change the JOIN line */
static int cr_irc_ok(const char *s)
{
    int n = lstrlenA(s);
    if (n < 2 || n > 63 || s[0] != '#') return 0;
    for (int i = 1; i < n; i++) if ((BYTE)s[i] <= 0x20 || (BYTE)s[i] >= 0x7f || s[i] == ',') return 0;
    return 1;
}

/* data\db\<name>.pfb, relocated by polcore, the same way pf_load reads prof_pol's */
static pfb_hdr *cr_pfb(BYTE *b, const WCHAR *file)
{
    WCHAR path[0x200 + 32];
    DWORD sz, got = 0;
    HANDLE f;
    BYTE *buf;
    pfb_hdr *p;
    memset(path, 0, sizeof path);
    if (((svc16c4_t)SVC(0x16c4))(0x14, path, 0x200, 0) <= 0) return NULL;
    path[0x200 - 1] = 0;
    lstrcatW(path, file);
    f = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (f == INVALID_HANDLE_VALUE) return NULL;
    sz = GetFileSize(f, NULL);
    buf = sz >= 0x20 && sz <= 0x100000 ? (BYTE *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sz + 16) : NULL;
    if (!buf || !ReadFile(f, buf, sz, &got, NULL) || got != sz || buf[3] != 0) {
        CloseHandle(f);
        if (buf) HeapFree(GetProcessHeap(), 0, buf);
        return NULL;
    }
    CloseHandle(f);
    p = ((pfb_hdr *(__cdecl *)(void *))SVC(0x498))(buf);
    if (!p) { HeapFree(GetProcessHeap(), 0, buf); return NULL; }
    lg("chat: %ls loaded, %u items, %u tables\r\n", file, p->nItems, p->nTabs);
    return p;
}
static int cr_check_field(BYTE *b, pdb_schema *s, const char *n, BYTE type, WORD size)
{
    int idx;
    const pdb_field *f = fs_field(s, n, &idx);
    return f && f->type == type && f->size == size && ((svc4bc_t)SVC(0x4bc))(s, n) == idx;
}
static int cr_check_count(BYTE *b, pdb_schema *s)
{
    return s->table_id == 1002 && cr_check_field(b, s, "z_zone", 4, 2) && cr_check_field(b, s, "z_count", 6, 4) &&
           cr_check_field(b, s, "z_spers", 5, 4) && 0x17 + (DWORD)s->nfields * 0x10 + s->rec_raw <= s->rec_size + 8;
}
static int cr_check_chan(BYTE *b, pdb_schema *s)
{
    static const struct { const char *n; BYTE type; WORD size; } want[] = {
        { "z_pf", 0, 1 }, { "z_lang", 2, 1 }, { "z_use", 0, 1 }, { "z_kind", 0, 1 }, { "z_idx", 1, 46 },
        { "z_npers", 5, 4 }, { "z_zone", 4, 2 }, { "z_cate0", 4, 2 }, { "z_cate1", 4, 2 }, { "z_cate2", 4, 2 },
        { "z_topic", 1, 73 }, { "z_capa", 5, 4 }, { "z_chlock", 2, 1 }, { "z_ctime", 6, 4 },
    };
    if (s->table_id != 1001) return 0;
    for (int i = 0; i < (int)(sizeof want / sizeof want[0]); i++)
        if (!cr_check_field(b, s, want[i].n, want[i].type, want[i].size)) return 0;
    return 0x17 + (DWORD)s->nfields * 0x10 + s->rec_raw <= s->rec_size + 8;
}
static const pfb_tab *cr_tab_of(const pfb_hdr *p, int field)
{
    const pfb_item *it = p ? pf_item(p, field) : NULL;
    return it ? it->tab : NULL;
}
/* one the list shows, so a room can't be made in a zone the viewer hides */
static int cr_zone_known(DWORD code)
{
    for (int i = 0; i < g_cr_zone_n; i++) if (g_cr_zone[i].code == code) return 1;
    return 0;
}

static void cr_db_done(int rooms, const char *state)
{
    lstrcpynA(rooms ? g_cr_rstate : g_cr_zstate, state, 32);
    if (g_cr_buf) { HeapFree(GetProcessHeap(), 0, g_cr_buf); g_cr_buf = NULL; }
    g_cr_db = 0;
    g_cr_job = -1;
    lg("chat: %s %s\r\n", rooms ? "room list" : "zone list", state);
    g_snap_dirty = 1;
}
/* a cancelled read can still be written into, so its buffer's never freed */
static void cr_db_abandon(BYTE *b)
{
    int rooms = g_cr_db >= 10;
    if (g_cr_job >= 0 && (g_cr_db % 10 == 2 || g_cr_db % 10 == 4)) ((svc4dc_t)SVC(0x4dc))(g_cr_job);
    g_cr_buf = NULL;
    cr_db_done(rooms, "cancelled");
}
static void cr_db_start(BYTE *b)
{
    int rooms = g_cr_db >= 10;
    int t = rooms ? ((int (__cdecl *)(void *, void *, void *))SVC(0x4c0))(g_cr_rpib, g_cr_where, g_cr_sortby)
                  : ((int (__cdecl *)(void *, void *, void *))SVC(0x4c0))(g_cr_cpib, NULL, NULL);
    if (t == NO_SLOT) { g_cr_db = rooms ? 11 : 1; return; }
    if (t < 0) { cr_db_done(rooms, failed(t)); return; }
    g_cr_job = t;
    g_cr_db = rooms ? 12 : 2;
}
static void cr_db_fetch(BYTE *b)
{
    int rooms = g_cr_db >= 10, r;
    if (rooms) r = ((int (__cdecl *)(int, void *, int, int, void *))SVC(0x4c8))(g_cr_set, g_cr_rpib, g_cr_roff,
                                                                               g_cr_roff + 100, g_cr_buf);
    else r = ((int (__cdecl *)(int, void *, int, int, void *))SVC(0x4c8))(g_cr_set, g_cr_cpib, 0, g_cr_count, g_cr_buf);
    if (r == NO_SLOT) { g_cr_db = rooms ? 13 : 3; return; }
    if (r < 0) { cr_db_done(rooms, failed(r)); return; }
    g_cr_job = r;
    g_cr_db = rooms ? 14 : 4;
}
static DWORD cr_u(BYTE *b, pdb_schema *s, const char *n, pdb_rec *rec, int bytes)
{
    pdb_item *it = pdb_get(b, s, n, rec);
    if (!it || !(it->flags & 1)) return 0;
    if (bytes) return it->v.bytes.p && !IsBadReadPtr(it->v.bytes.p, 1) ? *(BYTE *)it->v.bytes.p : 0;
    return it->type == 3 || it->type == 4 ? it->v.u16 : it->v.u32;
}
static void cr_str(BYTE *b, pdb_schema *s, const char *n, pdb_rec *rec, char *out, int cap)
{
    pdb_item *it = pdb_get(b, s, n, rec);
    out[0] = 0;
    if (it && (it->flags & 1) && it->v.str && !IsBadStringPtrA(it->v.str, cap + 1)) sjis_copy(out, it->v.str, cap);
}
static void cr_parse_zones(BYTE *b)
{
    int guard = 0;
    for (int i = 0; i < g_cr_zone_n; i++) g_cr_zone[i].rooms = g_cr_zone[i].people = 0;
    for (BYTE *rec = g_cr_buf; rec && guard++ < CR_ZONES * 2; rec = *(BYTE **)rec) {
        DWORD z;
        if (!*(DWORD *)(rec + 0xc)) continue;
        z = cr_u(b, g_cr_cpib, "z_zone", (pdb_rec *)rec, 0);
        for (int i = 0; i < g_cr_zone_n; i++)
            if (g_cr_zone[i].code == z) {
                g_cr_zone[i].rooms = cr_u(b, g_cr_cpib, "z_count", (pdb_rec *)rec, 0);
                g_cr_zone[i].people = cr_u(b, g_cr_cpib, "z_spers", (pdb_rec *)rec, 0);
            }
    }
}
static void cr_parse_rooms(BYTE *b)
{
    int guard = 0;
    g_cr_rooms_n = 0;
    for (BYTE *rec = g_cr_buf; rec && g_cr_rooms_n < CR_ROOMS && guard++ < CR_ROOMS * 2; rec = *(BYTE **)rec) {
        cr_room *r = &g_cr_rooms[g_cr_rooms_n];
        pdb_rec *pr = (pdb_rec *)rec;
        char idx[64];
        BYTE pf, kind, use;
        if (!*(DWORD *)(rec + 0xc)) continue;
        memset(r, 0, sizeof *r);
        pf = (BYTE)cr_u(b, g_cr_rpib, "z_pf", pr, 1);
        use = (BYTE)cr_u(b, g_cr_rpib, "z_use", pr, 1);
        kind = (BYTE)cr_u(b, g_cr_rpib, "z_kind", pr, 1);
        cr_str(b, g_cr_rpib, "z_idx", pr, idx, 47);
        if (!pf || !use || !kind) continue;
        wsprintfA(r->irc, "%c%02X%c%c", pf, cr_u(b, g_cr_rpib, "z_lang", pr, 1), use, kind);
        lstrcpynA(r->irc + 5, idx, sizeof r->irc - 5);
        if (!cr_irc_ok(r->irc)) continue;
        cr_str(b, g_cr_rpib, "z_topic", pr, r->topic, 74);
        r->locked = cr_u(b, g_cr_rpib, "z_chlock", pr, 1) != 0;
        r->people = cr_u(b, g_cr_rpib, "z_npers", pr, 0);
        r->capa = cr_u(b, g_cr_rpib, "z_capa", pr, 0);
        r->ctime = cr_u(b, g_cr_rpib, "z_ctime", pr, 0);
        r->cate[0] = (WORD)cr_u(b, g_cr_rpib, "z_cate0", pr, 0);
        r->cate[1] = (WORD)cr_u(b, g_cr_rpib, "z_cate1", pr, 0);
        r->cate[2] = (WORD)cr_u(b, g_cr_rpib, "z_cate2", pr, 0);
        g_cr_rooms_n++;
    }
}
/* this entry's own part k, not one pf_part would borrow from the entry before */
static int cr_part_there(const pfb_tab *t, int i, int k)
{
    const char *x = t->e[i].text;
    int j = 0;
    if (!x) return 0;
    for (; j < k && *x; x++) if (*x == '\n') j++;
    return j == k && *x && *x != '\n';
}
static int cr_db_load(BYTE *b, int rooms)
{
    if (!g_cr_cpib) g_cr_cpib = pib_load(b, L"ch_count.pib", "ch_count", cr_check_count);
    if (!g_cr_cpfb) g_cr_cpfb = cr_pfb(b, L"ch_count.pfb");
    if (!g_cr_cpib || !cr_tab_of(g_cr_cpfb, 0)) return 0;
    /* the room categories ride along with the zones, Create Room needs them before any room list */
    if (!g_cr_rpfb) g_cr_rpfb = cr_pfb(b, L"c_chan.pfb");
    if (!g_cr_zone_n) {
        const pfb_tab *t = cr_tab_of(g_cr_cpfb, 0);
        int lang = SVC(0xf18) ? ((svc_v)SVC(0xf18))() : 0, row = 0;
        for (int i = 0; i < t->n && g_cr_zone_n < CR_ZONES; i++) {
            /* the viewer lists a group's name (part 0) and the zone (part 2), leaving out rows 5-8, or 6-8 in Japanese
               (app.dll 0x100a12bd, by svc 0xf18). By row, not by name, same as it does */
            if (cr_part_there(t, i, 0)) row++;
            if (!cr_part_there(t, i, 2)) continue;
            if (row < (lang == 0 ? 6 : 5) || row > 8) g_cr_zone[g_cr_zone_n++].code = (WORD)t->e[i].code;
            row++;
        }
    }
    if (!rooms) return 1;
    if (!g_cr_rpib) g_cr_rpib = pib_load(b, L"c_chan.pib", "c_chan", cr_check_chan);
    return g_cr_rpib != NULL;
}
static void cr_zones_request(BYTE *b)
{
    char buf[128], *p;
    if (take_request(REQ_CHATZONES, buf, sizeof buf) < 0) return;
    g_cr_used = 1;
    g_db_last = GetTickCount64();
    p = buf;
    lstrcpynA(g_cr_ztok, req_line(&p), sizeof g_cr_ztok);
    if (cr_session(b) < 0) { cr_db_done(0, "failed:no chat"); return; }
    if (!cr_db_load(b, 0)) { cr_db_done(0, "failed:no schema"); return; }
    lstrcpyA(g_cr_zstate, "searching");
    g_snap_dirty = 1;
    g_cr_db = 1;
    cr_db_start(b);
}
static void cr_rooms_request(BYTE *b)
{
    char buf[128], *p;
    int zone, off;
    if (take_request(REQ_CHATROOMS, buf, sizeof buf) < 0) return;
    g_cr_used = 1;
    g_db_last = GetTickCount64();
    p = buf;
    lstrcpynA(g_cr_rtok, req_line(&p), sizeof g_cr_rtok);
    g_cr_rooms_n = g_cr_rtotal = 0;
    if (!parse_num(req_line(&p), 5, &zone) || !parse_num(req_line(&p), 5, &off) || off % 100 || off > 9900) {
        cr_db_done(1, "failed:bad request");
        return;
    }
    if (cr_session(b) < 0) { cr_db_done(1, "failed:no chat"); return; }
    if (!cr_db_load(b, 1)) { cr_db_done(1, "failed:no schema"); return; }
    if (!cr_zone_known((DWORD)zone)) { cr_db_done(1, "failed:bad zone"); return; }
    g_cr_rzone = zone;
    g_cr_roff = off;
    /* the viewer's plain list: this zone, z_capa op 3 against 1, official rooms first then by name */
    memset(g_cr_cond, 0, sizeof g_cr_cond);
    g_cr_vzone = (DWORD)zone;
    g_cr_vcapa = 1;
    ((svc4a0_t)SVC(0x4a0))(g_cr_rpib, &g_cr_cond[0].item, "z_zone", &g_cr_vzone, 1);
    ((void (__cdecl *)(pdb_cond *, int, const char *))SVC(0x4a4))(&g_cr_cond[0], 1, NULL);
    ((svc4a0_t)SVC(0x4a0))(g_cr_rpib, &g_cr_cond[1].item, "z_capa", &g_cr_vcapa, 1);
    ((void (__cdecl *)(pdb_cond *, int, const char *))SVC(0x4a4))(&g_cr_cond[1], 3, NULL);
    if (!(g_cr_cond[0].item.flags & 1) || !(g_cr_cond[1].item.flags & 1)) { cr_db_done(1, "failed:schema"); return; }
    g_cr_where = ((svc4ac_t)SVC(0x4ac))(8, &g_cr_cond[0], &g_cr_cond[1], NULL, NULL, NULL, NULL, NULL, NULL);
    if (!g_cr_where) { cr_db_done(1, "failed:schema"); return; }
    g_cr_sortby[0].order = 1;
    g_cr_sortby[0].field = (BYTE)((svc4bc_t)SVC(0x4bc))(g_cr_rpib, "z_kind");
    g_cr_sortby[0].next = &g_cr_sortby[1];
    g_cr_sortby[1].order = 1;
    g_cr_sortby[1].field = (BYTE)((svc4bc_t)SVC(0x4bc))(g_cr_rpib, "z_topic");
    g_cr_sortby[1].next = NULL;
    lg("chat: room list for zone %d from %d\r\n", zone, off);
    lstrcpyA(g_cr_rstate, "searching");
    g_snap_dirty = 1;
    g_cr_db = 11;
    cr_db_start(b);
}
static void cr_db_pump(BYTE *b)
{
    int rooms = g_cr_db >= 10, step = g_cr_db % 10, r;
    pdb_schema *s = rooms ? g_cr_rpib : g_cr_cpib;
    if (step == 1) { if (!g_draining) cr_db_start(b); else cr_db_done(rooms, "cancelled"); return; }
    if (step == 3) { if (!g_draining) cr_db_fetch(b); else cr_db_done(rooms, "cancelled"); return; }
    if (step == 2) {
        int set = -1, count = 0;
        r = ((int (__cdecl *)(int, int *, int *))SVC(0x4c4))(g_cr_job, &set, &count);
        if (r == 0) return;
        g_cr_job = -1;
        if (r == -0x14cc || (r > 0 && (set < 0 || count < 1))) {
            if (rooms) g_cr_rtotal = 0;
            else cr_parse_zones(b);
            cr_db_done(rooms, "done");
            return;
        }
        if (r < 0) { cr_db_done(rooms, failed(r)); return; }
        g_cr_set = set;
        g_cr_count = count;
        if (rooms) {
            g_cr_rtotal = count;
            while (g_cr_roff > count && g_cr_roff >= 100) g_cr_roff -= 100;
            if (g_cr_roff > count) g_cr_roff = 0;
        }
        if (!rooms && count > CR_ZONES) g_cr_count = count = CR_ZONES;
        g_cr_buf = (BYTE *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, (SIZE_T)s->rec_size * (count > 100 ? 100 : count) + 64);
        if (!g_cr_buf) { cr_db_done(rooms, "failed:alloc"); return; }
        g_cr_db = rooms ? 13 : 3;
        cr_db_fetch(b);
        return;
    }
    r = ((svc_i)SVC(0x4cc))(g_cr_job);
    if (r == 0) return;
    g_cr_job = -1;
    if (r < 0) { cr_db_done(rooms, failed(r)); return; }
    if (rooms) cr_parse_rooms(b); else cr_parse_zones(b);
    cr_db_done(rooms, "done");
}
