/* layouts are 1.18.15e's, shared by US, EU and JP */
#ifndef POLCORE_H
#define POLCORE_H
#include <stddef.h>

#define NORETURN __declspec(noreturn)

enum hook_address {
    AT_MENU_SESSION,
    AT_GAME_RETURN,
    AT_CREATE_MUTEX_SLOT,
    AT_OPEN_MUTEX_SLOT,
    AT_CREATE_DIALOG_SLOT,
    AT_SPLASH_PROC,
    AT_SPLASH_CLOSE,
    AT_REGION_INDEX,        /* int, 0 JP, 1 US, 2 EU, set from env.dat before a polcore loads */
    AT_DX_CHECK,
    AT_SERVICES,            /* function pointers indexed by byte offset */
    AT_SHA1_INIT,
    AT_SHA1_UPDATE,
    AT_SHA1_FINAL,
    AT_SET_SEA_SECRET,
    AT_SESSION_INIT,
    AT_INET_INIT,           /* sets the inet flag, connect fails with -2071 without it */
    AT_CON_OPEN,
    AT_CON_STEP,
    AT_CON_IDLE,
    AT_ENCODE_POLID,
    AT_ENCODED_POLID,
    AT_AUTH_STATE,          /* unsigned, the login state machine: 0x12 to start it, 30 once I'm in */
    AT_AUTH_REASON,
    AT_AUTH_RESULT,
    AT_AUTH_ERROR,          /* unsigned, the last error. Seeded so the first read means no error */
    AT_LOGIN_FLAGS,         /* WORD from the login result, bit 0 is the disconnect bit */
    AT_LINK_INDEX,
    AT_SESSION_READY,
    AT_SESSION_ALIVE,
    AT_LINKS,
    AT_IGNORE_ROWS,
    AT_FRIENDS_A,
    AT_FRIENDS_B,
    AT_SERVER_STAMPS,
    AT_LOCAL_STAMPS,
    AT_CONTENT_IDS,
    AT_HANDLES,
    AT_STATUS_ANSWERS,      /* a pointer for each job 0-3, POLCORE_STATUS_STRIDE apart: LOAD_MY_STATUS's answer */
    AT_GCHAT_LINE_HELD,
    AT_POLL_CALLBACK,
    AT_FX_COUNT_CALL,
    AT_FX_SOUND_ON,
    AT_FX_SOUND,
    AT_FX_MARK_CALL,
    AT_FX_LOOKUP_CALL,
    AT_FX_EXPANDED,
    AT_FX_ADD_LINE,
    AT_FX_CHAT_LOG,
    AT_FX_WORD_FILTER,
    AT_FX_AT_OBJECT,
    AT_FX_EXPANDER,
    AT_FX_MENU_FRONT,
    AT_FX_LICENCE_MENU,
    AT_FX_CONFIRM_MENU,
    AT_FX_LOBBY_MENU,
    AT_FX_CHAR_LIST_MENU,
    AT_FX_CHAR_INDEX,
    AT_FX_FRIEND_DECLINE,
    AT_FX_FRIEND_ACCEPT,
    ADDRESS_COUNT
};

struct hook_addr {
    const char *name, *sig;   /* sig: hex bytes of code at or near it, ?? where a build can differ */
    DWORD usual, sig_at;
    int operand;              /* the match's byte holding the address (less the module's base), or ADDR_MATCH */
    BYTE module, code, required, rel;   /* rel: operand is a call's rel32 and the address is its target */
    DWORD at;
    int hits;
    int shows_op;             /* with shows: the match's byte holding an address, whose target bytes must equal shows */
    const char *shows;
    int calls_op;             /* with calls: the match's rel32 of a call, whose target has to start with the calls bytes */
    const char *calls;
};
static struct hook_addr g_addr[ADDRESS_COUNT];
static BYTE *pc_addr_of(BYTE *base, int id) { return base + g_addr[id].at; }
static int pc_addr_found(int id) { return g_addr[id].at != 0; }

static void **pc_slot_at(BYTE *b, unsigned off) { return (void **)(pc_addr_of(b, AT_SERVICES) + off); }
static void *pc_slot(BYTE *b, unsigned off) { return *pc_slot_at(b, off); }
static int pc_has(BYTE *b, unsigned off) { return pc_slot(b, off) != NULL; }

#define PC_SVC0(ret, name, sv) \
    static ret name(BYTE *b) { return ((ret (__cdecl *)(void))pc_slot(b, sv))(); }
#define PC_SVC0_V(ret, name, sv) \
    static ret name(BYTE *b) { ((ret (__cdecl *)(void))pc_slot(b, sv))(); }
#define PC_SVC1(ret, name, sv, t1, n1) \
    static ret name(BYTE *b, t1 n1) { return ((ret (__cdecl *)(t1))pc_slot(b, sv))(n1); }
#define PC_SVC1_V(ret, name, sv, t1, n1) \
    static ret name(BYTE *b, t1 n1) { ((ret (__cdecl *)(t1))pc_slot(b, sv))(n1); }
#define PC_SVC2(ret, name, sv, t1, n1, t2, n2) \
    static ret name(BYTE *b, t1 n1, t2 n2) { return ((ret (__cdecl *)(t1, t2))pc_slot(b, sv))(n1, n2); }
#define PC_SVC2_V(ret, name, sv, t1, n1, t2, n2) \
    static ret name(BYTE *b, t1 n1, t2 n2) { ((ret (__cdecl *)(t1, t2))pc_slot(b, sv))(n1, n2); }
#define PC_SVC3(ret, name, sv, t1, n1, t2, n2, t3, n3) \
    static ret name(BYTE *b, t1 n1, t2 n2, t3 n3) { return ((ret (__cdecl *)(t1, t2, t3))pc_slot(b, sv))(n1, n2, n3); }
#define PC_SVC3_V(ret, name, sv, t1, n1, t2, n2, t3, n3) \
    static ret name(BYTE *b, t1 n1, t2 n2, t3 n3) { ((ret (__cdecl *)(t1, t2, t3))pc_slot(b, sv))(n1, n2, n3); }
