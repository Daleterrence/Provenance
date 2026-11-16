/* polcore's own negative codes pass straight through, mine sit in a range it doesn't use */
#define FS_PENDING        0
#define FS_DONE           1
#define FS_E_ARGS     (-0x7f01)
#define FS_E_HANDLE   (-0x7f03)
#define FS_E_PIB      (-0x7f04)
#define FS_E_MEM      (-0x7f05)
#define FS_E_NOREC    (-0x7f06)
#define FS_E_MISMATCH (-0x7f07)
#define FS_E_ITEM     (-0x7f09)
#define FS_E_CANCELLED (-0x7f0a)

#define FS_STRICT_PNUM  1

#define P8(a, i) a[i], a[(i)+1], a[(i)+2], a[(i)+3], a[(i)+4], a[(i)+5], a[(i)+6], a[(i)+7]

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
    if (!where) return FS_E_ITEM;
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
static pe_req g_me_req;
static char g_me_mail[0x150];
static WCHAR g_me_name[32];
static unsigned __int64 g_me_hid;

/* 1 load waits for a slot, 2 load job, 3 save waits, 4 save job, 5 rename. The loaded record stays for the saves */
static void my_profile_done(const char *state);
static void my_profile_go(BYTE *b, db_job *j);
static db_job g_me = { .job = -1, .state = g_s.me_state, .cap = sizeof g_s.me_state, .done = my_profile_done,
                       .start = my_profile_go, .wait_ms = JOB_TIMEOUT_MS, .keep = 1, .last_read = 2 };
#define ME_REC ((pdb_rec *)g_me.rec)
static void my_profile_done(const char *state)
{
    db_job_done(&g_me, state);
    base_log("profile edit: %s\r\n", state);
    g_snap_dirty = 1;
}
static const char *const g_me_fields[] = { "z_mail", "z_age", "z_age_f", "z_sex", "z_job", "z_area",
                                           "z_lang0", "z_lang1", "z_lang2", "z_fav0", "z_fav1", "z_fav2", "z_ficon" };
#define ME_EDITABLE ((int)(sizeof g_me_fields / sizeof g_me_fields[0]) - 1)
static void my_profile_table(BYTE *b, jw *w, const char *key, const pfb_item *pi, int parts)
{
    static char part[0x80];
    jw_arr(w, key);
    for (int i = 0; pi && pi->tab && i < pi->tab->n; i++) {
        jw_arr(w, NULL);
        jw_uint(w, NULL, pi->tab->e[i].code);
        for (int k = 0; k < (parts > 0 ? parts : 1); k++) {
            if (parts > 0) profile_db_pfb_part(pi->tab, i, k, part, sizeof part);
            else profile_db_pfb_last(pi->tab->e[i].text, part, sizeof part);
            jw_pol(w, NULL, b, part, 0x180, 0x400);
        }
        jw_end(w);
    }
    jw_end(w);
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
    lstrcpynA(out + lead + 3, at, cap - lead - 3);
}

/* my FFXI characters, the viewer's edit page tabs (app.dll 0x10224e5c): my record's z_phead slots when it's this
   handle's, else the Content IDs linked to the handle. Only title 1 gets one here */
