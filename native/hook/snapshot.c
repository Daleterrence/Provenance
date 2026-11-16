enum { SNAPSHOT_NAME_CAP = 64, SNAPSHOT_STATE_CAP = 110, SNAPSHOT_GROUP_CAP = 96, SNAPSHOT_TEXT_CAP = 96,
       SNAPSHOT_COMMENT_CAP = 0x33 * 3 + 8 };

/* the bytes last written, so a snapshot that hasn't changed isn't written again */
static char *g_snap_kept;
static int g_snap_kept_n;
static char g_snap_kept_path[MAX_PATH + 64];
static void snapshot_write(const jw *w)
{
    char path[MAX_PATH + 64];
    DWORD a;
    ipc_json file;
    base_ipc_file(path, IPC_SOCIAL);
    a = GetFileAttributesA(path);
    if (g_snap_kept && g_snap_kept_n == w->n && !lstrcmpA(g_snap_kept_path, path) && memcmp(g_snap_kept, w->p, (size_t)w->n) == 0 &&
        a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY))
        return;
    if (g_snap_kept) HeapFree(GetProcessHeap(), 0, g_snap_kept);
    g_snap_kept = NULL;
    g_snap_kept_n = 0;
    if (!base_ipc_json_open(IPC_SOCIAL, &file)) return;
    if (!base_ipc_json_commit(&file, base_ipc_json_write(&file, w->p, w->n))) return;
    g_snap_kept = (char *)HeapAlloc(GetProcessHeap(), 0, (SIZE_T)w->n + 1);
    if (!g_snap_kept) return;
    memcpy(g_snap_kept, w->p, (size_t)w->n);
    g_snap_kept_n = w->n;
    lstrcpyA(g_snap_kept_path, path);
}