#define PC_SVC4(ret, name, sv, t1, n1, t2, n2, t3, n3, t4, n4) \
    static ret name(BYTE *b, t1 n1, t2 n2, t3 n3, t4 n4) { return ((ret (__cdecl *)(t1, t2, t3, t4))pc_slot(b, sv))(n1, n2, n3, n4); }
#define PC_SVC4_V(ret, name, sv, t1, n1, t2, n2, t3, n3, t4, n4) \
    static ret name(BYTE *b, t1 n1, t2 n2, t3 n3, t4 n4) { ((ret (__cdecl *)(t1, t2, t3, t4))pc_slot(b, sv))(n1, n2, n3, n4); }
#define PC_SVC5(ret, name, sv, t1, n1, t2, n2, t3, n3, t4, n4, t5, n5) \
    static ret name(BYTE *b, t1 n1, t2 n2, t3 n3, t4 n4, t5 n5) { return ((ret (__cdecl *)(t1, t2, t3, t4, t5))pc_slot(b, sv))(n1, n2, n3, n4, n5); }
#define PC_SVC5_V(ret, name, sv, t1, n1, t2, n2, t3, n3, t4, n4, t5, n5) \
    static ret name(BYTE *b, t1 n1, t2 n2, t3 n3, t4 n4, t5 n5) { ((ret (__cdecl *)(t1, t2, t3, t4, t5))pc_slot(b, sv))(n1, n2, n3, n4, n5); }
#define PC_SVC6(ret, name, sv, t1, n1, t2, n2, t3, n3, t4, n4, t5, n5, t6, n6) \
    static ret name(BYTE *b, t1 n1, t2 n2, t3 n3, t4 n4, t5 n5, t6 n6) { return ((ret (__cdecl *)(t1, t2, t3, t4, t5, t6))pc_slot(b, sv))(n1, n2, n3, n4, n5, n6); }
#define PC_SVC6_V(ret, name, sv, t1, n1, t2, n2, t3, n3, t4, n4, t5, n5, t6, n6) \
    static ret name(BYTE *b, t1 n1, t2 n2, t3 n3, t4 n4, t5 n5, t6 n6) { ((ret (__cdecl *)(t1, t2, t3, t4, t5, t6))pc_slot(b, sv))(n1, n2, n3, n4, n5, n6); }

#define NO_SLOT (-0x100)
#define MAIL_SUSPENDED (-0x153a)
#define NO_CHAT_LINK (-0x41f)
#define HANDLE_LIST_STALE (-0x105)
#define NOTHING_FOUND (-0x14cc)
#define ALREADY_LISTED (-0x1c13)
#define FRIEND_LIST_FULL (-7188)
#define ACCOUNT_REFUSED_TOP (-0x1450)      /* a load answered in this range turns the account away (suspended, IP restricted, password changed...) */
#define ACCOUNT_REFUSED_BOTTOM (-0x154f)
#define JOB_TIMEOUT_MS 120000        /* polcore's own gives up at 100s */

#define SJIS_GETA 0x81ac
#define WIDE_GETA 0x3013

#include "polcore_records.h"

#define SV_SJIS_TO_WIDE            0xa38   /* POL's Shift-JIS (EU's Latin extension too) to UTF-16, no bound on src */
PC_SVC3(int, pc_sjis_to_wide, SV_SJIS_TO_WIDE, WCHAR *, dst, const char *, src, int, repl)
#define SV_WIDE_TO_SJIS            0xa40   /* a NULL dst measures, repl goes in for what SJIS hasn't got */
PC_SVC3(int, pc_wide_to_sjis, SV_WIDE_TO_SJIS, char *, dst, const WCHAR *, src, int, repl)
#define SV_SERVER_TIME     0xac4
PC_SVC1(int, pc_server_time, SV_SERVER_TIME, DWORD *, now)
#define SV_VIEWER_LANGUAGE 0xf18
PC_SVC0(int, pc_viewer_language, SV_VIEWER_LANGUAGE)
static int pc_viewer_language_or(BYTE *b, int none) { return pc_has(b, SV_VIEWER_LANGUAGE) ? pc_viewer_language(b) : none; }
#define SV_DATA_PATH               0x16c4
PC_SVC4(int, pc_data_path, SV_DATA_PATH, int, id, WCHAR *, out, int, cap, int, flags)
#define SV_DATA_FILE               0x16c8
PC_SVC4(int, pc_data_file, SV_DATA_FILE, int, id, WCHAR *, out, int, cap, int, flags)
#define SV_DIC_RELOCATE            0x1154
static void *pc_dic_relocate(BYTE *b, void *dic, int size)
{
    return ((void *(__cdecl *)(void *, int))pc_slot(b, SV_DIC_RELOCATE))(dic, size);
}
#define SV_DIC_HAS_WORD            0x1674
PC_SVC3(int, pc_dic_has_word, SV_DIC_HAS_WORD, void *, dic, const WCHAR *, name, int, len)
#define SV_MY_ID           0x558
PC_SVC0(unsigned __int64, pc_my_id, SV_MY_ID)
static unsigned __int64 pc_my_id_or(BYTE *b, unsigned __int64 none) { return pc_has(b, SV_MY_ID) ? pc_my_id(b) : none; }
#define SV_FORMAT_POLID            0x584
PC_SVC3_V(void, pc_format_polid, SV_FORMAT_POLID, unsigned, lo, unsigned, hi, char *, out)
#define SV_FORMAT_HID              0x76c
PC_SVC3(int, pc_format_hid, SV_FORMAT_HID, char *, out, DWORD, lo, DWORD, hi)
#define SV_MAIL_ADDRESS_OK         0x6e8
PC_SVC2(int, pc_mail_address_ok, SV_MAIL_ADDRESS_OK, const char *, sjis, int, len)
#define SV_MY_STATUS       0x338
PC_SVC0(int, pc_my_status, SV_MY_STATUS)
static int pc_my_status_or(BYTE *b, int none) { return pc_has(b, SV_MY_STATUS) ? pc_my_status(b) : none; }
#define SV_GAME_TAG        0x2f4   /* the game slot polcore has for me, 0 or more from the character select on and -1 outside FFXI */
PC_SVC0(int, pc_game_tag, SV_GAME_TAG)
#define SV_CURRENT_HANDLE  0x2f8
PC_SVC0(int, pc_current_handle, SV_CURRENT_HANDLE)
static int pc_current_handle_or(BYTE *b, int none) { return pc_has(b, SV_CURRENT_HANDLE) ? pc_current_handle(b) : none; }

