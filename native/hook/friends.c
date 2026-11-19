
static int g_edit_kind;   /* 1 list upload, 2 comment, 3 upload that became a reload */
static WCHAR g_comment_want[0x33];

#define edit_fail(why) requests_refuse(g_s.edit_state, sizeof g_s.edit_state, "friends: edit refused - %s\r\n", why)
static void friends_upload_start(BYTE *b)
{
    int reload = pc_friends_server_newer(b);
    int t = pc_friends_save(b);
    if (t == NO_SLOT) { g_s.edit_upload_want = 1; return; }
    g_s.edit_upload_want = 0;
    base_log("friends: list upload start -> %d%s\r\n", t, reload ? " (the server list is newer: reload)" : "");
    if (t < 0) { lstrcpyA(g_s.edit_state, requests_failed(t)); g_snap_dirty = 1; return; }
    g_s.edit_job = t;
    g_edit_kind = reload ? 3 : 1;
    lstrcpyA(g_s.edit_state, "saving");
    g_snap_dirty = 1;
}
static void friends_comment_start(BYTE *b)
{
    int t = pc_comment_save(b, -2, g_comment_want[0] ? g_comment_want : NULL);
    if (t == NO_SLOT) return;
    g_s.comment_wanted = 0;
    base_log("friends: my comment start -> %d\r\n", t);
    if (t < 0) { lstrcpyA(g_s.edit_state, requests_failed(t)); g_snap_dirty = 1; return; }
    g_s.edit_job = t;
    g_edit_kind = 2;
    lstrcpyA(g_s.edit_state, "saving");
    g_snap_dirty = 1;
}
static int friends_comment_text(const char *s, WCHAR *out)
{
    static WCHAR wide[0x400];
    int i;
    if (MultiByteToWideChar(CP_UTF8, 0, s, -1, wide, 0x400) <= 0) return 0;
    for (i = 0; i < 0x31 && wide[i]; i++) out[i] = wide[i] < 0x20 ? L' ' : wide[i];
    if (i == 0x31 && out[i - 1] >= 0xd800 && out[i - 1] < 0xdc00) out[i - 1] = 0;
    return 1;
}
static void friends_edit_request(BYTE *b)
{
    char buf[1024], *p, *op, *tok, *rows, *key, *arg, k[17], nm[16];
    int row, r;
    pol_friend_copy fr;
    if (g_s.answer_kind) return;
    if (requests_take(REQ_FRIEND, buf, sizeof buf) < 0) {
        if (requests_take(REQ_COMMENT, buf, sizeof buf) < 0) return;
        p = buf;
        tok = requests_line(&p);
        lstrcpynA(g_s.edit_token, tok, sizeof g_s.edit_token);
        if (!tok[0] || p == tok + lstrlenA(tok)) { edit_fail("failed:bad request"); return; }
        { char *e = p + lstrlenA(p); while (e > p && (e[-1] == '\n' || e[-1] == '\r')) *--e = 0; }
        memset(g_comment_want, 0, sizeof g_comment_want);
        if (*p && !friends_comment_text(p, g_comment_want)) { edit_fail("failed:bad text"); return; }
        g_s.comment_wanted = 1;
        friends_comment_start(b);
        return;
    }
    p = buf;
    op = requests_line(&p); tok = requests_line(&p); rows = requests_line(&p); key = requests_line(&p);
    arg = requests_line(&p);
    lstrcpynA(g_s.edit_token, tok, sizeof g_s.edit_token);
    if (!requests_parse_num(rows, 4, &row)) { edit_fail("failed:bad request"); return; }
    if (lstrcmpA(op, "unignore") == 0) {
        memset(&fr, 0, sizeof fr);
        if (row >= 100 || pc_ignore_copy(b, row, &fr) < 0 || !pc_friend_valid(&fr.f)) {
            edit_fail("failed:no such entry"); return;
        }
        peers_friend_key(b, &fr.f, k);
        if (lstrcmpA(k, key) != 0) { edit_fail("failed:list changed"); return; }
        r = pc_ignore_remove(b, row);
        base_log("friends: unignore index %d -> %d\r\n", row, r);
        if (r < 0) { lstrcpyA(g_s.edit_state, requests_failed(r)); g_snap_dirty = 1; return; }
        friends_upload_start(b);
        return;
    }
    if (row >= 200 || !peers_get_friend(b, row, &fr)) { edit_fail("failed:no such friend"); return; }
    peers_friend_key(b, &fr.f, k);
    if (lstrcmpA(k, key) != 0) { edit_fail("failed:friend list changed"); return; }
    if ((fr.f.flags & FRIEND_PENDING) && lstrcmpA(op, "delete") != 0) { edit_fail("failed:pending"); return; }
    if (lstrcmpA(op, "rename") == 0) {
        if (!peers_ascii_name_ok(arg)) { edit_fail("failed:bad name"); return; }
        if (lstrcmpA(fr.f.name, arg) == 0) { lstrcpyA(g_s.edit_state, "saved"); g_snap_dirty = 1; return; }
        memset(fr.f.name, 0, sizeof fr.f.name);
        lstrcpynA(fr.f.name, arg, 16);
        pc_set_friend_row(b, row, &fr);
        base_log("friends: rename row %d\r\n", row);
    } else if (lstrcmpA(op, "delete") == 0) {
        r = pc_friend_delete(b, row);
        base_log("friends: delete row %d -> %d\r\n", row, r);
        if (r < 0) { lstrcpyA(g_s.edit_state, requests_failed(r)); g_snap_dirty = 1; return; }
    } else if (lstrcmpA(op, "ignore") == 0) {
        if (peers_is_me(b, fr.f.id_lo, fr.f.id_hi)) { edit_fail("failed:self"); return; }
        memcpy(nm, fr.f.name, 15); nm[15] = 0;
        r = pc_ignore_add(b, -1, arg[0] == '1' ? 1u : 0u, fr.f.id_lo, fr.f.id_hi,
                               (unsigned)pc_friend_slot(&fr.f), nm);
        base_log("friends: ignore row %d (%s) -> %d\r\n", row, arg[0] == '1' ? "every handle" : "this handle", r);
        if (r < 0) { lstrcpyA(g_s.edit_state, requests_failed(r)); g_snap_dirty = 1; return; }
    } else {
        edit_fail("failed:bad request");
        return;
    }
    g_snap_dirty = 1;
    friends_upload_start(b);
}
static struct { DWORD hid_lo, hid_hi; pol_game games[8]; } g_slots_kept[200];
static void friends_slots_keep(BYTE *b)
{
    for (int i = 0; i < 200; i++) {
        const pol_friend *f = pc_friend_row(b, i);
        g_slots_kept[i].hid_lo = f->hid_lo;
        g_slots_kept[i].hid_hi = f->hid_hi;
        memcpy(g_slots_kept[i].games, f->games, sizeof f->games);
    }
}
/* the upload's answer writes the list's game slots over rows presence has filled in, which polcore's own list load
   never does. So a row that's still the same handle gets back what it had */
