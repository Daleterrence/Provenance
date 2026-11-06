/* handles and Content IDs, listed from memory (svc 0x2b4/0x2c4). A switch is the viewer's one ChangeMyStatus (app.dll
   0x10040c9e), a link or unlink a local 0x2dc/0x2e0 and then one PutChrList (0x1038) */
enum { HOP_NONE, HOP_SWITCH, HOP_LINK, HOP_UNLINK, HOP_LINKALL };
static int g_hall[8], g_hall_n;
static int g_in_hold;
static int writes_pending(void);

static int handle_switching(void) { return g_s.hop == HOP_SWITCH; }

#define handle_fail(why) refuse(g_s.hstate, sizeof g_s.hstate, "handles: refused - %s\r\n", why)
static void handle_done(const char *state)
{
    lstrcpynA(g_s.hstate, state, sizeof g_s.hstate);
    lg("handles: %s\r\n", state);
    g_s.hop = HOP_NONE;
    g_s.hjob = -1;
    g_s.hwant = 0;
    g_snap_dirty = 1;
}
static void handle_switch_start(BYTE *b)
{
    int t = pc_change_my_status(b, g_s.hslot, -2, 1, -2, 0, -2);
    g_s.hwant = t == NO_SLOT;
    if (g_s.hwant) return;
    lg("handles: ChangeMyStatus(%d,-2,1,-2,0,-2) -> ticket %d\r\n", g_s.hslot, t);
    if (t < 0) { handle_done(failed(t)); return; }
    g_s.hjob = t;
}
/* polcore links into a handle's first free slot of 8, not where it was, so it's put back by hand in the records
   (Content IDs at polcore.dll 0x10403080, handles 0x10405800) on this thread */
static void handle_place(BYTE *b, int idx, int slot, int pos)
{
    BYTE *hs = handle_record(b, slot)->chars;
    DWORD *f = &content_record(b, idx)->link;
    int now = (int)CHR_PLACE(*f);
    if (pos < 0 || pos > 7 || now == pos || !(*f & CHR_LINKED) || (int)CHR_HANDLE(*f) != slot || (hs[pos] & 1) ||
        !(hs[now] & 1) || ((hs[now] >> 1) & 0x3f) != idx)
        return;
    hs[pos] = hs[now];
    hs[now] &= 0xfe;   /* what polcore's own unlink leaves (polcore.dll 0x10022d00) */
    *f = (*f & ~(7u << 22)) | (DWORD)pos << 22;
}
/* a put that didn't go puts polcore back how the server has it, which costs nothing (no reload, the viewer doesn't) */
static void handle_undo(BYTE *b)
{
    if (g_s.hop == HOP_LINKALL) {
        for (int i = 0; i < g_hall_n; i++) pc_unlink_content(b, g_hall[i], g_s.hslot);
    } else if (g_s.hop == HOP_LINK) {
        pc_unlink_content(b, g_s.hidx, g_s.hslot);
        if (g_s.hfrom >= 0 && pc_link_content(b, g_s.hidx, g_s.hfrom) >= 0) handle_place(b, g_s.hidx, g_s.hfrom, g_s.hpos);
    } else if (pc_link_content(b, g_s.hidx, g_s.hslot) >= 0) {
        handle_place(b, g_s.hidx, g_s.hslot, g_s.hpos);
    }
}
/* the put only goes when polcore's stamps say the server's list isn't newer, else it quietly loads instead and
   still says 1 (polcore.dll 0x10023350) */