#define SV_CLEAR_ERROR     0xd10
PC_SVC0(int, pc_clear_error, SV_CLEAR_ERROR)
#define SV_NOTICE_STATUS           0xd20
PC_SVC0(unsigned char, pc_notice_status, SV_NOTICE_STATUS)
#define SV_NOTICE_DAYS             0xd24   /* days left on a LM-02 notice */
PC_SVC0(unsigned char, pc_notice_days, SV_NOTICE_DAYS)
#define SV_SET_PASSWORD            0xd50
PC_SVC2(int, pc_set_password, SV_SET_PASSWORD, void *, encoded, unsigned, mode)
#define SV_SET_POLCOM_HOST 0xd54
PC_SVC1(int, pc_set_polcom_host, SV_SET_POLCOM_HOST, void *, encoded)
#define SV_ENCODE_PASSWORD 0xd60
PC_SVC2(int, pc_encode_password, SV_ENCODE_PASSWORD, void *, out, const char *, pw)
#define SV_ENCODE_HOST     0xd90
PC_SVC2(int, pc_encode_host, SV_ENCODE_HOST, void *, out, const char *, host)
#define SV_JAILED                  0x13e4
PC_SVC0(int, pc_jailed, SV_JAILED)
static int pc_jailed_or(BYTE *b, int none) { return pc_has(b, SV_JAILED) ? pc_jailed(b) : none; }
#define SV_BAD_PASSWORD_FLAG       0x734
PC_SVC0(int, pc_bad_password_flag, SV_BAD_PASSWORD_FLAG)
#define SV_SIGN_OUT_PREPARE 0xcc0
PC_SVC1_V(void, pc_sign_out_prepare, SV_SIGN_OUT_PREPARE, int, what)
#define SV_SIGN_OUT        0xcc4
PC_SVC0(int, pc_sign_out, SV_SIGN_OUT)
#define SV_MESSAGE_NOTICES         0x1020   /* MESSAGE_NOTICES, without it polcore marks every message as lost */
PC_SVC1(int, pc_message_notices, SV_MESSAGE_NOTICES, int, on)
#define SV_OPTION_LOAD_START 0xe2c
PC_SVC0(int, pc_option_load_start, SV_OPTION_LOAD_START)
#define SV_OPTION_LOAD_DONE        0xe30   /* 0 still reading, above 0 loaded */
PC_SVC0(int, pc_option_load_done, SV_OPTION_LOAD_DONE)
#define SV_OPTION_LOAD_END 0xe3c
PC_SVC0_V(void, pc_option_load_end, SV_OPTION_LOAD_END)
#define SV_OPTION_RECORD   0xe40
static BYTE *pc_option_record(BYTE *b, const char *name)
{
    return ((BYTE *(__cdecl *)(const char *))pc_slot(b, SV_OPTION_RECORD))(name);
}
#define SV_SET_IDLE_TIMEOUT        0x11d4
PC_SVC1_V(void, pc_set_idle_timeout, SV_SET_IDLE_TIMEOUT, int, ms)

#define SV_STATUS_LOAD     0x5d8
PC_SVC0(int, pc_status_load, SV_STATUS_LOAD)
#define SV_STATUS_CHECK    0x5dc
PC_SVC1(int, pc_status_check, SV_STATUS_CHECK, int, job)
#define SV_FRIENDS_LOAD    0x28c
PC_SVC0(int, pc_friends_load, SV_FRIENDS_LOAD)
#define SV_FRIENDS_CHECK   0x290
PC_SVC1(int, pc_friends_check, SV_FRIENDS_CHECK, int, job)
#define SV_HANDLES_LOAD    0x2ac
PC_SVC0(int, pc_handles_load, SV_HANDLES_LOAD)
#define SV_HANDLES_CHECK   0x2b0
PC_SVC1(int, pc_handles_check, SV_HANDLES_CHECK, int, job)
#define SV_CHARACTERS_LOAD 0x2bc
PC_SVC0(int, pc_characters_load, SV_CHARACTERS_LOAD)
#define SV_CHARACTERS_CHECK 0x2c0
PC_SVC1(int, pc_characters_check, SV_CHARACTERS_CHECK, int, job)
#define SV_GROUPS_LOAD     0x5fc
PC_SVC0(int, pc_groups_load, SV_GROUPS_LOAD)
#define SV_GROUPS_CHECK    0x600
PC_SVC1(int, pc_groups_check, SV_GROUPS_CHECK, int, job)
#define SV_HANDLES_SAVE    0x1030
PC_SVC0(int, pc_handles_save, SV_HANDLES_SAVE)
#define SV_HANDLES_SAVE_CHECK 0x1034
PC_SVC1(int, pc_handles_save_check, SV_HANDLES_SAVE_CHECK, int, job)
#define SV_CANCEL_JOB              0x520   /* frees a job slot, but stalls every job poll for 10s */
PC_SVC1(int, pc_cancel_job, SV_CANCEL_JOB, int, job)
#define SV_FREE_JOB                0x4dc   /* frees a job slot and makes every job poll return 0 for the next 10s */
PC_SVC1_V(void, pc_free_job, SV_FREE_JOB, int, job)

