/* handles and Content IDs, listed from memory (svc 0x2b4/0x2c4). A switch is the viewer's one ChangeMyStatus (app.dll
   0x10040c9e), a link or unlink a local 0x2dc/0x2e0 and then one PutChrList (0x1038) */
#define CHR_LINKED 0x8000
#define CHR_HANDLE(f) (((f) >> 16) & 0x3f)
enum { HOP_NONE, HOP_SWITCH, HOP_LINK, HOP_UNLINK, HOP_LINKALL };
static int g_hop, g_hjob = -1, g_hwant, g_hslot = -1, g_hidx = -1, g_hfrom = -1, g_hpos = -1, g_hreload;
static int g_hall[8], g_hall_n;
static char g_htoken[48], g_hstate[48] = "";
static int g_in_hold;
static int writes_pending(void);

static int handle_switching(void) { return g_hop == HOP_SWITCH; }

#define handle_fail(why) refuse(g_hstate, sizeof g_hstate, "handles: refused - %s\r\n", why)
static void handle_done(const char *state)
{
    lstrcpynA(g_hstate, state, sizeof g_hstate);
    lg("handles: %s\r\n", state);
    g_hop = HOP_NONE;
    g_hjob = -1;
    g_hwant = 0;
    g_snap_dirty = 1;
}
static void handle_switch_start(BYTE *b)
{
    int t = ((int (__cdecl *)(int, int, int, int, int, int))SVC(0x30c))(g_hslot, -2, 1, -2, 0, -2);
    g_hwant = t == NO_SLOT;
    if (g_hwant) return;
    lg("handles: ChangeMyStatus(%d,-2,1,-2,0,-2) -> ticket %d\r\n", g_hslot, t);
    if (t < 0) { handle_done(failed(t)); return; }
    g_hjob = t;
}
/* polcore links into a handle's first free slot of 8, not where it was, so it's put back by hand in the records
   (Content IDs at polcore.dll 0x10403080, handles 0x10405800) on this thread */
static void handle_place(BYTE *b, int idx, int slot, int pos)
{
    BYTE *hs = b + 0x405820 + slot * 0x28;
    DWORD *f = (DWORD *)(b + 0x403090 + idx * 0x68);
    int now = (int)((*f >> 22) & 7);
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
    if (g_hop == HOP_LINKALL) {
        for (int i = 0; i < g_hall_n; i++) ((int (__cdecl *)(int, int))SVC(0x2e0))(g_hall[i], g_hslot);
    } else if (g_hop == HOP_LINK) {
        ((int (__cdecl *)(int, int))SVC(0x2e0))(g_hidx, g_hslot);
        if (g_hfrom >= 0 && ((int (__cdecl *)(int, int))SVC(0x2dc))(g_hidx, g_hfrom) >= 0) handle_place(b, g_hidx, g_hfrom, g_hpos);
    } else if (((int (__cdecl *)(int, int))SVC(0x2dc))(g_hidx, g_hslot) >= 0) {
        handle_place(b, g_hidx, g_hslot, g_hpos);
    }
}
/* the put only goes when polcore's stamps say the server's list isn't newer, else it quietly loads instead and
   still says 1 (polcore.dll 0x10023350) */
static void handle_put_start(BYTE *b)
{
    int reload = *(unsigned *)(b + 0x403048) > *(unsigned *)(b + 0x403054);
    int t = ((svc_v)SVC(0x1038))();
    g_hwant = t == NO_SLOT;
    if (g_hwant) return;
    lg("handles: Content ID list upload -> %d%s\r\n", t, reload ? " (the server list is newer: reload)" : "");
    g_hreload = reload;
    if (t < 0) {
        handle_undo(b);
        handle_done(failed(t));
        return;
    }
    g_hjob = t;
}
static void handle_put_done(BYTE *b, int r)
{
    BYTE c[0x68];
    DWORD f;
    int linked;
    if (r != 1) {
        handle_undo(b);
        handle_done(failed(r));
        return;
    }
    /* a put that became a load already has the server's copy in polcore */
    f = get_chr(b, g_hidx, c) ? *(DWORD *)(c + 0x10) : 0;
    linked = (f & CHR_LINKED) && (int)CHR_HANDLE(f) == g_hslot;
    for (int i = 0; g_hop == HOP_LINKALL && i < g_hall_n; i++) {
        f = get_chr(b, g_hall[i], c) ? *(DWORD *)(c + 0x10) : 0;
        if (!(f & CHR_LINKED) || (int)CHR_HANDLE(f) != g_hslot) linked = 0;
    }
    if (g_hreload) handle_done("failed:reloaded");
    else if (linked != (g_hop != HOP_UNLINK)) handle_done("failed:not saved");
    else handle_done("saved");
}
/* my profile's the handle's, so a loaded one's the old handle's now */
static void handle_switched(BYTE *b)
{
    char jp[MAX_PATH + 64];
    g_my_slot = ((svc_v)SVC(0x2f8))();
    ipc_file(jp, "myprofile.json");
    DeleteFileA(jp);
    if (!lstrcmpA(g_me_state, "ready")) g_me_state[0] = 0;
    lg("handles: active handle now slot %d\r\n", g_my_slot);
}

