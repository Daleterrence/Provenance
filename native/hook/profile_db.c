#define FS_LOG lg

#pragma pack(push, 1)

typedef struct pdb_field {
    char  name[0x10];
    WORD  size;         /* the file stores size + 1, svc 0x494 takes the 1 off */
    BYTE  type;         /* 0,2 bytes (ptr, len)  1 string  3,4 u16  5,6 u32  7,8 u64  9 packed */
    BYTE  b13;
    DWORD flags;        /* the viewer only sends a field with flags & 0x42 */
} pdb_field;

/* data\db\prof_pol.pib. svc 0x494 turns the offsets at +0x14, +0x1c and +0x2c into pointers, in place */
typedef struct pdb_schema {
    char       name[0x10];
    DWORD      rec_raw;
    BYTE      *p14;
    WORD       table_id;    /* 1000 = handle profiles */
    BYTE       b1a;
    BYTE       nfields;
    pdb_field *fields;
    DWORD      rec_size;
    BYTE       relocated;
    BYTE       b25, b26, flags27;
    DWORD      d28;
    BYTE      *p2c;
} pdb_schema;

typedef struct pdb_item {
    struct pdb_item *next;
    BYTE  flags;            /* bit 0 = set */
    BYTE  level;            /* per-field visibility, echoed back as the record has it */
    BYTE  field;
    BYTE  type;
    union {
        struct { void *p; DWORD len; } bytes;
        char  *str;
        WORD   u16;
        DWORD  u32;
        unsigned __int64 u64;
    } v;
} pdb_item;

typedef struct pdb_cond {
    struct pdb_cond *next;
    BYTE  op;               /* 1 = equal, the only one the viewer uses */
    BYTE  pad[3];
    pdb_item item;
} pdb_cond;

typedef struct pdb_rec {
    struct pdb_rec *next;
    BYTE  level;            /* 1 private, 2 friends, 3 everyone */
    BYTE  pad[7];
    pdb_item *items;
} pdb_rec;

typedef struct pol_handle {
    DWORD lo;           /* bit 0 valid, bits 1-31 = handle id bits 0-30 */
    DWORD hi;           /* bits 0-12 = id bits 31-43, bits 13-18 = slot */
    char  name[16];     /* 15 bytes SJIS + NUL */
    DWORD flags;
    DWORD pic;
    BYTE  chars[8];
} pol_handle;

#pragma pack(pop)

typedef char fs_chk_field[sizeof(pdb_field) == 0x18 ? 1 : -1];
typedef char fs_chk_item[sizeof(pdb_item) == 0x10 ? 1 : -1];
typedef char fs_chk_cond[sizeof(pdb_cond) == 0x18 ? 1 : -1];
typedef char fs_chk_rec[sizeof(pdb_rec) == 0x10 ? 1 : -1];
typedef char fs_chk_hnd[sizeof(pol_handle) == 0x28 ? 1 : -1];
typedef char fs_chk_hdr[sizeof(pdb_schema) == 0x30 ? 1 : -1];

typedef pdb_schema *(__cdecl *svc494_t)(void *pib_file);
typedef pdb_item   *(__cdecl *svc49c_t)(pdb_schema *, const char *name, pdb_rec *rec);
typedef void        (__cdecl *svc4a0_t)(pdb_schema *, pdb_item *out, const char *name, const void *value, int level);
typedef void        (__cdecl *svc4a8_t)(pdb_cond *, int op);
typedef pdb_cond   *(__cdecl *svc4ac_t)(int n, ...);
typedef int         (__cdecl *svc4bc_t)(pdb_schema *, const char *name);
typedef int         (__cdecl *svc4b4_t)(pdb_schema *, pdb_cond *where, pdb_rec *out);
typedef int         (__cdecl *svc4b8_t)(int job);
typedef void        (__cdecl *svc4d0_t)(pdb_rec *set, int level, int n, ...);
typedef int         (__cdecl *svc4d4_t)(pdb_schema *, pdb_cond *where, pdb_rec *set);
typedef int         (__cdecl *svc4d8_t)(int job);
typedef void        (__cdecl *svc4dc_t)(int job);
typedef int         (__cdecl *svcac4_t)(DWORD *now);
typedef int         (__cdecl *svc2b4_t)(unsigned slot, pol_handle *out);
typedef int         (__cdecl *svc2b8_t)(unsigned slot, const pol_handle *in);
typedef int         (__cdecl *svc16c4_t)(int id, WCHAR *out, int cap, int flags);

static pdb_schema *g_fs_pib;    /* never freed, polcore's job slots point into it */

static const pdb_field *fs_field(const pdb_schema *s, const char *name, int *idx)
{
    int i;
    for (i = 0; i < s->nfields; i++)
        if (strncmp(s->fields[i].name, name, sizeof s->fields[i].name) == 0) {
            if (idx) *idx = i;
            return &s->fields[i];
        }
    return NULL;
}

