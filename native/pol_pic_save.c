/* the viewer's profile pic save (app.dll CProfileCli): load my record, UPDATE z_ficon + z_utime, then SetHandle
   locally. Only built inside pol_hook.c, which defines SVC, FS_LOG and FS_GETHANDLE_FN first */
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

/* polcore's own negative codes pass straight through, ours sit in a range it doesn't use */
#define FS_PENDING        0
#define FS_DONE           1
#define FS_E_ARGS     (-0x7f01)
#define FS_E_BUSY     (-0x7f02)
#define FS_E_HANDLE   (-0x7f03)
#define FS_E_PIB      (-0x7f04)
#define FS_E_MEM      (-0x7f05)
#define FS_E_NOREC    (-0x7f06)
#define FS_E_MISMATCH (-0x7f07)
#define FS_E_TIMEOUT  (-0x7f08)
#define FS_E_ITEM     (-0x7f09)
#define FS_E_CANCELLED (-0x7f0a)

#define FS_JOB_GUARD_MS 120000u   /* polcore fails a job by itself at 100 s anyway */
#define FS_STRICT_PNUM  1

enum { ST_IDLE, ST_LOAD_START, ST_LOAD_POLL, ST_UPD_START, ST_UPD_POLL };

static struct {
    int        st;
    int        slot;
    unsigned   pic;
    unsigned __int64 hid;
    int        job;
    DWORD      t0;
    pdb_rec   *rec;
} g_fs = { ST_IDLE, -1, 0, 0, -1, 0, NULL };

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

static pdb_schema *fs_load_pib(BYTE *b)
{
    WCHAR path[0x200 + 32];
    HANDLE f;
    DWORD sz, got = 0;
    BYTE *buf;
    pdb_schema *s;
    if (g_fs_pib) return g_fs_pib;
    memset(path, 0, sizeof path);
    if (((svc16c4_t)SVC(0x16c4))(0x14, path, 0x200, 0) <= 0) return NULL;  /* "<root>\data\db\" */
    path[0x200 - 1] = 0;
    lstrcatW(path, L"prof_pol.pib");
    f = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (f == INVALID_HANDLE_VALUE) { FS_LOG("pic: cannot open %ls (%u)\r\n", path, GetLastError()); return NULL; }
    sz = GetFileSize(f, NULL);
    if (sz < 0x3c || sz > 0x10000) { CloseHandle(f); return NULL; }
    buf = (BYTE *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sz);
    if (!buf) { CloseHandle(f); return NULL; }
    if (!ReadFile(f, buf, sz, &got, NULL) || got != sz) { CloseHandle(f); HeapFree(GetProcessHeap(), 0, buf); return NULL; }
    CloseHandle(f);
    /* check the raw file before polcore relocates it, in 64 bits so a huge offset can't wrap past the check */
    s = (pdb_schema *)buf;
    if (memcmp(s->name, "prof_pol", 9) != 0 || s->relocated != 0 || s->nfields == 0 ||
        (DWORD)(ULONG_PTR)s->fields >= sz ||
        (unsigned __int64)(DWORD)(ULONG_PTR)s->fields + (unsigned __int64)s->nfields * 0x18 > sz ||
        (DWORD)(ULONG_PTR)s->p14 >= sz || (DWORD)(ULONG_PTR)s->p2c >= sz) {
        HeapFree(GetProcessHeap(), 0, buf);
        return NULL;
    }
    s = ((svc494_t)SVC(0x494))(buf);
    if (s != (pdb_schema *)buf || !fs_check_schema(b, s)) {
        FS_LOG("pic: prof_pol.pib is not the expected schema\r\n");
        HeapFree(GetProcessHeap(), 0, buf);    /* no job's seen it yet */
        return NULL;
    }
    FS_LOG("pic: %ls loaded, %u fields, record buffer %u bytes\r\n", path, s->nfields, s->rec_size);
    g_fs_pib = s;
    return s;
}

static int fs_handle(BYTE *b, int slot, pol_handle *h)
{
    memset(h, 0, sizeof *h);
    return ((svc2b4_t)FS_GETHANDLE_FN)((unsigned)slot, h) >= 0 && (h->lo & 1);
}

static unsigned __int64 fs_hid(const pol_handle *h)
{
    return (((unsigned __int64)h->hi << 32 | h->lo) >> 1) & 0xfffffffffffULL;
}

/* SetHandle only marks the slot dirty and copies the pic into my group entries, nothing goes out */
static int fs_apply_local(BYTE *b, int slot, unsigned pic, unsigned __int64 hid)
{
    pol_handle h;
    int r;
    if (!fs_handle(b, slot, &h) || fs_hid(&h) != hid) return FS_E_HANDLE;
    h.pic = pic;
    r = ((svc2b8_t)SVC(0x2b8))((unsigned)slot, &h);
    return r < 0 ? r : FS_DONE;
}

