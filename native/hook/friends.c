static int g_answer_kind;

static char g_edit_token[48];
static char g_edit_state[48] = "";
static int g_edit_job = -1;
static int g_edit_kind;   /* 1 list upload, 2 comment, 3 upload that became a reload */
static int g_edit_upload_want;
static WCHAR g_comment_want[0x33];
static int g_comment_wanted;
typedef int (__cdecl *svc_add)(int, unsigned, unsigned, unsigned, unsigned, const char *);

#define edit_fail(why) refuse(g_edit_state, sizeof g_edit_state, "friends: edit refused - %s\r\n", why)
/* like the viewer, a local edit (svc 0x5f4/0x2f0/0x2e4/0x2e8) marks the row, then one list upload sends it */
static void friend_upload_start(BYTE *b)
{
    int reload = *(unsigned *)(b + 0x403040) > *(unsigned *)(b + 0x40304c);
    int t = ((svc_v)SVC(0x1028))();
    if (t == NO_SLOT) { g_edit_upload_want = 1; return; }
    g_edit_upload_want = 0;
    lg("friends: list upload start -> %d%s\r\n", t, reload ? " (the server list is newer: reload)" : "");
    if (t < 0) { wsprintfA(g_edit_state, "failed:%d", t); g_snap_dirty = 1; return; }
    g_edit_job = t;
    g_edit_kind = reload ? 3 : 1;
    lstrcpyA(g_edit_state, "saving");
    g_snap_dirty = 1;
}
static void comment_start(BYTE *b)
{
    int t = ((int (__cdecl *)(int, const WCHAR *))SVC(0x67c))(-2, g_comment_want[0] ? g_comment_want : NULL);
    if (t == NO_SLOT) return;
    g_comment_wanted = 0;
    lg("friends: my comment start -> %d\r\n", t);
    if (t < 0) { wsprintfA(g_edit_state, "failed:%d", t); g_snap_dirty = 1; return; }
    g_edit_job = t;
    g_edit_kind = 2;
    lstrcpyA(g_edit_state, "saving");
    g_snap_dirty = 1;
}
/* UTF-8 -> a comment of 0x31 UTF-16 chars at most (into a zeroed out), control chars as spaces, never half a
   surrogate pair. 0 = not valid UTF-8. */
