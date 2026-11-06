/* the viewer's profile pic save: load my record, UPDATE z_ficon + z_utime, then SetHandle
   locally */

/* polcore's own negative codes pass straight through, mine sit in a range it doesn't use */
#define FS_PENDING        0
#define FS_DONE           1
#define FS_E_ARGS     (-0x7f01)
#define FS_E_BUSY     (-0x7f02)
#define FS_E_HANDLE   (-0x7f03)
#define FS_E_PIB      (-0x7f04)
#define FS_E_MEM      (-0x7f05)
#define FS_E_NOREC    (-0x7f06)
#define FS_E_MISMATCH (-0x7f07)
#define FS_E_ITEM     (-0x7f09)
#define FS_E_CANCELLED (-0x7f0a)

#define FS_STRICT_PNUM  1

enum { ST_IDLE, ST_LOAD_START, ST_LOAD_POLL, ST_UPD_START, ST_UPD_POLL };

static struct {
    int        st;
    int        slot;
    unsigned   pic;
    unsigned __int64 hid;
    int        job;
    pdb_rec   *rec;
} g_fs = { ST_IDLE, -1, 0, 0, -1, NULL };



/* SetHandle only marks the slot dirty and copies the pic into my group entries, nothing goes out */
static int my_profile_pic_apply_local(BYTE *b, int slot, unsigned pic, unsigned __int64 hid)
{
    pol_handle h;
    int r;
    if (!handle_get(b, slot, &h) || handle_hid(&h) != hid) return FS_E_HANDLE;
    h.pic = pic;
    r = pc_set_handle(b, (unsigned)slot, &h);
    return r < 0 ? r : FS_DONE;
}

static void my_profile_pic_reset(void)
{
    if (g_fs.rec) HeapFree(GetProcessHeap(), 0, g_fs.rec);
    g_fs.rec = NULL;
    g_fs.st = ST_IDLE;
    g_fs.job = -1;
    g_fs.slot = -1;
}

static int my_profile_pic_fail(int code, const char *what)
{
    DB_LOG("pic: %s -> %d (0x%x)\r\n", what, code, (unsigned)code);
    my_profile_pic_reset();
    return code;
}

#define P8(a, i) a[i], a[(i)+1], a[(i)+2], a[(i)+3], a[(i)+4], a[(i)+5], a[(i)+6], a[(i)+7]

/* once per loop turn, same args, until it's non-zero (1 done, < 0 error). no_write lets a running job finish but
   starts no write */
static int my_profile_pic_save(BYTE *b, int slot, unsigned pic, int no_write)   /* pic = sheet*8 + cell */
{
    pdb_schema *s;
    int r;

    if (g_fs.st != ST_IDLE && (slot != g_fs.slot || pic != g_fs.pic)) return FS_E_BUSY;

    switch (g_fs.st) {
    case ST_IDLE: {
        pol_handle h;
        if (slot < 0 || slot > 63) return FS_E_ARGS;
        if (!(s = profile_db_load_pib(b))) return FS_E_PIB;
        /* the viewer keeps its slot in bits 13-18 too and uses it as the GetHandle index, so both have to agree */
        if (!handle_get(b, slot, &h) || (int)((h.hi >> 13) & 0x3f) != slot) return FS_E_HANDLE;
        g_fs.hid = handle_hid(&h);
        g_fs.rec = (pdb_rec *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, s->rec_size + 8);
        if (!g_fs.rec) return FS_E_MEM;
        g_fs.slot = slot;
        g_fs.pic = pic;
        g_fs.st = ST_LOAD_START;
    }   /* fall through */
    case ST_LOAD_START: {
        static pdb_cond c;
        pdb_cond *where;
        s = g_profile_db_pib;
        memset(&c, 0, sizeof c);
        pc_pdb_item_set(b, s, &c.item, "z_hid", &g_fs.hid, 1);
        if (!(c.item.flags & 1)) return my_profile_pic_fail(FS_E_ITEM, "z_hid condition");
        pc_pdb_cond_op(b, &c, 1);
        where = pc_pdb_where(b, 1, &c);
        r = pc_pdb_load_start(b, s, where, g_fs.rec);
        if (r == NO_SLOT) return FS_PENDING;              /* all 4 job slots busy, try again next turn */
        if (r < 0) return my_profile_pic_fail(r, "profile load start (svc 0x4b4)");
        g_fs.job = r;
        g_fs.st = ST_LOAD_POLL;
        DB_LOG("pic: loading profile of slot %d (hid 0x%08x%08x), job %d\r\n", slot,
               (unsigned)(g_fs.hid >> 32), (unsigned)g_fs.hid, r);
        return FS_PENDING;
    }
    case ST_LOAD_POLL: {
        pdb_item *it;
        s = g_profile_db_pib;
        r = pc_pdb_load_check(b, g_fs.job);
        if (r == 0) return FS_PENDING;
        g_fs.job = -1;
        if (r < 0) return my_profile_pic_fail(r, "profile load (svc 0x4b8)");
        if (!g_fs.rec->items) return my_profile_pic_fail(FS_E_NOREC, "no profile record on the server");
        /* the record has to be this handle's. The viewer doesn't check */
        it = pc_pdb_find(b, s, "z_hid", g_fs.rec);
        if (!it || it->v.u64 != g_fs.hid) return my_profile_pic_fail(FS_E_MISMATCH, "record z_hid differs");
        it = pc_pdb_find(b, s, "z_pnum", g_fs.rec);
        if (FS_STRICT_PNUM && it && (it->flags & 1) && it->v.bytes.p &&
            *(BYTE *)it->v.bytes.p != (BYTE)g_fs.slot)
            return my_profile_pic_fail(FS_E_MISMATCH, "record z_pnum differs from slot");
        if (!pc_pdb_find(b, s, "z_ficon", g_fs.rec) ||
            !pc_pdb_find(b, s, "z_utime", g_fs.rec))
            return my_profile_pic_fail(FS_E_NOREC, "record lacks z_ficon / z_utime");
        DB_LOG("pic: profile loaded, record level %u\r\n", g_fs.rec->level);
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
        s = g_profile_db_pib;
        if (no_write) return my_profile_pic_fail(FS_E_CANCELLED, "shutting down before the update");
        rf = pc_pdb_find(b, s, "z_ficon", g_fs.rec);
        ru = pc_pdb_find(b, s, "z_utime", g_fs.rec);
        if (!rf || !ru) return my_profile_pic_fail(FS_E_NOREC, "record lacks z_ficon / z_utime");
        pc_pdb_item_set(b, s, &it[0], "z_ficon", &fval, rf->level);
        pc_server_time(b, &now);          /* server-synced seconds */
        pc_pdb_item_set(b, s, &it[1], "z_utime", &now, ru->level);
        /* svc 0x4d4 writes every SET item but only sizes the flagged ones, so an unflagged one corrupts the body */
        if (!(it[0].flags & 1) || !(it[1].flags & 1)) return my_profile_pic_fail(FS_E_ITEM, "SET items");
        pc_pdb_item_set(b, s, &k.item, "z_pnum", &pnum, 1);
        if (!(k.item.flags & 1)) return my_profile_pic_fail(FS_E_ITEM, "z_pnum condition");
        pc_pdb_cond_op(b, &k, 1);
        where = pc_pdb_where(b, 1, &k);
        /* 64 item slots, NULLs skipped, same as the viewer */
        sl[0] = &it[0];
        sl[1] = &it[1];
        pc_pdb_rec_set(b, &set, g_fs.rec->level, 0x40,
                               P8(sl, 0), P8(sl, 8), P8(sl, 16), P8(sl, 24),
                               P8(sl, 32), P8(sl, 40), P8(sl, 48), P8(sl, 56));
        /* the body's built into polcore's job buffer right here, so the items, cond and set can go out of scope */
        r = pc_pdb_update_start(b, s, where, &set);
        if (r == NO_SLOT) return FS_PENDING;
        if (r < 0) return my_profile_pic_fail(r, "profile update start (svc 0x4d4)");
        g_fs.job = r;
        g_fs.st = ST_UPD_POLL;
        DB_LOG("pic: update z_ficon=%u z_utime=%u level %u/%u/%u where z_pnum=%u, job %d\r\n",
               fval, now, g_fs.rec->level, rf->level, ru->level, pnum, r);
        return FS_PENDING;
    }
    case ST_UPD_POLL: {
        r = pc_pdb_update_check(b, g_fs.job);
        if (r == 0) return FS_PENDING;
        g_fs.job = -1;
        /* the viewer shits itself after 18000 polls and calls the save done anyway regardless (luv me POL innit), for
           me polcore's own fail at 100 s is an error */
        if (r < 0) return my_profile_pic_fail(r, "profile update (svc 0x4d8)");
        r = my_profile_pic_apply_local(b, g_fs.slot, g_fs.pic, g_fs.hid);
        if (r < 0) DB_LOG("pic: server updated, local handle apply failed %d\r\n", r);
        else DB_LOG("pic: slot %d pic %u saved\r\n", g_fs.slot, g_fs.pic);
        my_profile_pic_reset();
        return FS_DONE;
    }
    }
    return my_profile_pic_fail(FS_E_ARGS, "bad state");
}

