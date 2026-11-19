#define DB_LOG base_log

static pdb_schema *g_profile_db_pib;    /* never freed, polcore's job slots point into it */

static const pdb_field *profile_db_field(const pdb_schema *s, const char *name, int *idx)
{
    int i;
    for (i = 0; i < s->nfields; i++)
        if (strncmp(s->fields[i].name, name, sizeof s->fields[i].name) == 0) {
            if (idx) *idx = i;
            return &s->fields[i];
        }
    return NULL;
}

static int profile_db_check_schema(BYTE *b, pdb_schema *s)
{
    static const struct { const char *n; BYTE type; WORD size; DWORD need; } want[] = {
        { "z_hid",   8, 8, 0    },
        { "z_ficon", 6, 4, 0x42 },
        { "z_utime", 6, 4, 0x42 },
        { "z_pnum",  2, 1, 0    },
    };
    int i, idx;
    if (s->table_id != 1000) return 0;
    for (i = 0; i < (int)(sizeof want / sizeof want[0]); i++) {
        const pdb_field *f = profile_db_field(s, want[i].n, &idx);
        if (!f || f->type != want[i].type || f->size != want[i].size) return 0;
        if (want[i].need && !(f->flags & want[i].need)) return 0;
        if (pc_pdb_field_index(b, s, want[i].n) != idx) return 0;
    }
    if (0x17 + (unsigned __int64)s->nfields * 0x10 + s->rec_raw > (unsigned __int64)s->rec_size + 8) return 0;
    return 1;
}

static BYTE *profile_db_file_read(BYTE *b, const WCHAR *file, WCHAR path[0x200 + 32], DWORD min, DWORD max, DWORD slack,
                                  int log_open, DWORD *size)
{
    HANDLE f;
    DWORD sz, got = 0;
    BYTE *buf;
    memset(path, 0, (0x200 + 32) * sizeof(WCHAR));
    if (pc_data_path(b, 0x14, path, 0x200, 0) <= 0) return NULL;
    path[0x200 - 1] = 0;
    lstrcatW(path, file);
    f = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (f == INVALID_HANDLE_VALUE) {
        if (log_open) DB_LOG("db: cannot open %ls (%u)\r\n", path, GetLastError());
        return NULL;
    }
    sz = GetFileSize(f, NULL);
    if (sz < min || sz > max) { CloseHandle(f); return NULL; }
    buf = (BYTE *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sz + slack);
    if (!buf) { CloseHandle(f); return NULL; }
    if (!ReadFile(f, buf, sz, &got, NULL) || got != sz) { CloseHandle(f); HeapFree(GetProcessHeap(), 0, buf); return NULL; }
    CloseHandle(f);
    *size = sz;
    return buf;
}

static pdb_schema *profile_db_load_file(BYTE *b, const WCHAR *file, const char *tag, int (*check)(BYTE *, pdb_schema *))
{
    WCHAR path[0x200 + 32];
    DWORD sz;
    BYTE *buf = profile_db_file_read(b, file, path, 0x3c, 0x10000, 0, 1, &sz);
    pdb_schema *s;
    if (!buf) return NULL;
    s = (pdb_schema *)buf;
    if (memcmp(s->name, tag, lstrlenA(tag) + 1) != 0 || s->relocated != 0 || s->nfields == 0 ||
        s->rec_size > 0x10000 || s->rec_raw > 0x10000 ||
        (DWORD)(ULONG_PTR)s->fields >= sz ||
        (unsigned __int64)(DWORD)(ULONG_PTR)s->fields + (unsigned __int64)s->nfields * 0x18 > sz ||
        (DWORD)(ULONG_PTR)s->p14 >= sz || (DWORD)(ULONG_PTR)s->p2c >= sz) {
        HeapFree(GetProcessHeap(), 0, buf);
        return NULL;
    }
    s = pc_pib_load(b, buf);
    if (s != (pdb_schema *)buf || !check(b, s)) {
        DB_LOG("db: %ls is not the expected schema\r\n", path);
        HeapFree(GetProcessHeap(), 0, buf);
        return NULL;
    }
    DB_LOG("db: %ls loaded, %u fields, record buffer %u bytes\r\n", path, s->nfields, s->rec_size);
    return s;
}

static pdb_schema *profile_db_load_pib(BYTE *b)
{
    if (!g_profile_db_pib)
        g_profile_db_pib = profile_db_load_file(b, L"prof_pol.pib", "prof_pol", profile_db_check_schema);
    return g_profile_db_pib;
}

