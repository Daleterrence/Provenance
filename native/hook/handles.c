/* handles and Content IDs, listed from memory (svc 0x2b4/0x2c4). A switch is the viewer's one ChangeMyStatus (app.dll
   0x10040c9e), a link a local 0x2dc (0x2e0 first for a move) and then one PutChrList (0x1038) */
enum { HOP_NONE, HOP_SWITCH, HOP_LINK, HOP_LINKALL, HOP_CREATE, HOP_DELETE, HOP_ORDER };
static int g_hall[8], g_hall_n;
static int g_in_hold;
static int hold_writes_pending(void);
static void handle_create(BYTE *b, char *p);
static void handle_delete(BYTE *b, int slot, const pol_handle *h);
static void handle_order(BYTE *b, char *p);
static void handle_list_put_start(BYTE *b);
static void handle_edit_undo(BYTE *b);
static void handle_edit_done(BYTE *b, int r);

static int handles_switching(void) { return g_s.handle_op == HOP_SWITCH; }
static int handles_list_edit(void) { return g_s.handle_op == HOP_CREATE || g_s.handle_op == HOP_DELETE; }

#define handle_fail(why) requests_refuse(g_s.handle_state, sizeof g_s.handle_state, "handles: refused - %s\r\n", why)
static void handles_done(const char *state)
{
    lstrcpynA(g_s.handle_state, state, sizeof g_s.handle_state);
    base_log("handles: %s\r\n", state);
    g_s.handle_op = HOP_NONE;
    g_s.handle_job = -1;
    g_s.handle_want = 0;
    g_snap_dirty = 1;
}
static void handles_switch_start(BYTE *b)
{
    int t = pc_change_my_status(b, g_s.handle_slot, -2, 1, -2, 0, -2);
    g_s.handle_want = t == NO_SLOT;
    if (g_s.handle_want) return;
    base_log("handles: ChangeMyStatus(%d,-2,1,-2,0,-2) -> ticket %d\r\n", g_s.handle_slot, t);
    if (t < 0) { handles_done(requests_failed(t)); return; }
    g_s.handle_job = t;
}
/* polcore links into the first free slot of 8, not where it was, so it's put back by hand in the records
   (Content IDs polcore.dll 0x10403080, handles 0x10405800) */
static void handles_place(BYTE *b, int idx, int slot, int pos)
{
    BYTE *hs = pc_handle_record(b, slot)->chars;
    DWORD *f = &pc_content_record(b, idx)->link;
    int now = (int)CHR_PLACE(*f);
    if (pos < 0 || pos > 7 || now == pos || !(*f & CHR_LINKED) || (int)CHR_HANDLE(*f) != slot || (hs[pos] & 1) ||
        !(hs[now] & 1) || ((hs[now] >> 1) & 0x3f) != idx)
        return;
    hs[pos] = hs[now];
    hs[now] &= 0xfe;   /* what polcore's own unlink leaves (polcore.dll 0x10022d00) */
    *f = (*f & ~(7u << 22)) | (DWORD)pos << 22;
}
/* a put that didn't go puts polcore back how the server has it, which costs nothing (no reload, the viewer doesn't) */
static void handles_undo(BYTE *b)
{
    if (g_s.handle_op == HOP_LINKALL) {
        for (int i = 0; i < g_hall_n; i++) pc_unlink_content(b, g_hall[i], g_s.handle_slot);
    } else if (g_s.handle_op == HOP_LINK) {
        pc_unlink_content(b, g_s.handle_idx, g_s.handle_slot);
        if (g_s.handle_from >= 0 && pc_link_content(b, g_s.handle_idx, g_s.handle_from) >= 0) handles_place(b, g_s.handle_idx, g_s.handle_from, g_s.handle_pos);
    } else {
        handle_edit_undo(b);
    }
}
/* the put only goes when polcore's stamps say the server's list isn't newer, else it quietly loads instead and
   still says 1 (polcore.dll 0x10023350) */