/* svc 0x4dc frees the job slot but also makes every polcore job poll return 0 for the next 10s (why tho?????),
   so it's only for shutdown */
static void my_profile_pic_cancel(BYTE *b)
{
    if (g_fs.job >= 0) {
        pc_free_job(b, g_fs.job);
        if (g_fs.st == ST_LOAD_POLL) g_fs.rec = NULL;   /* a cancelled read can still write into it, so never freed */
    }
    my_profile_pic_reset();
}

/* my own profile save and the handle rename, the viewer's way. The save is the pic save's UPDATE with N items
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

static const pe_field *my_profile_edit_find(const pe_req *q, const char *name)
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
static int my_profile_edit_update_start(BYTE *b, pdb_schema *s, pdb_rec *rec, int slot, const pe_req *q)
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
        const pe_field *f = my_profile_edit_find(q, nm);
        const pdb_field *sf;
        pdb_item *ri;
        /* only what was edited. The viewer can flag z_lang1/2 with z_lang0, but never does (app.dll 0x10227221) */
        if (!f) continue;
        sf = profile_db_field(s, nm, &idx);
        ri = pc_pdb_find(b, s, nm, rec);
        if (!sf || !ri || !(sf->flags & 0x42)) return PE_E_FIELD;
        lvl = ri->level;
        if (!strcmp(nm, "z_area")) {
            /* big-endian, z_area first */
            static const char *const loc[4] = { "z_area", "z_cntry", "z_wide", "z_local" };
            DWORD v = f->v;
            int j;
            if (n + 4 > 64) return PE_E_COUNT;
            for (j = 0; j < 4; j++) {
                const pdb_field *lf = profile_db_field(s, loc[j], NULL);
                if (!lf || lf->type != 2 || lf->size != 1) return PE_E_FIELD;
                b8[n] = (BYTE)(v >> (24 - 8 * j));
                pc_pdb_item_set(b, s, &it[n], loc[j], &b8[n], lvl);
                sl[n] = &it[n]; n++;
            }
            continue;
        }
        if (n >= 64) return PE_E_COUNT;
        switch (sf->type) {
        case 1:
            if (!f->str || (DWORD)lstrlenA(f->str) > (DWORD)sf->size - 1) return PE_E_VALUE;
            pc_pdb_item_set(b, s, &it[n], nm, f->str, lvl);
            break;
        case 2:
            if (sf->size != 1) return PE_E_FIELD;
            if (f->v > 0xff) return PE_E_VALUE;
            b8[n] = (BYTE)f->v;
            pc_pdb_item_set(b, s, &it[n], nm, &b8[n], lvl);
            break;
        case 4:
            if (f->v > 0xffff) return PE_E_VALUE;
            d32[n] = f->v;
            pc_pdb_item_set(b, s, &it[n], nm, &d32[n], lvl);
            break;
        case 5: case 6:
            d32[n] = f->v;
            pc_pdb_item_set(b, s, &it[n], nm, &d32[n], lvl);
            break;
        default:
            return PE_E_FIELD;
        }
        sl[n] = &it[n]; n++;
    }
    if (q->utime) {
        pdb_item *ru = pc_pdb_find(b, s, "z_utime", rec);
        if (!ru || n >= 64) return PE_E_COUNT;
        pc_server_time(b, &now);
        pc_pdb_item_set(b, s, &it[n], "z_utime", &now, ru->level);
        sl[n] = &it[n]; n++;
    }
    if (n == 0) return PE_E_COUNT;
    for (i = 0; i < n; i++) if (!(it[i].flags & 1)) return FS_E_ITEM;
    pc_pdb_item_set(b, s, &k.item, "z_pnum", &pnum, 1);
    if (!(k.item.flags & 1)) return FS_E_ITEM;
    pc_pdb_cond_op(b, &k, 1);
    where = pc_pdb_where(b, 1, &k);
    pc_pdb_rec_set(b, &set, (q->level >= 1 && q->level <= 3) ? q->level : rec->level, 0x40,
                           P8(sl, 0), P8(sl, 8), P8(sl, 16), P8(sl, 24),
                           P8(sl, 32), P8(sl, 40), P8(sl, 48), P8(sl, 56));
    /* the body's built right here, so the items can live on this stack. -0x1401 if it comes to 0x800 or more */
    return pc_pdb_update_start(b, s, where, &set);
}