static pdb_item *profile_db_get(BYTE *b, pdb_schema *s, const char *name, pdb_rec *rec)
{
    return pc_pdb_find(b, s, name, rec);
}

static pdb_cond *db_where_hid(BYTE *b, pdb_schema *s, unsigned __int64 *hid, pdb_cond *c)
{
    memset(c, 0, sizeof *c);
    pc_pdb_item_set(b, s, &c->item, "z_hid", hid, 1);
    if (!(c->item.flags & 1)) return NULL;
    pc_pdb_cond_op(b, c, 1);
    return pc_pdb_where(b, 1, c);
}
static pdb_cond *db_where_character(BYTE *b, pdb_schema *s, DWORD *ctsid, unsigned __int64 *ctid, pdb_cond c[2])
{
    memset(c, 0, 2 * sizeof c[0]);
    pc_pdb_item_set(b, s, &c[0].item, "z_ctsid", ctsid, 1);
    pc_pdb_item_set(b, s, &c[1].item, "z_ctid", ctid, 1);
    if (!(c[0].item.flags & 1) || !(c[1].item.flags & 1)) return NULL;
    pc_pdb_cond_op(b, &c[0], 1);
    pc_pdb_cond_op(b, &c[1], 1);
    return pc_pdb_where(b, 2, &c[0], &c[1]);
}

static void db_job_started(db_job *j, int phase, int t)
{
    if (t == NO_SLOT) { j->step = 1 + 2 * phase; return; }
    if (t < 0) return;
    j->job = t;
    j->step = 2 + 2 * phase;
}
static void db_job_done(db_job *j, const char *state)
{
    lstrcpynA(j->state, state, j->cap);
    if (j->rec && !j->keep) { HeapFree(GetProcessHeap(), 0, j->rec); j->rec = NULL; }
    j->step = 0;
    j->job = -1;
}
static void db_job_abandon(BYTE *b, db_job *j)
{
    if (j->step < 1 || j->step > j->last_read) return;
    if (!(j->step & 1) && j->job >= 0) {
        pc_free_job(b, j->job);
        j->rec = NULL;   /* a cancelled read can still be written into, so its record's never freed */
    }
    j->done("cancelled");
}
static void db_job_waiting(BYTE *b, db_job *j, int cancel_on_drain)
{
    if (g_s.draining && cancel_on_drain) j->done("cancelled");
    else if (j->wait_ms && GetTickCount64() - j->t0 >= j->wait_ms) j->done("failed:busy");
    else j->start(b, j);
}
static int db_job_pending(BYTE *b, db_job *j, int r, int cancel_on_drain)
{
    if (r) return 0;
    if (g_s.draining && cancel_on_drain) db_job_abandon(b, j);
    return 1;
}

static int hold_db_free(void);

