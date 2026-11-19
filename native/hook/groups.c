static void group_chat_group_going(BYTE *b, DWORD lo, DWORD hi);

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
static int groups_get_group(BYTE *b, DWORD lo, DWORD hi, grp_hdr *g)
{
    memset(g, 0, sizeof *g);
    return pc_group_header(b, lo, hi, g) == 1;
}
static int groups_get_member(BYTE *b, DWORD lo, DWORD hi, int i, grp_mem *m)
{
    memset(m, 0, sizeof *m);
    return pc_group_member(b, lo, hi, (unsigned)i, m) == 1 && (m->flags & 1);
}

enum { GRP_CREATE, GRP_DISBAND, GRP_CLASS, GRP_COMMENT };
static DWORD g_grp_new[2];

#define group_fail(why) requests_refuse(g_s.grp_state, sizeof g_s.grp_state, "groups: refused - %s\r\n", why)
static int groups_hex8(const char *x, DWORD *v)
{
    *v = 0;
    for (int i = 0; i < 8; i++) {
        if (!((x[i] >= '0' && x[i] <= '9') || (x[i] >= 'a' && x[i] <= 'f') || (x[i] >= 'A' && x[i] <= 'F'))) return 0;
        *v = *v << 4 | (DWORD)(x[i] <= '9' ? x[i] - '0' : (x[i] | 0x20) - 'a' + 10);
    }
    return 1;
}
static void groups_start(int t, int poll, const char *what)
{
    base_log("groups: %s -> %d\r\n", what, t);
    if (t < 0) { group_fail(requests_failed(t)); return; }
    g_s.grp_job = t;
    g_s.grp_poll = poll;
    lstrcpyA(g_s.grp_state, "saving");
    g_snap_dirty = 1;
}
static void groups_send(BYTE *b, int kind, DWORD toLo, DWORD toHi, int toSlot, int fromSlot,
                       const char *subj, const char *text, const BYTE att[0x20], const char *tok)
{
    char myName[16], sj_subj[0x84], sj_text[0x104];
    lstrcpynA(g_s.send_token, tok, sizeof g_s.send_token);
    if (!peers_slot_name(b, fromSlot, myName)) { mail_send_fail("failed:no handle"); return; }
    pol_sjis(b, subj, sj_subj, 0x80);
    pol_sjis(b, text, sj_text, 0x100);
    int n = mail_send_msg_build(b, kind, toLo, toHi, fromSlot, toSlot, myName, sj_subj, sj_text, att, 10);
    base_log("groups: message kind 0x%x from slot %d to slot %d (body %d)\r\n", kind, fromSlot, toSlot, n);
    if (n < 0) { mail_send_fail("failed:alloc"); return; }
    mail_send_start(b);
    g_snap_dirty = 1;
}
static void groups_answer(BYTE *b, int accept, int mid, const char *subj, const char *tok)
{
    const mailrec *m = NULL;
    DWORD lo, hi;
    grp_hdr gh;
    lstrcpynA(g_s.grp_token, tok, sizeof g_s.grp_token);
    for (int i = 0; i < g_s.mail_n; i++) if (g_mail[i].mid == mid) { m = &g_mail[i]; break; }
    if (!m || pc_mail_kind(m->hdr) != 0xe || !m->has_att) {
        group_fail("failed:no such invitation");
        return;
    }
    memcpy(&lo, m->att, 4);
    memcpy(&hi, m->att + 4, 4);
    if (!lo && !hi) { group_fail("failed:no such invitation"); return; }
    if (accept && groups_get_group(b, lo, hi, &gh) && (gh.flags & 1)) {
        int cls = (int)((gh.flags >> 1) & 7), slot = (int)((gh.flags >> 4) & 0x3f);
        if (cls >= 3 && slot != MAIL(m->hdr)->to_slot) { group_fail("failed:already a member"); return; }
    }
    lstrcpyA(g_s.grp_state, "sent as a message");
    groups_send(b, accept ? 0xf : 0x10, MAIL(m->hdr)->from_lo, MAIL(m->hdr)->from_hi,
               MAIL(m->hdr)->from_slot, MAIL(m->hdr)->to_slot, subj, "", m->att, tok);
}
static void groups_request(BYTE *b)
{
    char buf[1024], *p, *op, *tok, *gid, *a, *bb, *c, *d, k[17], mk[17], nm[0x80];
    static WCHAR w[0x33];
    const char *x;
    DWORD lo = 0, hi = 0;
    pol_friend_copy fr;
    BYTE att[0x20];
    grp_hdr gh = { 0 };
    grp_mem m;
    int myClass = 0, haveGroup = 0, mid, slot, idx, cls, tcls, me, row, used = 0;
    if (g_s.send_queue_n || g_s.answer_kind || mail_send_slots_free(b) < 1) return;
    if (requests_take(REQ_GROUP, buf, sizeof buf) < 0) return;
    p = buf;
    op = requests_line(&p); tok = requests_line(&p); gid = requests_line(&p); a = requests_line(&p);
    bb = requests_line(&p); c = requests_line(&p);
    d = requests_line(&p);
    lstrcpynA(g_s.grp_token, tok, sizeof g_s.grp_token);
    if (mail_send_pace_refusal() && (!lstrcmpA(op, "accept") || !lstrcmpA(op, "decline") || !lstrcmpA(op, "invite"))) {
        group_fail(mail_send_pace_refusal());
        return;
    }
    if (!pc_has(b, SV_GROUP_HEADER) || !pc_has(b, SV_GROUP_MEMBER) || !pc_has(b, SV_GROUP_CREATE)) {
        group_fail("failed:no group services");
        return;
    }
    if (lstrlenA(gid) == 16 && groups_hex8(gid, &hi) && groups_hex8(gid + 8, &lo)) {
        haveGroup = groups_get_group(b, lo, hi, &gh) && (gh.flags & 1);
        myClass = haveGroup ? (int)((gh.flags >> 1) & 7) : 0;
    }
    if (lstrcmpA(op, "accept") == 0 || lstrcmpA(op, "decline") == 0) {
        if (!requests_parse_num(a, 6, &mid)) { group_fail("failed:bad request"); return; }
        groups_answer(b, op[0] == 'a', mid, bb, tok);
        return;
    }
    if (lstrcmpA(op, "create") == 0) {
        slot = pc_current_handle(b);
        for (x = a; *x; x++) if ((BYTE)*x < 0x20) break;
        memset(nm, 0, sizeof nm);
        if (*x || !a[0]) { group_fail("failed:bad name"); return; }
        pol_sjis(b, a, nm, sizeof nm);
        if (!nm[0] || lstrlenA(nm) > 0x14) { group_fail("failed:name too long"); return; }
        if (slot < 0) slot = g_my_slot;
        g_grp_new[0] = g_grp_new[1] = 0xffffffff;
        groups_start(pc_group_create(b, nm, slot), GRP_CREATE, "create");
        return;
    }
    if (!haveGroup) { group_fail("failed:no such group"); return; }
    if (lstrcmpA(op, "disband") == 0) {
        if (myClass != 5) { group_fail("failed:not master"); return; }
        group_chat_group_going(b, lo, hi);
        groups_start(pc_group_disband(b, lo, hi), GRP_DISBAND, "disband");
    } else if (lstrcmpA(op, "leave") == 0) {
        if (myClass == 5) { group_fail("failed:master cannot leave"); return; }
        if (myClass < 3) { group_fail("failed:not a member"); return; }
        group_chat_group_going(b, lo, hi);
        groups_start(pc_group_set_class(b, lo, hi, gh.myIndex, 1), GRP_CLASS, "leave");
    } else if (lstrcmpA(op, "class") == 0) {
        if (!requests_parse_num(a, 2, &idx) || idx > 63 || !requests_parse_num(bb, 1, &cls)) {
            group_fail("failed:bad request"); return;
        }
        if (!groups_get_member(b, lo, hi, idx, &m)) { group_fail("failed:no such member"); return; }
        peers_key(b, m.idLo, m.idHi, (m.hHi >> 13) & 0x3f, k);
        if (lstrcmpA(k, d) != 0) { group_fail("failed:no such member"); return; }
        tcls = (int)((m.flags >> 18) & 7);
        me = (int)((m.flags >> 21) & 1);
        if (me ||
            (cls == 1 && ((myClass != 4 && myClass != 5) || tcls == 5)) ||
            (cls == 3 && ((myClass != 4 && myClass != 5) || tcls != 4)) ||
            (cls == 4 && (myClass != 5 || tcls != 3)) ||
            (cls == 5 && (myClass != 5 || (tcls != 3 && tcls != 4))) ||
            (cls != 1 && cls != 3 && cls != 4 && cls != 5)) { group_fail("failed:not allowed"); return; }
        groups_start(pc_group_set_class(b, lo, hi, (unsigned)idx, cls), GRP_CLASS,
                    "class");
    } else if (lstrcmpA(op, "comment") == 0) {
        if (myClass < 3) { group_fail("failed:not a member"); return; }
        memset(w, 0, sizeof w);
        if (a[0] && !friends_comment_text(a, w)) { group_fail("failed:bad text"); return; }
        groups_start(pc_group_comment(b, lo, hi, -3, -3, w), GRP_COMMENT,
                    "comment");
    } else if (lstrcmpA(op, "invite") == 0) {
        if (myClass != 4 && myClass != 5) { group_fail("failed:not allowed"); return; }
        if (!requests_parse_num(a, 3, &row) || row >= 200) { group_fail("failed:bad request"); return; }
        if (!peers_get_friend(b, row, &fr) || (fr.f.flags & FRIEND_PENDING)) {
            group_fail("failed:no such friend");
            return;
        }
        peers_friend_key(b, &fr.f, k);
        if (lstrcmpA(k, d) != 0) { group_fail("failed:no such friend"); return; }
        if (peers_is_me(b, fr.f.id_lo, fr.f.id_hi)) { group_fail("failed:self"); return; }
        for (int mi = 0; mi < 64; mi++) {
            if (!groups_get_member(b, lo, hi, mi, &m)) continue;
            used++;
            peers_key(b, m.idLo, m.idHi, (m.hHi >> 13) & 0x3f, mk);
            if (lstrcmpA(mk, k) == 0) { group_fail("failed:already in group"); return; }
        }
        if (used >= 64) { group_fail("failed:group full"); return; }
        memset(att, 0, sizeof att);
        memcpy(att, &gh.idLo, 4);
        memcpy(att + 4, &gh.idHi, 4);
        memcpy(att + 8, gh.name, 0x14);
        lstrcpyA(g_s.grp_state, "sent as a message");
        groups_send(b, 0xe, fr.f.id_lo, fr.f.id_hi, pc_friend_slot(&fr.f),
                   (int)((gh.flags >> 4) & 0x3f), bb, c, att, tok);
    } else {
        group_fail("failed:bad request");
    }
}
static void groups_pump(BYTE *b, int ctl)
{
    if (g_s.grp_job < 0) {
        if (ctl && !g_s.draining && g_s.edit_job < 0) groups_request(b);
        return;
    }
    int r;
    switch (g_s.grp_poll) {
    case GRP_CREATE: r = pc_group_create_check(b, g_s.grp_job, g_grp_new); break;
    case GRP_DISBAND: r = pc_group_disband_check(b, g_s.grp_job); break;
    case GRP_CLASS: r = pc_group_set_class_check(b, g_s.grp_job); break;
    default: r = pc_group_comment_check(b, g_s.grp_job); break;
    }
    if (r == 0) return;
    g_s.grp_job = -1;
    if (r == 1) lstrcpyA(g_s.grp_state, "saved"); else lstrcpyA(g_s.grp_state, requests_failed(r));
    base_log("groups: job done -> %d\r\n", r);
    g_snap_dirty = 1;
}