static int my_profile_edit_mail_ok(BYTE *b, const char *sjis)
{
    return pc_mail_address_ok(b, sjis, lstrlenA(sjis)) == 0;
}

/* the handle rename: SetHandle, then the handle-list upload (KPutHandleList), like the viewer's handle editor */
#define HR_E_NAME    (-0x7f30)
#define HR_E_NGWORD  (-0x7f31)
#define HR_E_DIC     (-0x7f32)
#define HR_E_REVERT  (-0x7f33)

static void *g_hr_dic;
static int my_profile_handle_ngword(BYTE *b, const WCHAR *name)   /* 1 = NG, 0 = ok, < 0 = no dictionary */
{
    if (!g_hr_dic) {
        WCHAR p[0x200];
        HANDLE h;
        DWORD sz, got = 0;
        BYTE *buf;
        memset(p, 0, sizeof p);
        if (pc_data_file(b, 0xb, p, 0x1ff, 0) <= 0) return HR_E_DIC;   /* ..\data\dic\entryz.dic */
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
        g_hr_dic = pc_dic_relocate(b, buf, (int)sz);
        if (!g_hr_dic) { HeapFree(GetProcessHeap(), 0, buf); return HR_E_DIC; }
    }
    return pc_dic_has_word(b, g_hr_dic, name, lstrlenW(name)) != 0;
}

enum { HR_IDLE, HR_UP_START, HR_UP_POLL, HR_RL_START, HR_RL_POLL };
static struct { int st, slot, job, err; char want[16], old[16]; } g_hr = { HR_IDLE, -1, -1, 0, {0}, {0} };

/* the server kept the old name, so put it back here too, or the next handle-list upload (the slot stays dirty) sends
   the rename anyway */
static void my_profile_handle_restore(BYTE *b)
{
    pol_handle h;
    if (!handle_get(b, g_hr.slot, &h)) return;
    memcpy(h.name, g_hr.old, 16);
    pc_set_handle(b, (unsigned)g_hr.slot, &h);
}

/* call once per pump turn with the same arguments until it's non-zero: 1 done, < 0 error */
static int my_profile_handle_rename(BYTE *b, int slot, const WCHAR *newName)
{
    pol_handle h;
    int r;
    switch (g_hr.st) {
    case HR_IDLE: {
        char sj[64];
        int  len;
        if (slot < 0 || slot > 63 || !newName) return FS_E_ARGS;
        len = pc_wide_to_sjis(b, NULL, newName, SJIS_GETA);
        if (len < 1 || len > 15) return HR_E_NAME;
        memset(sj, 0, sizeof sj);
        pc_wide_to_sjis(b, sj, newName, SJIS_GETA);
        if (!peers_ascii_name_ok(sj)) return HR_E_NAME;
        r = my_profile_handle_ngword(b, newName);
        if (r < 0) return r;
        if (r) return HR_E_NGWORD;
        if (!handle_get(b, slot, &h) || (int)((h.hi >> 13) & 0x3f) != slot) return FS_E_HANDLE;
        memcpy(g_hr.old, h.name, 16);
        memset(h.name, 0, sizeof h.name);
        memcpy(h.name, sj, (size_t)len);
        r = pc_set_handle(b, (unsigned)slot, &h);
        if (r < 0) return r;
        memcpy(g_hr.want, h.name, 16);
        g_hr.slot = slot; g_hr.st = HR_UP_START;
    }   /* fall through */
    case HR_UP_START:
        r = pc_handles_save(b);                                       /* KPutHandleList */
        if (r == NO_SLOT) return FS_PENDING;
        if (r < 0) { g_hr.st = HR_IDLE; my_profile_handle_restore(b); return r; }
        g_hr.job = r; g_hr.st = HR_UP_POLL;
        return FS_PENDING;
    case HR_UP_POLL:
        r = pc_handles_save_check(b, g_hr.job);
        if (r == 0) return FS_PENDING;
        g_hr.job = -1;
        /* -0x105: reload the handle list like the viewer does, then report it */
        if (r == HANDLE_LIST_STALE) { g_hr.err = r; g_hr.st = HR_RL_START; return FS_PENDING; }
        g_hr.st = HR_IDLE;
        if (r < 0) { my_profile_handle_restore(b); return r; }
        handle_get(b, g_hr.slot, &h);
        h.name[15] = 0;                 /* bytes after the NUL can be left over from the old name */
        return strcmp(h.name, g_hr.want) == 0 ? FS_DONE : HR_E_REVERT;
    case HR_RL_START:
        r = pc_handles_load(b);                                        /* KGetHandleList */
        if (r == NO_SLOT) return FS_PENDING;
        if (r < 0) { g_hr.st = HR_IDLE; return g_hr.err; }
        g_hr.job = r; g_hr.st = HR_RL_POLL;
        return FS_PENDING;
    case HR_RL_POLL:
        r = pc_handles_check(b, g_hr.job);
        if (r == 0) return FS_PENDING;
        g_hr.job = -1; g_hr.st = HR_IDLE;
        return g_hr.err;
    }
    g_hr.st = HR_IDLE;
    return FS_E_ARGS;
}