static int comment_text(const char *s, WCHAR *out)
{
    static WCHAR wide[0x400];
    int i;
    if (MultiByteToWideChar(CP_UTF8, 0, s, -1, wide, 0x400) <= 0) return 0;
    for (i = 0; i < 0x31 && wide[i]; i++) out[i] = wide[i] < 0x20 ? L' ' : wide[i];
    if (i == 0x31 && out[i - 1] >= 0xd800 && out[i - 1] < 0xdc00) out[i - 1] = 0;
    return 1;
}
static void friend_edit_request(BYTE *b)
{
    char buf[1024], *p, *op, *tok, *rows, *key, *arg, k[17], nm[16];
    int row, r;
    BYTE fr[0x140];
    if (g_answer_kind) return;
    if (take_request(REQ_FRIEND, buf, sizeof buf) < 0) {
        if (take_request(REQ_COMMENT, buf, sizeof buf) < 0) return;
        p = buf;
        tok = req_line(&p);
        lstrcpynA(g_edit_token, tok, sizeof g_edit_token);
        /* no token line means it isn't the launcher's, and a stray empty file mustn't clear the comment */
        if (!tok[0] || p == tok + lstrlenA(tok)) { edit_fail("failed:bad request"); return; }
        { char *e = p + lstrlenA(p); while (e > p && (e[-1] == '\n' || e[-1] == '\r')) *--e = 0; }
        memset(g_comment_want, 0, sizeof g_comment_want);
        if (*p && !comment_text(p, g_comment_want)) { edit_fail("failed:bad text"); return; }
        g_comment_wanted = 1;
        comment_start(b);
        return;
    }
    p = buf;
    op = req_line(&p); tok = req_line(&p); rows = req_line(&p); key = req_line(&p); arg = req_line(&p);
    lstrcpynA(g_edit_token, tok, sizeof g_edit_token);
    if (!parse_num(rows, 4, &row)) { edit_fail("failed:bad request"); return; }
    /* <key> is the row's snapshot key, so an edit never lands on a row that changed */
    if (lstrcmpA(op, "unignore") == 0) {
        memset(fr, 0, sizeof fr);
        if (row >= 100 || ((svc_ip)SVC(0x2a4))(row, fr) < 0 || !(*(DWORD *)(fr + 0x98) & 1)) {
            edit_fail("failed:no such entry"); return;
        }
        friend_key(b, fr, k);
        if (lstrcmpA(k, key) != 0) { edit_fail("failed:list changed"); return; }
        r = ((svc_i)SVC(0x2e8))(row);
        lg("friends: unignore index %d -> %d\r\n", row, r);
        if (r < 0) { wsprintfA(g_edit_state, "failed:%d", r); g_snap_dirty = 1; return; }
        friend_upload_start(b);
        return;
    }
    if (row >= 200 || !get_friend(b, row, fr)) { edit_fail("failed:no such friend"); return; }
    friend_key(b, fr, k);
    if (lstrcmpA(k, key) != 0) { edit_fail("failed:friend list changed"); return; }
    if ((*(DWORD *)(fr + 0x08) & 0x10000000) && lstrcmpA(op, "delete") != 0) { edit_fail("failed:pending"); return; }
    if (lstrcmpA(op, "rename") == 0) {
        if (!ascii_name_ok(arg)) { edit_fail("failed:bad name"); return; }
        if (lstrcmpA((const char *)fr + 0xa0, arg) == 0) { lstrcpyA(g_edit_state, "saved"); g_snap_dirty = 1; return; }
        memset(fr + 0xa0, 0, 0x10);
        lstrcpynA((char *)fr + 0xa0, arg, 16);
        ((void (__cdecl *)(int, void *))SVC(0x5f4))(row, fr);
        lg("friends: rename row %d\r\n", row);
    } else if (lstrcmpA(op, "delete") == 0) {
        r = ((svc_i)SVC(0x2f0))(row);
        lg("friends: delete row %d -> %d\r\n", row, r);
        if (r < 0) { wsprintfA(g_edit_state, "failed:%d", r); g_snap_dirty = 1; return; }
    } else if (lstrcmpA(op, "ignore") == 0) {
        if (is_me(b, *(DWORD *)(fr + 0x00), *(DWORD *)(fr + 0x04))) { edit_fail("failed:self"); return; }
        memcpy(nm, fr + 0xa0, 15); nm[15] = 0;
        /* AddBlock takes them off the friend list as well (polcore.dll 0x10024ad0) */
        r = ((svc_add)SVC(0x2e4))(-1, arg[0] == '1' ? 1u : 0u, *(DWORD *)(fr + 0x00), *(DWORD *)(fr + 0x04),
                                  FR_SLOT(fr), nm);
        lg("friends: ignore row %d (%s) -> %d\r\n", row, arg[0] == '1' ? "every handle" : "this handle", r);
        if (r < 0) { wsprintfA(g_edit_state, "failed:%d", r); g_snap_dirty = 1; return; }
    } else {
        edit_fail("failed:bad request");
        return;
    }
    g_snap_dirty = 1;
    friend_upload_start(b);
}
static void friend_edit_pump(BYTE *b, int ctl)
{
    if (g_edit_job < 0) {
        if (g_edit_upload_want) friend_upload_start(b);   /* applied locally already, so it still goes in a drain */
        else if (g_comment_wanted) comment_start(b);
        else if (ctl && !g_draining) friend_edit_request(b);
        return;
    }
    /* a comment (svc 0x67c) is polled with 0x684. Not 0x680, however much it looks like it should be */
    int r = ((svc_i)SVC(g_edit_kind == 2 ? 0x684 : 0x102c))(g_edit_job);
    if (r == 0) return;
    g_edit_job = -1;
    if (r == 1) lstrcpyA(g_edit_state, g_edit_kind == 3 ? "failed:reloaded" : "saved");
    else wsprintfA(g_edit_state, "failed:%d", r);
    lg("friends: %s done -> %d\r\n", g_edit_kind == 2 ? "my comment" : "list upload", r);
    g_snap_dirty = 1;
}