typedef struct me_chr { int slot; DWORD ctsid, lo, hi; } me_chr;
static int my_profile_chars(BYTE *b, me_chr *out, const char **from)
{
    pdb_item *ph = g_me.rec && ME_REC->items && g_profile_db_pib
        ? profile_db_get(b, g_profile_db_pib, "z_phead", ME_REC)
        : NULL;
    const pol_friend *h = ph && (ph->flags & 1) ? (const pol_friend *)ph->v.bytes.p : NULL;
    pol_chr c;
    pol_handle me;
    int n = 0, i;
    *from = "nowhere";
    if (h && !IsBadReadPtr(h, sizeof *h) && pc_friend_valid(h) && pc_friend_hid(h) == g_me_hid) {
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
static void my_profile_chars_json(BYTE *b, jw *w)
{
    me_chr ch[8];
    pol_chr c;
    char k[25];
    const char *from;
    int n = my_profile_chars(b, ch, &from), i, j;
    jw_arr(w, "characters");
    for (i = 0; i < n; i++) {
        int named = 0;
        for (j = 0; j < 64 && !named; j++)
            named = peers_get_chr(b, j, &c) && c.ctsid == ch[i].ctsid && c.ctid_lo == ch[i].lo && c.ctid_hi == ch[i].hi;
        my_profile_chr_key(&ch[i], k);
        jw_obj(w, NULL);
        jw_int(w, "slot", ch[i].slot);
        jw_lit(w, "key", k);
        jw_uint(w, "world", (ch[i].ctsid >> 16) & 0xff);
        if (named) jw_sjis(w, "name", b, (const BYTE *)c.name, 0x0f, 64);
        else jw_raw(w, "name", "\"\"");
        jw_end(w);
    }
    jw_end(w);
    base_log("profile edit: %d FFXI characters (from %s)\r\n", n, from);
}
/* same read as a friend's tab, but the ids come from my_profile_chars for that slot, never the request.
   The key only checks the card's still the same character */
static void my_profile_ffxi_request(BYTE *b)
{
    char buf[128], *p, *tok, *key, *sl, want[25];
    const char *from;
    me_chr ch[8];
    pol_handle h;
    int n, count, i;
    if (!hold_db_free()) return;
    if (requests_take(REQ_MYFFXIPROFILE, buf, sizeof buf) < 0) return;
    g_s.db_last = GetTickCount64();
    p = buf;
    tok = requests_line(&p);
    key = requests_line(&p);
    sl = requests_line(&p);
    lstrcpynA(g_fp_token, tok, sizeof g_fp_token);
    g_fp_json[0] = 0;
    g_fp_slot = -1;
    g_fp_ctsid = g_fp_lo = g_fp_hi = 0;
    if (!requests_parse_num(sl, 2, &n) || n > 63) {
        profiles_ffxiprofile_done("failed:bad request");
        return;
    }
    g_fp_slot = n;
    memset(&h, 0, sizeof h);
    pc_get_handle(b, (unsigned)g_my_slot, &h);
    /* the handle the editor loaded, so a card left open across a switch can't read the new handle's */
    if (!g_me_hid || handle_hid(&h) != g_me_hid) {
        profiles_ffxiprofile_done("failed:not loaded");
        return;
    }
    count = my_profile_chars(b, ch, &from);
    for (i = 0; i < count && i < 8; i++) {
        my_profile_chr_key(&ch[i], want);
        if (ch[i].slot == n && lstrcmpA(want, key) == 0) break;
    }
    if (i >= count || i >= 8) {
        profiles_ffxiprofile_done("failed:no character");
        return;
    }
    g_fp_ctsid = ch[i].ctsid;
    g_fp_lo = ch[i].lo;
    g_fp_hi = ch[i].hi;
    if (!profiles_character_load_pib(b) || !profiles_ffxiprofile_check_schema(b, g_ch_pib)) {
        profiles_ffxiprofile_done("failed:no schema");
        return;
    }
    base_log("profile edit: FINAL FANTASY XI profile for the character in slot %d\r\n", n);
    ffxi_read_go(b, &g_fp_read);
}
static int my_profile_write_json(BYTE *b)
{
    static const struct { const char *name; int parts; } tabs[] = {
        { "z_sex", 0 }, { "z_job", 0 }, { "z_area", 3 }, { "z_lang0", 0 }, { "z_fav0", 2 } };
    pdb_schema *s = g_profile_db_pib;
    pfb_hdr *p = g_pfb;
    char key[16];
    int i, ok = 0;
    ipc_json file;
    jw w = {0};
    pol_handle h;
    memset(&h, 0, sizeof h);
    pc_get_handle(b, (unsigned)g_my_slot, &h);
    h.name[15] = 0;
    jw_obj(&w, NULL);
    jw_str(&w, "token", g_s.me_token, 128);
    jw_pol(&w, "handle", b, h.name, 0x400, 0xc00);
    jw_int(&w, "slot", g_my_slot);
    wsprintfA(key, "%03x%08x", (unsigned)(g_me_hid >> 32), (unsigned)g_me_hid);
    jw_lit(&w, "key", key);
    jw_uint(&w, "level", ME_REC->level);
    jw_obj(&w, "fields");
    for (i = 0; i < (int)(sizeof g_me_fields / sizeof g_me_fields[0]); i++) {
        pdb_item *it = profile_db_get(b, s, g_me_fields[i], ME_REC);
        int idx = pc_pdb_field_index(b, s, g_me_fields[i]);
        const pfb_item *pi = idx >= 0 ? profile_db_pfb_item(p, idx) : NULL;
        DWORD v = 0;
        if (!it || idx < 0) continue;
        jw_obj(&w, g_me_fields[i]);
        if (pi) jw_pol(&w, "label", b, pi->label, 0x100, 0xc00);
        else jw_raw(&w, "label", "\"\"");
        jw_uint(&w, "level", it->level);
        if (!strcmp(g_me_fields[i], "z_mail")) {
            char masked[0x150];
            my_profile_mask_mail(it->v.str && !IsBadStringPtrA(it->v.str, 0x140) ? it->v.str : "",
                masked, sizeof masked);
            jw_pol(&w, "s", b, masked, 0x400, 0xc00);
        } else if (!strcmp(g_me_fields[i], "z_area")) {
            pdb_item *c = profile_db_get(b, s, "z_cntry", ME_REC), *a = profile_db_get(b, s, "z_wide", ME_REC),
                     *l = profile_db_get(b, s, "z_local", ME_REC);
            v = (it->v.bytes.p ? (DWORD)*(BYTE *)it->v.bytes.p << 24 : 0) |
                (c && c->v.bytes.p ? (DWORD)*(BYTE *)c->v.bytes.p << 16 : 0) |
                (a && a->v.bytes.p ? (DWORD)*(BYTE *)a->v.bytes.p << 8 : 0) |
                (l && l->v.bytes.p ? (DWORD)*(BYTE *)l->v.bytes.p : 0);
            jw_uint(&w, "v", v);
        } else {
            switch (s->fields[idx].type) {
            case 2: v = it->v.bytes.p ? *(BYTE *)it->v.bytes.p : 0; break;
            case 3: case 4: v = it->v.u16; break;
            default: v = it->v.u32; break;
            }
            jw_uint(&w, "v", v);
        }
        jw_end(&w);
    }
    jw_end(&w);
    jw_obj(&w, "tables");
    for (i = 0; i < 5; i++) {
        int idx = pc_pdb_field_index(b, s, tabs[i].name);
        my_profile_table(b, &w, tabs[i].name, idx >= 0 ? profile_db_pfb_item(p, idx) : NULL, tabs[i].parts);
    }
    jw_end(&w);
    my_profile_chars_json(b, &w);
    jw_end(&w);
    if (!w.fail && base_ipc_json_open(IPC_MYPROFILE, &file))
        ok = base_ipc_json_commit(&file, base_ipc_json_write(&file, w.p, w.n));
    jw_free(&w);
    return ok;
}
static void my_profile_abandon(BYTE *b) { db_job_abandon(b, &g_me); }
static void my_profile_load_start(BYTE *b)
{
    int t = pc_pdb_load_start(b, g_profile_db_pib, g_me.where, ME_REC);
    if (t != NO_SLOT) base_log("profile edit: load -> %d\r\n", t);
    if (t < 0 && t != NO_SLOT) { my_profile_done(requests_failed(t)); return; }
    db_job_started(&g_me, 0, t);
}
static void my_profile_save_start(BYTE *b)
{
    int t = my_profile_edit_update_start(b, g_profile_db_pib, ME_REC, g_my_slot, &g_me_req);
    if (t != NO_SLOT) base_log("profile edit: save %d field(s), level %d -> %d\r\n", g_me_req.n, g_me_req.level, t);
    if (t < 0 && t != NO_SLOT) { my_profile_done(requests_failed(t)); return; }
    db_job_started(&g_me, 1, t);
}
static void my_profile_go(BYTE *b, db_job *j)
{
    if (j->step == 3) my_profile_save_start(b); else my_profile_load_start(b);
}
static int my_profile_field_ok(const char *n)
{
    for (int i = 0; i < ME_EDITABLE; i++) if (!strcmp(g_me_fields[i], n)) return i + 1;
    return 0;
}
static int handles_list_edit(void);
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
    /* a rename uploads the handle list too, so it'd carry a create or delete's change with it */
    if (kind == 3 && handles_list_edit()) { my_profile_done("failed:busy"); return; }
    if (kind == 3 && g_s.handle_unconfirmed) { my_profile_done("failed:unconfirmed"); return; }
    if (!profile_db_load_pib(b) || !profile_db_load_pfb(b)) { my_profile_done("failed:no schema"); return; }
    g_me.t0 = GetTickCount64();
    if (kind == 1) {
        base_ipc_file(jp, IPC_MYPROFILE);
        DeleteFileA(jp);
        memset(&h, 0, sizeof h);
        pc_get_handle(b, (unsigned)g_my_slot, &h);
        g_me_hid = handle_hid(&h);
        if (!g_me.rec)
            g_me.rec = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, g_profile_db_pib->rec_size + 8);
        else memset(g_me.rec, 0, g_profile_db_pib->rec_size + 8);
        if (!g_me.rec) { my_profile_done("failed:alloc"); return; }
        g_me.where = db_where_hid(b, g_profile_db_pib, &g_me_hid, &g_me.cond[0]);
        if (!g_me.where) { my_profile_done("failed:schema"); return; }
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
        g_me.step = 5;
        g_me.t0 = GetTickCount64();
        g_snap_dirty = 1;
        return;
    }
    lvl = requests_line(&p);
    nomail[0] = 0;
    memset(&h, 0, sizeof h);
    pc_get_handle(b, (unsigned)g_my_slot, &h);
    /* one loaded under another handle (before a switch) isn't this one's */
    if (!g_me.rec || !ME_REC->items || handle_hid(&h) != g_me_hid) {
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
        if (!strcmp(line, "nomail")) { pol_sjis(b, eq, nomail, sizeof nomail); continue; }
        if (!(idx = my_profile_field_ok(line)) || g_me_req.n >= PE_MAXF) {
            my_profile_done("failed:bad field"); return;
        }
        g_me_req.f[g_me_req.n].name = g_me_fields[idx - 1];   /* static, buf's gone by the time the save runs */
        if (idx == 1) {
            pol_sjis(b, eq, g_me_mail, sizeof g_me_mail);
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
    switch (g_me.step) {
    case 0:
        if (ctl && !g_s.draining) my_profile_request(b);
        return;
    case 1:
        db_job_waiting(b, &g_me, 1);
        return;
    case 2:
        r = pc_pdb_load_check(b, g_me.job);
        if (db_job_pending(b, &g_me, r, 1)) return;
        g_me.job = -1;
        if (r < 0) { my_profile_done(requests_failed(r)); return; }
        if (!ME_REC->items) { my_profile_done("failed:no profile"); return; }
        /* as in the profile pic save, the record must be this handle's (the viewer doesn't check) */
        it = profile_db_get(b, g_profile_db_pib, "z_hid", ME_REC);
        if (!it || it->v.u64 != g_me_hid) { my_profile_done("failed:mismatch"); return; }
        it = profile_db_get(b, g_profile_db_pib, "z_pnum", ME_REC);
        if (FS_STRICT_PNUM && it && (it->flags & 1) && it->v.bytes.p && *(BYTE *)it->v.bytes.p != (BYTE)g_my_slot) {
            my_profile_done("failed:mismatch");
            return;
        }
        my_profile_done(my_profile_write_json(b) ? "ready" : "failed:write");
        return;
    case 3:
        db_job_waiting(b, &g_me, 0);   /* a write, so it still runs during a drain */
        return;
    case 4:
        r = pc_pdb_update_check(b, g_me.job);
        if (r == 0) return;
        g_me.job = -1;
        my_profile_done(r == 1 ? "saved" : requests_failed(r));
        return;
    case 5:
        r = my_profile_handle_rename(b, g_my_slot, g_me_name);
        if (r == 0) {
            if (GetTickCount64() - g_me.t0 >= 2 * JOB_TIMEOUT_MS) {
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

/* the viewer's pic save: load my record, UPDATE z_ficon and z_utime, then SetHandle locally.
   1 load waits for a slot, 2 load job, 3 update waits, 4 update job */
static int g_pic_slot;
static unsigned g_pic_pic;   /* sheet * 8 + cell */
static unsigned __int64 g_pic_hid;
static void my_profile_pic_done(const char *state);
static void my_profile_pic_go(BYTE *b, db_job *j);
static db_job g_pic = { .job = -1, .state = g_s.pic_state, .cap = sizeof g_s.pic_state, .done = my_profile_pic_done,
                        .start = my_profile_pic_go, .last_read = 2 };
#define PIC_REC ((pdb_rec *)g_pic.rec)

static void my_profile_pic_done(const char *state)
{
    db_job_done(&g_pic, state);
    g_s.pic_want = -1;
    g_snap_dirty = 1;
}
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
    if (g_pic.rec) HeapFree(GetProcessHeap(), 0, g_pic.rec);
    g_pic.rec = NULL;
    g_pic.step = 0;
    g_pic.job = -1;
}
static void my_profile_pic_fail(int code, const char *what)
{
    DB_LOG("profile picture: %s -> %d (0x%x)\r\n", what, code, (unsigned)code);
    my_profile_pic_done(requests_failed(code));
}
static void my_profile_pic_load_start(BYTE *b, db_job *j)
{
    pdb_schema *s = g_profile_db_pib;
    int r;
    j->where = db_where_hid(b, s, &g_pic_hid, &j->cond[0]);
    if (!j->where) { my_profile_pic_fail(FS_E_ITEM, "z_hid condition"); return; }
    r = pc_pdb_load_start(b, s, j->where, PIC_REC);
    if (r < 0 && r != NO_SLOT) { my_profile_pic_fail(r, "profile load start (svc 0x4b4)"); return; }
    db_job_started(j, 0, r);
    if (r >= 0)
        DB_LOG("profile picture: loading profile of slot %d (hid 0x%08x%08x), job %d\r\n", g_pic_slot,
               (unsigned)(g_pic_hid >> 32), (unsigned)g_pic_hid, r);
}
static void my_profile_pic_update_start(BYTE *b, db_job *j)
{
    pe_req q;
    int r;
    if (g_s.draining) { my_profile_pic_fail(FS_E_CANCELLED, "shutting down before the update"); return; }
    memset(&q, 0, sizeof q);
    q.utime = 1;
    q.n = 1;
    q.f[0].name = "z_ficon";
    q.f[0].v = g_pic_pic;
    r = my_profile_edit_update_start(b, g_profile_db_pib, PIC_REC, g_pic_slot, &q);
    if (r < 0 && r != NO_SLOT) { my_profile_pic_fail(r, "profile update start (svc 0x4d4)"); return; }
    db_job_started(j, 1, r);
    if (r >= 0) DB_LOG("profile picture: update z_ficon=%u, job %d\r\n", g_pic_pic, r);
}
static void my_profile_pic_go(BYTE *b, db_job *j)
{
    if (j->step == 3) my_profile_pic_update_start(b, j); else my_profile_pic_load_start(b, j);
}
static void my_profile_pic_begin(BYTE *b)
{
    pol_handle h;
    pdb_schema *s;
    if (g_my_slot < 0 || g_my_slot > 63) { my_profile_pic_done(requests_failed(FS_E_ARGS)); return; }
    if (!(s = profile_db_load_pib(b))) { my_profile_pic_done(requests_failed(FS_E_PIB)); return; }
    /* the viewer keeps its slot in bits 13-18 too and uses it as the GetHandle index, so both have to agree */
    if (!handle_get(b, g_my_slot, &h) || (int)((h.hi >> 13) & 0x3f) != g_my_slot) {
        my_profile_pic_done(requests_failed(FS_E_HANDLE));
        return;
    }
    g_pic_hid = handle_hid(&h);
    g_pic.rec = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, s->rec_size + 8);
    if (!g_pic.rec) { my_profile_pic_done(requests_failed(FS_E_MEM)); return; }
    g_pic_slot = g_my_slot;
    g_pic_pic = (unsigned)g_s.pic_want;
    my_profile_pic_load_start(b, &g_pic);
}
static void my_profile_pic_loaded(BYTE *b)
{
    pdb_schema *s = g_profile_db_pib;
    pdb_item *it;
    if (!PIC_REC->items) { my_profile_pic_fail(FS_E_NOREC, "no profile record on the server"); return; }
    /* the record has to be this handle's. The viewer doesn't check */
    it = pc_pdb_find(b, s, "z_hid", PIC_REC);
    if (!it || it->v.u64 != g_pic_hid) { my_profile_pic_fail(FS_E_MISMATCH, "record z_hid differs"); return; }
    it = pc_pdb_find(b, s, "z_pnum", PIC_REC);
    if (FS_STRICT_PNUM && it && (it->flags & 1) && it->v.bytes.p && *(BYTE *)it->v.bytes.p != (BYTE)g_pic_slot) {
        my_profile_pic_fail(FS_E_MISMATCH, "record z_pnum differs from slot");
        return;
    }
    if (!pc_pdb_find(b, s, "z_ficon", PIC_REC) || !pc_pdb_find(b, s, "z_utime", PIC_REC)) {
        my_profile_pic_fail(FS_E_NOREC, "record lacks z_ficon / z_utime");
        return;
    }
    DB_LOG("profile picture: profile loaded, record level %u\r\n", PIC_REC->level);
    g_pic.step = 3;
    my_profile_pic_update_start(b, &g_pic);
}
/* svc 0x4dc frees the job slot but also makes every polcore job poll return 0 for 10s (why tho?????),
   so it's only for shutdown */
static void my_profile_pic_cancel(BYTE *b)
{
    if (g_pic.job >= 0) {
        pc_free_job(b, g_pic.job);
        if (g_pic.step == 2) g_pic.rec = NULL;   /* a cancelled read can still write into it, so never freed */
    }
    my_profile_pic_reset();
}

#define pic_fail(why) requests_refuse(g_s.pic_state, sizeof g_s.pic_state, "profile picture: request refused - %s\r\n", why)
static void my_profile_pic_pump(BYTE *b, int ctl)
{
    db_job *j = &g_pic;
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
        base_log("profile picture: save pic %d on slot %d\r\n", v, g_my_slot);
        g_snap_dirty = 1;
        my_profile_pic_begin(b);
        return;
    }
    /* a write that's running is left to finish, one that hasn't started is called off */
    if (g_s.draining && j->step != 2 && j->step != 4) {
        my_profile_pic_reset();
        g_s.pic_want = -1;
        lstrcpyA(g_s.pic_state, "failed:cancelled");
        return;
    }
    switch (j->step) {
    case 1: case 3:
        db_job_waiting(b, j, 0);
        return;
    case 2:
        r = pc_pdb_load_check(b, j->job);
        if (db_job_pending(b, j, r, 0)) return;
        j->job = -1;
        if (r < 0) { my_profile_pic_fail(r, "profile load (svc 0x4b8)"); return; }
        my_profile_pic_loaded(b);
        return;
    case 4:
        r = pc_pdb_update_check(b, j->job);
        if (db_job_pending(b, j, r, 0)) return;
        j->job = -1;
        /* the viewer gives up after 18000 polls and calls the save done anyway, here polcore's own fail at 100s is an error */
        if (r < 0) { my_profile_pic_fail(r, "profile update (svc 0x4d8)"); return; }
        r = my_profile_pic_apply_local(b, g_pic_slot, g_pic_pic, g_pic_hid);
        if (r < 0) DB_LOG("profile picture: server updated, local handle apply failed %d\r\n", r);
        else DB_LOG("profile picture: slot %d pic %u saved\r\n", g_pic_slot, g_pic_pic);
        my_profile_pic_done("saved");
        return;
    }
}