static void handle_put_start(BYTE *b)
{
    int reload = handles_server_newer(b);
    int t = pc_links_save(b);
    g_s.hwant = t == NO_SLOT;
    if (g_s.hwant) return;
    lg("handles: Content ID list upload -> %d%s\r\n", t, reload ? " (the server list is newer: reload)" : "");
    g_s.hreload = reload;
    if (t < 0) {
        handle_undo(b);
        handle_done(failed(t));
        return;
    }
    g_s.hjob = t;
}
static void handle_put_done(BYTE *b, int r)
{
    pol_chr c;
    DWORD f;
    int linked;
    if (r != 1) {
        handle_undo(b);
        handle_done(failed(r));
        return;
    }
    /* a put that became a load already has the server's copy in polcore */
    f = get_chr(b, g_s.hidx, &c) ? c.link : 0;
    linked = (f & CHR_LINKED) && (int)CHR_HANDLE(f) == g_s.hslot;
    for (int i = 0; g_s.hop == HOP_LINKALL && i < g_hall_n; i++) {
        f = get_chr(b, g_hall[i], &c) ? c.link : 0;
        if (!(f & CHR_LINKED) || (int)CHR_HANDLE(f) != g_s.hslot) linked = 0;
    }
    if (g_s.hreload) handle_done("failed:reloaded");
    else if (linked != (g_s.hop != HOP_UNLINK)) handle_done("failed:not saved");
    else handle_done("saved");
}
/* my profile's the handle's, so a loaded one's the old handle's now */
static void handle_switched(BYTE *b)
{
    char jp[MAX_PATH + 64];
    g_my_slot = pc_current_handle(b);
    ipc_file(jp, IPC_MYPROFILE);
    DeleteFileA(jp);
    if (!lstrcmpA(g_s.me_state, "ready")) g_s.me_state[0] = 0;
    lg("handles: active handle now slot %d\r\n", g_my_slot);
}

/* while a switch runs, a write is taken and answered as not done, it'd go from the new handle otherwise. A chat
   enter waiting when the switch is taken is called off the same way */
#define SWITCHING "failed:handle changing"
static void writes_refuse(BYTE *b)
{
    static const char *const ext[] = { REQ_SEND, REQ_FRIEND, REQ_COMMENT, REQ_ANSWER, REQ_GROUP, REQ_PIC, REQ_STATUS, REQ_MYPROFILE,
                                       REQ_SAVEPROFILE, REQ_RENAME, REQ_CHATJOIN, REQ_CHATSEND, REQ_CHATCREATE, REQ_CHATMASTER,
                                       REQ_GROUPCHATJOIN, REQ_GROUPCHATSEND, REQ_XIPROFILE };
    char buf[512], *p, *l1, *l2, *l3;
    for (int i = 0; i < (int)(sizeof ext / sizeof ext[0]); i++) {
        if (take_request(ext[i], buf, sizeof buf) < 0) continue;
        lg("handles: %s request refused - the handle's changing\r\n", ext[i]);
        p = buf;
        l1 = req_line(&p); l2 = req_line(&p); l3 = req_line(&p);
        switch (i) {
        case 0: case 3: lstrcpynA(g_s.send_token, l2, sizeof g_s.send_token); send_fail(SWITCHING); break;
        case 1: lstrcpynA(g_s.edit_token, l2, sizeof g_s.edit_token); edit_fail(SWITCHING); break;
        case 2: lstrcpynA(g_s.edit_token, l1, sizeof g_s.edit_token); edit_fail(SWITCHING); break;
        case 4: lstrcpynA(g_s.grp_token, l2, sizeof g_s.grp_token); group_fail(SWITCHING); break;
        case 5: lstrcpynA(g_s.pic_token, l2, sizeof g_s.pic_token); pic_fail(SWITCHING); break;
        case 7: case 8: case 9: lstrcpynA(g_s.me_token, l1, sizeof g_s.me_token); me_done(SWITCHING); break;
        case 10: chat_join_refused(l1, l3); break;
        case 11: chat_send_refused(l1); break;
        case 12: chat_create_refused(l1, l2); break;
        case 13: chat_master_refused(l1, l2); break;
        case 14: gc_join_refused(l1, SWITCHING); break;
        case 15: gc_send_refused(l1, SWITCHING); break;
        case 16: lstrcpynA(g_s.xi_token, l1, sizeof g_s.xi_token); xi_done(SWITCHING); break;
        }
    }
}
/* written by the app for the handle that's active now, but not taken yet */
static int writes_asked(void)
{
    static const char *const ext[] = { REQ_SEND, REQ_FRIEND, REQ_COMMENT, REQ_ANSWER, REQ_GROUP, REQ_PIC, REQ_STATUS };
    for (int i = 0; i < (int)(sizeof ext / sizeof ext[0]); i++) if (request_waiting(ext[i])) return 1;
    return 0;
}