static void snapshot_social(BYTE *b)
{
    char sk[17], cu[0x33 * 3 + 1], u8[0x33 * 3 + 1], hk[17], id[17];
    WCHAR wc[0x34];
    DWORD ids[64];
    int i, k, ng, st, held = 0, have_hn = 0;
    unsigned pic = 0;
    unsigned __int64 me;
    const char *name;
    char hn[64];
    grp_hdr gh;
    grp_mem m;
    jw w = {0};
    if (!b) return;
    mail_inbox_recheck(b);
    jw_obj(&w, NULL);
    jw_lit(&w, "ffxiService", g_ffxi_service ? "active" : "none");
    jw_uint(&w, "notice", g_notice);
    jw_uint(&w, "noticeDays", g_notice_days);
    jw_lit(&w, "presence", g_presence);
    jw_bool(&w, "friendsLoaded", g_friends_loaded);
    jw_bool(&w, "lean", g_lean);
    jw_lit(&w, "mail", g_mail_state);
    jw_bool(&w, "statusBusy", g_s.status_job >= 0 || g_s.status_want >= 0);
    jw_bool(&w, "badPassword", g_bad_password);
    jw_job(&w, "send", g_s.send_token, g_s.send_state);
    jw_end(&w);
    jw_arr(&w, "sends");
    for (i = 0; i < SEND_HIST; i++) {
        k = (g_s.send_hist_next + i) % SEND_HIST;
        if (!g_s.send_hist[k].token[0]) continue;
        jw_obj(&w, NULL);
        jw_str(&w, "token", g_s.send_hist[k].token, 100);
        jw_str(&w, "state", g_s.send_hist[k].state, SNAPSHOT_STATE_CAP);
        jw_uint(&w, "time", g_s.send_hist[k].time);
        jw_uint(&w, "seq", g_s.send_hist[k].seq);
        if (g_s.send_hist[k].from_slot >= 0) {
            jw_int(&w, "fromSlot", g_s.send_hist[k].from_slot);
            jw_bool(&w, "toOther", mail_inbox_polid_ok(b, g_s.send_hist[k].to_lo, g_s.send_hist[k].to_hi) &&
                                   !peers_is_me(b, g_s.send_hist[k].to_lo, g_s.send_hist[k].to_hi));
        }
        jw_end(&w);
    }
    jw_end(&w);
    jw_arr(&w, "returned");
    for (i = 0; i < g_s.returned_n; i++) {
        jw_obj(&w, NULL);
        jw_uint(&w, "time", g_returned[i].time);
        jw_uint(&w, "seq", g_returned[i].seq);
        jw_uint(&w, "fromSlot", g_returned[i].from_slot);
        jw_end(&w);
    }
    jw_end(&w);
    /* the messages the hook has, and whether each one's PUT has started */
    jw_arr(&w, "queue");
    for (i = 0; i < g_s.send_queue_n; i++) {
        jw_obj(&w, NULL);
        jw_str(&w, "token", g_sq[i].token, 100);
        jw_lit(&w, "state", g_sq[i].job >= 0 ? "sending" : "queued");
        jw_end(&w);
    }
    jw_end(&w);
    jw_job(&w, "edit", g_s.edit_token, g_s.edit_state);
    jw_end(&w);
    jw_obj(&w, "pic");
    jw_str(&w, "token", g_s.pic_token, 100);
    jw_lit(&w, "state", g_s.pic_state);
    jw_end(&w);
    profiles_search_mark(b);
    jw_obj(&w, "search");
    jw_str(&w, "token", g_s.search_token, 100);
    jw_lit(&w, "state", g_s.search_state);
    jw_lit(&w, "kind", g_srch_kinds[g_s.search_kind]);
    jw_int(&w, "total", g_s.search_total);
    jw_bool(&w, "tooMany", g_s.search_many);
    jw_arr(&w, "results");
    for (i = 0; i < g_s.search_n; i++) {
        const char *shown = g_s.search_kind == SK_CHARACTER ? g_srch[i].chname : g_srch[i].name;
        jw_obj(&w, NULL);
        jw_int(&w, "index", i);
        jw_sjis(&w, "name", b, (const BYTE *)shown, 0x0f, SNAPSHOT_NAME_CAP);
        jw_sjis(&w, "handle", b, (const BYTE *)g_srch[i].name, 0x0f, SNAPSHOT_NAME_CAP);
        jw_sjis(&w, "world", b, (const BYTE *)g_srch[i].world, 0x0f, SNAPSHOT_NAME_CAP);
        jw_uint(&w, "pic", g_srch[i].pic);
        jw_lit(&w, "key", g_srch[i].key);
        jw_bool(&w, "friend", g_srch[i].isfriend);
        jw_bool(&w, "me", g_srch[i].me);
        jw_end(&w);
    }
    jw_end(&w);
    jw_end(&w);
    jw_job(&w, "me", g_s.me_token, g_s.me_state);
    jw_end(&w);
    jw_job(&w, "group", g_s.grp_token, g_s.grp_state);
    jw_end(&w);
    jw_job(&w, "profile", g_s.prof_token, g_s.prof_state);
    if (g_prof_json && g_prof_json[0] && lstrcmpA(g_s.prof_state, "done") == 0)
        jw_splice(&w, g_prof_json, lstrlenA(g_prof_json));
    jw_end(&w);
    jw_job(&w, "character", g_ch_token, g_ch_state);
    jw_uint(&w, "ctsid", g_ch_ctsid);
    jw_uint(&w, "ctidLow", g_ch_lo);
    jw_uint(&w, "ctidHigh", g_ch_hi);
    if (g_ch_json[0] && lstrcmpA(g_ch_state, "done") == 0) jw_splice(&w, g_ch_json, lstrlenA(g_ch_json));
    jw_end(&w);
    jw_job(&w, "ffxiprofile", g_fp_token, g_fp_state);
    jw_int(&w, "slot", g_fp_slot);
    jw_uint(&w, "ctsid", g_fp_ctsid);
    jw_uint(&w, "ctidLow", g_fp_lo);
    jw_uint(&w, "ctidHigh", g_fp_hi);
    if (g_fp_json[0] && lstrcmpA(g_fp_state, "done") == 0) jw_splice(&w, g_fp_json, lstrlenA(g_fp_json));
    jw_end(&w);
    chat_room_snapshot(b, &w);
    group_chat_snapshot(b, &w);
    if (pc_has(b, SV_GROUP_IDS) && pc_has(b, SV_GROUP_HEADER) && pc_has(b, SV_GROUP_MEMBER)) {
        ng = pc_group_ids(b, ids);
        jw_arr(&w, "groupList");
        for (int g = 0; g < ng && g < 4; g++) {
            if (!groups_get_group(b, ids[2 * g], ids[2 * g + 1], &gh)) continue;
            wsprintfA(id, "%08x%08x", gh.idHi, gh.idLo);
            jw_obj(&w, NULL);
            jw_lit(&w, "id", id);
            jw_sjis(&w, "name", b, (const BYTE *)gh.name, 0x14, SNAPSHOT_GROUP_CAP);
            jw_uint(&w, "myClass", (gh.flags >> 1) & 7);
            jw_uint(&w, "myStatus", (gh.flags >> 10) & 7);
            jw_arr(&w, "members");
            for (int mi = 0; mi < 64; mi++) {
                if (!groups_get_member(b, ids[2 * g], ids[2 * g + 1], mi, &m)) continue;
                m.comment[0x32] = 0;
                cu[0] = 0;
                WideCharToMultiByte(CP_UTF8, 0, m.comment, -1, cu, (int)sizeof cu - 1, NULL, NULL);
                peers_key(b, m.idLo, m.idHi, (m.hHi >> 13) & 0x3f, sk);
                /* +0x20 is a handle word like a handle record's +0x00: H-ID in bits 1-44, slot in 45-50 */
                peers_hid_key(hid_of(m.hLo, m.hHi), hk);
                jw_obj(&w, NULL);
                jw_int(&w, "index", mi);
                jw_sjis(&w, "name", b, (const BYTE *)m.name, 0x0f, SNAPSHOT_NAME_CAP);
                jw_lit(&w, "key", sk);
                jw_uint(&w, "slot", (m.hHi >> 13) & 0x3f);
                jw_lit(&w, "hid", hk);
                jw_bool(&w, "mine", peers_is_me(b, m.idLo, m.idHi));
                jw_uint(&w, "pic", m.pic);
                jw_uint(&w, "class", (m.flags >> 18) & 7);
                jw_uint(&w, "status", (m.flags >> 4) & 7);
                jw_bool(&w, "me", (m.flags >> 21) & 1);
                jw_bool(&w, "ignored", (m.flags >> 1) & 1);
                jw_bool(&w, "inChat", (m.flags >> 22) & 1);
                jw_sjis(&w, "text", b, (const BYTE *)m.text, 0x17, SNAPSHOT_TEXT_CAP);
                jw_str(&w, "comment", cu, SNAPSHOT_COMMENT_CAP);
                jw_end(&w);
            }
            jw_end(&w);
            jw_end(&w);
        }
        jw_end(&w);
    }
    /* GetMyComment (svc 0x688) and GetMyStatus (0x338) are local reads, fine every 2s */
    if (pc_has(b, SV_MY_COMMENT)) {
        memset(wc, 0, sizeof wc);
        pc_my_comment(b, -2, wc);
        wc[0x33] = 0;
        u8[0] = 0;
        WideCharToMultiByte(CP_UTF8, 0, wc, -1, u8, (int)sizeof u8 - 1, NULL, NULL);
        u8[sizeof u8 - 1] = 0;
        jw_str(&w, "myComment", u8, 0x33 * 6 + 8);
    }
    jw_arr(&w, "blocked");
    for (int bi = 0; bi < 100; bi++) {
        const pol_friend *a = pc_ignore_row(b, bi);
        if (IsBadReadPtr(a, sizeof *a)) break;
        if (!pc_friend_valid(a)) continue;
        peers_friend_key(b, a, sk);
        jw_obj(&w, NULL);
        jw_int(&w, "index", bi);
        jw_sjis(&w, "name", b, (const BYTE *)a->name, 0x0f, SNAPSHOT_NAME_CAP);
        jw_lit(&w, "key", sk);
        jw_bool(&w, "allHandles", a->flags & 3);
        jw_end(&w);
    }
    jw_end(&w);
    st = pc_my_status_or(b, 4);
    name = st == 0 ? "online" : st == 1 ? "away" : st == 2 ? "busy" : "appear-offline";
    jw_lit(&w, "myStatus", name);
    /* the current handle's record: +0x08 the name in SJIS, +0x1c the profile pic id */
    if (pc_has(b, SV_CURRENT_HANDLE) && pc_has(b, SV_GET_HANDLE)) {
        int slot = pc_current_handle(b);
        pol_handle rec = {0};
        if (slot < 0) slot = g_my_slot;
        if (pc_get_handle(b, (unsigned)slot, &rec) == 0) {
            json_sjis(b, hn, sizeof hn, (const BYTE *)rec.name, 0x0f);
            have_hn = 1;
            pic = rec.pic;
        }
    }
    jw_raw(&w, "myHandle", have_hn ? hn : "\"\"");
    me = pc_my_id(b);
    sk[0] = 0;
    if (me) peers_key(b, (unsigned)me, (unsigned)(me >> 32), (unsigned)g_my_slot, sk);
    jw_lit(&w, "myKey", sk);
    jw_uint(&w, "myPic", pic);
    handles_json(b, &w);
    jw_arr(&w, "friends");
    for (i = 0; i < 200; i++) {
        const pol_friend *a = pc_friend_row(b, i);
        const pol_friend_extra *bn = pc_friend_extra(b, i);
        WCHAR fw[0x33] = {0};
        char combuf[0x33 * 3 + 1] = {0};
        DWORD cs, cl, chi;
        if (IsBadReadPtr(a, sizeof *a) || IsBadReadPtr(bn, sizeof *bn)) break;
        if (!pc_friend_valid(a)) continue;   /* polcore.dll 0x10024d30 */
        /* the comment's UTF-16 while the names are SJIS */
        memcpy(fw, bn->comment, 0x32 * sizeof(WCHAR));
        WideCharToMultiByte(CP_UTF8, 0, fw, -1, combuf, (int)sizeof combuf - 1, NULL, NULL);
        peers_friend_key(b, a, sk);
        jw_obj(&w, NULL);
        jw_int(&w, "row", i);
        jw_lit(&w, "key", sk);
        jw_sjis(&w, "name", b, (const BYTE *)a->name, 0x0f, SNAPSHOT_NAME_CAP);
        jw_str(&w, "comment", combuf, 192);
        jw_lit(&w, "status", peers_friend_status(a->flags));
        jw_bool(&w, "online", peers_friend_online(a->flags));
        jw_bool(&w, "pending", a->flags & FRIEND_PENDING);
        jw_uint(&w, "pic", bn->pic);
        jw_bool(&w, "me", peers_is_me(b, a->id_lo, a->id_hi));
        if (peers_friend_ffxi(a, &cs, &cl, &chi)) {
            jw_obj(&w, "game");
            jw_uint(&w, "world", (cs >> 16) & 0xff);
            jw_uint(&w, "ctsid", cs);
            jw_uint(&w, "ctidLow", cl);
            jw_uint(&w, "ctidHigh", chi);
            jw_end(&w);
        }
        jw_end(&w);
    }
    jw_end(&w);
    jw_arr(&w, "messages");
    /* states 0-3, oldest first since slots get reused */
    for (i = mail_inbox_next(0, 0xf); i >= 0; i = mail_inbox_next(g_mail[i].mid, 0xf)) {
        const mailrec *ml = &g_mail[i];
        int hold = mail_inbox_held(ml) || mail_inbox_undecided(b, ml) || ml->raw_pending;
        held += hold;
        message_store_msg_json(b, &w, ml->hdr, ml->mid,
                      hold ? "pending" : ml->state == 3 ? "realtime" : ml->state == 2 ? "failed"
                                                     : ml->state == 1 ? "read" : "pending",
                      -1,   /* no read bit, a server copy hasn't got one */
                      hold ? "" : ml->subj, hold ? "" : ml->text, ml->has_att && !hold ? ml->att : NULL);
    }
    jw_end(&w);
    jw_end(&w);
    if (!w.fail) snapshot_write(&w);
    jw_free(&w);
    if (g_mail_fresh && !held) {
        g_mail_fresh = 0;
        base_log("messages: snapshot has the new message\r\n");
    }
}
