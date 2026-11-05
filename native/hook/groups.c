static void gc_group_going(BYTE *b, DWORD lo, DWORD hi);

#pragma pack(push, 1)
typedef struct grp_hdr {
    DWORD idLo, idHi, notice, flags;             /* flags: bit 0 valid, 1-3 my class, 4-9 my slot, 10-12 my status */
    char  name[0x14];
    BYTE  z24, nMembers, myIndex, stale;
} grp_hdr;
typedef struct grp_mem {
    DWORD idLo, idHi, flags;                     /* flags: bit 0 valid, 1 ignored, 4-6 status, 18-20 class, 21 me */
    DWORD content[3];
    DWORD tLo, tHi, hLo, hHi;
    char  name[16];
    DWORD pic;
    char  text[0x18];
    WCHAR comment[0x33];
    WORD  stale;
    BYTE  pad[4];
} grp_mem;
#pragma pack(pop)
typedef char gr_chk_hdr[sizeof(grp_hdr) == 0x28 ? 1 : -1];
typedef char gr_chk_mem[sizeof(grp_mem) == 0xc0 ? 1 : -1];
static int get_group(BYTE *b, DWORD lo, DWORD hi, grp_hdr *g)
{
    memset(g, 0, sizeof *g);
    return ((int (__cdecl *)(DWORD, DWORD, void *))SVC(0x618))(lo, hi, g) == 1;
}
static int get_member(BYTE *b, DWORD lo, DWORD hi, int i, grp_mem *m)
{
    memset(m, 0, sizeof *m);
    return ((int (__cdecl *)(DWORD, DWORD, unsigned, void *))SVC(0x61c))(lo, hi, (unsigned)i, m) == 1 && (m->flags & 1);
}

static int g_grp_job = -1, g_grp_poll;
static DWORD g_grp_new[2];
static char g_grp_token[48], g_grp_state[32] = "";