/* the viewer's link before it starts a game (app.dll 0x100235d5): unlinked ones go on the active handle, all in one
   PutChrList. The app only sends what fits, so it's all or nothing here */
static void handle_linkall(BYTE *b, int slot, const pol_handle *h, char *ix, char *ck, char *p)
{
    char k[17];
    pol_chr c;
    int idx[8], n = 0, r;
    if (slot != pc_current_handle(b)) { handle_fail("failed:not active"); return; }
    while (ix[0]) {
        if (n == 8 || !parse_num(ix, 2, &idx[n]) || idx[n] > 63) { handle_fail("failed:bad request"); return; }
        for (int i = 0; i < n; i++) if (idx[i] == idx[n]) { handle_fail("failed:bad request"); return; }
        if (!get_chr(b, idx[n], &c) || c.title != 1) { handle_fail("failed:list changed"); return; }
        chr_key(&c, k);
        if (lstrcmpA(k, ck) != 0) { handle_fail("failed:list changed"); return; }
        if (c.link & CHR_LINKED) { handle_fail("failed:already linked"); return; }
        n++;
        ix = req_line(&p); ck = req_line(&p);
    }
    if (!n) { handle_fail("failed:bad request"); return; }
    if (handle_used(h) + n > 8) { handle_fail("failed:handle full"); return; }
    for (int i = 0; i < n; i++) {
        r = pc_link_content(b, idx[i], slot);
        if (r < 0) {
            while (i-- > 0) pc_unlink_content(b, idx[i], slot);
            handle_fail(failed(r));
            return;
        }
    }
    g_s.hop = HOP_LINKALL;
    g_s.hslot = slot;
    g_s.hidx = idx[0];
    g_s.hfrom = g_s.hpos = -1;
    memcpy(g_hall, idx, sizeof idx);
    g_hall_n = n;
    lstrcpyA(g_s.hstate, "working");
    lg("handles: %d unlinked content index(es) linked to the active slot %d\r\n", n, slot);
    g_snap_dirty = 1;
    handle_put_start(b);
}

/* <op> switch|link|unlink|linkall, token, slot, handle key, then index/key pairs. The keys are the snapshot's, so a
   request never lands on a record that changed */
