/* the viewer's profile pic save: load my record, UPDATE z_ficon + z_utime, then SetHandle
   locally. Only built inside pol_hook.c, which defines SVC and FS_LOG first */

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
        g_fs.st = ST_LOAD_POLL;
        FS_LOG("pic: loading profile of slot %d (hid 0x%08x%08x), job %d\r\n", slot,
               (unsigned)(g_fs.hid >> 32), (unsigned)g_fs.hid, r);
        return FS_PENDING;
    }
    case ST_LOAD_POLL: {
        pdb_item *it;
        s = g_fs_pib;
        r = ((svc4b8_t)SVC(0x4b8))(g_fs.job);
        if (r == 0) return FS_PENDING;
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
        g_fs.st = ST_UPD_POLL;
        FS_LOG("pic: update z_ficon=%u z_utime=%u level %u/%u/%u where z_pnum=%u, job %d\r\n",
               fval, now, g_fs.rec->level, rf->level, ru->level, pnum, r);
        return FS_PENDING;
    }
    case ST_UPD_POLL: {
        r = ((svc4d8_t)SVC(0x4d8))(g_fs.job);
        if (r == 0) return FS_PENDING;
        g_fs.job = -1;
        /* the viewer shits itself after 18000 polls and calls the save done anyway regardless (luv me POL innit), for
           me polcore's own fail at 100 s is an error */
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

/* svc 0x4dc frees the job slot but also makes every polcore job poll return 0 for the next 10s (why tho?????),
   so it's only for shutdown */
static void profile_pic_cancel(BYTE *b)
{
    if (g_fs.job >= 0) {
        ((svc4dc_t)SVC(0x4dc))(g_fs.job);
        if (g_fs.st == ST_LOAD_POLL) g_fs.rec = NULL;   /* a cancelled read can still write into it, so never freed */
    }
    fs_reset();
}
