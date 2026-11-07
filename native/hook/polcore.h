/* polcore.h - what the hook knows about polcore's insides, and the few places in pol.exe it uses, each address and
   record layout written once. The layouts are 1.18.15e's, which US, EU and JP share. b is always the polcore module's
   base. */
#ifndef POLCORE_H
#define POLCORE_H
#include <stddef.h>

/* every fixed address the hook uses. addresses.c has where each one is in 1.18.15e and finds them all again before
   any gets used, in case a POL update's moved them */
enum hook_address {
    /* pol.exe */
    AT_MENU_SESSION,        /* the menu session, jumped over to the hook's own */
    AT_GAME_RETURN,         /* the content launcher's GameStart call, the trace call after it gets the hook's thunk */
    AT_CREATE_MUTEX_SLOT,   /* WinMain's MSLU slots, hooked while they still point at MSLU's own stub */
    AT_OPEN_MUTEX_SLOT,
    AT_CREATE_DIALOG_SLOT,
    AT_SPLASH_PROC,         /* the startup splash's dialog proc, only ever compared */
    AT_SPLASH_CLOSE,        /* destroys the splash on the thread that made it */
    AT_REGION_INDEX,        /* int, 0 JP, 1 US, 2 EU, set from env.dat before a polcore loads */
    /* polcore */
    AT_SERVICES,            /* the service table, function pointers indexed by byte offset */
    AT_SHA1_INIT,           /* polcore's own SHA-1, the secret hash uses it */
    AT_SHA1_UPDATE,
    AT_SHA1_FINAL,
    AT_SET_SEA_SECRET,      /* also service 0x1778 */
    AT_SESSION_INIT,        /* also service 0xc90 */
    AT_INET_INIT,           /* sets the inet flag, connect fails with -2071 without it */
    AT_CON_OPEN,            /* POLCON_OPEN, type 3 is the network */
    AT_CON_STEP,            /* POLCON_STEP, the auth state machine, one step */
    AT_CON_IDLE,            /* what has to run between steps, below 0 when the session's gone */
    AT_ENCODE_POLID,        /* scrambles the POL ID into polcore, reversible */
    AT_ENCODED_POLID,       /* where ENCODE_POLID leaves it */
    AT_AUTH_STATE,          /* unsigned, the login state machine: 0x12 to start it, 30 once I'm in */
    AT_AUTH_REASON,         /* int, why it stopped, only logged */
    AT_AUTH_RESULT,         /* int, the server's answer to the step it's on, only logged */
    AT_AUTH_ERROR,          /* unsigned, the last error. Seeded so the first read means no error */
    AT_LOGIN_FLAGS,         /* WORD from the login result, bit 0 is the disconnect bit */
    AT_LINK_INDEX,          /* int, which connection record the session uses (0-15) */
    AT_SESSION_READY,       /* int, 1 once the login's done (service 0xd10), below 0 is why it dropped */
    AT_SESSION_ALIVE,       /* int, 0 once the session's gone */
    AT_LINKS,               /* 16 connection records of POLCORE_LINK_SIZE bytes */
    AT_IGNORE_ROWS,         /* 100 pol_friend rows, the ignore list */
    AT_FRIENDS_A,           /* 200 pol_friend rows */
    AT_FRIENDS_B,           /* 200 pol_friend_extra rows */
    AT_SERVER_STAMPS,       /* the server's stamp for each list (STAMP_*), newer than mine when its list is */
    AT_LOCAL_STAMPS,        /* and mine */
    AT_CONTENT_IDS,         /* 64 pol_chr */
    AT_HANDLES,             /* 64 pol_handle */
    AT_STATUS_ANSWERS,      /* a pointer for each job 0-3, POLCORE_STATUS_STRIDE apart: LOAD_MY_STATUS's answer */
    AT_GCHAT_LINE_HELD,     /* BYTE, non-zero while polcore holds a group chat line it hasn't sent */
    ADDRESS_COUNT
};

struct hook_addr {
    const char *name, *sig;   /* sig: hex bytes of code at or near it, ?? where a build can differ */
    DWORD usual, sig_at;      /* the address in 1.18.15e, and where sig starts there */
    int operand;              /* the match's byte holding the address (less the module's base), or ADDR_MATCH */
    BYTE module, code, required;
    DWORD at;                 /* what the hook uses: usual until it's checked, 0 when it isn't there */
    int shows_op;             /* with shows: the match's byte holding an address, whose bytes have to be shows */
    const char *shows;        /* hex bytes, no ?? */
    int calls_op;             /* with calls: the match's rel32 of a call, whose target has to start with calls */
    const char *calls;        /* hex bytes, ?? allowed */
};
static struct hook_addr g_addr[ADDRESS_COUNT];
static BYTE *addr_of(BYTE *base, int id) { return base + g_addr[id].at; }
static int addr_found(int id) { return g_addr[id].at != 0; }

static void **pc_slot_at(BYTE *b, unsigned off) { return (void **)(addr_of(b, AT_SERVICES) + off); }
static void *pc_slot(BYTE *b, unsigned off) { return *pc_slot_at(b, off); }
static int pc_has(BYTE *b, unsigned off) { return pc_slot(b, off) != NULL; }

/* what a job's start or poll answers besides 0 (not done yet), 1 (done) and the server's own codes */
#define NO_SLOT (-0x100)             /* polcore has no free job slot, try next turn */
#define NO_CHAT_LINK (-0x41f)        /* there's no chat link to send on */
#define HANDLE_LIST_STALE (-0x105)   /* the server's handle list moved on, the viewer reloads it and reports this */
#define NOTHING_FOUND (-0x14cc)      /* a search with no hits, or a profile the viewer isn't allowed to see */
#define ALREADY_LISTED (-0x1c13)     /* adding someone who's on the list already */
#define FRIEND_LIST_FULL (-7188)     /* 200 friends already */
#define ACCOUNT_REFUSED_TOP (-0x1450)      /* a load answered in this range turns the account away: suspended, */
#define ACCOUNT_REFUSED_BOTTOM (-0x154f)   /* IP restricted, password changed and so on */
#define JOB_TIMEOUT_MS 120000        /* how long the hook waits on one polcore job (polcore's own gives up at 100 s) */

/* the mark polcore puts in for a character the other set hasn't got, for wide_to_sjis and sjis_to_wide */
#define SJIS_GETA 0x81ac
#define WIDE_GETA 0x3013

/* records polcore hands over, the same layouts it keeps */
#pragma pack(push, 1)

typedef struct pdb_field {
    char  name[0x10];
    WORD  size;         /* the file stores size + 1, svc 0x494 takes the 1 off */
    BYTE  type;         /* 0,2 bytes (ptr, len)  1 string  3,4 u16  5,6 u32  7,8 u64  9 packed */
    BYTE  b13;
    DWORD flags;        /* the viewer only sends a field with flags & 0x42 */
} pdb_field;

/* data\db\prof_pol.pib. svc 0x494 turns the offsets at +0x14, +0x1c and +0x2c into pointers, in place */
typedef struct pdb_schema {
    char       name[0x10];
    DWORD      rec_raw;
    BYTE      *p14;
    WORD       table_id;    /* 1000 = handle profiles */
    BYTE       b1a;
    BYTE       nfields;
    pdb_field *fields;
    DWORD      rec_size;
    BYTE       relocated;
    BYTE       b25, b26, flags27;
    DWORD      d28;
    BYTE      *p2c;
} pdb_schema;

typedef struct pdb_item {
    struct pdb_item *next;
    BYTE  flags;            /* bit 0 = set */
    BYTE  level;            /* per-field visibility, echoed back as the record has it */
    BYTE  field;
    BYTE  type;
    union {
        struct { void *p; DWORD len; } bytes;
        char  *str;
        WORD   u16;
        DWORD  u32;
        unsigned __int64 u64;
    } v;
} pdb_item;

typedef struct pdb_cond {
    struct pdb_cond *next;
    BYTE  op;               /* 1 = equal, the only one the viewer uses */
    BYTE  pad[3];
    pdb_item item;
} pdb_cond;