static int fs_check_schema(BYTE *b, pdb_schema *s)
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
        const pdb_field *f = fs_field(s, want[i].n, &idx);
        if (!f || f->type != want[i].type || f->size != want[i].size) return 0;
        if (want[i].need && !(f->flags & want[i].need)) return 0;
        if (((svc4bc_t)SVC(0x4bc))(s, want[i].n) != idx) return 0;
    }
    /* the decoder's worst case: items from (buf+0x17)&~7, nfields*16 bytes of them, then rec_raw, in rec_size + 8 */
    if (0x17 + (DWORD)s->nfields * 0x10 + s->rec_raw > s->rec_size + 8) return 0;
    return 1;
}

/* data\db\<file>, relocated by polcore. check gets the relocated schema, NULL when either says no */
static pdb_schema *pib_load(BYTE *b, const WCHAR *file, const char *tag, int (*check)(BYTE *, pdb_schema *))
{
    WCHAR path[0x200 + 32];
    HANDLE f;
    DWORD sz, got = 0;
    BYTE *buf;
    pdb_schema *s;
    memset(path, 0, sizeof path);
    if (((svc16c4_t)SVC(0x16c4))(0x14, path, 0x200, 0) <= 0) return NULL;  /* "<root>\data\db\" */
    path[0x200 - 1] = 0;
    lstrcatW(path, file);
    f = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (f == INVALID_HANDLE_VALUE) { FS_LOG("db: cannot open %ls (%u)\r\n", path, GetLastError()); return NULL; }
    sz = GetFileSize(f, NULL);
    if (sz < 0x3c || sz > 0x10000) { CloseHandle(f); return NULL; }
    buf = (BYTE *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sz);
    if (!buf) { CloseHandle(f); return NULL; }
    if (!ReadFile(f, buf, sz, &got, NULL) || got != sz) { CloseHandle(f); HeapFree(GetProcessHeap(), 0, buf); return NULL; }
    CloseHandle(f);
    /* check the raw file before polcore relocates it, in 64 bits so a huge offset can't wrap past the check */
    s = (pdb_schema *)buf;
    if (memcmp(s->name, tag, lstrlenA(tag) + 1) != 0 || s->relocated != 0 || s->nfields == 0 ||
        (DWORD)(ULONG_PTR)s->fields >= sz ||
        (unsigned __int64)(DWORD)(ULONG_PTR)s->fields + (unsigned __int64)s->nfields * 0x18 > sz ||
        (DWORD)(ULONG_PTR)s->p14 >= sz || (DWORD)(ULONG_PTR)s->p2c >= sz) {
        HeapFree(GetProcessHeap(), 0, buf);
        return NULL;
    }
    s = ((svc494_t)SVC(0x494))(buf);
    if (s != (pdb_schema *)buf || !check(b, s)) {
        FS_LOG("db: %ls is not the expected schema\r\n", path);
        HeapFree(GetProcessHeap(), 0, buf);    /* no job's seen it yet */
        return NULL;
    }
    FS_LOG("db: %ls loaded, %u fields, record buffer %u bytes\r\n", path, s->nfields, s->rec_size);
    return s;
}

static pdb_schema *fs_load_pib(BYTE *b)
{
    if (!g_fs_pib) g_fs_pib = pib_load(b, L"prof_pol.pib", "prof_pol", fs_check_schema);
    return g_fs_pib;
}

static int fs_handle(BYTE *b, int slot, pol_handle *h)
{
    memset(h, 0, sizeof *h);
    return ((svc2b4_t)SVC(0x2b4))((unsigned)slot, h) >= 0 && (h->lo & 1);
}

static unsigned __int64 fs_hid(const pol_handle *h)
{
    return (((unsigned __int64)h->hi << 32 | h->lo) >> 1) & 0xfffffffffffULL;
}

static pdb_item *pdb_get(BYTE *b, pdb_schema *s, const char *name, pdb_rec *rec)
{
    return ((svc49c_t)SVC(0x49c))(s, name, rec);
}

/* searches, profiles (friends', FFXI and my own), character lookups and picture saves all go to the profile server, so
   they share the 3 s gap and go one at a time like the viewer */
static ULONGLONG g_db_last;

static int db_free(void);

#pragma pack(push, 1)
typedef struct pfb_ent  { DWORD code; char *text; } pfb_ent;
typedef struct pfb_tab  { WORD n; WORD pad; pfb_ent *e; } pfb_tab;
typedef struct pfb_item { char *label; BYTE field, ui, r6, r7; DWORD r8; pfb_tab *tab; DWORD r10; } pfb_item;
typedef struct pfb_hdr  { BYTE nItems, nConds, nTabs, relocated; pfb_item *items; void *conds; pfb_tab *tabs;
                          DWORD r10, r14; WORD nBlocks; BYTE r1a, encrypted; char *title; } pfb_hdr;
#pragma pack(pop)
typedef char pf_chk_item[sizeof(pfb_item) == 0x14 ? 1 : -1];
typedef char pf_chk_hdr[sizeof(pfb_hdr) == 0x20 ? 1 : -1];