/* while a switch runs, a write is taken and answered as not done, it'd go from the new handle otherwise. A chat
   enter waiting when the switch is taken is called off the same way */
#define SWITCHING "failed:handle changing"
static void writes_refuse(BYTE *b)
{
    static const char *const ext[] = { "send", "friend", "comment", "answer", "group", "pic", "status", "myprofile",
                                       "saveprofile", "rename", "chatjoin", "chatsend", "chatcreate", "chatmaster",
                                       "groupchatjoin", "groupchatsend", "xiprofile" };
    char buf[512], *p, *l1, *l2, *l3;
    for (int i = 0; i < (int)(sizeof ext / sizeof ext[0]); i++) {
        if (take_request(ext[i], buf, sizeof buf) < 0) continue;
        lg("handles: %s request refused - the handle's changing\r\n", ext[i]);
        p = buf;
        l1 = req_line(&p); l2 = req_line(&p); l3 = req_line(&p);
        switch (i) {
        case 0: case 3: lstrcpynA(g_send_token, l2, sizeof g_send_token); send_fail(SWITCHING); break;
        case 1: lstrcpynA(g_edit_token, l2, sizeof g_edit_token); edit_fail(SWITCHING); break;
        case 2: lstrcpynA(g_edit_token, l1, sizeof g_edit_token); edit_fail(SWITCHING); break;
        case 4: lstrcpynA(g_grp_token, l2, sizeof g_grp_token); group_fail(SWITCHING); break;
        case 5: lstrcpynA(g_pic_token, l2, sizeof g_pic_token); pic_fail(SWITCHING); break;
        case 7: case 8: case 9: lstrcpynA(g_me_token, l1, sizeof g_me_token); me_done(SWITCHING); break;
        case 10: chat_join_refused(l1, l3); break;
        case 11: chat_send_refused(l1); break;
        case 12: chat_create_refused(l1, l2); break;
        case 13: chat_master_refused(l1, l2); break;
        case 14: gc_join_refused(l1, SWITCHING); break;
        case 15: gc_send_refused(l1, SWITCHING); break;
        case 16: lstrcpynA(g_xi_token, l1, sizeof g_xi_token); xi_done(SWITCHING); break;
        }
    }
}
/* written by the app for the handle that's active now, but not taken yet */
static int writes_asked(void)
{
    static const char *const ext[] = { "send", "friend", "comment", "answer", "group", "pic", "status" };
    for (int i = 0; i < (int)(sizeof ext / sizeof ext[0]); i++) if (request_waiting(ext[i])) return 1;
    return 0;
}

/* the viewer's link before it starts a game (app.dll 0x100235d5): unlinked ones go on the active handle, all in one
   PutChrList. The app only sends what fits, so it's all or nothing here */
static void handle_linkall(BYTE *b, int slot, BYTE *h, char *ix, char *ck, char *p)
{
    char k[17];
    BYTE c[0x68];
    int idx[8], n = 0, r;
    if (slot != ((svc_v)SVC(0x2f8))()) { handle_fail("failed:not active"); return; }
    while (ix[0]) {
        if (n == 8 || !parse_num(ix, 2, &idx[n]) || idx[n] > 63) { handle_fail("failed:bad request"); return; }
        for (int i = 0; i < n; i++) if (idx[i] == idx[n]) { handle_fail("failed:bad request"); return; }
        if (!get_chr(b, idx[n], c) || *(WORD *)(c + 2) != 1) { handle_fail("failed:list changed"); return; }
        chr_key(c, k);
        if (lstrcmpA(k, ck) != 0) { handle_fail("failed:list changed"); return; }
        if (*(DWORD *)(c + 0x10) & CHR_LINKED) { handle_fail("failed:already linked"); return; }
        n++;
        ix = req_line(&p); ck = req_line(&p);
    }
    if (!n) { handle_fail("failed:bad request"); return; }
    if (handle_used(h) + n > 8) { handle_fail("failed:handle full"); return; }
    for (int i = 0; i < n; i++) {
        r = ((int (__cdecl *)(int, int))SVC(0x2dc))(idx[i], slot);
        if (r < 0) {
            while (i-- > 0) ((int (__cdecl *)(int, int))SVC(0x2e0))(idx[i], slot);
            handle_fail(failed(r));
            return;
        }
    }
    g_hop = HOP_LINKALL;
    g_hslot = slot;
    g_hidx = idx[0];
    g_hfrom = g_hpos = -1;
    memcpy(g_hall, idx, sizeof idx);
    g_hall_n = n;
    lstrcpyA(g_hstate, "working");
    lg("handles: %d unlinked content index(es) linked to the active slot %d\r\n", n, slot);
    g_snap_dirty = 1;
    handle_put_start(b);
}

