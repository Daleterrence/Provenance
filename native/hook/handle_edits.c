static struct { int n, idx[64]; BYTE old[64], want[64]; } g_hord;

static void handle_create_release(BYTE *b)
{
    pol_handle h;
    if (handle_get(b, g_s.handle_slot, &h) && !handle_hid(&h)) pc_release_handle(b, g_s.handle_slot);
}
static void handle_order_back(BYTE *b, int n)
{
    pol_chr c;
    for (int i = 0; i < n; i++) {
        if (!peers_get_chr(b, g_hord.idx[i], &c)) continue;
        c.link = (c.link & ~0xffu) | g_hord.old[i];
        pc_set_content_id(b, (unsigned)g_hord.idx[i], &c);
    }
}
static void handle_edit_undo(BYTE *b)
{
    if (g_s.handle_op == HOP_CREATE) handle_create_release(b);
    else if (g_s.handle_op == HOP_ORDER) handle_order_back(b, g_hord.n);
}

/* a failed one leaves its slot marked as changed in polcore, and the next handle list upload would send it again */
static void handle_list_failed(BYTE *b, const char *state)
{
    handle_edit_undo(b);
    g_s.handle_unconfirmed = 1;
    handles_done(state);
}
static void handle_list_done(BYTE *b, int r)
{
    pol_handle h;
    int there = handle_get(b, g_s.handle_slot, &h);
    if (r == 1 && g_s.handle_reload) {
        handles_done("failed:reloaded");
    } else if (r == 1 && (g_s.handle_op == HOP_CREATE ? there && handle_hid(&h) : !there)) {
        handles_done("saved");
    } else if (r == HANDLE_LIST_STALE && g_s.handle_op == HOP_CREATE) {
        handle_create_release(b);
        handles_done(requests_failed(r));
    } else {
        handle_list_failed(b, r == 1 ? "failed:not saved" : requests_failed(r));
    }
}
static void handle_list_put_start(BYTE *b)
{
    int reload = pc_handle_list_server_newer(b);
    int t = pc_handles_save(b);
    g_s.handle_want = t == NO_SLOT;
    if (g_s.handle_want) return;
    base_log("handles: handle list upload -> %d%s\r\n", t, reload ? " (the server list is newer: reload)" : "");
    g_s.handle_reload = reload;
    if (t < 0) { handle_list_failed(b, requests_failed(t)); return; }
    g_s.handle_job = t;
}
static void handle_order_done(BYTE *b, int r)
{
    pol_chr c;
    int kept = 1;
    if (r != 1) {
        handle_order_back(b, g_hord.n);
        handles_done(requests_failed(r));
        return;
    }
    for (int i = 0; i < g_hord.n; i++)
        if (!peers_get_chr(b, g_hord.idx[i], &c) || (BYTE)c.link != g_hord.want[i]) kept = 0;
    handles_done(g_s.handle_reload ? "failed:reloaded" : kept ? "saved" : "failed:not saved");
}
static void handle_edit_done(BYTE *b, int r)
{
    if (g_s.handle_op == HOP_ORDER) handle_order_done(b, r); else handle_list_done(b, r);
}

static int handle_list_busy(BYTE *b) { return g_me.step != 0 || mail_send_slots_free(b) < 1; }
static void handle_edit_started(int op, int slot)
{
    g_s.handle_op = op;
    g_s.handle_slot = slot;
    g_s.handle_idx = g_s.handle_from = g_s.handle_pos = -1;
    lstrcpyA(g_s.handle_state, "working");
    g_snap_dirty = 1;
}

static void handle_create(BYTE *b, char *p)
{
    WCHAR wname[32];
    BYTE id[16];
    char sj[64], *name = requests_line(&p);
    pol_handle h;
    int used = 0, top = -1, len, ng, slot;
    if (g_s.handle_unconfirmed) { handle_fail("failed:unconfirmed"); return; }
    for (int i = 0; i < 64; i++) {
        if (!handle_get(b, i, &h)) continue;
        used++;
        if ((int)((h.flags >> 2) & 0x3f) > top) top = (int)((h.flags >> 2) & 0x3f);
    }
    if (used >= 64) { handle_fail("failed:handles full"); return; }
    memset(wname, 0, sizeof wname);
    if (!name[0] || MultiByteToWideChar(CP_UTF8, 0, name, -1, wname, 31) <= 0) {
        handle_fail("failed:bad name");
        return;
    }
    ng = my_profile_handle_name_check(b, wname, sj, &len);
    if (ng == HR_E_NAME) { handle_fail("failed:bad name"); return; }
    if (ng == HR_E_NGWORD) { handle_fail("failed:word"); return; }
    if (ng) { handle_fail("failed:no dictionary"); return; }
    if (handle_list_busy(b)) { handle_fail("failed:busy"); return; }
    memset(id, 0, sizeof id);
    memcpy(id, sj, (size_t)len);
    slot = pc_claim_handle(b, -1, 1, id);
    if (slot < 0) { handle_fail(requests_failed(slot)); return; }
    /* a reused slot keeps its old order, so it's put after the others. The viewer does this in a second upload when its
       list closes, here it's in the same one */
    if (top >= 0 && top < 63 && handle_get(b, slot, &h)) {
        h.flags = (h.flags & ~(0x3fu << 2)) | (DWORD)(top + 1) << 2;
        pc_set_handle(b, (unsigned)slot, &h);
    }
    handle_edit_started(HOP_CREATE, slot);
    base_log("handles: create '%s' in slot %d\r\n", sj, slot);
    handle_list_put_start(b);
}