static pfb_hdr *g_pfb;
static pfb_hdr *pf_load(BYTE *b)
{
    WCHAR path[0x200 + 32];
    DWORD sz, got = 0;
    if (g_pfb) return g_pfb;
    memset(path, 0, sizeof path);
    if (((svc16c4_t)SVC(0x16c4))(0x14, path, 0x200, 0) <= 0) return NULL;   /* "<root>\data\db\" */
    path[0x200 - 1] = 0;
    lstrcatW(path, L"prof_pol.pfb");
    HANDLE f = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (f == INVALID_HANDLE_VALUE) return NULL;
    sz = GetFileSize(f, NULL);
    if (sz < 0x20 || sz > 0x100000) { CloseHandle(f); return NULL; }
    BYTE *buf = (BYTE *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sz + 16);
    if (!buf) { CloseHandle(f); return NULL; }
    if (!ReadFile(f, buf, sz, &got, NULL) || got != sz || buf[3] != 0) {
        CloseHandle(f);
        HeapFree(GetProcessHeap(), 0, buf);
        return NULL;
    }
    CloseHandle(f);
    g_pfb = ((pfb_hdr *(__cdecl *)(void *))SVC(0x498))(buf);
    if (g_pfb) lg(LOG_PROFILE "prof_pol.pfb loaded, %u items, %u tables\r\n", g_pfb->nItems, g_pfb->nTabs);
    return g_pfb;
}
static const pfb_item *pf_item(const pfb_hdr *p, int field)
{
    for (int i = 0; i < p->nItems; i++) if (p->items[i].field == field) return &p->items[i];
    return NULL;
}
static int pf_find(const pfb_tab *t, DWORD v)
{
    if (!t) return -1;
    for (int i = 0; i < t->n; i++) if (t->e[i].code == v) return i;
    return -1;
}
/* level k (0..2) of entry i, an empty level taken from the nearest entry before it (app.dll 0x102264a9) */
static void pf_part(const pfb_tab *t, int i, int k, char *out, int cap)
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
static void pf_last(const char *x, char *out, int cap)
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
static int pf_value(BYTE *b, pdb_schema *s, pfb_hdr *p, pdb_rec *rec, const char *name, int V,
                    char out[3][0x80], int *lines)
{
    int idx = ((svc4bc_t)SVC(0x4bc))(s, name), e;
    DWORD v = 0;
    pdb_item *it = pdb_get(b, s, name, rec);
    int header = !strcmp(name, "z_name") || !strcmp(name, "z_mail");
    *lines = 1;
    out[0][0] = out[1][0] = out[2][0] = 0;
    if (idx < 0 || idx >= s->nfields || !it || rec->level < V) return PV_PRIVATE;
    if (!header && (it->level == 0 || ((s->fields[idx].flags & 0x40) && it->level < V))) return PV_PRIVATE;
    const pfb_item *pi = pf_item(p, idx);
    if (!strcmp(name, "z_area")) {
        pdb_item *c = pdb_get(b, s, "z_cntry", rec), *w = pdb_get(b, s, "z_wide", rec);
        pdb_item *l = pdb_get(b, s, "z_local", rec);
        BYTE a = it->v.bytes.p ? *(BYTE *)it->v.bytes.p : 0;
        if (!a || !c || !w || !l || !c->v.bytes.p || !w->v.bytes.p || !l->v.bytes.p) return PV_NOTSET;
        v = (DWORD)a << 24 | (DWORD)*(BYTE *)c->v.bytes.p << 16 | (DWORD)*(BYTE *)w->v.bytes.p << 8 |
            *(BYTE *)l->v.bytes.p;
        if (!pi || (e = pf_find(pi->tab, v)) < 0) return PV_NOTSET;
        pf_part(pi->tab, e, 0, out[0], 0x80);
        pf_part(pi->tab, e, 1, out[1], 0x80);
        pf_part(pi->tab, e, 2, out[2], 0x80);
        *lines = 3;
        return PV_OK;
    }
    if (!strncmp(name, "z_fav", 5)) {
        if (!(v = it->v.u16) || !pi || (e = pf_find(pi->tab, v)) < 0) return PV_NOTSET;
        pf_part(pi->tab, e, 0, out[0], 0x80);
        pf_part(pi->tab, e, 1, out[1], 0x80);
        *lines = 2;
        return PV_OK;
    }
    if (!strcmp(name, "z_age")) {
        pdb_item *f = pdb_get(b, s, "z_age_f", rec);
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
            if ((e = pf_find(pi->tab, v)) < 0) return PV_NOTSET;
            pf_last(pi->tab->e[e].text, out[0], 0x80);
        } else wsprintfA(out[0], "%lu", v);
    }
    return out[0][0] ? PV_OK : PV_NOTSET;
}

/* the app asks for the next lookup as soon as one's answered, so the user's own reads and saves go first */
static int user_db_waiting(void)
{
    static const char *const ext[] = { REQ_SEARCH, REQ_PROFILE, REQ_FFXIPROFILE, REQ_MYPROFILE, REQ_SAVEPROFILE, REQ_RENAME, REQ_PIC,
                                       REQ_CHATZONES, REQ_CHATROOMS, REQ_HANDLE, REQ_XIPROFILE };
    for (int i = 0; i < (int)(sizeof ext / sizeof ext[0]); i++) if (request_waiting(ext[i])) return 1;
    return 0;
}