/* saveprofile levels: 1 private, 2 friends, 3 everyone, 0 keep */
static int g_me_job = -1;
static pdb_rec *g_me_rec;
static pdb_cond g_me_cond;
static pe_req g_me_req;
static char g_me_mail[0x150];
static WCHAR g_me_name[32];
static ULONGLONG g_me_t0;
static unsigned __int64 g_me_hid;

static void my_profile_done(const char *state)
{
    lstrcpynA(g_s.me_state, state, sizeof g_s.me_state);
    g_s.me_step = 0;
    g_me_job = -1;
    base_log("profile edit: %s\r\n", state);
    g_snap_dirty = 1;
}
static const char *const g_me_fields[] = { "z_mail", "z_age", "z_age_f", "z_sex", "z_job", "z_area",
                                           "z_lang0", "z_lang1", "z_lang2", "z_fav0", "z_fav1", "z_fav2", "z_ficon" };
#define ME_EDITABLE ((int)(sizeof g_me_fields / sizeof g_me_fields[0]) - 1)
static int my_profile_table(BYTE *b, char *out, int cap, const pfb_item *pi, int parts)
{
    static char part[0x80], js[0x400];
    int o = 0;
    if (!pi || !pi->tab) { lstrcpynA(out, "[]", cap); return 2; }
    out[o++] = '[';
    for (int i = 0; i < pi->tab->n && o < cap - 0x1000; i++) {
        o += wsprintfA(out + o, "%s[%lu", i ? "," : "", pi->tab->e[i].code);
        for (int k = 0; k < (parts > 0 ? parts : 1); k++) {
            if (parts > 0) profile_db_pfb_part(pi->tab, i, k, part, sizeof part);
            else profile_db_pfb_last(pi->tab->e[i].text, part, sizeof part);
            json_pol(b, part, 0x180, js, sizeof js);
            o += wsprintfA(out + o, ",%s", js);
        }
        out[o++] = ']';
    }
    out[o++] = ']';
    out[o] = 0;
    return o;
}
/* the e-mail address from my POL profile goes in masked (first letter, ***, then the domain). No @ means it's
   the viewer's "No Mail address" text, left alone */
static void my_profile_mask_mail(const char *in, char *out, int cap)
{
    const char *at = strchr(in, '@');
    int lead;
    if (!at) { lstrcpynA(out, in, cap); return; }
    lead = ((BYTE)in[0] >= 0x81 && (BYTE)in[0] <= 0x9f) || (BYTE)in[0] >= 0xe0 ? 2 : 1;   /* SJIS lead byte */
    if (lead > at - in) lead = (int)(at - in);
    lstrcpynA(out, in, lead + 1);
    lstrcatA(out, "***");
    lstrcatA(out, at);
}

/* my FFXI characters, the viewer's edit page tabs (app.dll 0x10224e5c): my record's z_phead slots when it's this
   handle's, else the Content IDs linked to the handle. Only title 1 gets one here */