#define SV_GET_HANDLE      0x2b4
PC_SVC2(int, pc_get_handle, SV_GET_HANDLE, unsigned, slot, pol_handle *, out)
#define SV_SET_HANDLE      0x2b8
PC_SVC2(int, pc_set_handle, SV_SET_HANDLE, unsigned, slot, const pol_handle *, in)
#define SV_GET_CONTENT_ID  0x2c4
PC_SVC2(int, pc_get_content_id, SV_GET_CONTENT_ID, unsigned, idx, void *, out)
#define SV_SET_CONTENT_ID          0x2c8
PC_SVC2(int, pc_set_content_id, SV_SET_CONTENT_ID, unsigned, idx, const void *, in)
#define SV_LINK_CONTENT    0x2dc
PC_SVC2(int, pc_link_content, SV_LINK_CONTENT, int, idx, int, slot)
#define SV_UNLINK_CONTENT  0x2e0
PC_SVC2(int, pc_unlink_content, SV_UNLINK_CONTENT, int, idx, int, slot)
#define SV_CHANGE_MY_STATUS        0x30c
PC_SVC6(int, pc_change_my_status, SV_CHANGE_MY_STATUS, int, handle, int, chr, int, a, int, status, int, b_, int, location)
#define SV_CHANGE_MY_STATUS_CHECK 0x310
PC_SVC1(int, pc_change_my_status_check, SV_CHANGE_MY_STATUS_CHECK, int, job)
#define SV_CLAIM_HANDLE            0x314
PC_SVC3(int, pc_claim_handle, SV_CLAIM_HANDLE, int, idx, int, a, const void *, id)
#define SV_RELEASE_HANDLE  0x318
PC_SVC1(int, pc_release_handle, SV_RELEASE_HANDLE, int, idx)
#define SV_SELECT_HANDLE   0x1434
PC_SVC1_V(void, pc_select_handle, SV_SELECT_HANDLE, int, idx)
#define SV_LINKS_SAVE      0x1038
PC_SVC0(int, pc_links_save, SV_LINKS_SAVE)
#define SV_LINKS_SAVE_CHECK 0x103c
PC_SVC1(int, pc_links_save_check, SV_LINKS_SAVE_CHECK, int, job)

#define SV_IGNORE_COPY     0x2a4
PC_SVC2(int, pc_ignore_copy, SV_IGNORE_COPY, int, row, void *, out)
#define SV_FRIEND_COPY     0x5cc
PC_SVC2(int, pc_friend_copy, SV_FRIEND_COPY, int, row, void *, out)
#define SV_SET_FRIEND_ROW  0x5f4
PC_SVC2_V(void, pc_set_friend_row, SV_SET_FRIEND_ROW, int, row, void *, row_copy)
#define SV_IGNORE_ADD              0x2e4   /* AddBlock, takes them off the friend list too */
PC_SVC6(int, pc_ignore_add, SV_IGNORE_ADD, int, a, unsigned, all_handles, unsigned, lo, unsigned, hi, unsigned, slot, const char *, name)
#define SV_IGNORE_REMOVE   0x2e8
PC_SVC1(int, pc_ignore_remove, SV_IGNORE_REMOVE, int, row)
#define SV_FRIEND_ADD      0x2ec
PC_SVC6(int, pc_friend_add, SV_FRIEND_ADD, int, a, unsigned, b_, unsigned, lo, unsigned, hi, unsigned, slot, const char *, name)
#define SV_FRIEND_DELETE   0x2f0
PC_SVC1(int, pc_friend_delete, SV_FRIEND_DELETE, int, row)
#define SV_FRIENDS_SAVE    0x1028
PC_SVC0(int, pc_friends_save, SV_FRIENDS_SAVE)
#define SV_FRIENDS_SAVE_CHECK 0x102c
PC_SVC1(int, pc_friends_save_check, SV_FRIENDS_SAVE_CHECK, int, job)
#define SV_COMMENT_SAVE            0x67c
PC_SVC2(int, pc_comment_save, SV_COMMENT_SAVE, int, who, const WCHAR *, text)
#define SV_COMMENT_SAVE_CHECK      0x684   /* poll of comment_save, not 0x680 */
PC_SVC1(int, pc_comment_save_check, SV_COMMENT_SAVE_CHECK, int, job)
#define SV_IN_CHAT_REFRESH         0x680
PC_SVC1(int, pc_in_chat_refresh, SV_IN_CHAT_REFRESH, int, a)
#define SV_MY_COMMENT      0x688
PC_SVC2(int, pc_my_comment, SV_MY_COMMENT, int, who, WCHAR *, out)