static void handle_delete(BYTE *b, int slot, const pol_handle *h)
{
    pol_handle o;
    char name[16];
    int others = 0, r;
    if (g_s.handle_unconfirmed) { handle_fail("failed:unconfirmed"); return; }
    if (slot == pc_current_handle(b) || slot == g_my_slot) { handle_fail("failed:active"); return; }
    for (int i = 0; i < 64; i++) others += i != slot && handle_get(b, i, &o);
    if (!others) { handle_fail("failed:active"); return; }
    if (handle_list_busy(b)) { handle_fail("failed:busy"); return; }
    memcpy(name, h->name, 15);
    name[15] = 0;
    base_log("handles: delete slot %d '%s'\r\n", slot, name);
    r = pc_release_handle(b, slot);
    if (r < 0) { handle_fail(requests_failed(r)); return; }
    handle_edit_started(HOP_DELETE, slot);
    handle_list_put_start(b);
}

static void handle_order(BYTE *b, char *p)
{
    BYTE had[256], has[256], now[64];
    char in_use[64], named[64], k[17], *ix, *ck, *ov;
    pol_chr c;
    int n = 0, total = 0, same = 1, packed = 1, idx, val, r;
    memset(named, 0, sizeof named);
    memset(had, 0, sizeof had);
    memset(has, 0, sizeof has);
    for (int i = 0; i < 64; i++) {
        in_use[i] = (char)peers_get_chr(b, i, &c);
        now[i] = (BYTE)c.link;
    }
    while (*(ix = requests_line(&p))) {
        ck = requests_line(&p); ov = requests_line(&p);
        if (n == 64 || !requests_parse_num(ix, 2, &idx) || idx > 63 || named[idx] ||
            !requests_parse_num(ov, 3, &val) || val > 255) {
            handle_fail("failed:bad request");
            return;
        }
        if (!in_use[idx] || !peers_get_chr(b, idx, &c)) { handle_fail("failed:list changed"); return; }
        peers_chr_key(&c, k);
        if (lstrcmpA(k, ck) != 0) { handle_fail("failed:list changed"); return; }
        named[idx] = 1;
        g_hord.idx[n] = idx;
        g_hord.old[n] = now[idx];
        g_hord.want[n] = (BYTE)val;
        now[idx] = (BYTE)val;
        n++;
    }
    if (!n) { handle_fail("failed:bad request"); return; }
    for (int i = 0; i < 64; i++) {
        if (!in_use[i]) continue;
        total++;
        if (has[now[i]]++) { handle_fail("failed:bad request"); return; }
    }
    for (int i = 0; i < n; i++) had[g_hord.old[i]]++;
    for (int i = 0; i < 64; i++)
        if (in_use[i] && !named[i]) had[now[i]]++;
    for (int v = 0; v < 256; v++) {
        if (had[v] != has[v]) same = 0;
        if (has[v] != (v < total)) packed = 0;
    }
    if (!same && !packed) { handle_fail("failed:bad request"); return; }
    for (int i = 0; i < n; i++) {
        r = peers_get_chr(b, g_hord.idx[i], &c) ? 0 : -1;
        c.link = (c.link & ~0xffu) | g_hord.want[i];
        if (r == 0) r = pc_set_content_id(b, (unsigned)g_hord.idx[i], &c);
        if (r < 0) {
            handle_order_back(b, i);
            handle_fail(requests_failed(r));
            return;
        }
    }
    g_hord.n = n;
    handle_edit_started(HOP_ORDER, -1);
    base_log("handles: order of %d Content ID(s)\r\n", n);
    handles_put_start(b);
}