typedef struct me_chr { int slot; DWORD ctsid, lo, hi; } me_chr;
static int my_profile_chars(BYTE *b, me_chr *out, const char **from)
{
    pdb_item *ph = g_me_rec && g_me_rec->items && g_profile_db_pib
        ? profile_db_get(b, g_profile_db_pib, "z_phead", g_me_rec)
        : NULL;
    const pol_friend *h = ph && (ph->flags & 1) ? (const pol_friend *)ph->v.bytes.p : NULL;
    pol_chr c;
    pol_handle me;
    int n = 0, i;
    *from = "nowhere";
    if (h && !IsBadReadPtr(h, sizeof *h) && friend_valid(h) && friend_hid(h) == g_me_hid) {
        *from = "z_phead";
        for (i = 0; i < 8; i++) {
            const pol_game *sl = &h->games[i];
            if (!(sl->flags & 1) || sl->title != 1) continue;
            out[n].slot = i;
            out[n].ctsid = sl->ctsid;
            out[n].lo = sl->ctid_lo;
            out[n].hi = sl->ctid_hi;
            n++;
        }
        return n;
    }
    memset(&me, 0, sizeof me);
    pc_get_handle(b, (unsigned)g_my_slot, &me);
    if (handle_hid(&me) != g_me_hid) return 0;
    *from = "handle";
    for (i = 0; i < 8; i++) {
        if (!(me.chars[i] & 1) || !peers_get_chr(b, (me.chars[i] >> 1) & 0x3f, &c) || c.title != 1) continue;
        out[n].slot = (me.chars[i] >> 1) & 0x3f;
        out[n].ctsid = c.ctsid;
        out[n].lo = c.ctid_lo;
        out[n].hi = c.ctid_hi;
        n++;
    }
    return n;
}
static void my_profile_chr_key(const me_chr *c, char k[25])
{
    wsprintfA(k, "%08x%08x%08x", c->ctsid, c->lo, c->hi);
}
/* the name's the Content ID list's, z_phead doesn't have one */
static int my_profile_chars_json(BYTE *b, char *out, int cap)
{
    me_chr ch[8];
    pol_chr c;
    char k[25], nm[64];
    const char *from;
    int n = my_profile_chars(b, ch, &from), o, i, j;
    o = wsprintfA(out, ",\"characters\":[");
    for (i = 0; i < n && o < cap - 200; i++) {
        lstrcpyA(nm, "\"\"");
        for (j = 0; j < 64; j++)
            if (peers_get_chr(b, j, &c) && c.ctsid == ch[i].ctsid && c.ctid_lo == ch[i].lo && c.ctid_hi == ch[i].hi) {
                json_sjis(nm, sizeof nm, (const BYTE *)c.name, 0x0f);
                break;
            }
        my_profile_chr_key(&ch[i], k);
        o += wsprintfA(out + o, "%s{\"slot\":%d,\"key\":\"%s\",\"world\":%u,\"name\":%s}", i ? "," : "", ch[i].slot, k,
                       (ch[i].ctsid >> 16) & 0xff, nm);
    }
    out[o++] = ']';
    out[o] = 0;
    base_log("profile edit: %d FFXI characters (from %s)\r\n", n, from);
    return o;
}
static int my_profile_write_json(BYTE *b)
{
    static const struct { const char *name; int parts; } tabs[] = {
        { "z_sex", 0 }, { "z_job", 0 }, { "z_area", 3 }, { "z_lang0", 0 }, { "z_fav0", 2 } };
    static char js[0xc00];
    pdb_schema *s = g_profile_db_pib;
    pfb_hdr *p = g_pfb;
    const int cap = 0x40000;
    char *out = (char *)HeapAlloc(GetProcessHeap(), 0, cap), path[MAX_PATH + 64], tmp[MAX_PATH + 80], tk[128];
    int o = 0, i, ok = 0;
    DWORD n;
    pol_handle h;
    if (!out) return 0;
    memset(&h, 0, sizeof h);
    pc_get_handle(b, (unsigned)g_my_slot, &h);
    h.name[15] = 0;
    json_pol(b, h.name, 0x400, js, sizeof js);
    json_str(tk, sizeof tk, g_s.me_token);
    o += wsprintfA(out + o, "{\"token\":%s,\"handle\":%s,\"slot\":%d,\"key\":\"%03x%08x\",\"level\":%u,\"fields\":{",
                   tk, js, g_my_slot, (unsigned)(g_me_hid >> 32), (unsigned)g_me_hid, g_me_rec->level);
    for (i = 0; i < (int)(sizeof g_me_fields / sizeof g_me_fields[0]); i++) {
        pdb_item *it = profile_db_get(b, s, g_me_fields[i], g_me_rec);
        int idx = pc_pdb_field_index(b, s, g_me_fields[i]);
        const pfb_item *pi = idx >= 0 ? profile_db_pfb_item(p, idx) : NULL;
        DWORD v = 0;
        if (!it || idx < 0) continue;
        if (pi) json_pol(b, pi->label, 0x100, js, sizeof js);
        else lstrcpyA(js, "\"\"");
        o += wsprintfA(out + o, "%s\"%s\":{\"label\":%s,\"level\":%u", i ? "," : "", g_me_fields[i], js, it->level);
        if (!strcmp(g_me_fields[i], "z_mail")) {
            char masked[0x150];
            my_profile_mask_mail(it->v.str && !IsBadStringPtrA(it->v.str, 0x140) ? it->v.str : "",
                masked, sizeof masked);
            json_pol(b, masked, 0x400, js, sizeof js);
            memcpy(out + o, ",\"s\":", 5); o += 5;
            lstrcpyA(out + o, js); o += lstrlenA(js);
        } else if (!strcmp(g_me_fields[i], "z_area")) {
            pdb_item *c = profile_db_get(b, s, "z_cntry", g_me_rec), *w = profile_db_get(b, s, "z_wide", g_me_rec),
                     *l = profile_db_get(b, s, "z_local", g_me_rec);
            v = (it->v.bytes.p ? (DWORD)*(BYTE *)it->v.bytes.p << 24 : 0) |
                (c && c->v.bytes.p ? (DWORD)*(BYTE *)c->v.bytes.p << 16 : 0) |
                (w && w->v.bytes.p ? (DWORD)*(BYTE *)w->v.bytes.p << 8 : 0) |
                (l && l->v.bytes.p ? (DWORD)*(BYTE *)l->v.bytes.p : 0);
            o += wsprintfA(out + o, ",\"v\":%lu", v);
        } else {
            switch (s->fields[idx].type) {
            case 2: v = it->v.bytes.p ? *(BYTE *)it->v.bytes.p : 0; break;
            case 3: case 4: v = it->v.u16; break;
            default: v = it->v.u32; break;
            }
            o += wsprintfA(out + o, ",\"v\":%lu", v);
        }
        out[o++] = '}';
    }
    lstrcpyA(out + o, "},\"tables\":{"); o += lstrlenA(out + o);
    for (i = 0; i < 5; i++) {
        int idx = pc_pdb_field_index(b, s, tabs[i].name);
        o += wsprintfA(out + o, "%s\"%s\":", i ? "," : "", tabs[i].name);
        o += my_profile_table(b, out + o, cap - o - 16, idx >= 0 ? profile_db_pfb_item(p, idx) : NULL, tabs[i].parts);
    }
    out[o++] = '}';
    o += my_profile_chars_json(b, out + o, cap - o - 4);
    out[o++] = '}';
    base_ipc_file(path, IPC_MYPROFILE);
    lstrcpyA(tmp, path); lstrcatA(tmp, ".tmp");
    HANDLE f = CreateFileA(tmp, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f != INVALID_HANDLE_VALUE) {
        ok = WriteFile(f, out, (DWORD)o, &n, NULL) && n == (DWORD)o;
        CloseHandle(f);
        ok = ok && MoveFileExA(tmp, path, MOVEFILE_REPLACE_EXISTING);
        if (!ok) DeleteFileA(tmp);
    }
    HeapFree(GetProcessHeap(), 0, out);
    return ok;
}
static void my_profile_abandon(BYTE *b)
{
    if (g_s.me_step == 2 && g_me_job >= 0) {
        pc_free_job(b, g_me_job);
        g_me_rec = NULL;                    /* a cancelled read can still write, so never freed */
    }
    if (g_s.me_step == 1 || g_s.me_step == 2) my_profile_done("cancelled");
}
static void my_profile_load_start(BYTE *b)
{
    int t = pc_pdb_load_start(b, g_profile_db_pib, &g_me_cond, g_me_rec);
    if (t == NO_SLOT) { g_s.me_step = 1; return; }
    base_log("profile edit: load -> %d\r\n", t);
    if (t < 0) { my_profile_done(requests_failed(t)); return; }
    g_me_job = t;
    g_s.me_step = 2;
}
static void my_profile_save_start(BYTE *b)
{
    int t = my_profile_edit_update_start(b, g_profile_db_pib, g_me_rec, g_my_slot, &g_me_req);
    if (t == NO_SLOT) { g_s.me_step = 3; return; }
    base_log("profile edit: save %d field(s), level %d -> %d\r\n", g_me_req.n, g_me_req.level, t);
    if (t < 0) { my_profile_done(requests_failed(t)); return; }
    g_me_job = t;
    g_s.me_step = 4;
}
static int my_profile_field_ok(const char *n)
{
    for (int i = 0; i < ME_EDITABLE; i++) if (!strcmp(g_me_fields[i], n)) return i + 1;
    return 0;
}
static void my_profile_request(BYTE *b)
{
    char buf[2048], *p, *tok, *lvl, *line, *name, nomail[0x80], jp[MAX_PATH + 64];
    int kind, level = 0, idx;
    pol_handle h;
    if (!hold_db_free()) return;
    kind = requests_take(REQ_MYPROFILE, buf, sizeof buf) >= 0 ? 1
         : requests_take(REQ_SAVEPROFILE, buf, sizeof buf) >= 0 ? 2
         : requests_take(REQ_RENAME, buf, sizeof buf) >= 0 ? 3 : 0;
    if (!kind) return;
    g_s.db_last = GetTickCount64();
    p = buf;
    tok = requests_line(&p);
    lstrcpynA(g_s.me_token, tok, sizeof g_s.me_token);
    if (!profile_db_load_pib(b) || !profile_db_pfb_load(b)) { my_profile_done("failed:no schema"); return; }
    g_me_t0 = GetTickCount64();
    if (kind == 1) {
        base_ipc_file(jp, IPC_MYPROFILE);
        DeleteFileA(jp);
        memset(&h, 0, sizeof h);
        pc_get_handle(b, (unsigned)g_my_slot, &h);
        g_me_hid = handle_hid(&h);
        if (!g_me_rec)
            g_me_rec = (pdb_rec *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, g_profile_db_pib->rec_size + 8);
        else memset(g_me_rec, 0, g_profile_db_pib->rec_size + 8);
        if (!g_me_rec) { my_profile_done("failed:alloc"); return; }
        memset(&g_me_cond, 0, sizeof g_me_cond);
        pc_pdb_item_set(b, g_profile_db_pib, &g_me_cond.item, "z_hid", &g_me_hid, 1);
        if (!(g_me_cond.item.flags & 1)) { my_profile_done("failed:schema"); return; }
        pc_pdb_cond_op(b, &g_me_cond, 1);
        pc_pdb_where(b, 1, &g_me_cond);
        lstrcpyA(g_s.me_state, "loading");
        g_snap_dirty = 1;
        my_profile_load_start(b);
        return;
    }
    if (kind == 3) {
        char *key, now[17];
        name = requests_line(&p);
        key = requests_line(&p);
        memset(&h, 0, sizeof h);
        pc_get_handle(b, (unsigned)g_my_slot, &h);
        /* the handle the editor loaded, so one left open across a switch can't rename the new one */
        peers_hid_key(handle_hid(&h), now);
        if (lstrcmpA(key, now) != 0) { my_profile_done("failed:not loaded"); return; }
        memset(g_me_name, 0, sizeof g_me_name);
        if (!name[0] || MultiByteToWideChar(CP_UTF8, 0, name, -1, g_me_name, 31) <= 0) {
            my_profile_done("failed:bad name");
            return;
        }
        lstrcpyA(g_s.me_state, "saving");
        g_s.me_step = 5;
        g_me_t0 = GetTickCount64();
        g_snap_dirty = 1;
        return;
    }
    lvl = requests_line(&p);
    nomail[0] = 0;
    memset(&h, 0, sizeof h);
    pc_get_handle(b, (unsigned)g_my_slot, &h);
    /* one loaded under another handle (before a switch) isn't this one's */
    if (!g_me_rec || !g_me_rec->items || handle_hid(&h) != g_me_hid) {
        my_profile_done("failed:not loaded");
        return;
    }
    if (lvl[0] >= '0' && lvl[0] <= '3' && !lvl[1]) level = lvl[0] - '0';
    else { my_profile_done("failed:bad request"); return; }
    memset(&g_me_req, 0, sizeof g_me_req);
    g_me_req.level = level;
    g_me_req.utime = 1;
    while (*(line = requests_line(&p))) {
        char *eq = strchr(line, '=');
        if (!eq) { my_profile_done("failed:bad request"); return; }
        *eq++ = 0;
        if (!strcmp(line, "nomail")) { json_utf8_to_sjis(eq, nomail, sizeof nomail); continue; }
        if (!(idx = my_profile_field_ok(line)) || g_me_req.n >= PE_MAXF) {
            my_profile_done("failed:bad field"); return;
        }
        g_me_req.f[g_me_req.n].name = g_me_fields[idx - 1];   /* static, buf's gone by the time the save runs */
        if (idx == 1) {
            json_utf8_to_sjis(eq, g_me_mail, sizeof g_me_mail);
            if (lstrlenA(g_me_mail) > 0x13f) { my_profile_done("failed:mail too long"); return; }
            /* the viewer checks an address with svc 0x6e8, and its own "No Mail address" text isn't one */
            if (!(nomail[0] && !lstrcmpA(g_me_mail, nomail)) &&
                (!g_me_mail[0] || !my_profile_edit_mail_ok(b, g_me_mail))) {
                my_profile_done("failed:bad mail");
                return;
            }
            g_me_req.f[g_me_req.n].str = g_me_mail;
        } else {
            DWORD v = 0;
            int d = 0;
            for (; *eq >= '0' && *eq <= '9' && d < 10; eq++, d++) v = v * 10 + (DWORD)(*eq - '0');
            if (!d || *eq) { my_profile_done("failed:bad value"); return; }
            if (idx == 2 && v > 999) { my_profile_done("failed:bad value"); return; }
            if (idx == 3 && v > 1) { my_profile_done("failed:bad value"); return; }
            g_me_req.f[g_me_req.n].v = v;
        }
        g_me_req.n++;
    }
    if (g_me_req.n == 0 && level == 0) { my_profile_done("failed:nothing to save"); return; }
    lstrcpyA(g_s.me_state, "saving");
    g_snap_dirty = 1;
    my_profile_save_start(b);
}
static void my_profile_pump(BYTE *b, int ctl)
{
    pdb_item *it;
    int r;
    switch (g_s.me_step) {   /* 1 load waits for a slot, 2 load job, 3 save waits, 4 save job, 5 rename */
    case 0:
        if (ctl && !g_s.draining) my_profile_request(b);
        return;
    case 1:
        if (g_s.draining) my_profile_done("cancelled");
        else if (GetTickCount64() - g_me_t0 >= JOB_TIMEOUT_MS) my_profile_done("failed:busy");
        else my_profile_load_start(b);
        return;
    case 2:
        r = pc_pdb_load_check(b, g_me_job);
        if (r == 0) {
            if (g_s.draining) my_profile_abandon(b);
            return;
        }
        g_me_job = -1;
        if (r < 0) { my_profile_done(requests_failed(r)); return; }
        if (!g_me_rec->items) { my_profile_done("failed:no profile"); return; }
        /* as in the profile pic save, the record must be this handle's (the viewer doesn't check) */
        it = profile_db_get(b, g_profile_db_pib, "z_hid", g_me_rec);
        if (!it || it->v.u64 != g_me_hid) { my_profile_done("failed:mismatch"); return; }
        it = profile_db_get(b, g_profile_db_pib, "z_pnum", g_me_rec);
        if (FS_STRICT_PNUM && it && (it->flags & 1) && it->v.bytes.p && *(BYTE *)it->v.bytes.p != (BYTE)g_my_slot) {
            my_profile_done("failed:mismatch");
            return;
        }
        my_profile_done(my_profile_write_json(b) ? "ready" : "failed:write");
        return;
    case 3:
        if (GetTickCount64() - g_me_t0 >= JOB_TIMEOUT_MS) my_profile_done("failed:busy");
        else my_profile_save_start(b);   /* a write, so it still runs during a drain */
        return;
    case 4:
        r = pc_pdb_update_check(b, g_me_job);
        if (r == 0) return;
        g_me_job = -1;
        my_profile_done(r == 1 ? "saved" : requests_failed(r));
        return;
    case 5:
        r = my_profile_handle_rename(b, g_my_slot, g_me_name);
        if (r == 0) {
            if (GetTickCount64() - g_me_t0 >= 2 * JOB_TIMEOUT_MS) {
                g_hr.st = HR_IDLE; my_profile_done("failed:timeout");
            }
            return;
        }
        if (r == 1) my_profile_done("saved");
        else my_profile_done(r == HR_E_REVERT ? "failed:reloaded" : r == HR_E_NAME ? "failed:bad name"
                     : r == HR_E_NGWORD ? "failed:word" : requests_failed(r));
        return;
    }
}