typedef struct pdb_rec {
    struct pdb_rec *next;
    BYTE  level;            /* 1 private, 2 friends, 3 everyone */
    BYTE  pad[7];
    pdb_item *items;
} pdb_rec;

typedef struct pol_handle {
    DWORD lo;           /* bit 0 valid, bits 1-31 = handle id bits 0-30 */
    DWORD hi;           /* bits 0-12 = id bits 31-43, bits 13-18 = slot */
    char  name[16];     /* 15 bytes SJIS + NUL */
    DWORD flags;
    DWORD pic;
    BYTE  chars[8];
} pol_handle;

#pragma pack(pop)

typedef char size_check_field[sizeof(pdb_field) == 0x18 ? 1 : -1];
typedef char size_check_item[sizeof(pdb_item) == 0x10 ? 1 : -1];
typedef char size_check_cond[sizeof(pdb_cond) == 0x18 ? 1 : -1];
typedef char size_check_rec[sizeof(pdb_rec) == 0x10 ? 1 : -1];
typedef char size_check_hnd[sizeof(pol_handle) == 0x28 ? 1 : -1];
typedef char size_check_hdr[sizeof(pdb_schema) == 0x30 ? 1 : -1];

#pragma pack(push, 1)
typedef struct pfb_ent  { DWORD code; char *text; } pfb_ent;
typedef struct pfb_tab  { WORD n; WORD pad; pfb_ent *e; } pfb_tab;
typedef struct pfb_item { char *label; BYTE field, ui, r6, r7; DWORD r8; pfb_tab *tab; DWORD r10; } pfb_item;
typedef struct pfb_hdr  { BYTE nItems, nConds, nTabs, relocated; pfb_item *items; void *conds; pfb_tab *tabs;
                          DWORD r10, r14; WORD nBlocks; BYTE r1a, encrypted; char *title; } pfb_hdr;
#pragma pack(pop)
typedef char pf_chk_item[sizeof(pfb_item) == 0x14 ? 1 : -1];
typedef char pf_chk_hdr[sizeof(pfb_hdr) == 0x20 ? 1 : -1];

#pragma pack(push, 1)
/* a friend or ignore row. polcore hands copies of 0x140 bytes (pol_friend_copy), the arrays hold the first 0xb0 */
typedef struct pol_game {
    WORD  flags;        /* bit 0 valid */
    WORD  title;        /* 1 is FFXI */
    DWORD ctsid;        /* FFXI's world id is its byte 2 */
    DWORD ctid_lo, ctid_hi;
} pol_game;

typedef struct pol_friend {
    DWORD    id_lo, id_hi;   /* POL ID */
    DWORD    flags;          /* bits 13-15 status + 1 (1 online, 2 away, 3 busy, 0 offline), 0x800 online, 0x10000 a game
                                slot's in use, bits 17-19 which one, 0x10000000 pending request, 3 on an ignore row for all handles */
    BYTE     r0c[12];
    pol_game games[8];
    DWORD    hid_lo;         /* bit 0 valid, the H-ID is bits 1-44 of hid_lo and hid_hi */
    DWORD    hid_hi;         /* bits 13-18 the handle's slot */
    char     name[16];       /* the handle's name, SJIS */
} pol_friend;

typedef struct pol_friend_copy {
    pol_friend f;
    BYTE       more[0x90];
} pol_friend_copy;

/* the friend list's second array, one row for each of the friend rows */
typedef struct pol_friend_extra {
    DWORD pic;
    BYTE  r04[0x18];
    WCHAR comment[0x34];
} pol_friend_extra;

/* a Content ID record */
typedef struct pol_chr {
    WORD  flags;        /* bit 0 valid */
    WORD  title;        /* 1 is FFXI */
    DWORD ctsid;
    DWORD ctid_lo, ctid_hi;
    DWORD link;         /* low byte the order, 0x8000 linked, bits 16-21 the handle's slot, bits 22-24 its place on it */
    BYTE  r14[4];
    char  name[0x10];
    char  text[0x40];
} pol_chr;

/* a message header, the first 0x48 bytes are the server's key and body points at the text once it's read */
typedef struct pol_mail {
    DWORD from_lo, from_hi;
    DWORD to_lo, to_hi;
    char  name[0x10];       /* the sender's handle, SJIS */
    BYTE  r20[0x10];
    DWORD seq, time, len;
    BYTE  from_slot, to_slot;
    WORD  flags;            /* bits 7-11 the kind (0 chat, 1 friend request), bits 12-13 1 for a delivery receipt, 0x8000 valid */
    WORD  r40;
    WORD  seen;             /* 0x100 once it's been read */
    DWORD r44, r48;
    char *body;
} pol_mail;
#pragma pack(pop)
typedef char pc_chk_game[sizeof(pol_game) == 0x10 ? 1 : -1];
typedef char pc_chk_friend[sizeof(pol_friend) == 0xb0 ? 1 : -1];
typedef char pc_chk_friend_copy[sizeof(pol_friend_copy) == 0x140 ? 1 : -1];
typedef char pc_chk_friend_extra[sizeof(pol_friend_extra) == 0x84 ? 1 : -1];
typedef char pc_chk_chr[sizeof(pol_chr) == 0x68 ? 1 : -1];
typedef char pc_chk_mail[sizeof(pol_mail) == 0x50 ? 1 : -1];

/* every field the code reads sits where polcore keeps it */
#define PC_AT(type, field, off) typedef char pc_at_##type##_##field[offsetof(type, field) == (off) ? 1 : -1]
PC_AT(pol_handle, name, 0x08);
PC_AT(pol_handle, flags, 0x18);
PC_AT(pol_handle, pic, 0x1c);
PC_AT(pol_handle, chars, 0x20);
PC_AT(pol_friend, flags, 0x08);
PC_AT(pol_friend, games, 0x18);
PC_AT(pol_friend, hid_lo, 0x98);
PC_AT(pol_friend, hid_hi, 0x9c);
PC_AT(pol_friend, name, 0xa0);
PC_AT(pol_friend_extra, comment, 0x1c);
PC_AT(pol_chr, ctsid, 0x04);
PC_AT(pol_chr, ctid_lo, 0x08);
PC_AT(pol_chr, link, 0x10);
PC_AT(pol_chr, name, 0x18);
PC_AT(pol_chr, text, 0x28);
PC_AT(pol_mail, name, 0x10);
PC_AT(pol_mail, seq, 0x30);
PC_AT(pol_mail, time, 0x34);
PC_AT(pol_mail, len, 0x38);
PC_AT(pol_mail, from_slot, 0x3c);
PC_AT(pol_mail, flags, 0x3e);
PC_AT(pol_mail, seen, 0x42);
PC_AT(pol_mail, body, 0x4c);