static pfb_hdr *g_pfb;
static pfb_hdr *profile_db_pfb_load(BYTE *b, const WCHAR *file, const char *tag)
{
    WCHAR path[0x200 + 32];
    DWORD sz;
    BYTE *buf = profile_db_file_read(b, file, path, 0x20, 0x100000, 16, 0, &sz);
    pfb_hdr *p;
    if (!buf) return NULL;
    p = buf[3] == 0 ? pc_pfb_load(b, buf) : NULL;
    if (!p) { HeapFree(GetProcessHeap(), 0, buf); return NULL; }
    base_log("%s%ls loaded, %u items, %u tables\r\n", tag, file, p->nItems, p->nTabs);
    return p;
}
static pfb_hdr *profile_db_load_pfb(BYTE *b)
{
    if (!g_pfb) g_pfb = profile_db_pfb_load(b, L"prof_pol.pfb", LOG_PROFILE);
    return g_pfb;
}
static const pfb_item *profile_db_pfb_item(const pfb_hdr *p, int field)
{
    for (int i = 0; i < p->nItems; i++) if (p->items[i].field == field) return &p->items[i];
    return NULL;
}
static int profile_db_pfb_find(const pfb_tab *t, DWORD v)
{
    if (!t) return -1;
    for (int i = 0; i < t->n; i++) if (t->e[i].code == v) return i;
    return -1;
}
static void profile_db_pfb_part(const pfb_tab *t, int i, int k, char *out, int cap)
{
    out[0] = 0;
    for (; i >= 0; i--) {
        const char *x = t->e[i].text;
        int j;
        if (!x) continue;
        for (j = 0; j < k && *x; x++) if (*x == '\n') j++;
        if (*x == '\n' || !*x) continue;
        for (j = 0; x[j] && x[j] != '\n' && j < cap - 1; j++) out[j] = x[j];
        out[j] = 0;
        return;
    }
}
static void profile_db_pfb_last(const char *x, char *out, int cap)
{
    const char *best = x, *q;
    int j;
    out[0] = 0;
    if (!x) return;
    for (q = x; *q; q++) if (*q == '\n' && q[1] && q[1] != '\n') best = q + 1;
    for (j = 0; best[j] && best[j] != '\n' && j < cap - 1; j++) out[j] = best[j];
    out[j] = 0;
}
enum { PV_OK, PV_PRIVATE, PV_NOTSET, PV_UNDER10 };
static int profile_db_pfb_value(BYTE *b, pdb_schema *s, pfb_hdr *p, pdb_rec *rec, const char *name, int V,
                    char out[3][0x80], int *lines)
{
    int idx = pc_pdb_field_index(b, s, name), e;
    DWORD v = 0;
    pdb_item *it = profile_db_get(b, s, name, rec);
    int header = !strcmp(name, "z_name") || !strcmp(name, "z_mail");
    *lines = 1;
    out[0][0] = out[1][0] = out[2][0] = 0;
    if (idx < 0 || idx >= s->nfields || !it || rec->level < V) return PV_PRIVATE;
    if (!header && (it->level == 0 || ((s->fields[idx].flags & 0x40) && it->level < V))) return PV_PRIVATE;
    const pfb_item *pi = profile_db_pfb_item(p, idx);
    if (!strcmp(name, "z_area")) {
        pdb_item *c = profile_db_get(b, s, "z_cntry", rec), *w = profile_db_get(b, s, "z_wide", rec);
        pdb_item *l = profile_db_get(b, s, "z_local", rec);
        BYTE a = it->v.bytes.p ? *(BYTE *)it->v.bytes.p : 0;
        if (!a || !c || !w || !l || !c->v.bytes.p || !w->v.bytes.p || !l->v.bytes.p) return PV_NOTSET;
        v = (DWORD)a << 24 | (DWORD)*(BYTE *)c->v.bytes.p << 16 | (DWORD)*(BYTE *)w->v.bytes.p << 8 |
            *(BYTE *)l->v.bytes.p;
        if (!pi || (e = profile_db_pfb_find(pi->tab, v)) < 0) return PV_NOTSET;
        profile_db_pfb_part(pi->tab, e, 0, out[0], 0x80);
        profile_db_pfb_part(pi->tab, e, 1, out[1], 0x80);
        profile_db_pfb_part(pi->tab, e, 2, out[2], 0x80);
        *lines = 3;
        return PV_OK;
    }
    if (!strncmp(name, "z_fav", 5)) {
        if (!(v = it->v.u16) || !pi || (e = profile_db_pfb_find(pi->tab, v)) < 0) return PV_NOTSET;
        profile_db_pfb_part(pi->tab, e, 0, out[0], 0x80);
        profile_db_pfb_part(pi->tab, e, 1, out[1], 0x80);
        *lines = 2;
        return PV_OK;
    }
    if (!strcmp(name, "z_age")) {
        pdb_item *f = profile_db_get(b, s, "z_age_f", rec);
        v = it->v.u32;
        if (v < 1 || v > 999) return PV_NOTSET;
        if (!f || !f->v.bytes.p || !*(BYTE *)f->v.bytes.p) wsprintfA(out[0], "%u", v);
        else if (v < 10) return PV_UNDER10;
        else wsprintfA(out[0], "%u0's", v / 10);
        return PV_OK;
    }
    switch (s->fields[idx].type) {
    case 1: if (it->v.str && !IsBadStringPtrA(it->v.str, 0x140)) lstrcpynA(out[0], it->v.str, 0x80); break;
    case 2: v = it->v.bytes.p ? *(BYTE *)it->v.bytes.p : 0; break;
    case 3: case 4: v = it->v.u16; break;
    case 5: case 6: v = it->v.u32; break;
    }
    if (s->fields[idx].type != 1) {
        if (pi && (pi->ui == 5 || pi->ui == 6)) {
            if ((e = profile_db_pfb_find(pi->tab, v)) < 0) return PV_NOTSET;
            profile_db_pfb_last(pi->tab->e[e].text, out[0], 0x80);
        } else wsprintfA(out[0], "%lu", v);
    }
    return out[0][0] ? PV_OK : PV_NOTSET;
}

static int profile_db_user_waiting(void)
{
    return requests_listed(RQ_USER);
}
