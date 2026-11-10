/* session.h - the jobs the hook has in flight and what each one waits on, for the files that share them. The rest of
   their state sits in globals next to the code that owns it */

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

/* a job on the profile server: the step it's at (0 idle, 1 waiting for a slot, 2 running, 3 and 4 the same for a second call),
   polcore's job number, and what polcore writes into. last_read is the highest step that's still a read */
typedef struct db_job {
    int step, job;
    ULONGLONG t0;
    void *rec;
    char *state;
    int cap;
    void (*done)(const char *state);
    void (*start)(BYTE *b, struct db_job *j);
    DWORD wait_ms;
    int keep;
    int last_read;
    __declspec(align(8)) pdb_cond cond[2];
    pdb_cond *where;
} db_job;

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
    int handle_op;
    int handle_job;
    int handle_want;
    int handle_slot;
    int handle_idx;
    int handle_from;
    int handle_pos;
    int handle_reload;
    char handle_token[48];
    char handle_state[48];
    /* presence */
    int status_job;
    int status_want;
    int status_loc;
    /* the inbox */
    int mail_n;
    int mail_mid;
    int mail_seen_n;
    int mail_seen_next;
    fetch_lane mail_lanes[FETCH_LANES];
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
    int send_queue_n;
    send_pace pace;
    send_hist send_hist[SEND_HIST];
    int send_hist_next;
    /* my profile and picture */
    char me_token[48];
    char me_state[32];
    int pic_want;
    char pic_state[32];
    char pic_token[48];
    char ffxi_token[48];
    char ffxi_state[32];
    char ffxi_json[0x200];
    char ffxi_key[25];
    /* searches and profile reads */
    int search_n;
    int search_kind;
    int search_total;
    int search_many;
    char search_token[48];
    char search_state[32];
    char prof_token[48];
    char prof_state[32];
} hook_session;

/* nothing running and nothing asked for: no jobs (-1), no slot picked, every counter at 0 */
#define SESSION_IDLE \
    .edit_job = -1, \
    .grp_job = -1, \
    .handle_job = -1, \
    .handle_slot = -1, \
    .handle_idx = -1, \
    .handle_from = -1, \
    .handle_pos = -1, \
    .status_job = -1, \
    .status_want = -1, \
    .status_loc = -2, \
    .mail_lanes = { { -1 }, { -1 } }, \
    .del_job = -1, \
    .pic_want = -1

static hook_session g_s = { SESSION_IDLE, .mail_refresh = 1 };   /* the inbox gets listed as soon as there's a hold */

