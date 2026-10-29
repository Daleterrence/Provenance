/* my own profile save and the handle rename, the viewer's way. The save is pol_pic_save.c's UPDATE with N items
   instead of 2 */
#define PE_MAXF 32

typedef struct pe_field {
    const char *name;
    DWORD       v;                  /* z_area is the 32-bit location code */
    const char *str;                /* z_mail, SJIS */
} pe_field;

typedef struct pe_req {
    int      level;                 /* 0 keeps the record's, 1 private, 2 friends, 3 everyone */
    int      utime;                 /* 1 = the profile page (adds z_utime), 0 = the away page */
    int      n;
    pe_field f[PE_MAXF];
} pe_req;

/* the viewer fills its SET slots in prof_pol.pfb item order, this is that order for every editable item */
static const char *const pe_order[] = {
    "z_purp", "z_ficon", "z_mail", "z_age", "z_age_f", "z_sex", "z_job", "z_area",
    "z_lang0", "z_lang1", "z_lang2", "z_fav0", "z_fav1", "z_fav2",
    "z_aa_mt", "z_aa_dt", "z_aa_t", "z_aa_m", "z_aa_c", "z_aa_w", "z_aa_d", "z_aa_e",
};
#define PE_NORDER ((int)(sizeof pe_order / sizeof pe_order[0]))

static const pe_field *pe_find(const pe_req *q, const char *name)
{
    int i;
    for (i = 0; i < q->n; i++) if (strcmp(q->f[i].name, name) == 0) return &q->f[i];
    return NULL;
}

#define PE_E_FIELD  (-0x7f20)
#define PE_E_VALUE  (-0x7f21)
#define PE_E_COUNT  (-0x7f22)

/* starts the UPDATE on a record loaded the same way as the profile pic save's. The job (poll it with svc 0x4d8),
   -0x100 when there's no free job slot, or < 0 */
static int pe_update_start(BYTE *b, pdb_schema *s, pdb_rec *rec, int slot, const pe_req *q)
{
    pdb_item it[64] = {0};
    void    *sl[64] = {0};
    BYTE     b8[64];
    DWORD    d32[64];               /* svc 0x4a0 reads a DWORD, even for a u16 */
    pdb_cond k = {0};
    pdb_rec  set = {0};
    pdb_cond *where;
    DWORD    now = 0;
    BYTE     pnum = (BYTE)slot;
    int      i, n = 0, idx, lvl;

    for (i = 0; i < q->n; i++) {
        int j, ok = 0;
        for (j = 0; j < PE_NORDER; j++) if (!strcmp(pe_order[j], q->f[i].name)) ok = 1;
        if (!ok) return PE_E_FIELD;
    }
    for (i = 0; i < PE_NORDER; i++) {
        const char *nm = pe_order[i];
        const pe_field *f = pe_find(q, nm);
        const pdb_field *sf;
        pdb_item *ri;
        /* only what was edited. The viewer can flag z_lang1/2 with z_lang0, but never does (app.dll 0x10227221) */
        if (!f) continue;
        sf = fs_field(s, nm, &idx);
        ri = ((svc49c_t)SVC(0x49c))(s, nm, rec);
        if (!sf || !ri || !(sf->flags & 0x42)) return PE_E_FIELD;
        lvl = ri->level;
        if (!strcmp(nm, "z_area")) {
            /* big-endian, z_area first */
            static const char *const loc[4] = { "z_area", "z_cntry", "z_wide", "z_local" };
            DWORD v = f->v;
            int j;
            if (n + 4 > 64) return PE_E_COUNT;
            for (j = 0; j < 4; j++) {
                const pdb_field *lf = fs_field(s, loc[j], NULL);
                if (!lf || lf->type != 2 || lf->size != 1) return PE_E_FIELD;
                b8[n] = (BYTE)(v >> (24 - 8 * j));
                ((svc4a0_t)SVC(0x4a0))(s, &it[n], loc[j], &b8[n], lvl);
                sl[n] = &it[n]; n++;
            }
            continue;
        }
        if (n >= 64) return PE_E_COUNT;
        switch (sf->type) {
        case 1:
            if (!f->str || (DWORD)lstrlenA(f->str) > (DWORD)sf->size - 1) return PE_E_VALUE;
            ((svc4a0_t)SVC(0x4a0))(s, &it[n], nm, f->str, lvl);
            break;
        case 2:
            if (sf->size != 1) return PE_E_FIELD;
            if (f->v > 0xff) return PE_E_VALUE;
            b8[n] = (BYTE)f->v;
            ((svc4a0_t)SVC(0x4a0))(s, &it[n], nm, &b8[n], lvl);
            break;
        case 4:
            if (f->v > 0xffff) return PE_E_VALUE;
            d32[n] = f->v;
            ((svc4a0_t)SVC(0x4a0))(s, &it[n], nm, &d32[n], lvl);
            break;
        case 5: case 6:
            d32[n] = f->v;
            ((svc4a0_t)SVC(0x4a0))(s, &it[n], nm, &d32[n], lvl);
            break;
        default:
            return PE_E_FIELD;
        }
        sl[n] = &it[n]; n++;
    }
    if (q->utime) {
        pdb_item *ru = ((svc49c_t)SVC(0x49c))(s, "z_utime", rec);
        if (!ru || n >= 64) return PE_E_COUNT;
        ((svcac4_t)SVC(0xac4))(&now);
        ((svc4a0_t)SVC(0x4a0))(s, &it[n], "z_utime", &now, ru->level);
        sl[n] = &it[n]; n++;
    }
    if (n == 0) return PE_E_COUNT;
    for (i = 0; i < n; i++) if (!(it[i].flags & 1)) return FS_E_ITEM;
    ((svc4a0_t)SVC(0x4a0))(s, &k.item, "z_pnum", &pnum, 1);
    if (!(k.item.flags & 1)) return FS_E_ITEM;
    ((svc4a8_t)SVC(0x4a8))(&k, 1);
    where = ((svc4ac_t)SVC(0x4ac))(1, &k);
    ((svc4d0_t)SVC(0x4d0))(&set, (q->level >= 1 && q->level <= 3) ? q->level : rec->level, 0x40,
                           P8(sl, 0), P8(sl, 8), P8(sl, 16), P8(sl, 24),
                           P8(sl, 32), P8(sl, 40), P8(sl, 48), P8(sl, 56));
    /* the body's built right here, so the items can live on this stack. -0x1401 if it comes to 0x800 or more */
    return ((svc4d4_t)SVC(0x4d4))(s, where, &set);
}