/* <op> switch|link|unlink|linkall, token, slot, handle key, then index/key pairs. The keys are the snapshot's, so a
   request never lands on a record that changed */
static void handle_request(BYTE *b)
{
    char buf[512], *p, *op, *tok, *sl, *hk, *ix, *ck, k[17];
    BYTE h[0x28], c[0x68];
    int slot, idx = -1, kind, cur, old = -1, r;
    DWORD f;
    /* a background character lookup finishes first, rather than the switch being turned away as busy */
    if (g_ch_step) return;
    if (take_request("handle", buf, sizeof buf) < 0) return;
    p = buf;
    op = req_line(&p); tok = req_line(&p); sl = req_line(&p); hk = req_line(&p); ix = req_line(&p); ck = req_line(&p);
    lstrcpynA(g_htoken, tok, sizeof g_htoken);
    kind = !lstrcmpA(op, "switch") ? HOP_SWITCH : !lstrcmpA(op, "link") ? HOP_LINK : !lstrcmpA(op, "unlink") ? HOP_UNLINK
         : !lstrcmpA(op, "linkall") ? HOP_LINKALL : 0;
    if (!kind || !tok[0] || !parse_num(sl, 2, &slot) || slot > 63 ||
        (kind != HOP_SWITCH && kind != HOP_LINKALL && (!parse_num(ix, 2, &idx) || idx > 63))) {
        handle_fail("failed:bad request");
        return;
    }
    if (!g_in_hold) { handle_fail("failed:not in hold"); return; }
    if (g_draining) { handle_fail("failed:draining"); return; }
    if (!handle_ok(b, slot, h)) { handle_fail("failed:list changed"); return; }
    handle_key(h, k);
    if (lstrcmpA(k, hk) != 0) { handle_fail("failed:list changed"); return; }
    if (kind == HOP_SWITCH) {
        cur = ((svc_v)SVC(0x2f8))();
        if (slot == cur) { handle_fail("failed:already active"); return; }
        /* they went under the old handle, some with its slot in them, and a read's answer is what the old handle
           was allowed to see */
        if (writes_pending() || writes_asked() || g_me_step || g_status_want >= 0 || g_srch_step || g_prof_step ||
            g_fp_step || g_xi_step || g_cr_db) {
            handle_fail("failed:busy");
            return;
        }
        g_hop = HOP_SWITCH;
        g_hslot = slot;
        lstrcpyA(g_hstate, "working");
        lg("handles: switch from slot %d to slot %d\r\n", cur, slot);
        g_snap_dirty = 1;
        /* the viewer quits private chat before a switch, so a room's left first, through the one PART there is */
        if (chat_room_open()) chat_end(b);
        if (gc_open() || gc_joined(b)) gc_end(b, "left:handle", 0);
        handle_switch_start(b);
        if (handle_switching()) writes_refuse(b);
        return;
    }
    if (kind == HOP_LINKALL) { handle_linkall(b, slot, h, ix, ck, p); return; }
    if (!get_chr(b, idx, c) || *(WORD *)(c + 2) != 1) { handle_fail("failed:list changed"); return; }
    chr_key(c, k);
    if (lstrcmpA(k, ck) != 0) { handle_fail("failed:list changed"); return; }
    f = *(DWORD *)(c + 0x10);
    if (kind == HOP_LINK) {
        if ((f & CHR_LINKED) && (int)CHR_HANDLE(f) == slot) { handle_fail("failed:already linked"); return; }
        if (handle_used(h) >= 8) { handle_fail("failed:handle full"); return; }
        /* polcore won't link one that's linked anywhere, so a move's an unlink first, like the viewer's. One linked to
           a slot with no handle can't be unlinked either (-0x1c04), so it's left as it is */
        if (f & CHR_LINKED) {
            BYTE oh[0x28];
            old = (int)CHR_HANDLE(f);
            if (!handle_ok(b, old, oh)) { handle_fail("failed:orphan"); return; }
            r = ((int (__cdecl *)(int, int))SVC(0x2e0))(idx, old);
            if (r < 0) { handle_fail(failed(r)); return; }
        }
        r = ((int (__cdecl *)(int, int))SVC(0x2dc))(idx, slot);
        if (r < 0) {
            if (old >= 0) ((int (__cdecl *)(int, int))SVC(0x2dc))(idx, old);
            handle_fail(failed(r));
            return;
        }
    } else {
        if (!(f & CHR_LINKED) || (int)CHR_HANDLE(f) != slot) { handle_fail("failed:not linked"); return; }
        r = ((int (__cdecl *)(int, int))SVC(0x2e0))(idx, slot);
        if (r < 0) { handle_fail(failed(r)); return; }
    }
    g_hop = kind;
    g_hslot = slot;
    g_hidx = idx;
    g_hfrom = old;
    g_hpos = (f & CHR_LINKED) ? (int)((f >> 22) & 7) : -1;
    lstrcpyA(g_hstate, "working");
    lg("handles: %s content index %d %s slot %d%s\r\n", kind == HOP_LINK ? "link" : "unlink", idx,
       kind == HOP_LINK ? "to" : "from", slot, old >= 0 ? " (moved)" : "");
    g_snap_dirty = 1;
    handle_put_start(b);
}
static void handle_pump(BYTE *b, int ctl)
{
    int r;
    if (g_hop == HOP_NONE) {
        if (ctl && !g_lean) handle_request(b);
        return;
    }
    if (g_hwant) {
        /* a switch still waiting for a slot isn't worth starting once the game or a log out is on its way */
        if (g_hop == HOP_SWITCH && g_draining) handle_done("failed:draining");
        else if (g_hop == HOP_SWITCH) handle_switch_start(b);
        else handle_put_start(b);   /* applied locally already, so it still goes in a drain */
        return;
    }
    if (g_hjob < 0) return;
    r = ((svc_i)SVC(g_hop == HOP_SWITCH ? 0x310 : 0x103c))(g_hjob);
    if (r == 0) return;
    g_hjob = -1;
    if (g_hop != HOP_SWITCH) { handle_put_done(b, r); return; }
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
    if (g_hop == HOP_NONE) return;
    if (g_hjob >= 0) CANCEL_JOB(g_hjob);
    if (g_hop != HOP_SWITCH) handle_undo(b);
    handle_done("failed:cancelled");
}
static void handles_json(BYTE *b, HANDLE out)
{
    char line[900], nm[64], tx[200], k[17], ids[48];
    BYTE rec[0x68];
    int cur = SVC(0x2f8) ? ((svc_v)SVC(0x2f8))() : -1, first = 1, i, j, o;
    DWORD f;
    wr_job(out, ",\"handleJob\":{\"token\":%s,\"state\":\"%s\"", g_htoken, g_hstate);
    wr(out, g_hop == HOP_SWITCH ? ",\"op\":\"switch\"}" : g_hop == HOP_LINK ? ",\"op\":\"link\"}"
                                : g_hop == HOP_UNLINK ? ",\"op\":\"unlink\"}" : g_hop == HOP_LINKALL ? ",\"op\":\"linkall\"}"
                                : "}");
    wr(out, ",\"handles\":[");
    for (i = 0; i < 64; i++) {
        if (!handle_ok(b, i, rec)) continue;
        sjis_json(nm, sizeof nm, rec + 0x08, 0x0f);
        handle_key(rec, k);
        for (o = 0, j = 0; j < 8; j++)
            if (rec[0x20 + j] & 1) o += wsprintfA(ids + o, "%s%u", o ? "," : "", (rec[0x20 + j] >> 1) & 0x3f);
        ids[o] = 0;
        wsprintfA(line, "%s{\"slot\":%d,\"name\":%s,\"key\":\"%s\",\"pic\":%u,\"order\":%u,\"active\":%s,"
                        "\"contentIds\":[%s]}",
                  first ? "" : ",", i, nm, k, *(unsigned *)(rec + 0x1c), (*(unsigned *)(rec + 0x18) >> 2) & 0x3f,
                  i == cur ? "true" : "false", ids);
        wr(out, line);
        first = 0;
    }
    wr(out, "],\"contentIds\":[");
    for (i = 0, first = 1; i < 64; i++) {
        if (!get_chr(b, i, rec)) continue;
        f = *(DWORD *)(rec + 0x10);
        sjis_json(nm, sizeof nm, rec + 0x18, 0x0f);
        sjis_json(tx, sizeof tx, rec + 0x28, 0x3f);
        chr_key(rec, k);
        wsprintfA(line, "%s{\"index\":%d,\"type\":%u,\"name\":%s,\"text\":%s,\"order\":%u,\"handle\":%d,\"key\":\"%s\"}",
                  first ? "" : ",", i, *(WORD *)(rec + 2), nm, tx, f & 0xff, (f & CHR_LINKED) ? (int)CHR_HANDLE(f) : -1, k);
        wr(out, line);
        first = 0;
    }
    wr(out, "]");
}
