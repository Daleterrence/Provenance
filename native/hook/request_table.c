/* every request the app can write, one row each: which lists it's in, where a refusal's token goes, and what the job it
   starts is doing (JB_ bits) */
#define SWITCHING "failed:handle changing"

enum { JB_DB = 1, JB_WRITE = 2, JB_WAIT = 4, JB_SWITCH = 8 };
typedef struct req_row {
    const char *ext;
    int in;
    char *token;
    int token_cap, token_line;
    void (*fail)(const char *why);
    void (*refuse)(const char *l1, const char *l2, const char *l3);
    int (*busy)(void);
} req_row;

static void request_fail_edit(const char *why) { edit_fail(why); }
static void request_fail_group(const char *why) { group_fail(why); }
static void request_fail_pic(const char *why) { pic_fail(why); }
static void request_refuse_chatjoin(const char *l1, const char *l2, const char *l3) { (void)l2; chat_room_join_refused_switching(l1, l3); }
static void request_refuse_chatsend(const char *l1, const char *l2, const char *l3) { (void)l2; (void)l3; chat_room_send_refused_switching(l1); }
static void request_refuse_chatcreate(const char *l1, const char *l2, const char *l3) { (void)l3; chat_room_create_refused(l1, l2); }
static void request_refuse_chatmaster(const char *l1, const char *l2, const char *l3) { (void)l3; chat_room_master_refused_switching(l1, l2); }
static void request_refuse_gjoin(const char *l1, const char *l2, const char *l3) { (void)l2; (void)l3; group_chat_join_refused(l1, SWITCHING); }
static void request_refuse_gsend(const char *l1, const char *l2, const char *l3) { (void)l2; (void)l3; group_chat_send_refused(l1, SWITCHING); }

static int busy_read(const db_job *j) { return j->step ? JB_DB | JB_SWITCH : 0; }
static int busy_send(void) { return (g_s.send_queue_n ? JB_WRITE : 0) | (mail_send_built() ? JB_WAIT : 0); }
static int busy_answer(void) { return g_s.answer_kind ? JB_WRITE | JB_WAIT : 0; }
static int busy_edit(void)
{
    return (g_s.edit_job >= 0 || g_s.edit_upload_want || g_s.comment_wanted ? JB_WRITE : 0) |
           (g_s.edit_upload_want || g_s.comment_wanted ? JB_WAIT : 0);
}
static int busy_group(void) { return g_s.grp_job >= 0 ? JB_WRITE : 0; }
static int busy_status(void) { return (g_s.status_job >= 0 ? JB_WRITE : 0) | (g_s.status_want >= 0 ? JB_SWITCH : 0); }
static int busy_pic(void) { return g_s.pic_want >= 0 ? JB_DB | JB_WRITE : 0; }
static int busy_search(void) { return busy_read(&g_search); }
static int busy_profile(void) { return busy_read(&g_prof); }
static int busy_tab(void) { return busy_read(&g_fp_read.j); }
static int busy_lists(void) { return busy_read(&g_cr); }
static int busy_card(void) { return busy_read(&g_xi); }
static int busy_character(void) { return g_ch_read.j.step ? JB_DB : 0; }
static int busy_me(void)
{
    return (g_me.step ? JB_DB | JB_SWITCH : 0) | (g_me.step >= 3 ? JB_WRITE : 0) | (g_me.step == 3 ? JB_WAIT : 0);
}
static int busy_handle(void) { return (g_s.handle_op ? JB_WRITE : 0) | (g_s.handle_want ? JB_WAIT : 0); }