#define SV_MAIL_LIST               0x524
PC_SVC2(int, pc_mail_list, SV_MAIL_LIST, void *, out, int, max)
#define SV_MAIL_LIST_CHECK 0x528
PC_SVC1(int, pc_mail_list_check, SV_MAIL_LIST_CHECK, int, job)
#define SV_MAIL_READ       0x518
PC_SVC1(int, pc_mail_read, SV_MAIL_READ, void *, hdr)
#define SV_MAIL_READ_CHECK 0x51c
PC_SVC1(int, pc_mail_read_check, SV_MAIL_READ_CHECK, int, job)
#define SV_MAIL_DELETE     0x508
PC_SVC1(int, pc_mail_delete, SV_MAIL_DELETE, void *, hdr)
#define SV_MAIL_DELETE_CHECK 0x50c
PC_SVC1(int, pc_mail_delete_check, SV_MAIL_DELETE_CHECK, int, job)
#define SV_MAIL_SEND       0x510
PC_SVC1(int, pc_mail_send, SV_MAIL_SEND, void *, msg)
#define SV_MAIL_SEND_CHECK 0x514
PC_SVC1(int, pc_mail_send_check, SV_MAIL_SEND_CHECK, int, job)
#define SV_MAIL_HEADER             0x438
static int pc_mail_header(BYTE *b, void *msg, int kind, unsigned from_lo, unsigned from_hi, unsigned to_lo, unsigned to_hi, const char *name, const char *subject, int from_slot, int to_slot, int a, int att_type, int body_len)
{
    return ((int (__cdecl *)(void *, int, unsigned, unsigned, unsigned, unsigned, const char *, const char *, int, int, int, int, int))pc_slot(b, SV_MAIL_HEADER))(msg, kind, from_lo, from_hi, to_lo, to_hi, name, subject, from_slot, to_slot, a, att_type, body_len);
}
#define SV_MAIL_BODY               0x440
PC_SVC5(int, pc_mail_body, SV_MAIL_BODY, const char *, subject, const char *, text, int, att_len, const void *, att, char *, out)
#define SV_MAIL_DECODE_HEADER      0x540
PC_SVC2(int, pc_mail_decode_header, SV_MAIL_DECODE_HEADER, void *, hdr, const char *, file)
#define SV_MAIL_PATH_UNREAD        0x470   /* r\b path from a header, out is 0x200 */
PC_SVC2_V(void, pc_mail_path_unread, SV_MAIL_PATH_UNREAD, void *, hdr, char *, out)
#define SV_MAIL_PATH_READ          0x474
PC_SVC2_V(void, pc_mail_path_read, SV_MAIL_PATH_READ, void *, hdr, char *, out)
#define SV_MAIL_PATH_SENT          0x47c
PC_SVC2_V(void, pc_mail_path_sent, SV_MAIL_PATH_SENT, void *, hdr, char *, out)
#define SV_JOB_SLOTS               0xff4
PC_SVC2_V(void, pc_job_slots, SV_JOB_SLOTS, int *, used, int *, total)
#define SV_SET_PUSH_CALLBACKS      0xd9c
PC_SVC1_V(void, pc_set_push_callbacks, SV_SET_PUSH_CALLBACKS, void **, callbacks)
#define SV_RESET_PUSH_CALLBACKS    0xda0
PC_SVC0_V(void, pc_reset_push_callbacks, SV_RESET_PUSH_CALLBACKS)
#define SV_NEW_MAIL_FLAG           0x1460
PC_SVC0(int, pc_new_mail_flag, SV_NEW_MAIL_FLAG)
#define SV_SET_NEW_MAIL_FLAG 0x1464
PC_SVC1_V(void, pc_set_new_mail_flag, SV_SET_NEW_MAIL_FLAG, int, flag)

#define SV_PIB_LOAD                0x494
static pdb_schema *pc_pib_load(BYTE *b, void *pib_file)
{
    return ((pdb_schema *(__cdecl *)(void *))pc_slot(b, SV_PIB_LOAD))(pib_file);
}
#define SV_PFB_LOAD        0x498
static pfb_hdr *pc_pfb_load(BYTE *b, void *pfb_file)
{
    return ((pfb_hdr *(__cdecl *)(void *))pc_slot(b, SV_PFB_LOAD))(pfb_file);
}
#define SV_PDB_FIND        0x49c
static pdb_item *pc_pdb_find(BYTE *b, pdb_schema *s, const char *name, pdb_rec *rec)
{
    return ((pdb_item *(__cdecl *)(pdb_schema *, const char *, pdb_rec *))pc_slot(b, SV_PDB_FIND))(s, name, rec);
}
#define SV_PDB_ITEM_SET    0x4a0
PC_SVC5_V(void, pc_pdb_item_set, SV_PDB_ITEM_SET, pdb_schema *, s, pdb_item *, out, const char *, name, const void *, value, int, level)
#define SV_PDB_COND_TEXT           0x4a4
PC_SVC3_V(void, pc_pdb_cond_text, SV_PDB_COND_TEXT, pdb_cond *, c, int, n, const char *, text)
#define SV_PDB_COND_OP     0x4a8
PC_SVC2_V(void, pc_pdb_cond_op, SV_PDB_COND_OP, pdb_cond *, c, int, op)
#define SV_PDB_WHERE               0x4ac
#define pc_pdb_where(b, ...) (((pdb_cond *(__cdecl *)(int, ...))pc_slot(b, SV_PDB_WHERE))(__VA_ARGS__))
#define SV_PDB_LOAD_START  0x4b4
PC_SVC3(int, pc_pdb_load_start, SV_PDB_LOAD_START, pdb_schema *, s, pdb_cond *, where, pdb_rec *, out)
#define SV_PDB_LOAD_CHECK  0x4b8
PC_SVC1(int, pc_pdb_load_check, SV_PDB_LOAD_CHECK, int, job)
#define SV_PDB_FIELD_INDEX 0x4bc
PC_SVC2(int, pc_pdb_field_index, SV_PDB_FIELD_INDEX, pdb_schema *, s, const char *, name)
#define SV_PDB_SEARCH_START 0x4c0
PC_SVC3(int, pc_pdb_search_start, SV_PDB_SEARCH_START, void *, pib, void *, where, void *, sort)
#define SV_PDB_SEARCH_COUNT        0x4c4
PC_SVC3(int, pc_pdb_search_count, SV_PDB_SEARCH_COUNT, int, job, int *, set, int *, count)
#define SV_PDB_SEARCH_HITS         0x4e0
PC_SVC2(int, pc_pdb_search_hits, SV_PDB_SEARCH_HITS, int, job, DWORD *, out)
#define SV_PDB_SEARCH_FETCH        0x4c8
PC_SVC5(int, pc_pdb_search_fetch, SV_PDB_SEARCH_FETCH, int, set, void *, pib, int, from, int, to, void *, buf)
#define SV_PDB_SEARCH_FETCH_CHECK 0x4cc
PC_SVC1(int, pc_pdb_search_fetch_check, SV_PDB_SEARCH_FETCH_CHECK, int, job)
#define SV_PDB_REC_SET             0x4d0
#define pc_pdb_rec_set(b, ...) (((void (__cdecl *)(pdb_rec *, int, int, ...))pc_slot(b, SV_PDB_REC_SET))(__VA_ARGS__))
#define SV_PDB_UPDATE_START 0x4d4
PC_SVC3(int, pc_pdb_update_start, SV_PDB_UPDATE_START, pdb_schema *, s, pdb_cond *, where, pdb_rec *, set)
#define SV_PDB_UPDATE_CHECK 0x4d8
PC_SVC1(int, pc_pdb_update_check, SV_PDB_UPDATE_CHECK, int, job)
#define SV_AWAY_TEXT               0x1200   /* the viewer's away text and who it's from, -0x14cc for none or no access */
static int pc_away_text(BYTE *b, void *pib, void *pfb, void *rec, int row, char *text, int text_cap, char *from, int from_cap)
{
    return ((int (__cdecl *)(void *, void *, void *, int, char *, int, char *, int))pc_slot(b, SV_AWAY_TEXT))(pib, pfb, rec, row, text, text_cap, from, from_cap);
}