/* Session, text and misc */
#define SV_SJIS_TO_WIDE            0xa38   /* POL's Shift-JIS (EU's Latin extension too) to UTF-16, no bound on src */
static int pc_sjis_to_wide(BYTE *b, WCHAR *dst, const char *src, int repl)
{
    return ((int (__cdecl *)(WCHAR *, const char *, int))pc_slot(b, SV_SJIS_TO_WIDE))(dst, src, repl);
}
#define SV_WIDE_TO_SJIS            0xa40   /* UTF-16 to Shift-JIS, a NULL dst measures. Never fails, repl goes in for what SJIS hasn't got */
static int pc_wide_to_sjis(BYTE *b, char *dst, const WCHAR *src, int repl)
{
    return ((int (__cdecl *)(char *, const WCHAR *, int))pc_slot(b, SV_WIDE_TO_SJIS))(dst, src, repl);
}
#define SV_SERVER_TIME     0xac4
static int pc_server_time(BYTE *b, DWORD *now) { return ((int (__cdecl *)(DWORD *))pc_slot(b, SV_SERVER_TIME))(now); }
#define SV_VIEWER_LANGUAGE 0xf18
static int pc_viewer_language(BYTE *b) { return ((int (__cdecl *)(void))pc_slot(b, SV_VIEWER_LANGUAGE))(); }
#define SV_DATA_PATH               0x16c4   /* the path of one of the viewer's data folders (0x14 db) */
static int pc_data_path(BYTE *b, int id, WCHAR *out, int cap, int flags)
{
    return ((int (__cdecl *)(int, WCHAR *, int, int))pc_slot(b, SV_DATA_PATH))(id, out, cap, flags);
}
#define SV_DATA_FILE               0x16c8   /* the path of one of the viewer's data files (0xb entryz.dic) */
static int pc_data_file(BYTE *b, int id, WCHAR *out, int cap, int flags)
{
    return ((int (__cdecl *)(int, WCHAR *, int, int))pc_slot(b, SV_DATA_FILE))(id, out, cap, flags);
}
#define SV_DIC_RELOCATE            0x1154   /* relocates a loaded word dictionary in place, NULL when the size is off */
static void *pc_dic_relocate(BYTE *b, void *dic, int size)
{
    return ((void *(__cdecl *)(void *, int))pc_slot(b, SV_DIC_RELOCATE))(dic, size);
}
#define SV_DIC_HAS_WORD            0x1674   /* 1 when the name has a word the dictionary bans */
static int pc_dic_has_word(BYTE *b, void *dic, const WCHAR *name, int len)
{
    return ((int (__cdecl *)(void *, const WCHAR *, int))pc_slot(b, SV_DIC_HAS_WORD))(dic, name, len);
}
#define SV_MY_ID           0x558
static unsigned __int64 pc_my_id(BYTE *b) { return ((unsigned __int64 (__cdecl *)(void))pc_slot(b, SV_MY_ID))(); }
#define SV_FORMAT_POLID            0x584   /* a POL ID as the text the viewer shows */
static void pc_format_polid(BYTE *b, unsigned lo, unsigned hi, char *out)
{
    ((void (__cdecl *)(unsigned, unsigned, char *))pc_slot(b, SV_FORMAT_POLID))(lo, hi, out);
}
#define SV_FORMAT_HID              0x76c   /* an H-ID as the text the viewer shows */
static int pc_format_hid(BYTE *b, char *out, DWORD lo, DWORD hi)
{
    return ((int (__cdecl *)(char *, DWORD, DWORD))pc_slot(b, SV_FORMAT_HID))(out, lo, hi);
}
#define SV_MAIL_ADDRESS_OK         0x6e8   /* 0 when the text is a usable mail address */
static int pc_mail_address_ok(BYTE *b, const char *sjis, int len)
{
    return ((int (__cdecl *)(const char *, int))pc_slot(b, SV_MAIL_ADDRESS_OK))(sjis, len);
}
#define SV_MY_STATUS       0x338
static int pc_my_status(BYTE *b) { return ((int (__cdecl *)(void))pc_slot(b, SV_MY_STATUS))(); }
#define SV_CURRENT_HANDLE  0x2f8
static int pc_current_handle(BYTE *b) { return ((int (__cdecl *)(void))pc_slot(b, SV_CURRENT_HANDLE))(); }

/* Login */
#define SV_CLEAR_ERROR     0xd10
static int pc_clear_error(BYTE *b) { return ((int (__cdecl *)(void))pc_slot(b, SV_CLEAR_ERROR))(); }
#define SV_NOTICE_STATUS           0xd20   /* the account notice code from the login result */
static unsigned char pc_notice_status(BYTE *b) { return ((unsigned char (__cdecl *)(void))pc_slot(b, SV_NOTICE_STATUS))(); }
#define SV_NOTICE_DAYS             0xd24   /* days left on a LM-02 notice */
static unsigned char pc_notice_days(BYTE *b) { return ((unsigned char (__cdecl *)(void))pc_slot(b, SV_NOTICE_DAYS))(); }
#define SV_SET_PASSWORD            0xd50   /* SET_PASSWORD, copies the encoded password into polcore */
static int pc_set_password(BYTE *b, void *encoded, unsigned mode)
{
    return ((int (__cdecl *)(void *, unsigned))pc_slot(b, SV_SET_PASSWORD))(encoded, mode);
}
#define SV_SET_POLCOM_HOST 0xd54
static int pc_set_polcom_host(BYTE *b, void *encoded)
{
    return ((int (__cdecl *)(void *))pc_slot(b, SV_SET_POLCOM_HOST))(encoded);
}
#define SV_ENCODE_PASSWORD 0xd60
static int pc_encode_password(BYTE *b, void *out, const char *pw)
{
    return ((int (__cdecl *)(void *, const char *))pc_slot(b, SV_ENCODE_PASSWORD))(out, pw);
}
#define SV_ENCODE_HOST     0xd90
static int pc_encode_host(BYTE *b, void *out, const char *host)
{
    return ((int (__cdecl *)(void *, const char *))pc_slot(b, SV_ENCODE_HOST))(out, host);
}
#define SV_JAILED                  0x13e4   /* non-zero for an account held in the viewer's jail screen */
static int pc_jailed(BYTE *b) { return ((int (__cdecl *)(void))pc_slot(b, SV_JAILED))(); }
#define SV_BAD_PASSWORD_FLAG       0x734   /* low byte set when the viewer shows its bad password message */
static int pc_bad_password_flag(BYTE *b) { return ((int (__cdecl *)(void))pc_slot(b, SV_BAD_PASSWORD_FLAG))(); }
#define SV_SIGN_OUT_PREPARE 0xcc0
static void pc_sign_out_prepare(BYTE *b, int what) { ((void (__cdecl *)(int))pc_slot(b, SV_SIGN_OUT_PREPARE))(what); }
#define SV_SIGN_OUT        0xcc4
static int pc_sign_out(BYTE *b) { return ((int (__cdecl *)(void))pc_slot(b, SV_SIGN_OUT))(); }
#define SV_MESSAGE_NOTICES         0x1020   /* MESSAGE_NOTICES, without it polcore marks every message as lost */
static int pc_message_notices(BYTE *b, int on) { return ((int (__cdecl *)(int))pc_slot(b, SV_MESSAGE_NOTICES))(on); }
#define SV_OPTION_LOAD_START 0xe2c
static int pc_option_load_start(BYTE *b) { return ((int (__cdecl *)(void))pc_slot(b, SV_OPTION_LOAD_START))(); }
#define SV_OPTION_LOAD_DONE        0xe30   /* 0 still reading, above 0 loaded */
static int pc_option_load_done(BYTE *b) { return ((int (__cdecl *)(void))pc_slot(b, SV_OPTION_LOAD_DONE))(); }
#define SV_OPTION_LOAD_END 0xe3c
static void pc_option_load_end(BYTE *b) { ((void (__cdecl *)(void))pc_slot(b, SV_OPTION_LOAD_END))(); }
#define SV_OPTION_RECORD   0xe40
static BYTE *pc_option_record(BYTE *b, const char *name)
{
    return ((BYTE *(__cdecl *)(const char *))pc_slot(b, SV_OPTION_RECORD))(name);
}
#define SV_SET_IDLE_TIMEOUT        0x11d4   /* polcore's idle disconnect, in ms */
static void pc_set_idle_timeout(BYTE *b, int ms) { ((void (__cdecl *)(int))pc_slot(b, SV_SET_IDLE_TIMEOUT))(ms); }