static int pe_mail_ok(BYTE *b, const char *sjis)
{
    return ((int (__cdecl *)(const char *, int))SVC(0x6e8))(sjis, lstrlenA(sjis)) == 0;
}

/* the handle rename: SetHandle, then the handle-list upload (KPutHandleList), like the viewer's HnEdit */
typedef int (__cdecl *svca40_t)(char *dst, const WCHAR *src, int repl);   /* a NULL dst measures */

#define HR_E_NAME    (-0x7f30)
#define HR_E_NGWORD  (-0x7f31)
#define HR_E_DIC     (-0x7f32)
#define HR_E_REVERT  (-0x7f33)

/* CreateHandle's rule (polcore.dll 0x100259b0): no byte >= 0x80 and not all blanks. Control characters are out
   on top */
static int hr_name_rule(const char *s)
{
    int any = 0;
    for (; *s; s++) {
        if ((BYTE)*s >= 0x80 || (BYTE)*s < 0x20) return 0;
        if ((BYTE)*s > 0x20) any = 1;
    }
    return any;
}

typedef int   (__cdecl *svc16c8_t)(int id, WCHAR *out, int cap, int flags);
typedef void *(__cdecl *svc1154_t)(void *dic, int size);
typedef int   (__cdecl *svc1674_t)(void *dic, const WCHAR *name, int len);

