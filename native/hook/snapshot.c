static void snapshot_social(BYTE *b)
{
    static char one[900], mj[28000];
    char path[MAX_PATH + 64], tmp[MAX_PATH + 80], line[900], nm[64], cm[192], gm[112], tk[100], sj[110], sk[17];
    char gn[96], mt[96], mc[0x33 * 3 + 8], cu[0x33 * 3 + 1], u8[0x33 * 3 + 1], js[0x33 * 6 + 8], hn[64] = "\"\"", hk[17];
    WCHAR wc[0x34];
    DWORD n, ids[8];
    int i, k, ng, st, first = 1, held = 0;
    unsigned pic = 0;
    unsigned __int64 me;
    const char *name;
    grp_hdr gh;
    grp_mem m;
    if (!b) return;
    mail_inbox_recheck(b);
    base_ipc_file(path, IPC_SOCIAL);
    lstrcpyA(tmp, path); lstrcatA(tmp, ".tmp");
    HANDLE h = CreateFileA(tmp, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    wsprintfA(line, "{\"ffxiService\":\"%s\",\"notice\":%u,\"noticeDays\":%u,\"presence\":\"%s\","
                    "\"friendsLoaded\":%s,\"mail\":\"%s\",\"statusBusy\":%s,\"badPassword\":%s",
              g_ffxi_service ? "active" : "none", g_notice, g_notice_days,
              g_presence, g_friends_loaded ? "true" : "false",
              g_mail_state, (g_s.status_job >= 0 || g_s.status_want >= 0) ? "true" : "false",
              g_bad_password ? "true" : "false");
    json_write(h, line);
    json_write_job(h, ",\"send\":{\"token\":%s,\"state\":\"%s\"}", g_s.send_token, g_s.send_state);
    json_write(h, ",\"sends\":[");
    for (i = 0; i < SEND_HIST; i++) {
        k = (g_s.send_hist_next + i) % SEND_HIST;
        if (!g_s.send_hist[k].token[0]) continue;
        json_str(tk, sizeof tk, g_s.send_hist[k].token);
        json_str(sj, sizeof sj, g_s.send_hist[k].state);
        n = (DWORD)wsprintfA(line, "%s{\"token\":%s,\"state\":%s,\"time\":%lu,\"seq\":%lu", first ? "" : ",", tk, sj,
                             g_s.send_hist[k].time, g_s.send_hist[k].seq);
        if (g_s.send_hist[k].from_slot >= 0)
            n += (DWORD)wsprintfA(line + n, ",\"fromSlot\":%d,\"toOther\":%s", g_s.send_hist[k].from_slot,
                                  mail_inbox_polid_ok(b, g_s.send_hist[k].to_lo, g_s.send_hist[k].to_hi) &&
                                  !peers_is_me(b, g_s.send_hist[k].to_lo, g_s.send_hist[k].to_hi) ? "true" : "false");
        lstrcpyA(line + n, "}");
        json_write(h, line);
        first = 0;
    }
    json_write(h, "],\"returned\":[");
    for (i = 0; i < g_s.returned_n; i++) {
        wsprintfA(line, "%s{\"time\":%lu,\"seq\":%lu,\"fromSlot\":%u}", i ? "," : "", g_returned[i].time,
                  g_returned[i].seq, g_returned[i].from_slot);
        json_write(h, line);
    }
    /* the messages the hook has, and whether each one's PUT has started */
    json_write(h, "],\"queue\":[");
    for (i = 0; i < g_s.sq_n; i++) {
        json_str(tk, sizeof tk, g_sq[i].token);
        wsprintfA(line, "%s{\"token\":%s,\"state\":\"%s\"}", i ? "," : "", tk, g_sq[i].job >= 0 ? "sending" : "queued");
        json_write(h, line);
    }
    json_write(h, "]");
    json_write_job(h, ",\"edit\":{\"token\":%s,\"state\":\"%s\"}", g_s.edit_token, g_s.edit_state);
    json_str(tk, sizeof tk, g_s.pic_token);
    wsprintfA(line, ",\"pic\":{\"token\":%s,\"want\":%d,\"state\":\"%s\"}", tk, g_s.pic_want, g_s.pic_state);
    json_write(h, line);
    profiles_search_mark(b);
    json_str(tk, sizeof tk, g_s.srch_token);
    wsprintfA(line, ",\"search\":{\"token\":%s,\"state\":\"%s\",\"kind\":\"%s\",\"total\":%d,\"tooMany\":%s,\"results\":[",
              tk, g_s.srch_state, g_srch_kinds[g_s.srch_kind], g_s.srch_total, g_s.srch_many ? "true" : "false");
    json_write(h, line);
    for (i = 0; i < g_s.srch_n; i++) {
        char hd[64], wd[64];
        const char *shown = g_s.srch_kind == SK_CHARACTER ? g_srch[i].chname : g_srch[i].name;
        json_sjis(nm, sizeof nm, (const BYTE *)shown, 0x0f);
        json_sjis(hd, sizeof hd, (const BYTE *)g_srch[i].name, 0x0f);
        json_sjis(wd, sizeof wd, (const BYTE *)g_srch[i].world, 0x0f);
        wsprintfA(line, "%s{\"index\":%d,\"name\":%s,\"handle\":%s,\"world\":%s,\"pic\":%u,\"key\":\"%s\",\"friend\":%s,"
                  "\"me\":%s}",
                  i ? "," : "", i, nm, hd, wd, g_srch[i].pic, g_srch[i].key, g_srch[i].isfriend ? "true" : "false",
                  g_srch[i].me ? "true" : "false");
        json_write(h, line);
    }
    json_write(h, "]}");
    json_write_job(h, ",\"me\":{\"token\":%s,\"state\":\"%s\"}", g_s.me_token, g_s.me_state);
    json_write_job(h, ",\"xi\":{\"token\":%s,\"state\":\"%s\"", g_s.xi_token, g_s.xi_state);
    if (g_s.xi_json[0] && !lstrcmpA(g_s.xi_state, "ready")) { json_write(h, ","); json_write(h, g_s.xi_json); }
    json_write(h, "}");
    json_write_job(h, ",\"group\":{\"token\":%s,\"state\":\"%s\"}", g_s.grp_token, g_s.grp_state);
    json_write_job(h, ",\"profile\":{\"token\":%s,\"state\":\"%s\"", g_s.prof_token, g_s.prof_state);
    if (g_prof_json && g_prof_json[0] && lstrcmpA(g_s.prof_state, "done") == 0) {
        json_write(h, ","); json_write(h, g_prof_json);
    }
    json_write(h, "}");
    json_write_job(h, ",\"character\":{\"token\":%s,\"state\":\"%s\"", g_ch_token, g_ch_state);
    wsprintfA(line, ",\"ctsid\":%u,\"ctidLow\":%u,\"ctidHigh\":%u", g_ch_ctsid, g_ch_lo, g_ch_hi);
    json_write(h, line);
    if (g_ch_json[0] && lstrcmpA(g_ch_state, "done") == 0) { json_write(h, ","); json_write(h, g_ch_json); }
    json_write(h, "}");
    json_write_job(h, ",\"ffxiprofile\":{\"token\":%s,\"state\":\"%s\"", g_fp_token, g_fp_state);
    wsprintfA(line, ",\"slot\":%d,\"ctsid\":%u,\"ctidLow\":%u,\"ctidHigh\":%u", g_fp_slot, g_fp_ctsid, g_fp_lo,
              g_fp_hi);
    json_write(h, line);
    if (g_fp_json[0] && lstrcmpA(g_fp_state, "done") == 0) { json_write(h, ","); json_write(h, g_fp_json); }
    json_write(h, "}");
    chat_room_snapshot(b, h);
    group_chat_snapshot(b, h);
    if (pc_has(b, SV_GROUP_IDS) && pc_has(b, SV_GROUP_HEADER) && pc_has(b, SV_GROUP_MEMBER)) {
        ng = pc_group_ids(b, ids);
        json_write(h, ",\"groupList\":[");
        for (int g = 0, gfirst = 1; g < ng && g < 4; g++) {
            if (!groups_get_group(b, ids[2 * g], ids[2 * g + 1], &gh)) continue;
            json_sjis(gn, sizeof gn, (const BYTE *)gh.name, 0x14);
            wsprintfA(one, "%s{\"id\":\"%08x%08x\",\"name\":%s,\"myClass\":%u,\"myStatus\":%u,\"members\":[",
                      gfirst ? "" : ",", gh.idHi, gh.idLo, gn, (gh.flags >> 1) & 7, (gh.flags >> 10) & 7);
            json_write(h, one);
            gfirst = 0;
            for (int mi = 0, mfirst = 1; mi < 64; mi++) {
                if (!groups_get_member(b, ids[2 * g], ids[2 * g + 1], mi, &m)) continue;
                json_sjis(nm, sizeof nm, (const BYTE *)m.name, 0x0f);
                json_sjis(mt, sizeof mt, (const BYTE *)m.text, 0x17);
                m.comment[0x32] = 0;
                cu[0] = 0;
                WideCharToMultiByte(CP_UTF8, 0, m.comment, -1, cu, (int)sizeof cu - 1, NULL, NULL);
                json_str(mc, sizeof mc, cu);
                peers_key(b, m.idLo, m.idHi, (m.hHi >> 13) & 0x3f, sk);
                /* +0x20 is a handle word like a handle record's +0x00: H-ID in bits 1-44, slot in 45-50 */
                peers_hid_key(hid_of(m.hLo, m.hHi), hk);
                wsprintfA(one, "%s{\"index\":%d,\"name\":%s,\"key\":\"%s\",\"slot\":%u,\"hid\":\"%s\",\"mine\":%s,"
                               "\"pic\":%u,\"class\":%u,\"status\":%u,\"me\":%s,\"ignored\":%s,\"inChat\":%s,\"text\":%s,"
                               "\"comment\":%s}",
                          mfirst ? "" : ",", mi, nm, sk, (m.hHi >> 13) & 0x3f, hk, peers_is_me(b, m.idLo, m.idHi) ? "true" : "false",
                          m.pic, (m.flags >> 18) & 7, (m.flags >> 4) & 7,
                          ((m.flags >> 21) & 1) ? "true" : "false", ((m.flags >> 1) & 1) ? "true" : "false",
                          ((m.flags >> 22) & 1) ? "true" : "false", mt, mc);
                json_write(h, one);
                mfirst = 0;
            }
            json_write(h, "]}");
        }
        json_write(h, "]");
    }
    /* GetMyComment (svc 0x688) and GetMyStatus (0x338) are local reads, fine every 2 s */
    if (pc_has(b, SV_MY_COMMENT)) {
        memset(wc, 0, sizeof wc);
        pc_my_comment(b, -2, wc);
        wc[0x33] = 0;
        u8[0] = 0;
        WideCharToMultiByte(CP_UTF8, 0, wc, -1, u8, (int)sizeof u8 - 1, NULL, NULL);
        json_str(js, sizeof js, u8);
        json_write(h, ",\"myComment\":"); json_write(h, js);
    }
    json_write(h, ",\"blocked\":[");
    for (int bi = 0, bfirst = 1; bi < 100; bi++) {
        const pol_friend *a = ignore_row(b, bi);
        if (IsBadReadPtr(a, sizeof *a)) break;
        if (!friend_valid(a)) continue;
        json_sjis(nm, sizeof nm, (const BYTE *)a->name, 0x0f);
        peers_friend_key(b, a, sk);
        wsprintfA(line, "%s{\"index\":%d,\"name\":%s,\"key\":\"%s\",\"allHandles\":%s}", bfirst ? "" : ",", bi, nm, sk,
                  (a->flags & 3) ? "true" : "false");
        json_write(h, line);
        bfirst = 0;
    }
    json_write(h, "]");
    st = pc_has(b, SV_MY_STATUS) ? pc_my_status(b) : 4;
    name = st == 0 ? "online" : st == 1 ? "away" : st == 2 ? "busy" : "appear-offline";
    json_write(h, ",\"myStatus\":\""); json_write(h, name); json_write(h, "\"");
    /* the current handle's record: +0x08 the name in SJIS, +0x1c the profile pic id */
    if (pc_has(b, SV_CURRENT_HANDLE) && pc_has(b, SV_GET_HANDLE)) {
        int slot = pc_current_handle(b);
        pol_handle rec = {0};
        if (slot < 0) slot = g_my_slot;
        if (pc_get_handle(b, (unsigned)slot, &rec) == 0) {
            json_sjis(hn, sizeof hn, (const BYTE *)rec.name, 0x0f);
            pic = rec.pic;
        }
    }
    json_write(h, ",\"myHandle\":"); json_write(h, hn);
    me = pc_my_id(b);
    sk[0] = 0;
    if (me) peers_key(b, (unsigned)me, (unsigned)(me >> 32), (unsigned)g_my_slot, sk);
    json_write(h, ",\"myKey\":\""); json_write(h, sk); json_write(h, "\"");
    wsprintfA(line, ",\"myPic\":%u", pic);
    json_write(h, line);
    handles_json(b, h);
    json_write(h, ",\"friends\":[");
    for (i = 0, first = 1; i < 200; i++) {
        const pol_friend *a = friend_row(b, i);
        const pol_friend_extra *bn = friend_extra(b, i);
        WCHAR fw[0x33] = {0};
        char combuf[0x33 * 3 + 1] = {0};
        unsigned idLo, idHi, fa;
        DWORD cs, cl, chi;
        if (IsBadReadPtr(a, sizeof *a) || IsBadReadPtr(bn, sizeof *bn)) break;
        if (!friend_valid(a)) continue;   /* polcore.dll 0x10024d30 */
        idLo = a->id_lo; idHi = a->id_hi;
        fa = a->flags;
        json_sjis(nm, sizeof nm, (const BYTE *)a->name, 0x0f);
        /* the comment's UTF-16 while the names are SJIS (learnt that from a list of one-letter comments :D) */
        memcpy(fw, bn->comment, 0x32 * sizeof(WCHAR));
        WideCharToMultiByte(CP_UTF8, 0, fw, -1, combuf, (int)sizeof combuf - 1, NULL, NULL);
        json_str(cm, sizeof cm, combuf);
        peers_friend_key(b, a, sk);
        gm[0] = 0;
        if (peers_friend_ffxi(a, &cs, &cl, &chi))
            wsprintfA(gm, ",\"game\":{\"world\":%u,\"ctsid\":%u,\"ctidLow\":%u,\"ctidHigh\":%u}", (cs >> 16) & 0xff,
                      cs, cl, chi);
        wsprintfA(line, "%s{\"row\":%d,\"key\":\"%s\",\"name\":%s,\"comment\":%s,\"status\":\"%s\",\"online\":%s,"
                        "\"pending\":%s,\"pic\":%u,\"me\":%s%s}",
                  first ? "" : ",", i, sk, nm, cm, peers_friend_status(fa),
                  peers_friend_online(fa) ? "true" : "false", (fa & FRIEND_PENDING) ? "true" : "false",
                  bn->pic, peers_is_me(b, idLo, idHi) ? "true" : "false", gm);
        json_write(h, line);
        first = 0;
    }
    json_write(h, "],\"messages\":[");
    /* states 0-3, oldest first since slots get reused */
    for (i = mail_inbox_next(0, 0xf), first = 1; i >= 0; i = mail_inbox_next(g_mail[i].mid, 0xf)) {
        const mailrec *ml = &g_mail[i];
        int o = 0, hold = mail_inbox_held(ml) || mail_inbox_undecided(b, ml);
        held += hold;
        if (!first) mj[o++] = ',';
        o += message_store_msg_json(b, mj + o, (int)sizeof mj - o, ml->hdr, ml->mid,
                      hold ? "pending" : ml->state == 3 ? "realtime" : ml->state == 2 ? "failed"
                                                     : ml->state == 1 ? "read" : "pending",
                      -1,   /* no read bit, a server copy hasn't got one */
                      hold ? "" : ml->subj, hold ? "" : ml->text, ml->has_att && !hold ? ml->att : NULL);
        WriteFile(h, mj, (DWORD)o, &n, NULL);
        first = 0;
    }
    json_write(h, "]}");
    CloseHandle(h);
    MoveFileExA(tmp, path, MOVEFILE_REPLACE_EXISTING);
    if (g_mail_fresh && !held) {
        g_mail_fresh = 0;
        base_log("mail: snapshot has the new message\r\n");
    }
}