/* Loads and their polls (one start and one check each, a job ticket in between) */
#define SV_STATUS_LOAD     0x5d8
static int pc_status_load(BYTE *b) { return ((int (__cdecl *)(void))pc_slot(b, SV_STATUS_LOAD))(); }
#define SV_STATUS_CHECK    0x5dc
static int pc_status_check(BYTE *b, int job) { return ((int (__cdecl *)(int))pc_slot(b, SV_STATUS_CHECK))(job); }
#define SV_FRIENDS_LOAD    0x28c
static int pc_friends_load(BYTE *b) { return ((int (__cdecl *)(void))pc_slot(b, SV_FRIENDS_LOAD))(); }
#define SV_FRIENDS_CHECK   0x290
static int pc_friends_check(BYTE *b, int job) { return ((int (__cdecl *)(int))pc_slot(b, SV_FRIENDS_CHECK))(job); }
#define SV_HANDLES_LOAD    0x2ac
static int pc_handles_load(BYTE *b) { return ((int (__cdecl *)(void))pc_slot(b, SV_HANDLES_LOAD))(); }
#define SV_HANDLES_CHECK   0x2b0
static int pc_handles_check(BYTE *b, int job) { return ((int (__cdecl *)(int))pc_slot(b, SV_HANDLES_CHECK))(job); }
#define SV_CHARACTERS_LOAD 0x2bc
static int pc_characters_load(BYTE *b) { return ((int (__cdecl *)(void))pc_slot(b, SV_CHARACTERS_LOAD))(); }
#define SV_CHARACTERS_CHECK 0x2c0
static int pc_characters_check(BYTE *b, int job) { return ((int (__cdecl *)(int))pc_slot(b, SV_CHARACTERS_CHECK))(job); }
#define SV_GROUPS_LOAD     0x5fc
static int pc_groups_load(BYTE *b) { return ((int (__cdecl *)(void))pc_slot(b, SV_GROUPS_LOAD))(); }
#define SV_GROUPS_CHECK    0x600
static int pc_groups_check(BYTE *b, int job) { return ((int (__cdecl *)(int))pc_slot(b, SV_GROUPS_CHECK))(job); }
#define SV_HANDLES_SAVE    0x1030
static int pc_handles_save(BYTE *b) { return ((int (__cdecl *)(void))pc_slot(b, SV_HANDLES_SAVE))(); }
#define SV_HANDLES_SAVE_CHECK 0x1034
static int pc_handles_save_check(BYTE *b, int job) { return ((int (__cdecl *)(int))pc_slot(b, SV_HANDLES_SAVE_CHECK))(job); }
#define SV_CANCEL_JOB              0x520   /* frees a job slot, but stalls every job poll for 10 s */
static int pc_cancel_job(BYTE *b, int job) { return ((int (__cdecl *)(int))pc_slot(b, SV_CANCEL_JOB))(job); }
#define SV_FREE_JOB                0x4dc   /* frees a job slot and makes every job poll return 0 for the next 10 s */
static void pc_free_job(BYTE *b, int job) { ((void (__cdecl *)(int))pc_slot(b, SV_FREE_JOB))(job); }

/* Handles and Content IDs */
#define SV_GET_HANDLE      0x2b4
static int pc_get_handle(BYTE *b, unsigned slot, pol_handle *out)
{
    return ((int (__cdecl *)(unsigned, pol_handle *))pc_slot(b, SV_GET_HANDLE))(slot, out);
}
#define SV_SET_HANDLE      0x2b8
static int pc_set_handle(BYTE *b, unsigned slot, const pol_handle *in)
{
    return ((int (__cdecl *)(unsigned, const pol_handle *))pc_slot(b, SV_SET_HANDLE))(slot, in);
}
#define SV_GET_CONTENT_ID  0x2c4
static int pc_get_content_id(BYTE *b, unsigned idx, void *out)
{
    return ((int (__cdecl *)(unsigned, void *))pc_slot(b, SV_GET_CONTENT_ID))(idx, out);
}
#define SV_LINK_CONTENT    0x2dc
static int pc_link_content(BYTE *b, int idx, int slot)
{
    return ((int (__cdecl *)(int, int))pc_slot(b, SV_LINK_CONTENT))(idx, slot);
}
#define SV_UNLINK_CONTENT  0x2e0
static int pc_unlink_content(BYTE *b, int idx, int slot)
{
    return ((int (__cdecl *)(int, int))pc_slot(b, SV_UNLINK_CONTENT))(idx, slot);
}
#define SV_CHANGE_MY_STATUS        0x30c   /* ChangeMyStatus, the viewer's presence push */
static int pc_change_my_status(BYTE *b, int handle, int chr, int a, int status, int b_, int location)
{
    return ((int (__cdecl *)(int, int, int, int, int, int))pc_slot(b, SV_CHANGE_MY_STATUS))(handle, chr, a, status, b_, location);
}
#define SV_CHANGE_MY_STATUS_CHECK 0x310
static int pc_change_my_status_check(BYTE *b, int job)
{
    return ((int (__cdecl *)(int))pc_slot(b, SV_CHANGE_MY_STATUS_CHECK))(job);
}
#define SV_CLAIM_HANDLE            0x314   /* claims a free handle slot for a new name */
static int pc_claim_handle(BYTE *b, int idx, int a, const void *id)
{
    return ((int (__cdecl *)(int, int, const void *))pc_slot(b, SV_CLAIM_HANDLE))(idx, a, id);
}
#define SV_RELEASE_HANDLE  0x318
static int pc_release_handle(BYTE *b, int idx) { return ((int (__cdecl *)(int))pc_slot(b, SV_RELEASE_HANDLE))(idx); }
#define SV_SELECT_HANDLE   0x1434
static void pc_select_handle(BYTE *b, int idx) { ((void (__cdecl *)(int))pc_slot(b, SV_SELECT_HANDLE))(idx); }
#define SV_LINKS_SAVE      0x1038
static int pc_links_save(BYTE *b) { return ((int (__cdecl *)(void))pc_slot(b, SV_LINKS_SAVE))(); }
#define SV_LINKS_SAVE_CHECK 0x103c
static int pc_links_save_check(BYTE *b, int job) { return ((int (__cdecl *)(int))pc_slot(b, SV_LINKS_SAVE_CHECK))(job); }

/* Friends, ignore list, comment */
#define SV_IGNORE_COPY     0x2a4
static int pc_ignore_copy(BYTE *b, int row, void *out)
{
    return ((int (__cdecl *)(int, void *))pc_slot(b, SV_IGNORE_COPY))(row, out);
}
#define SV_FRIEND_COPY     0x5cc
static int pc_friend_copy(BYTE *b, int row, void *out)
{
    return ((int (__cdecl *)(int, void *))pc_slot(b, SV_FRIEND_COPY))(row, out);
}
#define SV_SET_FRIEND_ROW  0x5f4
static void pc_set_friend_row(BYTE *b, int row, void *row_copy)
{
    ((void (__cdecl *)(int, void *))pc_slot(b, SV_SET_FRIEND_ROW))(row, row_copy);
}
#define SV_IGNORE_ADD              0x2e4   /* AddBlock, takes them off the friend list too */
static int pc_ignore_add(BYTE *b, int a, unsigned all_handles, unsigned lo, unsigned hi, unsigned slot, const char *name)
{
    return ((int (__cdecl *)(int, unsigned, unsigned, unsigned, unsigned, const char *))pc_slot(b, SV_IGNORE_ADD))(a, all_handles, lo, hi, slot, name);
}
#define SV_IGNORE_REMOVE   0x2e8
static int pc_ignore_remove(BYTE *b, int row) { return ((int (__cdecl *)(int))pc_slot(b, SV_IGNORE_REMOVE))(row); }
#define SV_FRIEND_ADD      0x2ec
static int pc_friend_add(BYTE *b, int a, unsigned b_, unsigned lo, unsigned hi, unsigned slot, const char *name)
{
    return ((int (__cdecl *)(int, unsigned, unsigned, unsigned, unsigned, const char *))pc_slot(b, SV_FRIEND_ADD))(a, b_, lo, hi, slot, name);
}
#define SV_FRIEND_DELETE   0x2f0
static int pc_friend_delete(BYTE *b, int row) { return ((int (__cdecl *)(int))pc_slot(b, SV_FRIEND_DELETE))(row); }
#define SV_FRIENDS_SAVE    0x1028
static int pc_friends_save(BYTE *b) { return ((int (__cdecl *)(void))pc_slot(b, SV_FRIENDS_SAVE))(); }
#define SV_FRIENDS_SAVE_CHECK 0x102c
static int pc_friends_save_check(BYTE *b, int job) { return ((int (__cdecl *)(int))pc_slot(b, SV_FRIENDS_SAVE_CHECK))(job); }
#define SV_COMMENT_SAVE            0x67c   /* uploads my comment (-2 for me) */
static int pc_comment_save(BYTE *b, int who, const WCHAR *text)
{
    return ((int (__cdecl *)(int, const WCHAR *))pc_slot(b, SV_COMMENT_SAVE))(who, text);
}
#define SV_COMMENT_SAVE_CHECK      0x684   /* poll of comment_save, not 0x680 */
static int pc_comment_save_check(BYTE *b, int job) { return ((int (__cdecl *)(int))pc_slot(b, SV_COMMENT_SAVE_CHECK))(job); }
#define SV_IN_CHAT_REFRESH         0x680   /* the viewer's once a second group chat refresh, local */
static int pc_in_chat_refresh(BYTE *b, int a) { return ((int (__cdecl *)(int))pc_slot(b, SV_IN_CHAT_REFRESH))(a); }
#define SV_MY_COMMENT      0x688
static int pc_my_comment(BYTE *b, int who, WCHAR *out)
{
    return ((int (__cdecl *)(int, WCHAR *))pc_slot(b, SV_MY_COMMENT))(who, out);
}