#define SV_GROUP_CREATE    0x608
PC_SVC2(int, pc_group_create, SV_GROUP_CREATE, const char *, name, int, slot)
#define SV_GROUP_CREATE_CHECK      0x60c
PC_SVC2(int, pc_group_create_check, SV_GROUP_CREATE_CHECK, int, job, DWORD *, new_id)
#define SV_GROUP_DISBAND   0x610
PC_SVC2(int, pc_group_disband, SV_GROUP_DISBAND, DWORD, lo, DWORD, hi)
#define SV_GROUP_DISBAND_CHECK 0x614
PC_SVC1(int, pc_group_disband_check, SV_GROUP_DISBAND_CHECK, int, job)
#define SV_GROUP_HEADER    0x618
PC_SVC3(int, pc_group_header, SV_GROUP_HEADER, DWORD, lo, DWORD, hi, void *, out)
#define SV_GROUP_MEMBER    0x61c
PC_SVC4(int, pc_group_member, SV_GROUP_MEMBER, DWORD, lo, DWORD, hi, unsigned, i, void *, out)
#define SV_GROUP_SET_CLASS         0x620
PC_SVC4(int, pc_group_set_class, SV_GROUP_SET_CLASS, DWORD, lo, DWORD, hi, unsigned, idx, int, cls)
#define SV_GROUP_SET_CLASS_CHECK 0x624
PC_SVC1(int, pc_group_set_class_check, SV_GROUP_SET_CLASS_CHECK, int, job)
#define SV_GROUP_COMMENT   0x630
PC_SVC5(int, pc_group_comment, SV_GROUP_COMMENT, DWORD, lo, DWORD, hi, int, a, int, b_, const WCHAR *, text)
#define SV_GROUP_COMMENT_CHECK 0x634
PC_SVC1(int, pc_group_comment_check, SV_GROUP_COMMENT_CHECK, int, job)
#define SV_GROUP_IDS       0x648
PC_SVC1(int, pc_group_ids, SV_GROUP_IDS, DWORD *, ids)

#define SV_GCHAT_START             0x6a8   /* 0 when polcore's busy */
static int pc_gchat_start(BYTE *b, DWORD lo, DWORD hi, const WCHAR *a, const WCHAR *b_, const WCHAR *c, void *callback, int d, int e, int f, int g, int h)
{
    return ((int (__cdecl *)(DWORD, DWORD, const WCHAR *, const WCHAR *, const WCHAR *, void *, int, int, int, int, int))pc_slot(b, SV_GCHAT_START))(lo, hi, a, b_, c, callback, d, e, f, g, h);
}
#define SV_GCHAT_JOINED_CHECK      0x6b0   /* above 0 is in */
PC_SVC0(int, pc_gchat_joined_check, SV_GCHAT_JOINED_CHECK)
#define SV_GCHAT_LEAVE             0x6b4   /* 0 when polcore's busy */
PC_SVC0(int, pc_gchat_leave, SV_GCHAT_LEAVE)
#define SV_GCHAT_LEFT_CHECK        0x6b8
PC_SVC0(int, pc_gchat_left_check, SV_GCHAT_LEFT_CHECK)
#define SV_GCHAT_SEND              0x6bc
PC_SVC1(int, pc_gchat_send, SV_GCHAT_SEND, const WCHAR *, text)
#define SV_GCHAT_FORCE_LEAVE 0x6c4
PC_SVC0(int, pc_gchat_force_leave, SV_GCHAT_FORCE_LEAVE)
#define SV_GCHAT_SIGN_OUT  0x6c8
PC_SVC0_V(void, pc_gchat_sign_out, SV_GCHAT_SIGN_OUT)
#define SV_GCHAT_CHANNEL           0x6cc
PC_SVC1(int, pc_gchat_channel, SV_GCHAT_CHANNEL, char *, out)

#define SV_CHAT_SESSION            0xcd0
PC_SVC0(int, pc_chat_session, SV_CHAT_SESSION)
#define SV_CHAT_UNIQUE_TEXT        0x224
PC_SVC2_V(void, pc_chat_unique_text, SV_CHAT_UNIQUE_TEXT, int, s, char *, out)
#define SV_CHAT_WHO        0x228
PC_SVC2(int, pc_chat_who, SV_CHAT_WHO, int, s, const char *, room)
#define SV_CHAT_PRIVMSG    0x22c
PC_SVC5(int, pc_chat_privmsg, SV_CHAT_PRIVMSG, int, s, DWORD, lo, DWORD, hi, const char *, room, const char *, frame)
#define SV_CHAT_AWAY       0x234
PC_SVC1(int, pc_chat_away, SV_CHAT_AWAY, int, s)
#define SV_CHAT_KICK       0x238
PC_SVC4(int, pc_chat_kick, SV_CHAT_KICK, int, s, const char *, room, DWORD, lo, DWORD, hi)
#define SV_CHAT_JOIN       0x240
PC_SVC3(int, pc_chat_join, SV_CHAT_JOIN, int, s, const char *, room, const char *, key)
#define SV_CHAT_COMMAND_CHECK      0x244
PC_SVC1(int, pc_chat_command_check, SV_CHAT_COMMAND_CHECK, int, s)
#define SV_CHAT_COMMAND_FORGET     0x248
PC_SVC1(int, pc_chat_command_forget, SV_CHAT_COMMAND_FORGET, int, s)
#define SV_CHAT_MESSAGE_CHECK      0x24c
PC_SVC2(int, pc_chat_message_check, SV_CHAT_MESSAGE_CHECK, int, s, int *, out)
#define SV_CHAT_TOPIC              0x254   /* TOPIC, NULL topic asks for it */
PC_SVC3(int, pc_chat_topic, SV_CHAT_TOPIC, int, s, const char *, room, const char *, topic)
#define SV_CHAT_MODE       0x258
static int pc_chat_mode(BYTE *b, int s, int mode, const char *room, DWORD lo, DWORD hi, const char *arg, int on)
{
    return ((int (__cdecl *)(int, int, const char *, DWORD, DWORD, const char *, int))pc_slot(b, SV_CHAT_MODE))(s, mode, room, lo, hi, arg, on);
}
#define SV_CHAT_PART       0x260
PC_SVC2(int, pc_chat_part, SV_CHAT_PART, int, s, const char *, room)
#define SV_CHAT_MY_ID              0x270
PC_SVC1(unsigned __int64, pc_chat_my_id, SV_CHAT_MY_ID, int, s)
#define SV_CHAT_MEMBERS            0x280
PC_SVC4(int, pc_chat_members, SV_CHAT_MEMBERS, int, s, const char *, room, BYTE *, slots, BYTE *, members)
#define SV_CHAT_ZONE_TEXT          0x360   /* turns a zone and categories into 12 characters with no end */
PC_SVC4_V(void, pc_chat_zone_text, SV_CHAT_ZONE_TEXT, const void *, v, char *, out, int, a, int, b_)
#define SV_CHAT_CHANGED            0x146c
PC_SVC1(int, pc_chat_changed, SV_CHAT_CHANGED, int, s)