static void handles_put_start(BYTE *b)
{
    int reload = pc_links_server_newer(b);
    int t = pc_links_save(b);
    g_s.handle_want = t == NO_SLOT;
    if (g_s.handle_want) return;
    base_log("handles: Content ID list upload -> %d%s\r\n", t, reload ? " (the server list is newer: reload)" : "");
    g_s.handle_reload = reload;
    if (t < 0) {
        handles_undo(b);
        handles_done(requests_failed(t));
        return;
    }
    g_s.handle_job = t;
}
static void handles_put_done(BYTE *b, int r)
{
    pol_chr c;
    DWORD f;
    int linked;
    if (r != 1) {
        handles_undo(b);
        handles_done(requests_failed(r));
        return;
    }
    /* a put that became a load already has the server's copy in polcore */
    f = peers_get_chr(b, g_s.handle_idx, &c) ? c.link : 0;
    linked = (f & CHR_LINKED) && (int)CHR_HANDLE(f) == g_s.handle_slot;
    for (int i = 0; g_s.handle_op == HOP_LINKALL && i < g_hall_n; i++) {
        f = peers_get_chr(b, g_hall[i], &c) ? c.link : 0;
        if (!(f & CHR_LINKED) || (int)CHR_HANDLE(f) != g_s.handle_slot) linked = 0;
    }
    if (g_s.handle_reload) handles_done("failed:reloaded");
    else if (!linked) handles_done("failed:not saved");
    else handles_done("saved");
}
/* my profile's the handle's, so a loaded one's the old handle's now */
static void handles_switched(BYTE *b)
{
    char jp[MAX_PATH + 64];
    g_my_slot = pc_current_handle(b);
    base_ipc_file(jp, IPC_MYPROFILE);
    DeleteFileA(jp);
    if (!lstrcmpA(g_s.me_state, "ready")) g_s.me_state[0] = 0;
    base_log("handles: active handle now slot %d\r\n", g_my_slot);
}

static int handles_writes_asked(void) { return requests_listed(RQ_ASKED); }

/* the viewer's link before a game (app.dll 0x100235d5): unlinked ones go on the active handle in one PutChrList.
   The app only sends what fits, so it's all or nothing */
static void handles_linkall(BYTE *b, int slot, const pol_handle *h, char *ix, char *ck, char *p)
{
    char k[17];
    pol_chr c;
    int idx[8], n = 0, r;
    if (slot != pc_current_handle(b)) { handle_fail("failed:not active"); return; }
    while (ix[0]) {
        if (n == 8 || !requests_parse_num(ix, 2, &idx[n]) || idx[n] > 63) { handle_fail("failed:bad request"); return; }
        for (int i = 0; i < n; i++) if (idx[i] == idx[n]) { handle_fail("failed:bad request"); return; }
        if (!peers_get_chr(b, idx[n], &c) || c.title != 1) { handle_fail("failed:list changed"); return; }
        peers_chr_key(&c, k);
        if (lstrcmpA(k, ck) != 0) { handle_fail("failed:list changed"); return; }
        if (c.link & CHR_LINKED) { handle_fail("failed:already linked"); return; }
        n++;
        ix = requests_line(&p); ck = requests_line(&p);
    }
    if (!n) { handle_fail("failed:bad request"); return; }
    if (peers_handle_used(h) + n > 8) { handle_fail("failed:handle full"); return; }
    for (int i = 0; i < n; i++) {
        r = pc_link_content(b, idx[i], slot);
        if (r < 0) {
            while (i-- > 0) pc_unlink_content(b, idx[i], slot);
            handle_fail(requests_failed(r));
            return;
        }
    }
    g_s.handle_op = HOP_LINKALL;
    g_s.handle_slot = slot;
    g_s.handle_idx = idx[0];
    g_s.handle_from = g_s.handle_pos = -1;
    memcpy(g_hall, idx, sizeof idx);
    g_hall_n = n;
    lstrcpyA(g_s.handle_state, "working");
    base_log("handles: %d unlinked content index(es) linked to the active slot %d\r\n", n, slot);
    g_snap_dirty = 1;
    handles_put_start(b);
}

/* <op>, token, then lines each op reads for itself (see handle_create, handle_order and below).
   The keys are the snapshot's, so a request can't land on a record that changed */