/* Messages */
#define SV_MAIL_LIST               0x524   /* starts the inbox list into an 8-aligned array of headers */
static int pc_mail_list(BYTE *b, void *out, int max)
{
    return ((int (__cdecl *)(void *, int))pc_slot(b, SV_MAIL_LIST))(out, max);
}
#define SV_MAIL_LIST_CHECK 0x528
static int pc_mail_list_check(BYTE *b, int job) { return ((int (__cdecl *)(int))pc_slot(b, SV_MAIL_LIST_CHECK))(job); }
#define SV_MAIL_READ       0x518
static int pc_mail_read(BYTE *b, void *hdr) { return ((int (__cdecl *)(void *))pc_slot(b, SV_MAIL_READ))(hdr); }
#define SV_MAIL_READ_CHECK 0x51c
static int pc_mail_read_check(BYTE *b, int job) { return ((int (__cdecl *)(int))pc_slot(b, SV_MAIL_READ_CHECK))(job); }
#define SV_MAIL_DELETE     0x508
static int pc_mail_delete(BYTE *b, void *hdr) { return ((int (__cdecl *)(void *))pc_slot(b, SV_MAIL_DELETE))(hdr); }
#define SV_MAIL_DELETE_CHECK 0x50c
static int pc_mail_delete_check(BYTE *b, int job) { return ((int (__cdecl *)(int))pc_slot(b, SV_MAIL_DELETE_CHECK))(job); }
#define SV_MAIL_SEND       0x510
static int pc_mail_send(BYTE *b, void *msg) { return ((int (__cdecl *)(void *))pc_slot(b, SV_MAIL_SEND))(msg); }
#define SV_MAIL_SEND_CHECK 0x514
static int pc_mail_send_check(BYTE *b, int job) { return ((int (__cdecl *)(int))pc_slot(b, SV_MAIL_SEND_CHECK))(job); }
#define SV_MAIL_HEADER             0x438   /* builds the 0xa8 byte header of a message */
static int pc_mail_header(BYTE *b, void *msg, int kind, unsigned from_lo, unsigned from_hi, unsigned to_lo, unsigned to_hi, const char *name, const char *subject, int from_slot, int to_slot, int a, int att_type, int body_len)
{
    return ((int (__cdecl *)(void *, int, unsigned, unsigned, unsigned, unsigned, const char *, const char *, int, int, int, int, int))pc_slot(b, SV_MAIL_HEADER))(msg, kind, from_lo, from_hi, to_lo, to_hi, name, subject, from_slot, to_slot, a, att_type, body_len);
}
#define SV_MAIL_BODY               0x440   /* builds a message's body, its length back */
static int pc_mail_body(BYTE *b, const char *subject, const char *text, int att_len, const void *att, char *out)
{
    return ((int (__cdecl *)(const char *, const char *, int, const void *, char *))pc_slot(b, SV_MAIL_BODY))(subject, text, att_len, att, out);
}
#define SV_MAIL_DECODE_HEADER      0x540   /* a saved message's header in this session's form */
static int pc_mail_decode_header(BYTE *b, void *hdr, const char *file)
{
    return ((int (__cdecl *)(void *, const char *))pc_slot(b, SV_MAIL_DECODE_HEADER))(hdr, file);
}
#define SV_JOB_SLOTS               0xff4   /* polcore's job slots in use and in all */
static void pc_job_slots(BYTE *b, int *used, int *total)
{
    ((void (__cdecl *)(int *, int *))pc_slot(b, SV_JOB_SLOTS))(used, total);
}
#define SV_SET_PUSH_CALLBACKS      0xd9c   /* registers the six push handlers */
static void pc_set_push_callbacks(BYTE *b, void **callbacks)
{
    ((void (__cdecl *)(void **))pc_slot(b, SV_SET_PUSH_CALLBACKS))(callbacks);
}
#define SV_RESET_PUSH_CALLBACKS    0xda0   /* polcore's own push handlers back */
static void pc_reset_push_callbacks(BYTE *b) { ((void (__cdecl *)(void))pc_slot(b, SV_RESET_PUSH_CALLBACKS))(); }
#define SV_NEW_MAIL_FLAG           0x1460   /* set by a push when there's new mail */
static int pc_new_mail_flag(BYTE *b) { return ((int (__cdecl *)(void))pc_slot(b, SV_NEW_MAIL_FLAG))(); }
#define SV_SET_NEW_MAIL_FLAG 0x1464
static void pc_set_new_mail_flag(BYTE *b, int flag) { ((void (__cdecl *)(int))pc_slot(b, SV_SET_NEW_MAIL_FLAG))(flag); }