static void fs_reset(void)
{
    if (g_fs.rec) HeapFree(GetProcessHeap(), 0, g_fs.rec);
    g_fs.rec = NULL;
    g_fs.st = ST_IDLE;
    g_fs.job = -1;
    g_fs.slot = -1;
}

static int fs_fail(int code, const char *what)
{
    FS_LOG("pic: %s -> %d (0x%x)\r\n", what, code, (unsigned)code);
    fs_reset();
    return code;
}

#define P8(a, i) a[i], a[(i)+1], a[(i)+2], a[(i)+3], a[(i)+4], a[(i)+5], a[(i)+6], a[(i)+7]

/* once per loop turn, same args, until it's non-zero (1 done, < 0 error). no_write lets a running job finish but
   starts no write */
static int profile_pic_save(BYTE *b, int slot, unsigned pic, int no_write)   /* pic = sheet*8 + cell */
{
    pdb_schema *s;
    int r;

    if (g_fs.st != ST_IDLE && (slot != g_fs.slot || pic != g_fs.pic)) return FS_E_BUSY;

    switch (g_fs.st) {
    case ST_IDLE: {
        pol_handle h;
        if (slot < 0 || slot > 63) return FS_E_ARGS;
        if (!(s = fs_load_pib(b))) return FS_E_PIB;
        /* the viewer keeps its slot in bits 13-18 too and uses it as the GetHandle index, so both have to agree */
        if (!fs_handle(b, slot, &h) || (int)((h.hi >> 13) & 0x3f) != slot) return FS_E_HANDLE;
        g_fs.hid = fs_hid(&h);
        g_fs.rec = (pdb_rec *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, s->rec_size + 8);
        if (!g_fs.rec) return FS_E_MEM;
        g_fs.slot = slot;
        g_fs.pic = pic;
        g_fs.st = ST_LOAD_START;
    }   /* fall through */
    case ST_LOAD_START: {
        static pdb_cond c;
        pdb_cond *where;
        s = g_fs_pib;
        memset(&c, 0, sizeof c);
        ((svc4a0_t)SVC(0x4a0))(s, &c.item, "z_hid", &g_fs.hid, 1);
        if (!(c.item.flags & 1)) return fs_fail(FS_E_ITEM, "z_hid condition");
        ((svc4a8_t)SVC(0x4a8))(&c, 1);
        where = ((svc4ac_t)SVC(0x4ac))(1, &c);
        r = ((svc4b4_t)SVC(0x4b4))(s, where, g_fs.rec);
        if (r == -0x100) return FS_PENDING;              /* all 4 job slots busy, try again next turn */
        if (r < 0) return fs_fail(r, "profile load start (svc 0x4b4)");
        g_fs.job = r;
        g_fs.t0 = GetTickCount();
        g_fs.st = ST_LOAD_POLL;
        FS_LOG("pic: loading profile of slot %d (hid 0x%08x%08x), job %d\r\n", slot,
               (unsigned)(g_fs.hid >> 32), (unsigned)g_fs.hid, r);
        return FS_PENDING;
    }
    case ST_LOAD_POLL: {
        pdb_item *it;
        s = g_fs_pib;
        r = ((svc4b8_t)SVC(0x4b8))(g_fs.job);
        if (r == 0) {
            if (GetTickCount() - g_fs.t0 > FS_JOB_GUARD_MS) {
                ((svc4dc_t)SVC(0x4dc))(g_fs.job);
                g_fs.rec = NULL;          /* polcore can still write into a cancelled read, so it's never freed */
                return fs_fail(FS_E_TIMEOUT, "profile load");
            }
            return FS_PENDING;
        }
        g_fs.job = -1;
        if (r < 0) return fs_fail(r, "profile load (svc 0x4b8)");
        if (!g_fs.rec->items) return fs_fail(FS_E_NOREC, "no profile record on the server");
        /* the record has to be this handle's. The viewer doesn't check */
        it = ((svc49c_t)SVC(0x49c))(s, "z_hid", g_fs.rec);
        if (!it || it->v.u64 != g_fs.hid) return fs_fail(FS_E_MISMATCH, "record z_hid differs");
        it = ((svc49c_t)SVC(0x49c))(s, "z_pnum", g_fs.rec);
        if (FS_STRICT_PNUM && it && (it->flags & 1) && it->v.bytes.p &&
            *(BYTE *)it->v.bytes.p != (BYTE)g_fs.slot)
            return fs_fail(FS_E_MISMATCH, "record z_pnum differs from slot");
        if (!((svc49c_t)SVC(0x49c))(s, "z_ficon", g_fs.rec) ||
            !((svc49c_t)SVC(0x49c))(s, "z_utime", g_fs.rec))
            return fs_fail(FS_E_NOREC, "record lacks z_ficon / z_utime");
        FS_LOG("pic: profile loaded, record level %u\r\n", g_fs.rec->level);
        g_fs.st = ST_UPD_START;
    }   /* fall through */
    case ST_UPD_START: {
        pdb_item it[2] = {0}, *rf, *ru;
        pdb_cond k = {0};
        pdb_rec set = {0};
        void *sl[64] = {0};
        pdb_cond *where;
        DWORD fval = g_fs.pic, now = 0;
        BYTE pnum = (BYTE)g_fs.slot;
        s = g_fs_pib;
        if (no_write) return fs_fail(FS_E_CANCELLED, "shutting down before the update");
        rf = ((svc49c_t)SVC(0x49c))(s, "z_ficon", g_fs.rec);
        ru = ((svc49c_t)SVC(0x49c))(s, "z_utime", g_fs.rec);
        if (!rf || !ru) return fs_fail(FS_E_NOREC, "record lacks z_ficon / z_utime");
        ((svc4a0_t)SVC(0x4a0))(s, &it[0], "z_ficon", &fval, rf->level);
        ((svcac4_t)SVC(0xac4))(&now);          /* server-synced seconds */
        ((svc4a0_t)SVC(0x4a0))(s, &it[1], "z_utime", &now, ru->level);
        /* svc 0x4d4 writes every SET item but only sizes the flagged ones, so an unflagged one corrupts the body */
        if (!(it[0].flags & 1) || !(it[1].flags & 1)) return fs_fail(FS_E_ITEM, "SET items");
        ((svc4a0_t)SVC(0x4a0))(s, &k.item, "z_pnum", &pnum, 1);
        if (!(k.item.flags & 1)) return fs_fail(FS_E_ITEM, "z_pnum condition");
        ((svc4a8_t)SVC(0x4a8))(&k, 1);
        where = ((svc4ac_t)SVC(0x4ac))(1, &k);
        /* 64 item slots, NULLs skipped, same as the viewer */
        sl[0] = &it[0];
        sl[1] = &it[1];
        ((svc4d0_t)SVC(0x4d0))(&set, g_fs.rec->level, 0x40,
                               P8(sl, 0), P8(sl, 8), P8(sl, 16), P8(sl, 24),
                               P8(sl, 32), P8(sl, 40), P8(sl, 48), P8(sl, 56));
        /* the body's built into polcore's job buffer right here, so the items, cond and set can go out of scope */
        r = ((svc4d4_t)SVC(0x4d4))(s, where, &set);
        if (r == -0x100) return FS_PENDING;
        if (r < 0) return fs_fail(r, "profile update start (svc 0x4d4)");
        g_fs.job = r;
        g_fs.t0 = GetTickCount();
        g_fs.st = ST_UPD_POLL;
        FS_LOG("pic: update z_ficon=%u z_utime=%u level %u/%u/%u where z_pnum=%u, job %d\r\n",
               fval, now, g_fs.rec->level, rf->level, ru->level, pnum, r);
        return FS_PENDING;
    }
    case ST_UPD_POLL: {
        r = ((svc4d8_t)SVC(0x4d8))(g_fs.job);
        if (r == 0) {
            /* the viewer shits itself after 18000 polls and calls the save done anyway regardless (luv me playonline
               innit), for us, it's an error */
            if (GetTickCount() - g_fs.t0 > FS_JOB_GUARD_MS) {
                ((svc4dc_t)SVC(0x4dc))(g_fs.job);
                return fs_fail(FS_E_TIMEOUT, "profile update");
            }
            return FS_PENDING;
        }
        g_fs.job = -1;
        if (r < 0) return fs_fail(r, "profile update (svc 0x4d8)");
        r = fs_apply_local(b, g_fs.slot, g_fs.pic, g_fs.hid);
        if (r < 0) FS_LOG("pic: server updated, local handle apply failed %d\r\n", r);
        else FS_LOG("pic: slot %d pic %u saved\r\n", g_fs.slot, g_fs.pic);
        fs_reset();
        return FS_DONE;
    }
    }
    return fs_fail(FS_E_ARGS, "bad state");
}

/* svc 0x4dc frees the job slot but also makes every polcore job poll return 0 for the next 10s (WHY THO?????),
   so it's only for shutdown or a real timeout */
static void profile_pic_cancel(BYTE *b)
{
    if (g_fs.job >= 0) {
        ((svc4dc_t)SVC(0x4dc))(g_fs.job);
        if (g_fs.st == ST_LOAD_POLL) g_fs.rec = NULL;   /* same as the timeout, polcore can still write into it */
    }
    fs_reset();
}