#define pic_fail(why) requests_refuse(g_s.pic_state, sizeof g_s.pic_state, "pic: request refused - %s\r\n", why)
static void my_profile_pic_pump(BYTE *b, int ctl)
{
    int r;
    if (g_s.pic_want < 0) {
        char buf[96], *p;
        int i, v = 0, d = 0;
        if (!ctl || g_s.draining || !hold_db_free()) return;
        if (requests_take(REQ_PIC, buf, sizeof buf) < 0) return;
        g_s.db_last = GetTickCount64();
        for (i = 0; buf[i] >= '0' && buf[i] <= '9' && d < 5; i++, d++) v = v * 10 + (buf[i] - '0');
        p = buf + i;
        if (*p == '\r') p++;
        if (*p == '\n') p++;
        lstrcpynA(g_s.pic_token, p, sizeof g_s.pic_token);
        for (p = g_s.pic_token; *p; p++) if (*p == '\r' || *p == '\n') { *p = 0; break; }
        if (!d || v > 9999 || (buf[i] && buf[i] != '\r' && buf[i] != '\n')) { pic_fail("failed:bad request"); return; }
        g_s.pic_want = v;
        lstrcpyA(g_s.pic_state, "saving");
        base_log("pic: save pic %d on slot %d\r\n", v, g_my_slot);
        g_snap_dirty = 1;
    }
    if (g_s.draining && g_fs.st != ST_LOAD_POLL && g_fs.st != ST_UPD_POLL) {
        my_profile_pic_reset();
        g_s.pic_want = -1;
        lstrcpyA(g_s.pic_state, "failed:cancelled");
        return;
    }
    r = my_profile_pic_save(b, g_my_slot, (unsigned)g_s.pic_want, g_s.draining);
    if (r == 0) return;
    if (r == 1) lstrcpyA(g_s.pic_state, "saved");
    else lstrcpyA(g_s.pic_state, requests_failed(r));
    g_s.pic_want = -1;
    g_snap_dirty = 1;
}