/* Profile databases (the .pib and .pfb files) */
#define SV_PIB_LOAD                0x494   /* turns a loaded .pib's offsets into pointers in place */
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
static void pc_pdb_item_set(BYTE *b, pdb_schema *s, pdb_item *out, const char *name, const void *value, int level)
{
    ((void (__cdecl *)(pdb_schema *, pdb_item *, const char *, const void *, int))pc_slot(b, SV_PDB_ITEM_SET))(s, out, name, value, level);
}
#define SV_PDB_COND_TEXT           0x4a4   /* a condition's search clause */
static void pc_pdb_cond_text(BYTE *b, pdb_cond *c, int n, const char *text)
{
    ((void (__cdecl *)(pdb_cond *, int, const char *))pc_slot(b, SV_PDB_COND_TEXT))(c, n, text);
}
#define SV_PDB_COND_OP     0x4a8
static void pc_pdb_cond_op(BYTE *b, pdb_cond *c, int op)
{
    ((void (__cdecl *)(pdb_cond *, int))pc_slot(b, SV_PDB_COND_OP))(c, op);
}
#define SV_PDB_WHERE               0x4ac   /* chains n conditions into a where */
#define pc_pdb_where(b, ...) (((pdb_cond *(__cdecl *)(int, ...))pc_slot(b, SV_PDB_WHERE))(__VA_ARGS__))
#define SV_PDB_LOAD_START  0x4b4
static int pc_pdb_load_start(BYTE *b, pdb_schema *s, pdb_cond *where, pdb_rec *out)
{
    return ((int (__cdecl *)(pdb_schema *, pdb_cond *, pdb_rec *))pc_slot(b, SV_PDB_LOAD_START))(s, where, out);
}
#define SV_PDB_LOAD_CHECK  0x4b8
static int pc_pdb_load_check(BYTE *b, int job) { return ((int (__cdecl *)(int))pc_slot(b, SV_PDB_LOAD_CHECK))(job); }
#define SV_PDB_FIELD_INDEX 0x4bc
static int pc_pdb_field_index(BYTE *b, pdb_schema *s, const char *name)
{
    return ((int (__cdecl *)(pdb_schema *, const char *))pc_slot(b, SV_PDB_FIELD_INDEX))(s, name);
}
#define SV_PDB_SEARCH_START 0x4c0
static int pc_pdb_search_start(BYTE *b, void *pib, void *where, void *sort)
{
    return ((int (__cdecl *)(void *, void *, void *))pc_slot(b, SV_PDB_SEARCH_START))(pib, where, sort);
}
#define SV_PDB_SEARCH_COUNT        0x4c4   /* poll of pdb_search_start, the result set and its size */
static int pc_pdb_search_count(BYTE *b, int job, int *set, int *count)
{
    return ((int (__cdecl *)(int, int *, int *))pc_slot(b, SV_PDB_SEARCH_COUNT))(job, set, count);
}
#define SV_PDB_SEARCH_HITS         0x4e0   /* the same poll for the friend search, out is set, hits and a too-many flag */
static int pc_pdb_search_hits(BYTE *b, int job, DWORD *out)
{
    return ((int (__cdecl *)(int, DWORD *))pc_slot(b, SV_PDB_SEARCH_HITS))(job, out);
}
#define SV_PDB_SEARCH_FETCH        0x4c8   /* starts reading rows of a result set */
static int pc_pdb_search_fetch(BYTE *b, int set, void *pib, int from, int to, void *buf)
{
    return ((int (__cdecl *)(int, void *, int, int, void *))pc_slot(b, SV_PDB_SEARCH_FETCH))(set, pib, from, to, buf);
}
#define SV_PDB_SEARCH_FETCH_CHECK 0x4cc
static int pc_pdb_search_fetch_check(BYTE *b, int job)
{
    return ((int (__cdecl *)(int))pc_slot(b, SV_PDB_SEARCH_FETCH_CHECK))(job);
}
#define SV_PDB_REC_SET             0x4d0   /* builds a record to write from n items */
#define pc_pdb_rec_set(b, ...) (((void (__cdecl *)(pdb_rec *, int, int, ...))pc_slot(b, SV_PDB_REC_SET))(__VA_ARGS__))
#define SV_PDB_UPDATE_START 0x4d4
static int pc_pdb_update_start(BYTE *b, pdb_schema *s, pdb_cond *where, pdb_rec *set)
{
    return ((int (__cdecl *)(pdb_schema *, pdb_cond *, pdb_rec *))pc_slot(b, SV_PDB_UPDATE_START))(s, where, set);
}
#define SV_PDB_UPDATE_CHECK 0x4d8
static int pc_pdb_update_check(BYTE *b, int job) { return ((int (__cdecl *)(int))pc_slot(b, SV_PDB_UPDATE_CHECK))(job); }
#define SV_AWAY_TEXT               0x1200   /* the viewer's away text and who it's from, -0x14cc for none or no access */
static int pc_away_text(BYTE *b, void *pib, void *pfb, void *rec, int row, char *text, int text_cap, char *from, int from_cap)
{
    return ((int (__cdecl *)(void *, void *, void *, int, char *, int, char *, int))pc_slot(b, SV_AWAY_TEXT))(pib, pfb, rec, row, text, text_cap, from, from_cap);
}

/* Friend groups */
#define SV_GROUP_CREATE    0x608
static int pc_group_create(BYTE *b, const char *name, int slot)
{
    return ((int (__cdecl *)(const char *, int))pc_slot(b, SV_GROUP_CREATE))(name, slot);
}
#define SV_GROUP_CREATE_CHECK      0x60c   /* poll of group_create, the new group's id */
static int pc_group_create_check(BYTE *b, int job, DWORD *new_id)
{
    return ((int (__cdecl *)(int, DWORD *))pc_slot(b, SV_GROUP_CREATE_CHECK))(job, new_id);
}
#define SV_GROUP_DISBAND   0x610
static int pc_group_disband(BYTE *b, DWORD lo, DWORD hi)
{
    return ((int (__cdecl *)(DWORD, DWORD))pc_slot(b, SV_GROUP_DISBAND))(lo, hi);
}
#define SV_GROUP_DISBAND_CHECK 0x614
static int pc_group_disband_check(BYTE *b, int job) { return ((int (__cdecl *)(int))pc_slot(b, SV_GROUP_DISBAND_CHECK))(job); }
#define SV_GROUP_HEADER    0x618
static int pc_group_header(BYTE *b, DWORD lo, DWORD hi, void *out)
{
    return ((int (__cdecl *)(DWORD, DWORD, void *))pc_slot(b, SV_GROUP_HEADER))(lo, hi, out);
}
#define SV_GROUP_MEMBER    0x61c
static int pc_group_member(BYTE *b, DWORD lo, DWORD hi, unsigned i, void *out)
{
    return ((int (__cdecl *)(DWORD, DWORD, unsigned, void *))pc_slot(b, SV_GROUP_MEMBER))(lo, hi, i, out);
}
#define SV_GROUP_SET_CLASS         0x620   /* starts changing a member's class (leave, invite answers, removals) */
static int pc_group_set_class(BYTE *b, DWORD lo, DWORD hi, unsigned idx, int cls)
{
    return ((int (__cdecl *)(DWORD, DWORD, unsigned, int))pc_slot(b, SV_GROUP_SET_CLASS))(lo, hi, idx, cls);
}
#define SV_GROUP_SET_CLASS_CHECK 0x624
static int pc_group_set_class_check(BYTE *b, int job)
{
    return ((int (__cdecl *)(int))pc_slot(b, SV_GROUP_SET_CLASS_CHECK))(job);
}
#define SV_GROUP_COMMENT   0x630
static int pc_group_comment(BYTE *b, DWORD lo, DWORD hi, int a, int b_, const WCHAR *text)
{
    return ((int (__cdecl *)(DWORD, DWORD, int, int, const WCHAR *))pc_slot(b, SV_GROUP_COMMENT))(lo, hi, a, b_, text);
}
#define SV_GROUP_COMMENT_CHECK 0x634
static int pc_group_comment_check(BYTE *b, int job) { return ((int (__cdecl *)(int))pc_slot(b, SV_GROUP_COMMENT_CHECK))(job); }
#define SV_GROUP_IDS       0x648
static int pc_group_ids(BYTE *b, DWORD *ids) { return ((int (__cdecl *)(DWORD *))pc_slot(b, SV_GROUP_IDS))(ids); }

/* Group chat */
#define SV_GCHAT_START             0x6a8   /* starts joining a group's chat, 0 when polcore's busy */
static int pc_gchat_start(BYTE *b, DWORD lo, DWORD hi, const WCHAR *a, const WCHAR *b_, const WCHAR *c, void *callback, int d, int e, int f, int g, int h)
{
    return ((int (__cdecl *)(DWORD, DWORD, const WCHAR *, const WCHAR *, const WCHAR *, void *, int, int, int, int, int))pc_slot(b, SV_GCHAT_START))(lo, hi, a, b_, c, callback, d, e, f, g, h);
}
#define SV_GCHAT_JOINED_CHECK      0x6b0   /* poll of the join, above 0 in */
static int pc_gchat_joined_check(BYTE *b) { return ((int (__cdecl *)(void))pc_slot(b, SV_GCHAT_JOINED_CHECK))(); }
#define SV_GCHAT_LEAVE             0x6b4   /* starts leaving, 0 when polcore's busy */
static int pc_gchat_leave(BYTE *b) { return ((int (__cdecl *)(void))pc_slot(b, SV_GCHAT_LEAVE))(); }
#define SV_GCHAT_LEFT_CHECK        0x6b8   /* poll of the leave, non-zero once out */
static int pc_gchat_left_check(BYTE *b) { return ((int (__cdecl *)(void))pc_slot(b, SV_GCHAT_LEFT_CHECK))(); }
#define SV_GCHAT_SEND              0x6bc   /* sends a line, 0 while the last one hasn't gone */
static int pc_gchat_send(BYTE *b, const WCHAR *text)
{
    return ((int (__cdecl *)(const WCHAR *))pc_slot(b, SV_GCHAT_SEND))(text);
}
#define SV_GCHAT_FORCE_LEAVE 0x6c4
static int pc_gchat_force_leave(BYTE *b) { return ((int (__cdecl *)(void))pc_slot(b, SV_GCHAT_FORCE_LEAVE))(); }
#define SV_GCHAT_SIGN_OUT  0x6c8
static void pc_gchat_sign_out(BYTE *b) { ((void (__cdecl *)(void))pc_slot(b, SV_GCHAT_SIGN_OUT))(); }
#define SV_GCHAT_CHANNEL           0x6cc   /* 0 when I'm in a group chat, its channel in out */
static int pc_gchat_channel(BYTE *b, char *out) { return ((int (__cdecl *)(char *))pc_slot(b, SV_GCHAT_CHANNEL))(out); }