static void handle_request(BYTE *b)
{
    char buf[512], *p, *op, *tok, *sl, *hk, *ix, *ck, k[17];
    pol_handle h;
    pol_chr c;
    int slot, idx = -1, kind, cur, old = -1, r;
    DWORD f;
    /* a background character lookup finishes first, rather than the switch being turned away as busy */
    if (g_s.ch_step) return;
    if (take_request(REQ_HANDLE, buf, sizeof buf) < 0) return;
    p = buf;
    op = req_line(&p); tok = req_line(&p); sl = req_line(&p); hk = req_line(&p); ix = req_line(&p); ck = req_line(&p);
    lstrcpynA(g_s.htoken, tok, sizeof g_s.htoken);
    kind = !lstrcmpA(op, "switch") ? HOP_SWITCH : !lstrcmpA(op, "link") ? HOP_LINK : !lstrcmpA(op, "unlink") ? HOP_UNLINK
         : !lstrcmpA(op, "linkall") ? HOP_LINKALL : 0;
    if (!kind || !tok[0] || !parse_num(sl, 2, &slot) || slot > 63 ||
        (kind != HOP_SWITCH && kind != HOP_LINKALL && (!parse_num(ix, 2, &idx) || idx > 63))) {
        handle_fail("failed:bad request");
        return;
    }
    if (!g_in_hold) { handle_fail("failed:not in hold"); return; }
    if (g_s.draining) { handle_fail("failed:draining"); return; }
    if (!handle_get(b, slot, &h)) { handle_fail("failed:list changed"); return; }
    hid_key(handle_hid(&h), k);
    if (lstrcmpA(k, hk) != 0) { handle_fail("failed:list changed"); return; }
    if (kind == HOP_SWITCH) {
        cur = pc_current_handle(b);
        if (slot == cur) { handle_fail("failed:already active"); return; }
        /* they went under the old handle, some with its slot in them, and a read's answer is what the old handle
           was allowed to see */
        if (writes_pending() || writes_asked() || g_s.me_step || g_s.status_want >= 0 || g_s.srch_step || g_s.prof_step ||
            g_s.fp_step || g_s.xi_step || g_s.cr_db) {
            handle_fail("failed:busy");
            return;
        }
        g_s.hop = HOP_SWITCH;
        g_s.hslot = slot;
        lstrcpyA(g_s.hstate, "working");
        lg("handles: switch from slot %d to slot %d\r\n", cur, slot);
        g_snap_dirty = 1;
        /* the viewer quits private chat before a switch, so a room's left first, through the one PART there is */
        if (chat_room_open()) chat_end(b);
        if (gc_open() || gc_joined(b)) gc_end(b, "left:handle", 0);
        handle_switch_start(b);
        if (handle_switching()) writes_refuse(b);
        return;
    }
    if (kind == HOP_LINKALL) { handle_linkall(b, slot, &h, ix, ck, p); return; }
    if (!get_chr(b, idx, &c) || c.title != 1) { handle_fail("failed:list changed"); return; }
    chr_key(&c, k);
    if (lstrcmpA(k, ck) != 0) { handle_fail("failed:list changed"); return; }
    f = c.link;
    if (kind == HOP_LINK) {
        if ((f & CHR_LINKED) && (int)CHR_HANDLE(f) == slot) { handle_fail("failed:already linked"); return; }
        if (handle_used(&h) >= 8) { handle_fail("failed:handle full"); return; }
        /* polcore won't link one that's linked anywhere, so a move's an unlink first, like the viewer's. One linked to
           a slot with no handle can't be unlinked either (-0x1c04), so it's left as it is */
        if (f & CHR_LINKED) {
            pol_handle oh;
            old = (int)CHR_HANDLE(f);
            if (!handle_get(b, old, &oh)) { handle_fail("failed:orphan"); return; }
            r = pc_unlink_content(b, idx, old);
            if (r < 0) { handle_fail(failed(r)); return; }
        }
        r = pc_link_content(b, idx, slot);
        if (r < 0) {
            if (old >= 0) pc_link_content(b, idx, old);
            handle_fail(failed(r));
            return;
        }
    } else {
        if (!(f & CHR_LINKED) || (int)CHR_HANDLE(f) != slot) { handle_fail("failed:not linked"); return; }
        r = pc_unlink_content(b, idx, slot);
        if (r < 0) { handle_fail(failed(r)); return; }
    }
    g_s.hop = kind;
    g_s.hslot = slot;
    g_s.hidx = idx;
    g_s.hfrom = old;
    g_s.hpos = (f & CHR_LINKED) ? (int)((f >> 22) & 7) : -1;
    lstrcpyA(g_s.hstate, "working");
    lg("handles: %s content index %d %s slot %d%s\r\n", kind == HOP_LINK ? "link" : "unlink", idx,
       kind == HOP_LINK ? "to" : "from", slot, old >= 0 ? " (moved)" : "");
    g_snap_dirty = 1;
    handle_put_start(b);
}
static void handle_pump(BYTE *b, int ctl)
{
    int r;
    if (g_s.hop == HOP_NONE) {
        if (ctl && !g_lean) handle_request(b);
        return;
    }
    if (g_s.hwant) {
        /* a switch still waiting for a slot isn't worth starting once the game or a log out is on its way */
        if (g_s.hop == HOP_SWITCH && g_s.draining) handle_done("failed:draining");
        else if (g_s.hop == HOP_SWITCH) handle_switch_start(b);
        else handle_put_start(b);   /* applied locally already, so it still goes in a drain */
        return;
    }
    if (g_s.hjob < 0) return;
    r = g_s.hop == HOP_SWITCH ? pc_change_my_status_check(b, g_s.hjob) : pc_links_save_check(b, g_s.hjob);
    if (r == 0) return;
    g_s.hjob = -1;
    if (g_s.hop != HOP_SWITCH) { handle_put_done(b, r); return; }
    if (r == 1) {
        handle_switched(b);
        handle_done("saved");
    } else {
        handle_done(failed(r));
    }
}
/* Play with a change still going: FFXI's world login reads polcore's records, so a link the server never confirmed
   is taken back first */
