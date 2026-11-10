/* my FFXI character's privacy, the second half of my_profile.c */
/* my characters' tabs on the viewer's editor (app.dll 0x10228190), read only */
static __declspec(align(8)) pdb_cond g_xi_cond[2];
static pdb_cond *g_xi_where;
static pdb_rec *g_xi_rec;
static unsigned __int64 g_xi_ctid;
static DWORD g_xi_ctsid;

static void my_profile_xi_done(const char *state)
{
    lstrcpynA(g_s.ffxi_state, state, sizeof g_s.ffxi_state);
    g_s.ffxi_step = 0;
    g_s.ffxi_job = -1;
    base_log("ffxi profile edit: %s\r\n", state);
    g_snap_dirty = 1;
}
static void my_profile_xi_load_start(BYTE *b)
{
    int t = pc_pdb_load_start(b, g_ch_pib, g_xi_where, g_xi_rec);
    if (t == NO_SLOT) { g_s.ffxi_step = 1; return; }
    base_log("ffxi profile edit: load -> %d\r\n", t);
    if (t < 0) { my_profile_xi_done(requests_failed(t)); return; }
    g_s.ffxi_job = t;
    g_s.ffxi_step = 2;
    g_s.ffxi_t0 = GetTickCount64();
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
    if (!hold_db_free()) return;
    if (requests_take(REQ_XIPROFILE, buf, sizeof buf) < 0) return;
    g_s.db_last = GetTickCount64();
    p = buf;
    lstrcpynA(g_s.ffxi_token, requests_line(&p), sizeof g_s.ffxi_token);
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
    g_s.ffxi_json[0] = 0;
    lstrcpyA(g_s.ffxi_key, ck);
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
    lstrcpyA(g_s.ffxi_state, "loading");
    g_snap_dirty = 1;
    g_s.ffxi_t0 = GetTickCount64();
    my_profile_xi_load_start(b);
}
/* the answer's members, name and wn already quoted */
static void my_profile_xi_build(const char *nm, const char *wn)
{
    jw w = {0};
    jw_lit(&w, "key", g_s.ffxi_key);
    jw_uint(&w, "level", g_xi_rec->level);
    jw_raw(&w, "name", nm);
    jw_raw(&w, "worldName", wn);
    jw_fit(&w, g_s.ffxi_json, sizeof g_s.ffxi_json, "FFXI profile edit");
    jw_free(&w);
}
static void my_profile_xi_pump(BYTE *b, int ctl)
{
    pdb_item *a, *c;
    char nm[0x100], wn[0x100];
    int r;
    switch (g_s.ffxi_step) {
    case 0:
        if (ctl && !g_s.draining) my_profile_xi_request(b);
        return;
    case 1:
        if (g_s.draining) my_profile_xi_done("cancelled");
        else if (GetTickCount64() - g_s.ffxi_t0 >= JOB_TIMEOUT_MS) my_profile_xi_done("failed:busy");
        else my_profile_xi_load_start(b);
        return;
    case 2:
        r = pc_pdb_load_check(b, g_s.ffxi_job);
        if (r == 0) {
            if (g_s.draining) {
                pc_free_job(b, g_s.ffxi_job);
                g_xi_rec = NULL;   /* a cancelled read can still write, so never freed */
                my_profile_xi_done("cancelled");
            }
            return;
        }
        g_s.ffxi_job = -1;
        if (r < 0) { my_profile_xi_done(requests_failed(r)); return; }
        if (!g_xi_rec->items) { my_profile_xi_done("failed:no profile"); return; }
        a = profile_db_get(b, g_ch_pib, "z_ctsid", g_xi_rec);
        c = profile_db_get(b, g_ch_pib, "z_ctid", g_xi_rec);
        if (!a || !c || a->v.u32 != g_xi_ctsid || c->v.u64 != g_xi_ctid) {
            my_profile_xi_done("failed:mismatch"); return;
        }
        profiles_character_text(b, g_xi_rec, "z_name", nm, sizeof nm, NULL);
        profiles_character_text(b, g_xi_rec, "z_worldname", wn, sizeof wn, NULL);
        my_profile_xi_build(nm, wn);
        base_log("ffxi profile edit: record level %u\r\n", g_xi_rec->level);
        my_profile_xi_done("ready");
        return;
    }
}