/* Chat rooms (the IRC link) */
#define SV_CHAT_SESSION            0xcd0   /* the link's session, below 0 without one */
static int pc_chat_session(BYTE *b) { return ((int (__cdecl *)(void))pc_slot(b, SV_CHAT_SESSION))(); }
#define SV_CHAT_UNIQUE_TEXT        0x224   /* text unique to now and this login */
static void pc_chat_unique_text(BYTE *b, int s, char *out)
{
    ((void (__cdecl *)(int, char *))pc_slot(b, SV_CHAT_UNIQUE_TEXT))(s, out);
}
#define SV_CHAT_WHO        0x228
static int pc_chat_who(BYTE *b, int s, const char *room)
{
    return ((int (__cdecl *)(int, const char *))pc_slot(b, SV_CHAT_WHO))(s, room);
}
#define SV_CHAT_PRIVMSG    0x22c
static int pc_chat_privmsg(BYTE *b, int s, DWORD lo, DWORD hi, const char *room, const char *frame)
{
    return ((int (__cdecl *)(int, DWORD, DWORD, const char *, const char *))pc_slot(b, SV_CHAT_PRIVMSG))(s, lo, hi, room, frame);
}
#define SV_CHAT_AWAY       0x234
static int pc_chat_away(BYTE *b, int s) { return ((int (__cdecl *)(int))pc_slot(b, SV_CHAT_AWAY))(s); }
#define SV_CHAT_KICK       0x238
static int pc_chat_kick(BYTE *b, int s, const char *room, DWORD lo, DWORD hi)
{
    return ((int (__cdecl *)(int, const char *, DWORD, DWORD))pc_slot(b, SV_CHAT_KICK))(s, room, lo, hi);
}
#define SV_CHAT_JOIN       0x240
static int pc_chat_join(BYTE *b, int s, const char *room, const char *key)
{
    return ((int (__cdecl *)(int, const char *, const char *))pc_slot(b, SV_CHAT_JOIN))(s, room, key);
}
#define SV_CHAT_COMMAND_CHECK      0x244   /* poll of the command in flight */
static int pc_chat_command_check(BYTE *b, int s) { return ((int (__cdecl *)(int))pc_slot(b, SV_CHAT_COMMAND_CHECK))(s); }
#define SV_CHAT_COMMAND_FORGET     0x248   /* forgets the command in flight */
static int pc_chat_command_forget(BYTE *b, int s) { return ((int (__cdecl *)(int))pc_slot(b, SV_CHAT_COMMAND_FORGET))(s); }
#define SV_CHAT_MESSAGE_CHECK      0x24c   /* poll of the PRIVMSG in flight */
static int pc_chat_message_check(BYTE *b, int s, int *out)
{
    return ((int (__cdecl *)(int, int *))pc_slot(b, SV_CHAT_MESSAGE_CHECK))(s, out);
}
#define SV_CHAT_TOPIC              0x254   /* TOPIC, NULL topic asks for it */
static int pc_chat_topic(BYTE *b, int s, const char *room, const char *topic)
{
    return ((int (__cdecl *)(int, const char *, const char *))pc_slot(b, SV_CHAT_TOPIC))(s, room, topic);
}
#define SV_CHAT_MODE       0x258
static int pc_chat_mode(BYTE *b, int s, int mode, const char *room, DWORD lo, DWORD hi, const char *arg, int on)
{
    return ((int (__cdecl *)(int, int, const char *, DWORD, DWORD, const char *, int))pc_slot(b, SV_CHAT_MODE))(s, mode, room, lo, hi, arg, on);
}
#define SV_CHAT_PART       0x260
static int pc_chat_part(BYTE *b, int s, const char *room)
{
    return ((int (__cdecl *)(int, const char *))pc_slot(b, SV_CHAT_PART))(s, room);
}
#define SV_CHAT_MY_ID              0x270   /* my id on the link */
static unsigned __int64 pc_chat_my_id(BYTE *b, int s)
{
    return ((unsigned __int64 (__cdecl *)(int))pc_slot(b, SV_CHAT_MY_ID))(s);
}
#define SV_CHAT_MEMBERS            0x280   /* polcore's member array for a room, local only */
static int pc_chat_members(BYTE *b, int s, const char *room, BYTE *slots, BYTE *members)
{
    return ((int (__cdecl *)(int, const char *, BYTE *, BYTE *))pc_slot(b, SV_CHAT_MEMBERS))(s, room, slots, members);
}
#define SV_CHAT_ZONE_TEXT          0x360   /* turns a zone and categories into 12 characters with no end */
static void pc_chat_zone_text(BYTE *b, const void *v, char *out, int a, int b_)
{
    ((void (__cdecl *)(const void *, char *, int, int))pc_slot(b, SV_CHAT_ZONE_TEXT))(v, out, a, b_);
}
#define SV_CHAT_CHANGED            0x146c   /* non-zero when the room's lists changed */
static int pc_chat_changed(BYTE *b, int s) { return ((int (__cdecl *)(int))pc_slot(b, SV_CHAT_CHANGED))(s); }

/* slots whose value is polcore's own handler, kept when the hook has no better */
#define SV_PUSH_DEFAULT            0x538   /* polcore's own handler for the push the list leaves to it */
#define SV_ROOM_NOTICE             0x644   /* polcore's room NOTICE handler, ours asks it first */

/* app.dll's region and viewer setup calls */
#define SV_SET_REGION      0xf14
static int pc_set_region(BYTE *b, int selector) { return ((int (__cdecl *)(int))pc_slot(b, SV_SET_REGION))(selector); }
#define SV_SET_JP_TABLE    0x17dc
static int pc_set_jp_table(BYTE *b, int on) { return ((int (__cdecl *)(int))pc_slot(b, SV_SET_JP_TABLE))(on); }
#define SV_SET_LANGUAGE            0x690   /* the language FFXI reads back through 0x694 as its area code */
static int pc_set_language(BYTE *b, int lang) { return ((int (__cdecl *)(int))pc_slot(b, SV_SET_LANGUAGE))(lang); }
#define SV_SET_JP_LANGUAGE         0x17e0   /* JP's own language step after set_jp_table */
static int pc_set_jp_language(BYTE *b, int lang) { return ((int (__cdecl *)(int))pc_slot(b, SV_SET_JP_LANGUAGE))(lang); }
#define SV_SET_MESSAGE_TITLE 0x10c0
static int pc_set_message_title(BYTE *b, int i, const char *text)
{
    return ((int (__cdecl *)(int, const char *))pc_slot(b, SV_SET_MESSAGE_TITLE))(i, text);
}
#define SV_SET_MESSAGE_TEXT 0x1458
static int pc_set_message_text(BYTE *b, int i, const char *text)
{
    return ((int (__cdecl *)(int, const char *))pc_slot(b, SV_SET_MESSAGE_TEXT))(i, text);
}
#define SV_SET_REGISTRY_KEY 0x5bc
static int pc_set_registry_key(BYTE *b, const char *key)
{
    return ((int (__cdecl *)(const char *))pc_slot(b, SV_SET_REGISTRY_KEY))(key);
}
#define SV_SET_INSTALL_FOLDER 0x1f4
static int pc_set_install_folder(BYTE *b, const char *folder)
{
    return ((int (__cdecl *)(const char *))pc_slot(b, SV_SET_INSTALL_FOLDER))(folder);
}
#define SV_FILE_INIT       0xe90
static int pc_file_init(BYTE *b) { return ((int (__cdecl *)(void))pc_slot(b, SV_FILE_INIT))(); }
#define SV_SET_USER_SLOT           0xd98   /* SET_USER_SLOT, the pub\homeNN folder */
static int pc_set_user_slot(BYTE *b, int slot) { return ((int (__cdecl *)(int))pc_slot(b, SV_SET_USER_SLOT))(slot); }

