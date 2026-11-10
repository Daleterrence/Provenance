#ifndef POLCORE_RECORDS_H
#define POLCORE_RECORDS_H

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

#endif