#define SV_PUSH_DEFAULT            0x538   /* polcore's default handler for the push, called when the list passes on it */
#define SV_ROOM_NOTICE             0x644   /* polcore's room NOTICE handler, mine asks it first */

#define SV_SET_REGION      0xf14
PC_SVC1(int, pc_set_region, SV_SET_REGION, int, selector)
#define SV_SET_JP_TABLE    0x17dc
PC_SVC1(int, pc_set_jp_table, SV_SET_JP_TABLE, int, on)
#define SV_SET_LANGUAGE            0x690
PC_SVC1(int, pc_set_language, SV_SET_LANGUAGE, int, lang)
#define SV_SET_JP_LANGUAGE         0x17e0
PC_SVC1(int, pc_set_jp_language, SV_SET_JP_LANGUAGE, int, lang)
#define SV_GET_MESSAGE_TITLE 0x10bc
PC_SVC3_V(void, pc_get_message_title, SV_GET_MESSAGE_TITLE, int, kind, char *, out, int, cap)
#define SV_SET_MESSAGE_TITLE 0x10c0
PC_SVC2(int, pc_set_message_title, SV_SET_MESSAGE_TITLE, int, i, const char *, text)
#define SV_SET_MESSAGE_TEXT 0x1458
PC_SVC2(int, pc_set_message_text, SV_SET_MESSAGE_TEXT, int, i, const char *, text)
#define SV_SET_REGISTRY_KEY 0x5bc
PC_SVC1(int, pc_set_registry_key, SV_SET_REGISTRY_KEY, const char *, key)
#define SV_SET_INSTALL_FOLDER 0x1f4
PC_SVC1(int, pc_set_install_folder, SV_SET_INSTALL_FOLDER, const char *, folder)
#define SV_FILE_INIT       0xe90
PC_SVC0(int, pc_file_init, SV_FILE_INIT)
#define SV_SET_USER_SLOT           0xd98
PC_SVC1(int, pc_set_user_slot, SV_SET_USER_SLOT, int, slot)

/* in use when bit 0 of lo is set. Handle ID is bits 1-44 of lo and hi together, slot is bits 13-18 of hi */
static unsigned __int64 hid_of(DWORD lo, DWORD hi) { return (((unsigned __int64)hi << 32 | lo) >> 1) & 0xfffffffffffULL; }
static unsigned __int64 handle_hid(const pol_handle *h) { return hid_of(h->lo, h->hi); }
static int handle_get(BYTE *b, int slot, pol_handle *h)
{
    memset(h, 0, sizeof *h);
    return slot >= 0 && slot < 64 && pc_get_handle(b, (unsigned)slot, h) >= 0 && (h->lo & 1);
}

#define POLCORE_LINK_SIZE         0x3a00
#define POLCORE_LINK_LAST_SEND    0x39d4
#define POLCORE_LINK_STEP         0x208
#define POLCORE_LINK_CONNECTED    5         /* 1 to 4 are still making the connection, a refused one goes back to 3 */
#define POLCORE_STATUS_STRIDE     0x338
#define STAMP_FRIENDS 0
#define STAMP_HANDLE_LIST 1
#define STAMP_CONTENT_IDS 2

/* POLCON_OPEN's first argument. 3 adds a bind request (states 40 and 41) the viewer's own login doesn't make, 1 doesn't */
#define CON_TYPE        1
#define AUTH_START      0x12
#define AUTH_IN         30
#define AUTH_NO_ERROR   0xfffffbee
static int auth_settled(unsigned st) { return st == AUTH_IN || st == 32 || st == 36 || st == 39; }