/* a handle record is in use when bit 0 of lo is set. The Handle ID is bits 1-44 of lo and hi together, and bits 13-18 of
   hi are the slot it's in. handle_get is 0 for a slot that has none, and fills h either way */
static unsigned __int64 hid_of(DWORD lo, DWORD hi) { return (((unsigned __int64)hi << 32 | lo) >> 1) & 0xfffffffffffULL; }
static unsigned __int64 handle_hid(const pol_handle *h) { return hid_of(h->lo, h->hi); }
static int handle_get(BYTE *b, int slot, pol_handle *h)
{
    memset(h, 0, sizeof *h);
    return slot >= 0 && slot < 64 && pc_get_handle(b, (unsigned)slot, h) >= 0 && (h->lo & 1);
}

#define POLCORE_LINK_SIZE         0x3a00
#define POLCORE_LINK_LAST_SEND    0x39d4    /* in a connection record, the tick polcore last sent something */
#define POLCORE_STATUS_STRIDE     0x338
/* which of the stamps is which list's */
#define STAMP_FRIENDS 0
#define STAMP_HANDLES 2

#define AUTH_START      0x12
#define AUTH_IN         30
#define AUTH_NO_ERROR   0xfffffbee
/* the states the login stops on, 30 is in and the rest are refusals */
static int auth_settled(unsigned st) { return st == AUTH_IN || st == 32 || st == 36 || st == 39; }

static unsigned *pc_auth_state(BYTE *b) { return (unsigned *)addr_of(b, AT_AUTH_STATE); }
static int *pc_auth_reason(BYTE *b) { return (int *)addr_of(b, AT_AUTH_REASON); }
static int *pc_auth_result(BYTE *b) { return (int *)addr_of(b, AT_AUTH_RESULT); }
static unsigned *pc_auth_error(BYTE *b) { return (unsigned *)addr_of(b, AT_AUTH_ERROR); }
static WORD *pc_login_flags(BYTE *b) { return (WORD *)addr_of(b, AT_LOGIN_FLAGS); }
static int *pc_link_index(BYTE *b) { return (int *)addr_of(b, AT_LINK_INDEX); }
static int *pc_session_ready(BYTE *b) { return (int *)addr_of(b, AT_SESSION_READY); }
static int *pc_session_alive(BYTE *b) { return (int *)addr_of(b, AT_SESSION_ALIVE); }
static DWORD *pc_link_last_send(BYTE *b, int link)
{
    return (DWORD *)(addr_of(b, AT_LINKS) + link * POLCORE_LINK_SIZE + POLCORE_LINK_LAST_SEND);
}
static BYTE *pc_gchat_line_held(BYTE *b) { return addr_of(b, AT_GCHAT_LINE_HELD); }
static BYTE *pc_status_answer(BYTE *b, int job)
{
    return *(BYTE **)(addr_of(b, AT_STATUS_ANSWERS) + job * POLCORE_STATUS_STRIDE);
}
static unsigned *pc_server_stamp(BYTE *b, int list) { return (unsigned *)addr_of(b, AT_SERVER_STAMPS) + list; }
static unsigned *pc_local_stamp(BYTE *b, int list) { return (unsigned *)addr_of(b, AT_LOCAL_STAMPS) + list; }

/* the three friend arrays and the two record arrays, by row. An out of range row is the caller's to avoid */
static pol_friend *friend_row(BYTE *b, int i) { return (pol_friend *)addr_of(b, AT_FRIENDS_A) + i; }
static pol_friend *ignore_row(BYTE *b, int i) { return (pol_friend *)addr_of(b, AT_IGNORE_ROWS) + i; }
static pol_friend_extra *friend_extra(BYTE *b, int i) { return (pol_friend_extra *)addr_of(b, AT_FRIENDS_B) + i; }
static pol_chr *content_record(BYTE *b, int idx) { return (pol_chr *)addr_of(b, AT_CONTENT_IDS) + idx; }
static pol_handle *handle_record(BYTE *b, int slot) { return (pol_handle *)addr_of(b, AT_HANDLES) + slot; }
/* the list's reload test: the server's stamp is newer than mine, so an upload turns into a load */
static int friends_server_newer(BYTE *b) { return *pc_server_stamp(b, STAMP_FRIENDS) > *pc_local_stamp(b, STAMP_FRIENDS); }
static int handles_server_newer(BYTE *b) { return *pc_server_stamp(b, STAMP_HANDLES) > *pc_local_stamp(b, STAMP_HANDLES); }

static int friend_valid(const pol_friend *f) { return (f->hid_lo & 1) != 0; }
static unsigned __int64 friend_hid(const pol_friend *f) { return hid_of(f->hid_lo, f->hid_hi); }
static int friend_slot(const pol_friend *f) { return (int)((f->hid_hi >> 13) & 0x3f); }
#define FRIEND_PENDING 0x10000000
static int chr_valid(const pol_chr *c) { return (c->flags & 1) != 0; }
#define CHR_LINKED 0x8000            /* pol_chr.link: on a handle */
#define CHR_HANDLE(f) (((f) >> 16) & 0x3f)
#define CHR_PLACE(f) (((f) >> 22) & 7)

#define MAIL(h) ((pol_mail *)(h))
#define MAIL_KEY_SIZE 0x48           /* the first bytes of a header are the server's key for the message */
#define MAIL_BUILT_SIZE 0xa8         /* what mail_header builds, mail_body puts the text after it */
#define MAIL_VALID 0x8000
static unsigned mail_kind(const void *h) { return (MAIL(h)->flags >> 7) & 0x1f; }
static unsigned mail_att_type(const void *h) { return MAIL(h)->flags & 0x7f; }   /* 10 is a group invitation or its answer */
static int mail_receipt(const void *h) { return ((MAIL(h)->flags >> 12) & 3) == 1; }

/* functions called by address, not through the service table */
static void pc_sha1_init(BYTE *b, void *ctx) { ((void (__cdecl *)(void *))addr_of(b, AT_SHA1_INIT))(ctx); }
static void pc_sha1_update(BYTE *b, void *ctx, const void *data, unsigned len)
{
    ((void (__cdecl *)(void *, const void *, unsigned))addr_of(b, AT_SHA1_UPDATE))(ctx, data, len);
}
static void pc_sha1_final(BYTE *b, void *out, void *ctx) { ((void (__cdecl *)(void *, void *))addr_of(b, AT_SHA1_FINAL))(out, ctx); }
static int pc_set_sea_secret(BYTE *b, int mode, const char *id, const void *secret, const void *otp)
{
    return ((int (__cdecl *)(int, const char *, const void *, const void *))addr_of(b, AT_SET_SEA_SECRET))(mode, id, secret, otp);
}
static void pc_encode_polid(BYTE *b, const char *id)
{
    ((void (__cdecl *)(void *, const char *))addr_of(b, AT_ENCODE_POLID))(addr_of(b, AT_ENCODED_POLID), id);
}
static int pc_session_init(BYTE *b) { return ((int (__cdecl *)(void))addr_of(b, AT_SESSION_INIT))(); }
static int pc_inet_init(BYTE *b) { return ((int (__cdecl *)(void))addr_of(b, AT_INET_INIT))(); }
static int pc_con_open(BYTE *b, int type, void *conn) { return ((int (__cdecl *)(int, void *))addr_of(b, AT_CON_OPEN))(type, conn); }
static int pc_con_step(BYTE *b) { return ((int (__cdecl *)(void))addr_of(b, AT_CON_STEP))(); }
static int pc_con_idle(BYTE *b) { return ((int (__cdecl *)(void))addr_of(b, AT_CON_IDLE))(); }
/* one turn of polcore's own pump, below 0 once the session's gone */
static int pump_once(BYTE *b)
{
    pc_con_step(b);
    return pc_con_idle(b);
}

#endif