static void handles_request(BYTE *b)
{
    char buf[2048], *p, *op, *tok, *sl, *hk, *ix, *ck, k[17];
    pol_handle h;
    pol_chr c;
    int slot, idx = -1, kind, cur, old = -1, r, n;
    DWORD f;
    /* a background character lookup finishes first, rather than the switch being turned away as busy */
    if (g_ch_read.j.step) return;
    if ((n = requests_take(REQ_HANDLE, buf, sizeof buf)) < 0) return;
    p = buf;
    op = requests_line(&p); tok = requests_line(&p);
    lstrcpynA(g_s.handle_token, tok, sizeof g_s.handle_token);
    kind = !lstrcmpA(op, "switch") ? HOP_SWITCH
         : !lstrcmpA(op, "link") ? HOP_LINK
         : !lstrcmpA(op, "linkall") ? HOP_LINKALL
         : !lstrcmpA(op, "create") ? HOP_CREATE
         : !lstrcmpA(op, "delete") ? HOP_DELETE
         : !lstrcmpA(op, "order") ? HOP_ORDER : 0;
    /* one that fills the buffer was cut short on the way in, and its last line can't be trusted */
    if (!kind || !tok[0] || n >= (int)sizeof buf - 1) { handle_fail("failed:bad request"); return; }
    if (!g_in_hold) { handle_fail("failed:not in hold"); return; }
    if (g_s.draining) { handle_fail("failed:draining"); return; }
    if (kind == HOP_CREATE) { handle_create(b, p); return; }
    if (kind == HOP_ORDER) { handle_order(b, p); return; }
    sl = requests_line(&p); hk = requests_line(&p); ix = requests_line(&p); ck = requests_line(&p);
    if (!requests_parse_num(sl, 2, &slot) || slot > 63 ||
        (kind == HOP_LINK && (!requests_parse_num(ix, 2, &idx) || idx > 63))) {
        handle_fail("failed:bad request");
        return;
    }
    if (!handle_get(b, slot, &h)) { handle_fail("failed:list changed"); return; }
    peers_hid_key(handle_hid(&h), k);
    if (lstrcmpA(k, hk) != 0) { handle_fail("failed:list changed"); return; }
    if (kind == HOP_DELETE) { handle_delete(b, slot, &h); return; }
    if (kind == HOP_SWITCH) {
        cur = pc_current_handle(b);
        if (slot == cur) { handle_fail("failed:already active"); return; }
        /* they went under the old handle, some with its slot in them, and a read's answer is what the old handle
           was allowed to see */
        if ((requests_busy() & (JB_WRITE | JB_SWITCH)) || handles_writes_asked()) {
            handle_fail("failed:busy");
            return;
        }
        g_s.handle_op = HOP_SWITCH;
        g_s.handle_slot = slot;
        lstrcpyA(g_s.handle_state, "working");
        base_log("handles: switch from slot %d to slot %d\r\n", cur, slot);
        g_snap_dirty = 1;
        /* the viewer quits private chat before a switch, so a room's left first, through the one PART there is */
        if (chat_room_open()) chat_room_end(b);
        if (group_chat_open() || group_chat_joined(b)) group_chat_end(b, "left:handle", 0);
        handles_switch_start(b);
        if (handles_switching()) handles_writes_refuse();
        return;
    }
    if (kind == HOP_LINKALL) { handles_linkall(b, slot, &h, ix, ck, p); return; }
    if (!peers_get_chr(b, idx, &c) || c.title != 1) { handle_fail("failed:list changed"); return; }
    peers_chr_key(&c, k);
    if (lstrcmpA(k, ck) != 0) { handle_fail("failed:list changed"); return; }
    f = c.link;
    if ((f & CHR_LINKED) && (int)CHR_HANDLE(f) == slot) { handle_fail("failed:already linked"); return; }
    if (peers_handle_used(&h) >= 8) { handle_fail("failed:handle full"); return; }
    /* polcore won't link one that's linked anywhere, so a move's an unlink first. One linked to a slot with no handle
       can't be unlinked (-0x1c04), so it's left */
    if (f & CHR_LINKED) {
        pol_handle oh;
        old = (int)CHR_HANDLE(f);
        if (!handle_get(b, old, &oh)) { handle_fail("failed:orphan"); return; }
        r = pc_unlink_content(b, idx, old);
        if (r < 0) { handle_fail(requests_failed(r)); return; }
    }
    r = pc_link_content(b, idx, slot);
    if (r < 0) {
        if (old >= 0) pc_link_content(b, idx, old);
        handle_fail(requests_failed(r));
        return;
    }
    g_s.handle_op = HOP_LINK;
    g_s.handle_slot = slot;
    g_s.handle_idx = idx;
    g_s.handle_from = old;
    g_s.handle_pos = (f & CHR_LINKED) ? (int)((f >> 22) & 7) : -1;
    lstrcpyA(g_s.handle_state, "working");
    base_log("handles: link content index %d to slot %d%s\r\n", idx, slot, old >= 0 ? " (moved)" : "");
    g_snap_dirty = 1;
    handles_put_start(b);
}
static void handles_pump(BYTE *b, int ctl)
{
    int r;
    if (g_s.handle_op == HOP_NONE) {
        if (ctl && !g_lean) handles_request(b);
        return;
    }
    if (g_s.handle_want) {
        /* a switch still waiting for a slot isn't worth starting once the game or a log out is on its way */
        if (g_s.handle_op == HOP_SWITCH && g_s.draining) handles_done("failed:draining");
        else if (g_s.handle_op == HOP_SWITCH) handles_switch_start(b);
        else if (handles_list_edit()) handle_list_put_start(b);
        else handles_put_start(b);   /* applied locally already, so it still goes in a drain */
        return;
    }
    if (g_s.handle_job < 0) return;
    r = g_s.handle_op == HOP_SWITCH ? pc_change_my_status_check(b, g_s.handle_job)
      : handles_list_edit() ? pc_handles_save_check(b, g_s.handle_job) : pc_links_save_check(b, g_s.handle_job);
    if (r == 0) return;
    g_s.handle_job = -1;
    if (handles_list_edit() || g_s.handle_op == HOP_ORDER) { handle_edit_done(b, r); return; }
    if (g_s.handle_op != HOP_SWITCH) { handles_put_done(b, r); return; }
    if (r == 1) {
        handles_switched(b);
        handles_done("saved");
    } else {
        handles_done(requests_failed(r));
    }
}
/* Play with a change still going: FFXI's world login reads polcore's records, so a link the server never confirmed
   is taken back first */