static unsigned *pc_auth_state(BYTE *b) { return (unsigned *)pc_addr_of(b, AT_AUTH_STATE); }
static int *pc_auth_reason(BYTE *b) { return (int *)pc_addr_of(b, AT_AUTH_REASON); }
static int *pc_auth_result(BYTE *b) { return (int *)pc_addr_of(b, AT_AUTH_RESULT); }
static unsigned *pc_auth_error(BYTE *b) { return (unsigned *)pc_addr_of(b, AT_AUTH_ERROR); }
static WORD *pc_login_flags(BYTE *b) { return (WORD *)pc_addr_of(b, AT_LOGIN_FLAGS); }
static int *pc_link_index(BYTE *b) { return (int *)pc_addr_of(b, AT_LINK_INDEX); }
static int *pc_session_ready(BYTE *b) { return (int *)pc_addr_of(b, AT_SESSION_READY); }
static int *pc_session_alive(BYTE *b) { return (int *)pc_addr_of(b, AT_SESSION_ALIVE); }
static DWORD *pc_link_last_send(BYTE *b, int link)
{
    return (DWORD *)(pc_addr_of(b, AT_LINKS) + link * POLCORE_LINK_SIZE + POLCORE_LINK_LAST_SEND);
}
static int pc_link_connected(BYTE *b)
{
    int s = *pc_link_index(b);
    BYTE *step;
    if (s < 0 || s > 15) return 0;
    step = pc_addr_of(b, AT_LINKS) + s * POLCORE_LINK_SIZE + POLCORE_LINK_STEP;
    return !IsBadReadPtr(step, 1) && *step >= POLCORE_LINK_CONNECTED;
}
static BYTE *pc_gchat_line_held(BYTE *b) { return pc_addr_of(b, AT_GCHAT_LINE_HELD); }
static BYTE *pc_status_answer(BYTE *b, int job)
{
    return *(BYTE **)(pc_addr_of(b, AT_STATUS_ANSWERS) + job * POLCORE_STATUS_STRIDE);
}
static unsigned *pc_server_stamp(BYTE *b, int list) { return (unsigned *)pc_addr_of(b, AT_SERVER_STAMPS) + list; }
static unsigned *pc_local_stamp(BYTE *b, int list) { return (unsigned *)pc_addr_of(b, AT_LOCAL_STAMPS) + list; }

static pol_friend *pc_friend_row(BYTE *b, int i) { return (pol_friend *)pc_addr_of(b, AT_FRIENDS_A) + i; }
static pol_friend *pc_ignore_row(BYTE *b, int i) { return (pol_friend *)pc_addr_of(b, AT_IGNORE_ROWS) + i; }
static pol_friend_extra *pc_friend_extra(BYTE *b, int i) { return (pol_friend_extra *)pc_addr_of(b, AT_FRIENDS_B) + i; }
static pol_chr *pc_content_record(BYTE *b, int idx) { return (pol_chr *)pc_addr_of(b, AT_CONTENT_IDS) + idx; }
static pol_handle *pc_handle_record(BYTE *b, int slot) { return (pol_handle *)pc_addr_of(b, AT_HANDLES) + slot; }
static int pc_friends_server_newer(BYTE *b) { return *pc_server_stamp(b, STAMP_FRIENDS) > *pc_local_stamp(b, STAMP_FRIENDS); }
static int pc_handle_list_server_newer(BYTE *b)
{
    return *pc_server_stamp(b, STAMP_HANDLE_LIST) > *pc_local_stamp(b, STAMP_HANDLE_LIST);
}
static int pc_links_server_newer(BYTE *b)
{
    return *pc_server_stamp(b, STAMP_CONTENT_IDS) > *pc_local_stamp(b, STAMP_CONTENT_IDS);
}

static int pc_friend_valid(const pol_friend *f) { return (f->hid_lo & 1) != 0; }
static unsigned __int64 pc_friend_hid(const pol_friend *f) { return hid_of(f->hid_lo, f->hid_hi); }
static int pc_friend_slot(const pol_friend *f) { return (int)((f->hid_hi >> 13) & 0x3f); }
#define FRIEND_PENDING 0x10000000
static int pc_chr_valid(const pol_chr *c) { return (c->flags & 1) != 0; }
#define CHR_LINKED 0x8000
#define CHR_HANDLE(f) (((f) >> 16) & 0x3f)
#define CHR_PLACE(f) (((f) >> 22) & 7)

#define MAIL(h) ((pol_mail *)(h))
#define MAIL_KEY_SIZE 0x48           /* the first bytes of a header are the server's key for the message */
#define MAIL_BUILT_SIZE 0xa8
#define MAIL_VALID 0x8000
static unsigned pc_mail_kind(const void *h) { return (MAIL(h)->flags >> 7) & 0x1f; }
static unsigned pc_mail_att_type(const void *h) { return MAIL(h)->flags & 0x7f; }   /* 10 is a group invitation or its answer */
static int pc_mail_receipt(const void *h) { return ((MAIL(h)->flags >> 12) & 3) == 1; }

static void pc_sha1_init(BYTE *b, void *ctx) { ((void (__cdecl *)(void *))pc_addr_of(b, AT_SHA1_INIT))(ctx); }
static void pc_sha1_update(BYTE *b, void *ctx, const void *data, unsigned len)
{
    ((void (__cdecl *)(void *, const void *, unsigned))pc_addr_of(b, AT_SHA1_UPDATE))(ctx, data, len);
}
static void pc_sha1_final(BYTE *b, void *out, void *ctx) { ((void (__cdecl *)(void *, void *))pc_addr_of(b, AT_SHA1_FINAL))(out, ctx); }
static int pc_set_sea_secret(BYTE *b, int mode, const char *id, const void *secret, const void *otp)
{
    return ((int (__cdecl *)(int, const char *, const void *, const void *))pc_addr_of(b, AT_SET_SEA_SECRET))(mode, id, secret, otp);
}
static void pc_encode_polid(BYTE *b, const char *id)
{
    ((void (__cdecl *)(void *, const char *))pc_addr_of(b, AT_ENCODE_POLID))(pc_addr_of(b, AT_ENCODED_POLID), id);
}
static int pc_session_init(BYTE *b) { return ((int (__cdecl *)(void))pc_addr_of(b, AT_SESSION_INIT))(); }
static int pc_inet_init(BYTE *b) { return ((int (__cdecl *)(void))pc_addr_of(b, AT_INET_INIT))(); }
static int pc_con_open(BYTE *b, int type, void *conn) { return ((int (__cdecl *)(int, void *))pc_addr_of(b, AT_CON_OPEN))(type, conn); }
static int pc_con_step(BYTE *b) { return ((int (__cdecl *)(void))pc_addr_of(b, AT_CON_STEP))(); }
static int pc_con_idle(BYTE *b) { return ((int (__cdecl *)(void))pc_addr_of(b, AT_CON_IDLE))(); }
static int pc_pump_once(BYTE *b)
{
    pc_con_step(b);
    return pc_con_idle(b);
}

#endif
