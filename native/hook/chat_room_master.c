/* the room master's kick, designate and password change (app.dll 0x1009ba31). Each step is one command or one frame,
   and each waits for the last */
static void chat_room_master_next(void)
{
    if (!g_cr_mop) return;
    if (++g_cr_mi >= g_cr_mn) {
        if (g_cr_mop == MO_KEY) lstrcpynA(g_cr_key, g_cr_newkey, sizeof g_cr_key);
        chat_room_master_end("done");
    }
}
static void chat_room_master_step(BYTE *b)
{
    WCHAR k[32];
    int r = 0;
    switch (g_cr_msteps[g_cr_mi]) {
    case MS_BAN: r = chat_room_mode(b, 4, g_cr_mlo, g_cr_mhi, NULL, 1); break;
    case MS_KICK: {
        int s = chat_frame_session(b);
        r = s < 0 ? NO_CHAT_LINK : pc_chat_kick(b, s, g_cr_room, g_cr_mlo, g_cr_mhi);
        break;
    }
    case MS_OP: r = chat_room_mode(b, 0xc, g_cr_mlo, g_cr_mhi, NULL, 1); break;
    case MS_UNKEY: r = chat_room_mode(b, 8, 0, 0, g_cr_key, 0); break;
    case MS_KEY: r = chat_room_mode(b, 8, 0, 0, g_cr_newkey, 1); break;
    /* the viewer shows its own line for the kick and the new master as the frame goes */
    case MS_KICKFRAME:
        chat_frame_add_line(CRK_KICKED, 0, NULL, g_cr_mname, NULL);
        chat_room_frame_tail(b, CRM_KICK, g_cr_mname);
        return;
    case MS_OPFRAME:
        chat_frame_add_line(CRK_MASTER, 0, NULL, g_cr_mname, NULL);
        chat_room_frame_tail(b, CRM_MASTER, g_cr_mname);
        return;
    case MS_KEYFRAME:
        MultiByteToWideChar(CP_ACP, 0, g_cr_newkey, -1, k, 32);
        chat_room_frame_tail(b, CRM_KEY, k);
        SecureZeroMemory(k, sizeof k);
        return;
    }
    if (r == 0) {
        if (++g_s.cr_tries < CR_TRIES) return;
        r = -1;
    }
    g_s.cr_tries = 0;
    if (r < 0) { chat_room_master_end(requests_failed(r)); return; }
    g_s.cr_wait = CRW_CMD;
    g_cr_sent_at = GetTickCount64();
}
/* 401 (no such nick): the member's already gone */
static void chat_room_master_done(int r)
{
    g_s.cr_wait = CRW_NONE;
    if (r < 0) { chat_room_master_end(r == -0x1192 ? "failed:left" : requests_failed(r)); return; }
    chat_room_master_next();
}
static void chat_room_master_refused(const char *state)
{
    SecureZeroMemory(g_cr_newkey, sizeof g_cr_newkey);
    lstrcpynA(g_cr_mstate, state, sizeof g_cr_mstate);
    base_log("chat rooms: master request refused - %s\r\n", state);
    g_snap_dirty = 1;
}
static int chat_room_password(const char *tok, char *key, int cap);
/* <token>, <1 when there's a new password in Credential Manager>, kick|master|password, <member key> */
static void chat_room_master_request(void)
{
    char buf[256], *p, *tok, *haskey, *op, *key, nk[sizeof g_cr_newkey];
    cr_person *me = NULL, *who = NULL;
    int k = -1, pw = 0;
    if (requests_take(REQ_CHATMASTER, buf, sizeof buf) < 0) return;
    p = buf;
    tok = requests_line(&p); haskey = requests_line(&p); op = requests_line(&p); key = requests_line(&p);
    nk[0] = 0;
    if (haskey[0]) pw = chat_room_password(tok, nk, sizeof nk);
    if (g_cr_mop) {
        SecureZeroMemory(nk, sizeof nk);
        base_log("chat rooms: master request ignored, one's still going\r\n");
        return;
    }
    lstrcpynA(g_cr_mtok, tok, sizeof g_cr_mtok);
    lstrcpynA(g_cr_newkey, nk, sizeof g_cr_newkey);
    SecureZeroMemory(nk, sizeof nk);
    if (g_s.cr_st != CR_IN || g_s.cr_leave) { chat_room_master_refused("failed:not in a room"); return; }
    for (int i = 0; i < g_cr_people_n; i++) {
        cr_person *q = &g_cr_people[i];
        if (!q->member) continue;
        if (chat_frame_is_me(q->lo, q->hi)) me = q;
        else if (requests_parse_num(key, 9, &k) && q->key == k) who = q;
    }
    if (!me || !me->master) { chat_room_master_refused("failed:not master"); return; }
    g_cr_mn = g_cr_mi = 0;
    if (!lstrcmpA(op, "password")) {
        if (haskey[0] && (!pw || !chat_room_new_key_ok(g_cr_newkey))) {
            chat_room_master_refused("failed:no password"); return;
        }
        if (*(DWORD *)(g_cr_slot + 0x8c) & (1u << 8)) g_cr_msteps[g_cr_mn++] = MS_UNKEY;
        if (haskey[0]) { g_cr_msteps[g_cr_mn++] = MS_KEY; g_cr_msteps[g_cr_mn++] = MS_KEYFRAME; }
        if (!g_cr_mn) { chat_room_master_refused("done"); return; }
        g_cr_mop = MO_KEY;
    } else if (!lstrcmpA(op, "kick") || !lstrcmpA(op, "master")) {
        BYTE *m = NULL;
        if (!who) { chat_room_master_refused("failed:left"); return; }
        g_cr_mlo = who->lo;
        g_cr_mhi = who->hi;
        lstrcpynW(g_cr_mname, who->name, 0x21);
        if (op[0] == 'k') {
            for (int i = 0; i < 0x40; i++)
                if ((g_cr_mem[i * 16 + 0xc] & 1) && *(DWORD *)(g_cr_mem + i * 16) == who->lo &&
                    *(DWORD *)(g_cr_mem + i * 16 + 4) == who->hi)
                    m = g_cr_mem + i * 16;
            /* banned first so a kicked member can't just come back, unless they're banned already */
            if (!m || !(*(DWORD *)(m + 8) & (1u << 4))) g_cr_msteps[g_cr_mn++] = MS_BAN;
            g_cr_msteps[g_cr_mn++] = MS_KICKFRAME;
            g_cr_msteps[g_cr_mn++] = MS_KICK;
            g_cr_mop = MO_KICK;
        } else {
            g_cr_msteps[g_cr_mn++] = MS_OP;
            g_cr_msteps[g_cr_mn++] = MS_OPFRAME;
            g_cr_mop = MO_MASTER;
        }
    } else {
        chat_room_master_refused("failed:bad request");
        return;
    }
    g_s.cr_tries = 0;
    lstrcpyA(g_cr_mstate, "working");
    base_log("chat rooms: master job %d, %d step(s)\r\n", g_cr_mop, g_cr_mn);
    g_snap_dirty = 1;
}