#define TOKEN(field) g_s.field, sizeof g_s.field
/* the refused ones come first, in the order they're taken in */
static const req_row g_requests[] = {
    { REQ_SEND, RQ_REFUSE | RQ_ASKED, TOKEN(send_token), 2, mail_send_fail, NULL, busy_send },
    { REQ_FRIEND, RQ_REFUSE | RQ_ASKED, TOKEN(edit_token), 2, request_fail_edit, NULL, busy_edit },
    { REQ_COMMENT, RQ_REFUSE | RQ_ASKED, TOKEN(edit_token), 1, request_fail_edit, NULL, busy_edit },
    { REQ_ANSWER, RQ_REFUSE | RQ_ASKED, TOKEN(send_token), 2, mail_send_fail, NULL, busy_answer },
    { REQ_GROUP, RQ_REFUSE | RQ_ASKED, TOKEN(grp_token), 2, request_fail_group, NULL, busy_group },
    { REQ_PIC, RQ_REFUSE | RQ_ASKED | RQ_USER, TOKEN(pic_token), 2, request_fail_pic, NULL, busy_pic },
    { REQ_STATUS, RQ_REFUSE | RQ_ASKED, NULL, 0, 0, NULL, NULL, busy_status },
    { REQ_MYPROFILE, RQ_REFUSE | RQ_USER, TOKEN(me_token), 1, my_profile_done, NULL, busy_me },
    { REQ_SAVEPROFILE, RQ_REFUSE | RQ_USER, TOKEN(me_token), 1, my_profile_done, NULL, busy_me },
    { REQ_RENAME, RQ_REFUSE | RQ_USER, TOKEN(me_token), 1, my_profile_done, NULL, busy_me },
    { REQ_CHATJOIN, RQ_REFUSE | RQ_SWEPT, NULL, 0, 0, NULL, request_refuse_chatjoin },
    { REQ_CHATSEND, RQ_REFUSE, NULL, 0, 0, NULL, request_refuse_chatsend },
    { REQ_CHATCREATE, RQ_REFUSE | RQ_SWEPT, NULL, 0, 0, NULL, request_refuse_chatcreate },
    { REQ_CHATMASTER, RQ_REFUSE | RQ_SWEPT, NULL, 0, 0, NULL, request_refuse_chatmaster },
    { REQ_GROUPCHATJOIN, RQ_REFUSE | RQ_SWEPT, NULL, 0, 0, NULL, request_refuse_gjoin },
    { REQ_GROUPCHATSEND, RQ_REFUSE | RQ_SWEPT, NULL, 0, 0, NULL, request_refuse_gsend },
    { REQ_XIPROFILE, RQ_REFUSE | RQ_USER, TOKEN(ffxi_token), 1, my_profile_xi_done, NULL, busy_card },
    { REQ_ACK },
    { REQ_SEARCH, RQ_USER, NULL, 0, 0, NULL, NULL, busy_search },
    { REQ_PROFILE, RQ_USER, NULL, 0, 0, NULL, NULL, busy_profile },
    { REQ_CHARACTER, 0, NULL, 0, 0, NULL, NULL, busy_character },
    { REQ_FFXIPROFILE, RQ_USER, NULL, 0, 0, NULL, NULL, busy_tab },
    { REQ_CHATZONES, RQ_USER, NULL, 0, 0, NULL, NULL, busy_lists },
    { REQ_CHATROOMS, RQ_USER, NULL, 0, 0, NULL, NULL, busy_lists },
    { REQ_CHATLEAVE },
    { REQ_HANDLE, RQ_USER, NULL, 0, 0, NULL, NULL, busy_handle },
    { REQ_GROUPCHATLEAVE, RQ_SWEPT },
};
#define REQ_ROWS ((int)(sizeof g_requests / sizeof g_requests[0]))

/* what's going on, as the JB_ bits of every job there is */
static int requests_busy(void)
{
    int bits = 0;
    for (int i = 0; i < REQ_ROWS; i++) if (g_requests[i].busy) bits |= g_requests[i].busy();
    return bits;
}
/* 1 when a request in the list is there to be taken, or one's been taken and not finished with */
static int requests_listed(int list)
{
    for (int i = 0; i < REQ_ROWS; i++) if ((g_requests[i].in & list) && requests_waiting(g_requests[i].ext)) return 1;
    return 0;
}
/* the file and its .taking copy */
static void requests_sweep(void)
{
    char p[MAX_PATH + 64];
    for (int i = 0; i < REQ_ROWS; i++) {
        if (!(g_requests[i].in & RQ_SWEPT)) continue;
        base_ipc_file(p, g_requests[i].ext);
        DeleteFileA(p);
        lstrcatA(p, IPC_TAKING);
        DeleteFileA(p);
    }
}
/* while a switch runs, a write is taken and answered as not done, it'd go from the new handle otherwise. A chat
   enter waiting when the switch is taken is called off the same way */
static void handles_writes_refuse(void)
{
    char buf[512], *p, *l1, *l2, *l3;
    for (int i = 0; i < REQ_ROWS; i++) {
        if (!(g_requests[i].in & RQ_REFUSE) || requests_take(g_requests[i].ext, buf, sizeof buf) < 0) continue;
        base_log("handles: %s request refused - the handle's changing\r\n", g_requests[i].ext);
        p = buf;
        l1 = requests_line(&p); l2 = requests_line(&p); l3 = requests_line(&p);
        if (g_requests[i].refuse) g_requests[i].refuse(l1, l2, l3);
        else if (g_requests[i].token) {
            lstrcpynA(g_requests[i].token, g_requests[i].token_line == 1 ? l1 : l2, g_requests[i].token_cap);
            g_requests[i].fail(SWITCHING);
        }
    }
}