static void friends_slots_put_back(BYTE *b)
{
    int n = 0;
    for (int i = 0; i < 200; i++) {
        pol_friend *f = pc_friend_row(b, i);
        if (!pc_friend_valid(f) || !(g_slots_kept[i].hid_lo & 1)) continue;
        if (pc_friend_hid(f) != hid_of(g_slots_kept[i].hid_lo, g_slots_kept[i].hid_hi)) continue;
        if (!(f->stamp_lo | f->stamp_hi) && !(f->flags & 0x10000)) continue;
        if (!memcmp(f->games, g_slots_kept[i].games, sizeof f->games)) continue;
        memcpy(f->games, g_slots_kept[i].games, sizeof f->games);
        n++;
    }
    if (!n) return;
    base_log("friends: %d row(s) kept their game slots after the upload\r\n", n);
    g_snap_dirty = 1;
}
static void friends_edit_pump(BYTE *b, int ctl)
{
    if (g_s.edit_job < 0) {
        if (g_s.edit_upload_want) friends_upload_start(b);
        else if (g_s.comment_wanted) friends_comment_start(b);
        else if (ctl && !g_s.draining) friends_edit_request(b);
        return;
    }
    int upload = g_edit_kind == 1, r;
    if (upload) friends_slots_keep(b);
    r = g_edit_kind == 2 ? pc_comment_save_check(b, g_s.edit_job) : pc_friends_save_check(b, g_s.edit_job);
    if (upload) friends_slots_put_back(b);
    if (r == 0) return;
    g_s.edit_job = -1;
    if (r == 1) lstrcpyA(g_s.edit_state, g_edit_kind == 3 ? "failed:reloaded" : "saved");
    else lstrcpyA(g_s.edit_state, requests_failed(r));
    base_log("friends: %s done -> %d\r\n", g_edit_kind == 2 ? "my comment" : "list upload", r);
    g_snap_dirty = 1;
}