static void handle_abandon(BYTE *b)
{
    if (g_s.hop == HOP_NONE) return;
    if (g_s.hjob >= 0) pc_cancel_job(b, g_s.hjob);
    if (g_s.hop != HOP_SWITCH) handle_undo(b);
    handle_done("failed:cancelled");
}
static void handles_json(BYTE *b, HANDLE out)
{
    char line[900], nm[64], tx[200], k[17], ids[48];
    pol_handle hd;
    pol_chr rec;
    int cur = pc_has(b, SV_CURRENT_HANDLE) ? pc_current_handle(b) : -1, first = 1, i, j, o;
    DWORD f;
    wr_job(out, ",\"handleJob\":{\"token\":%s,\"state\":\"%s\"", g_s.htoken, g_s.hstate);
    wr(out, g_s.hop == HOP_SWITCH ? ",\"op\":\"switch\"}" : g_s.hop == HOP_LINK ? ",\"op\":\"link\"}"
                                : g_s.hop == HOP_UNLINK ? ",\"op\":\"unlink\"}" : g_s.hop == HOP_LINKALL ? ",\"op\":\"linkall\"}"
                                : "}");
    wr(out, ",\"handles\":[");
    for (i = 0; i < 64; i++) {
        if (!handle_get(b, i, &hd)) continue;
        sjis_json(nm, sizeof nm, (const BYTE *)hd.name, 0x0f);
        hid_key(handle_hid(&hd), k);
        for (o = 0, j = 0; j < 8; j++)
            if (hd.chars[j] & 1) o += wsprintfA(ids + o, "%s%u", o ? "," : "", (hd.chars[j] >> 1) & 0x3f);
        ids[o] = 0;
        wsprintfA(line, "%s{\"slot\":%d,\"name\":%s,\"key\":\"%s\",\"pic\":%u,\"order\":%u,\"active\":%s,"
                        "\"contentIds\":[%s]}",
                  first ? "" : ",", i, nm, k, hd.pic, (hd.flags >> 2) & 0x3f,
                  i == cur ? "true" : "false", ids);
        wr(out, line);
        first = 0;
    }
    wr(out, "],\"contentIds\":[");
    for (i = 0, first = 1; i < 64; i++) {
        if (!get_chr(b, i, &rec)) continue;
        f = rec.link;
        sjis_json(nm, sizeof nm, (const BYTE *)rec.name, 0x0f);
        sjis_json(tx, sizeof tx, (const BYTE *)rec.text, 0x3f);
        chr_key(&rec, k);
        wsprintfA(line, "%s{\"index\":%d,\"type\":%u,\"name\":%s,\"text\":%s,\"order\":%u,\"handle\":%d,\"key\":\"%s\"}",
                  first ? "" : ",", i, rec.title, nm, tx, f & 0xff, (f & CHR_LINKED) ? (int)CHR_HANDLE(f) : -1, k);
        wr(out, line);
        first = 0;
    }
    wr(out, "]");
}