static void *g_hr_dic;
static int hr_ngword(BYTE *b, const WCHAR *name)   /* 1 = NG, 0 = ok, < 0 = no dictionary */
{
    if (!g_hr_dic) {
        WCHAR p[0x200];
        HANDLE h;
        DWORD sz, got = 0;
        BYTE *buf;
        memset(p, 0, sizeof p);
        if (((svc16c8_t)SVC(0x16c8))(0xb, p, 0x1ff, 0) <= 0) return HR_E_DIC;   /* ..\data\dic\entryz.dic */
        h = CreateFileW(p, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
        if (h == INVALID_HANDLE_VALUE) return HR_E_DIC;
        sz = GetFileSize(h, NULL);
        buf = (sz > 0x20 && sz < 0x1000000) ? (BYTE *)HeapAlloc(GetProcessHeap(), 0, sz) : NULL;
        if (!buf || !ReadFile(h, buf, sz, &got, NULL) || got != sz) {
            CloseHandle(h);
            if (buf) HeapFree(GetProcessHeap(), 0, buf);
            return HR_E_DIC;
        }
        CloseHandle(h);
        /* relocated in place, so buf is the dictionary from here on and never gets freed. NULL if the size is off */
        g_hr_dic = ((svc1154_t)SVC(0x1154))(buf, (int)sz);
        if (!g_hr_dic) { HeapFree(GetProcessHeap(), 0, buf); return HR_E_DIC; }
    }
    return ((svc1674_t)SVC(0x1674))(g_hr_dic, name, lstrlenW(name)) != 0;
}

enum { HR_IDLE, HR_UP_START, HR_UP_POLL, HR_RL_START, HR_RL_POLL };
static struct { int st, slot, job, err; char want[16], old[16]; } g_hr = { HR_IDLE, -1, -1, 0, {0}, {0} };

/* the server kept the old name, so put it back here too, or the next handle-list upload (the slot stays dirty) sends
   the rename anyway */
static void hr_restore(BYTE *b)
{
    pol_handle h;
    if (!fs_handle(b, g_hr.slot, &h)) return;
    memcpy(h.name, g_hr.old, 16);
    ((svc2b8_t)SVC(0x2b8))((unsigned)g_hr.slot, &h);
}

/* call once per pump turn with the same arguments until it's non-zero: 1 done, < 0 error */
static int handle_rename(BYTE *b, int slot, const WCHAR *newName)
{
    pol_handle h;
    int r;
    switch (g_hr.st) {
    case HR_IDLE: {
        char sj[64];
        int  len;
        if (slot < 0 || slot > 63 || !newName) return FS_E_ARGS;
        len = ((svca40_t)SVC(0xa40))(NULL, newName, 0x81ac);
        if (len < 1 || len > 15) return HR_E_NAME;
        memset(sj, 0, sizeof sj);
        ((svca40_t)SVC(0xa40))(sj, newName, 0x81ac);
        if (!hr_name_rule(sj)) return HR_E_NAME;
        r = hr_ngword(b, newName);
        if (r < 0) return r;
        if (r) return HR_E_NGWORD;
        if (!fs_handle(b, slot, &h) || (int)((h.hi >> 13) & 0x3f) != slot) return FS_E_HANDLE;
        memcpy(g_hr.old, h.name, 16);
        memset(h.name, 0, sizeof h.name);
        memcpy(h.name, sj, (size_t)len);
        r = ((svc2b8_t)SVC(0x2b8))((unsigned)slot, &h);
        if (r < 0) return r;
        memcpy(g_hr.want, h.name, 16);
        g_hr.slot = slot; g_hr.st = HR_UP_START;
    }   /* fall through */
    case HR_UP_START:
        r = ((svc_v)SVC(0x1030))();                                       /* KPutHandleList */
        if (r == -0x100) return FS_PENDING;
        if (r < 0) { g_hr.st = HR_IDLE; hr_restore(b); return r; }
        g_hr.job = r; g_hr.st = HR_UP_POLL;
        return FS_PENDING;
    case HR_UP_POLL:
        r = ((svc_i)SVC(0x1034))(g_hr.job);
        if (r == 0) return FS_PENDING;
        g_hr.job = -1;
        /* -0x105: reload the handle list like the viewer does, then report it */
        if (r == -0x105) { g_hr.err = r; g_hr.st = HR_RL_START; return FS_PENDING; }
        g_hr.st = HR_IDLE;
        if (r < 0) { hr_restore(b); return r; }
        fs_handle(b, g_hr.slot, &h);
        h.name[15] = 0;                 /* bytes after the NUL can be left over from the old name */
        return strcmp(h.name, g_hr.want) == 0 ? FS_DONE : HR_E_REVERT;
    case HR_RL_START:
        r = ((svc_v)SVC(0x2ac))();                                        /* KGetHandleList */
        if (r == -0x100) return FS_PENDING;
        if (r < 0) { g_hr.st = HR_IDLE; return g_hr.err; }
        g_hr.job = r; g_hr.st = HR_RL_POLL;
        return FS_PENDING;
    case HR_RL_POLL:
        r = ((svc_i)SVC(0x2b0))(g_hr.job);
        if (r == 0) return FS_PENDING;
        g_hr.job = -1; g_hr.st = HR_IDLE;
        return g_hr.err;
    }
    g_hr.st = HR_IDLE;
    return FS_E_ARGS;
}