/* my characters' tabs on the viewer's editor (app.dll 0x10228190), read only */
static __declspec(align(8)) pdb_cond g_xi_cond[2];
static pdb_cond *g_xi_where;
static pdb_rec *g_xi_rec;
static unsigned __int64 g_xi_ctid;
static DWORD g_xi_ctsid;

static void my_profile_xi_done(const char *state)
{
    lstrcpynA(g_s.xi_state, state, sizeof g_s.xi_state);
    g_s.xi_step = 0;
    g_s.xi_job = -1;
    base_log("ffxi profile edit: %s\r\n", state);
    g_snap_dirty = 1;
}
static void my_profile_xi_load_start(BYTE *b)
{
    int t = pc_pdb_load_start(b, g_ch_pib, g_xi_where, g_xi_rec);
    if (t == NO_SLOT) { g_s.xi_step = 1; return; }
    base_log("ffxi profile edit: load -> %d\r\n", t);
    if (t < 0) { my_profile_xi_done(requests_failed(t)); return; }
    g_s.xi_job = t;
    g_s.xi_step = 2;
    g_s.xi_t0 = GetTickCount64();
}
/* only a character from the loaded profile's own list, the ids are never the app's */
static int my_profile_xi_find(BYTE *b, const char *key, me_chr *out)
{
    me_chr ch[8];
    char k[25];
    const char *from;
    int n = my_profile_chars(b, ch, &from);
    for (int i = 0; i < n; i++) {
        my_profile_chr_key(&ch[i], k);
        if (!lstrcmpA(k, key)) { *out = ch[i]; return 1; }
    }
    return 0;
}
static void my_profile_xi_request(BYTE *b)
{
    char buf[256], *p, *hk, *ck, now[17];
    me_chr c;
    pol_handle h;
    if (GetTickCount64() - g_s.xi_last < 3000 || !hold_db_free()) return;
    if (requests_take(REQ_XIPROFILE, buf, sizeof buf) < 0) return;
    g_s.xi_last = g_s.db_last = GetTickCount64();
    p = buf;
    lstrcpynA(g_s.xi_token, requests_line(&p), sizeof g_s.xi_token);
    hk = requests_line(&p);
    ck = requests_line(&p);
    memset(&h, 0, sizeof h);
    pc_get_handle(b, (unsigned)g_my_slot, &h);
    peers_hid_key(handle_hid(&h), now);
    /* my profile has to be loaded, and for this handle, since the characters come from it */
    if (lstrcmpA(hk, now) != 0 || !g_me_rec || !g_me_rec->items ||
        handle_hid(&h) != g_me_hid) {
        my_profile_xi_done("failed:not loaded");
        return;
    }
    if (!my_profile_xi_find(b, ck, &c)) { my_profile_xi_done("failed:no character"); return; }
    if (!profiles_character_load_pib(b)) { my_profile_xi_done("failed:no schema"); return; }
    if (!g_xi_rec) g_xi_rec = (pdb_rec *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, g_ch_pib->rec_size + 8);
    else memset(g_xi_rec, 0, g_ch_pib->rec_size + 8);
    if (!g_xi_rec) { my_profile_xi_done("failed:alloc"); return; }
    g_s.xi_json[0] = 0;
    lstrcpyA(g_s.xi_key, ck);
    g_xi_ctsid = c.ctsid;
    g_xi_ctid = (unsigned __int64)c.hi << 32 | c.lo;
    memset(g_xi_cond, 0, sizeof g_xi_cond);
    pc_pdb_item_set(b, g_ch_pib, &g_xi_cond[0].item, "z_ctsid", &g_xi_ctsid, 1);
    pc_pdb_item_set(b, g_ch_pib, &g_xi_cond[1].item, "z_ctid", &g_xi_ctid, 1);
    if (!(g_xi_cond[0].item.flags & 1) || !(g_xi_cond[1].item.flags & 1)) {
        my_profile_xi_done("failed:schema"); return;
    }
    pc_pdb_cond_op(b, &g_xi_cond[0], 1);
    pc_pdb_cond_op(b, &g_xi_cond[1], 1);
    g_xi_where = pc_pdb_where(b, 2, &g_xi_cond[0], &g_xi_cond[1]);
    if (!g_xi_where) { my_profile_xi_done("failed:schema"); return; }
    base_log("ffxi profile edit: slot %d\r\n", c.slot);
    lstrcpyA(g_s.xi_state, "loading");
    g_snap_dirty = 1;
    g_s.xi_t0 = GetTickCount64();
    my_profile_xi_load_start(b);
}
static void my_profile_xi_pump(BYTE *b, int ctl)
{
    pdb_item *a, *c;
    char nm[0x100], wn[0x100];
    int r;
    switch (g_s.xi_step) {
    case 0:
        if (ctl && !g_s.draining) my_profile_xi_request(b);
        return;
    case 1:
        if (g_s.draining) my_profile_xi_done("cancelled");
        else if (GetTickCount64() - g_s.xi_t0 >= JOB_TIMEOUT_MS) my_profile_xi_done("failed:busy");
        else my_profile_xi_load_start(b);
        return;
    case 2:
        r = pc_pdb_load_check(b, g_s.xi_job);
        if (r == 0) {
            if (GetTickCount64() - g_s.xi_t0 >= JOB_TIMEOUT_MS || g_s.draining) {
                pc_free_job(b, g_s.xi_job);
                g_xi_rec = NULL;   /* a cancelled read can still write, so never freed */
                my_profile_xi_done(g_s.draining ? "cancelled" : "failed:timeout");
            }
            return;
        }
        g_s.xi_job = -1;
        if (r < 0) { my_profile_xi_done(requests_failed(r)); return; }
        if (!g_xi_rec->items) { my_profile_xi_done("failed:no profile"); return; }
        a = profile_db_get(b, g_ch_pib, "z_ctsid", g_xi_rec);
        c = profile_db_get(b, g_ch_pib, "z_ctid", g_xi_rec);
        if (!a || !c || a->v.u32 != g_xi_ctsid || c->v.u64 != g_xi_ctid) {
            my_profile_xi_done("failed:mismatch"); return;
        }
        profiles_character_text(b, g_xi_rec, "z_name", nm, sizeof nm, NULL);
        profiles_character_text(b, g_xi_rec, "z_worldname", wn, sizeof wn, NULL);
        wsprintfA(g_s.xi_json, "\"key\":\"%s\",\"level\":%u,\"name\":%s,\"worldName\":%s", g_s.xi_key, g_xi_rec->level, nm, wn);
        base_log("ffxi profile edit: record level %u\r\n", g_xi_rec->level);
        my_profile_xi_done("ready");
        return;
    }
}
