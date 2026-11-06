/* session.h - the hook's state for one login in a single struct, so resetting it can't miss a global somebody adds */

#define FETCH_LANES 2
#define SEND_HIST 8
typedef struct { int job, kind, idx; char *body; } fetch_lane;   /* a mail list or read in flight */
typedef struct { char token[48]; char state[48]; DWORD time, seq, to_lo, to_hi; int from_slot; } send_hist;

#define PACE_PER_MIN 40
#define PACE_PER_HOUR 600
enum { PACE_OK, PACE_MIN, PACE_HOUR, PACE_HELD, PACE_STOPPED };
typedef struct {
    ULONGLONG take[PACE_PER_HOUR];   /* ring of PUT start times */
    int next, count;
    ULONGLONG hold, failed;          /* no PUT before hold, when the last failure landed */
    int fails, stopped;              /* failed sends in a row, no more sends this session */
} send_pace;

typedef struct hook_session {
    /* general */
    int draining;
    ULONGLONG db_last;
    /* chat rooms */
    int cr_st;
    int cr_wait;
    int cr_tries;
    int cr_open;
    int cr_leave;
    int cr_hello;
    int cr_replies;
    int cr_joined;   /* a JOIN's gone and no PART since: the server might have me in the room */
    int cr_db;   /* chat's zone and room lists, 0 idle, the steps are cr_db_pump's */
    /* friend edits, answers and my comment */
    int answer_kind;
    char edit_token[48];
    char edit_state[48];
    int edit_job;
    int edit_upload_want;
    int comment_wanted;
    int answer_wait;
    /* friend groups */
    int grp_job;
    int grp_poll;
    char grp_token[48];
    char grp_state[32];
    /* handles */
    int hop;
    int hjob;
    int hwant;
    int hslot;
    int hidx;
    int hfrom;
    int hpos;
    int hreload;
    char htoken[48];
    char hstate[48];
    /* presence */
    int status_job;
    int status_want;
    int status_loc;
    /* the inbox */
    int mail_n;
    int mail_mid;
    int mail_seen_n;
    int mail_seen_next;
    fetch_lane rd[FETCH_LANES];
    int del_job;
    int del_idx;
    ULONGLONG mail_retry_at;
    volatile int mail_refresh;
    volatile LONG push_n;
    int sent_ids_n;
    int sent_ids_next;
    int returned_n;
    int returned_next;
    /* sending */
    char send_token[48];   /* the snapshot's "send", the newest message and its state */
    char send_state[48];
    int sq_n;
    send_pace pace;
    send_hist send_hist[SEND_HIST];
    int send_hist_next;
    /* my profile and picture */
    int me_step;
    int xi_step;   /* my FFXI character's privacy: 1 load waits for a slot, 2 load job */
    char me_token[48];
    char me_state[32];
    int pic_want;
    char pic_state[32];
    char pic_token[48];
    int xi_job;
    char xi_token[48];
    char xi_state[32];
    char xi_json[0x200];
    char xi_key[25];
    ULONGLONG xi_t0;
    ULONGLONG xi_last;
    /* searches and profile reads */
    int srch_step;   /* 0 idle, 1 start waits, 2 search job, 3 fetch waits, 4 fetch job */
    int prof_step;   /* 0 idle, 1 load running, 2 start waits for a slot */
    int ch_step;
    int fp_step;
    int srch_n;
    int srch_kind;
    int srch_total;
    int srch_many;
    char srch_token[48];
    char srch_state[32];
    char prof_token[48];
    char prof_state[32];
} hook_session;

/* nothing running and nothing asked for: no jobs (-1), no slot picked, every counter at 0 */
#define SESSION_IDLE \
    .edit_job = -1, \
    .grp_job = -1, \
    .hjob = -1, \
    .hslot = -1, \
    .hidx = -1, \
    .hfrom = -1, \
    .hpos = -1, \
    .status_job = -1, \
    .status_want = -1, \
    .status_loc = -2, \
    .rd = { { -1 }, { -1 } }, \
    .del_job = -1, \
    .pic_want = -1, \
    .xi_job = -1

static hook_session g_s = { SESSION_IDLE, .mail_refresh = 1 };   /* the inbox gets listed as soon as there's a hold */