static void handles_abandon(BYTE *b)
{
    if (g_s.handle_op == HOP_NONE) return;
    if (g_s.handle_job >= 0) pc_cancel_job(b, g_s.handle_job);
    if (handles_list_edit()) g_s.handle_unconfirmed = 1;
    if (g_s.handle_op != HOP_SWITCH) handles_undo(b);
    handles_done("failed:cancelled");
}
enum { HANDLES_NAME_CAP = 64, HANDLES_TEXT_CAP = 200 };
static void handles_json(BYTE *b, jw *w)
{
    char k[17];
    pol_handle hd;
    pol_chr rec;
    int cur = pc_current_handle_or(b, -1), i, j;
    DWORD f;
    jw_job(w, "handleJob", g_s.handle_token, g_s.handle_state);
    if (g_s.handle_op == HOP_SWITCH) jw_lit(w, "op", "switch");
    else if (g_s.handle_op == HOP_LINK) jw_lit(w, "op", "link");
    else if (g_s.handle_op == HOP_LINKALL) jw_lit(w, "op", "linkall");
    else if (g_s.handle_op == HOP_CREATE) jw_lit(w, "op", "create");
    else if (g_s.handle_op == HOP_DELETE) jw_lit(w, "op", "delete");
    else if (g_s.handle_op == HOP_ORDER) jw_lit(w, "op", "order");
    if (g_s.handle_unconfirmed) jw_bool(w, "unconfirmed", 1);
    jw_end(w);
    jw_arr(w, "handles");
    for (i = 0; i < 64; i++) {
        if (!handle_get(b, i, &hd)) continue;
        peers_hid_key(handle_hid(&hd), k);
        jw_obj(w, NULL);
        jw_int(w, "slot", i);
        jw_sjis(w, "name", b, (const BYTE *)hd.name, 0x0f, HANDLES_NAME_CAP);
        jw_lit(w, "key", k);
        jw_uint(w, "pic", hd.pic);
        jw_uint(w, "order", (hd.flags >> 2) & 0x3f);
        jw_bool(w, "active", i == cur);
        jw_arr(w, "contentIds");
        for (j = 0; j < 8; j++)
            if (hd.chars[j] & 1) jw_uint(w, NULL, (hd.chars[j] >> 1) & 0x3f);
        jw_end(w);
        jw_end(w);
    }
    jw_end(w);
    jw_arr(w, "contentIds");
    for (i = 0; i < 64; i++) {
        if (!peers_get_chr(b, i, &rec)) continue;
        f = rec.link;
        peers_chr_key(&rec, k);
        jw_obj(w, NULL);
        jw_int(w, "index", i);
        jw_uint(w, "type", rec.title);
        jw_sjis(w, "name", b, (const BYTE *)rec.name, 0x0f, HANDLES_NAME_CAP);
        jw_sjis(w, "text", b, (const BYTE *)rec.text, 0x3f, HANDLES_TEXT_CAP);
        jw_uint(w, "order", f & 0xff);
        jw_int(w, "handle", (f & CHR_LINKED) ? (int)CHR_HANDLE(f) : -1);
        jw_lit(w, "key", k);
        jw_end(w);
    }
    jw_end(w);
}