#define group_fail(why) refuse(g_grp_state, sizeof g_grp_state, "groups: refused - %s\r\n", why)
static int hex8(const char *x, DWORD *v)
{
    *v = 0;
    for (int i = 0; i < 8; i++) {
        if (!((x[i] >= '0' && x[i] <= '9') || (x[i] >= 'a' && x[i] <= 'f') || (x[i] >= 'A' && x[i] <= 'F'))) return 0;
        *v = *v << 4 | (DWORD)(x[i] <= '9' ? x[i] - '0' : (x[i] | 0x20) - 'a' + 10);
    }
    return 1;
}
static void group_start(int t, int poll, const char *what)
{
    lg("groups: %s -> %d\r\n", what, t);
    if (t < 0) { group_fail(failed(t)); return; }
    g_grp_job = t;
    g_grp_poll = poll;
    lstrcpyA(g_grp_state, "saving");
    g_snap_dirty = 1;
}
static void group_send(BYTE *b, int kind, DWORD toLo, DWORD toHi, int toSlot, int fromSlot,
                       const char *subj, const char *text, const BYTE att[0x20], const char *tok)
{
    char myName[16], sj_subj[0x84], sj_text[0x104];
    lstrcpynA(g_send_token, tok, sizeof g_send_token);
    if (!slot_name(b, fromSlot, myName)) { send_fail("failed:no handle"); return; }
    utf8_to_sjis(subj, sj_subj, 0x80);
    utf8_to_sjis(text, sj_text, 0x100);
    int n = msg_build(b, kind, toLo, toHi, fromSlot, toSlot, myName, sj_subj, sj_text, att, 10);
    lg("groups: message kind 0x%x from slot %d to slot %d (body %d)\r\n", kind, fromSlot, toSlot, n);
    if (n < 0) { send_fail("failed:alloc"); return; }
    mail_send_start(b);
    g_snap_dirty = 1;
}
/* the group id comes from the invitation, never the request */
static void group_answer(BYTE *b, int accept, int mid, const char *subj, const char *tok)
{
    const mailrec *m = NULL;
    DWORD lo, hi;
    grp_hdr gh;
    lstrcpynA(g_grp_token, tok, sizeof g_grp_token);
    for (int i = 0; i < g_mail_n; i++) if (g_mail[i].mid == mid) { m = &g_mail[i]; break; }
    if (!m || ((*(const WORD *)(m->hdr + 0x3e) >> 7) & 0x1f) != 0xe || !m->has_att) {
        group_fail("failed:no such invitation");
        return;
    }
    memcpy(&lo, m->att, 4);
    memcpy(&hi, m->att + 4, 4);
    if (!lo && !hi) { group_fail("failed:no such invitation"); return; }
    if (accept && get_group(b, lo, hi, &gh) && (gh.flags & 1)) {
        int cls = (int)((gh.flags >> 1) & 7), slot = (int)((gh.flags >> 4) & 0x3f);
        if (cls >= 3 && slot != m->hdr[0x3d]) { group_fail("failed:already a member"); return; }
    }
    lstrcpyA(g_grp_state, "sent as a message");
    group_send(b, accept ? 0xf : 0x10, *(const DWORD *)(m->hdr + 0x00), *(const DWORD *)(m->hdr + 0x04),
               m->hdr[0x3c], m->hdr[0x3d], subj, "", m->att, tok);
}
static void group_request(BYTE *b)
{
    char buf[1024], *p, *op, *tok, *gid, *a, *bb, *c, *d, k[17], mk[17], nm[0x80];
    static WCHAR w[0x33];
    const char *x;
    DWORD lo = 0, hi = 0;
    BYTE fr[0x140], att[0x20];
    grp_hdr gh;
    grp_mem m;
    int myClass = 0, haveGroup = 0, mid, slot, idx, cls, tcls, me, row, used = 0;
    /* a group job's -0x100 is a failure, so wait for a free polcore slot */
    if (g_sq_n || g_answer_kind || slots_free(b) < 1) return;
    if (take_request("group", buf, sizeof buf) < 0) return;
    p = buf;
    op = req_line(&p); tok = req_line(&p); gid = req_line(&p); a = req_line(&p); bb = req_line(&p); c = req_line(&p);
    d = req_line(&p);
    lstrcpynA(g_grp_token, tok, sizeof g_grp_token);
    if (pace_refusal() && (!lstrcmpA(op, "accept") || !lstrcmpA(op, "decline") || !lstrcmpA(op, "invite"))) {
        group_fail(pace_refusal());
        return;
    }
    if (!SVC(0x618) || !SVC(0x61c) || !SVC(0x608)) { group_fail("failed:no group services"); return; }
    if (lstrlenA(gid) == 16 && hex8(gid, &hi) && hex8(gid + 8, &lo)) {
        haveGroup = get_group(b, lo, hi, &gh) && (gh.flags & 1);
        myClass = haveGroup ? (int)((gh.flags >> 1) & 7) : 0;
    }
    if (lstrcmpA(op, "accept") == 0 || lstrcmpA(op, "decline") == 0) {
        if (!parse_num(a, 6, &mid)) { group_fail("failed:bad request"); return; }
        group_answer(b, op[0] == 'a', mid, bb, tok);
        return;
    }
    if (lstrcmpA(op, "create") == 0) {
        slot = ((svc_v)SVC(0x2f8))();
        for (x = a; *x; x++) if ((BYTE)*x < 0x20) break;
        memset(nm, 0, sizeof nm);
        if (*x || !a[0]) { group_fail("failed:bad name"); return; }
        utf8_to_sjis(a, nm, sizeof nm);
        if (!nm[0] || lstrlenA(nm) > 0x14) { group_fail("failed:name too long"); return; }
        if (slot < 0) slot = g_my_slot;
        g_grp_new[0] = g_grp_new[1] = 0xffffffff;
        group_start(((int (__cdecl *)(const char *, int))SVC(0x608))(nm, slot), 0x60c, "create");
        return;
    }
    if (!haveGroup) { group_fail("failed:no such group"); return; }
    if (lstrcmpA(op, "disband") == 0) {
        if (myClass != 5) { group_fail("failed:not master"); return; }
        gc_group_going(b, lo, hi);
        group_start(((int (__cdecl *)(DWORD, DWORD))SVC(0x610))(lo, hi), 0x614, "disband");
    } else if (lstrcmpA(op, "leave") == 0) {
        if (myClass == 5) { group_fail("failed:master cannot leave"); return; }
        if (myClass < 3) { group_fail("failed:not a member"); return; }
        gc_group_going(b, lo, hi);
        group_start(((int (__cdecl *)(DWORD, DWORD, unsigned, int))SVC(0x620))(lo, hi, gh.myIndex, 1), 0x624, "leave");
    } else if (lstrcmpA(op, "class") == 0) {
        if (!parse_num(a, 2, &idx) || idx > 63 || !parse_num(bb, 1, &cls)) { group_fail("failed:bad request"); return; }
        if (!get_member(b, lo, hi, idx, &m)) { group_fail("failed:no such member"); return; }
        peer_key(b, m.idLo, m.idHi, (m.hHi >> 13) & 0x3f, k);
        if (lstrcmpA(k, d) != 0) { group_fail("failed:no such member"); return; }
        tcls = (int)((m.flags >> 18) & 7);
        me = (int)((m.flags >> 21) & 1);
        /* the viewer's member menu: remove (1) a non-master as master or sub-master, relieve (3) a sub-master, and as
           master only make a member sub-master (4) or anyone below master (5) */
        if (me ||
            (cls == 1 && ((myClass != 4 && myClass != 5) || tcls == 5)) ||
            (cls == 3 && ((myClass != 4 && myClass != 5) || tcls != 4)) ||
            (cls == 4 && (myClass != 5 || tcls != 3)) ||
            (cls == 5 && (myClass != 5 || (tcls != 3 && tcls != 4))) ||
            (cls != 1 && cls != 3 && cls != 4 && cls != 5)) { group_fail("failed:not allowed"); return; }
        group_start(((int (__cdecl *)(DWORD, DWORD, unsigned, int))SVC(0x620))(lo, hi, (unsigned)idx, cls), 0x624,
                    "class");
    } else if (lstrcmpA(op, "comment") == 0) {
        if (myClass < 3) { group_fail("failed:not a member"); return; }
        memset(w, 0, sizeof w);
        if (a[0] && !comment_text(a, w)) { group_fail("failed:bad text"); return; }
        group_start(((int (__cdecl *)(DWORD, DWORD, int, int, const WCHAR *))SVC(0x630))(lo, hi, -3, -3, w), 0x634,
                    "comment");
    } else if (lstrcmpA(op, "invite") == 0) {
        if (myClass != 4 && myClass != 5) { group_fail("failed:not allowed"); return; }
        if (!parse_num(a, 3, &row) || row >= 200) { group_fail("failed:bad request"); return; }
        if (!get_friend(b, row, fr) || (*(DWORD *)(fr + 0x08) & 0x10000000)) {
            group_fail("failed:no such friend");
            return;
        }
        friend_key(b, fr, k);
        if (lstrcmpA(k, d) != 0) { group_fail("failed:no such friend"); return; }
        if (is_me(b, *(DWORD *)(fr + 0x00), *(DWORD *)(fr + 0x04))) { group_fail("failed:self"); return; }
        for (int mi = 0; mi < 64; mi++) {
            if (!get_member(b, lo, hi, mi, &m)) continue;
            used++;
            peer_key(b, m.idLo, m.idHi, (m.hHi >> 13) & 0x3f, mk);
            if (lstrcmpA(mk, k) == 0) { group_fail("failed:already in group"); return; }
        }
        if (used >= 64) { group_fail("failed:group full"); return; }
        memset(att, 0, sizeof att);
        memcpy(att, &gh.idLo, 4);
        memcpy(att + 4, &gh.idHi, 4);
        memcpy(att + 8, gh.name, 0x14);
        lstrcpyA(g_grp_state, "sent as a message");
        group_send(b, 0xe, *(DWORD *)(fr + 0x00), *(DWORD *)(fr + 0x04), (int)FR_SLOT(fr),
                   (int)((gh.flags >> 4) & 0x3f), bb, c, att, tok);
    } else {
        group_fail("failed:bad request");
    }
}
static void group_pump(BYTE *b, int ctl)
{
    if (g_grp_job < 0) {
        if (ctl && !g_draining && g_edit_job < 0) group_request(b);
        return;
    }
    int r = g_grp_poll == 0x60c ? ((int (__cdecl *)(int, DWORD *))SVC(0x60c))(g_grp_job, g_grp_new)
                                : ((svc_i)SVC(g_grp_poll))(g_grp_job);
    if (r == 0) return;
    g_grp_job = -1;
    if (r == 1) lstrcpyA(g_grp_state, "saved"); else wsprintfA(g_grp_state, "failed:%d", r);
    lg("groups: job done -> %d\r\n", r);
    g_snap_dirty = 1;
}
