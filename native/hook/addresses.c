#define IN_EXE 0
#define IN_POLCORE 1
#define IN_FFXI 2
#define IS_DATA 0
#define IS_CODE 1
#define SKIPPABLE 0
#define REQUIRED 1
#define ADDR_MATCH (-1)

#define SIG_STAMPS "8b 44 24 04 85 c0 b8 ?? ?? ?? ?? 75 05 b8 ?? ?? ?? ?? 8b 4c 24 08 85 c9 74 02 89 08"
#define SIG_SPLASH "6a 00 68 ?? ?? ?? ?? 6a 00 6a 67 ff 35 ?? ?? ?? ?? ff 15 ?? ?? ?? ??"
#define SIG_FX_NETSTAT "8d 7e 1c e8 ?? ?? ?? ?? 25 ff ff 00 00 8b cf 50 e8 ?? ?? ?? ?? e8 ?? ?? ?? ?? 25 ff ff 00 00 8b cf 50 e8 ?? ?? ?? ??"
#define SIG_FX_SETTER "8b 44 24 04 56 8b f1 8b 4e 08 89 06 3b c1 7e 25 6a 48 e8 ?? ?? ?? ?? 83 c4 04 85 c0 74 0e e8 ?? ?? ?? ??"
#define SIG_FX_LOOKUP "8b 15 ?? ?? ?? ?? c7 05 ?? ?? ?? ?? 00 00 00 00 8b 02 50 68 ?? ?? ?? ?? e8 ?? ?? ?? ?? 8b f0 83 c4 08 85 f6 " \
    "0f 84 ?? ?? ?? ?? 0f bf 7e 14 85 ff 0f 84 ?? ?? ?? ?? f6 46 17 20 74 12 68 ?? ?? ?? ?? 68"
#define SIG_FX_FROM_LINE "c6 44 24 0c 12 8b 46 08 c6 44 24 0d 00 50 68 ?? ?? ?? ?? 51 c6 44 24 1a 00 e8 ?? ?? ?? ?? 8b 0d ?? ?? ?? ?? " \
    "83 c4 0c 8d 54 24 0c 8d 44 24 14 6a 00 6a 01 6a 00 52 50 e8 ?? ?? ?? ??"
#define SIG_FX_EXPAND "8b fe 83 c9 ff 33 c0 6a 00 f2 ae f7 d1 49 6a 01 51 8b 0d ?? ?? ?? ?? 56 8d 54 24 64 68 00 08 00 00 52 e8 ?? ?? ?? ??"
/* the address sits past the end of what's matched, so the signature carries ?? up to and over it */
#define ANY1 " ??"
#define ANY4 ANY1 ANY1 ANY1 ANY1
#define ANY16 ANY4 ANY4 ANY4 ANY4
#define SIG_FX_MENU_FRONT "8b 48 0c 85 c9 74 ?? 8b 51 08 85 d2 74 ?? 3b 05" ANY4
#define SIG_FX_LICENCE "89 5e 1c 89 5e 14 89 6e 18 89 ?? ?? ?? ?? ?? eb ?? 89 ?? ?? ?? ?? ?? 68" ANY16 ANY16 ANY16 ANY1 ANY1 ANY1
#define SIG_FX_LOBBY "89 41 2c 8b 15 ?? ?? ?? ?? 89 7c 24 10 89 42 30 a1" ANY4
#define SIG_FX_CHAR_INDEX "a1 ?? ?? ?? ?? 8d 0c 80 8d 14 48 a1 ?? ?? ?? ?? c1 e2 04 8d 4c 02 44"

static struct hook_addr g_addr[ADDRESS_COUNT] = {
    /* starts on the push ebp, a detour past the frame setup leaves the stub's stack a dword off */
    [AT_MENU_SESSION] = { .name = "menu session", .module = IN_EXE, .code = IS_CODE, .required = REQUIRED,
        .usual = 0x27030, .sig_at = 0x27030, .operand = ADDR_MATCH,
        .sig = "55 8b ec 6a ff 68 ?? ?? ?? ?? 64 a1 00 00 00 00 50 81 ec 88 08 00 00 a1 ?? ?? ?? ?? 33 c5 89 45 f0 "
            "56 57 50 8d 45 f4 64 a3 00 00 00 00 8b f1 80 7e 21 01" },
    [AT_GAME_RETURN] = { .name = "game return", .module = IN_EXE, .code = IS_CODE, .required = REQUIRED,
        .usual = 0x20c58, .sig_at = 0x20bf4, .operand = ADDR_MATCH,
        .sig = "51 68 ?? ?? ?? ?? 6a 17 6a 00 50 ff 15 ?? ?? ?? ?? 85 c0 0f 88 ?? ?? ?? ?? "
            "68 ?? ?? ?? ?? e8 ?? ?? ?? ?? a1 ?? ?? ?? ?? 8d 55 d4 c1 e0 04 83 c4 04 05 ?? ?? ?? ?? "
            "c7 45 d4 1c 00 00 00 c7 45 d8 00 02 00 00 52 0f 10 00 8b 45 10 ff 37 89 45 ec 8b 45 d0 "
            "0f 11 45 dc 50 8b 08 ff 51 0c 68 ?? ?? ?? ?? 8b f8 e8 ?? ?? ?? ??",
        .shows_op = 2, .shows = "0c 79 9d 98 36 62 d4 11 80 e9 00 10 5a 81 e8 90",
        .calls_op = 101, .calls = "c2 00 00" },
    [AT_CREATE_MUTEX_SLOT] = { .name = "CreateMutexW slot", .module = IN_EXE, .code = IS_DATA, .required = SKIPPABLE,
        .usual = 0xee734, .sig_at = 0x25c07, .operand = 8,
        .sig = "8b f1 6a 01 6a 00 ff 15 ?? ?? ?? ??" },
    [AT_OPEN_MUTEX_SLOT] = { .name = "OpenMutexW slot", .module = IN_EXE, .code = IS_DATA, .required = SKIPPABLE,
        .usual = 0xee7b4, .sig_at = 0x35096, .operand = 9,
        .sig = "6a 01 68 01 00 1f 00 ff 15 ?? ?? ?? ?? 85 c0" },
    [AT_CREATE_DIALOG_SLOT] = { .name = "CreateDialogParamW slot", .module = IN_EXE, .code = IS_DATA, .required = SKIPPABLE,
        .usual = 0xee7e4, .sig_at = 0x2642a, .operand = 19,
        .sig = SIG_SPLASH },
    [AT_SPLASH_PROC] = { .name = "splash proc", .module = IN_EXE, .code = IS_CODE, .required = SKIPPABLE,
        .usual = 0x26b10, .sig_at = 0x2642a, .operand = 3,
        .sig = SIG_SPLASH },
    /* Called as a plain function, so it starts on the push ebp or it runs on the caller's frame */
    [AT_SPLASH_CLOSE] = { .name = "splash close", .module = IN_EXE, .code = IS_CODE, .required = REQUIRED,
        .usual = 0x26a40, .sig_at = 0x26a40, .operand = ADDR_MATCH,
        .sig = "55 8b ec 83 ec 08 83 3d ?? ?? ?? ?? 00 74 ?? 6a 05 e8 ?? ?? ?? ?? 2b 05 ?? ?? ?? ?? 1b 15 ?? ?? ?? ??" },
    [AT_REGION_INDEX] = { .name = "region index", .module = IN_EXE, .code = IS_DATA, .required = REQUIRED,
        .usual = 0xf03dc, .sig_at = 0x263dd, .operand = 2,
        .sig = "8b 0d ?? ?? ?? ?? 8b c1 83 e8 00 74 ?? 83 e8 01 74 ??" },
    [AT_DX_CHECK] = { .name = "DirectX check", .module = IN_EXE, .code = IS_CODE, .required = SKIPPABLE,
        .usual = 0x25970, .sig_at = 0x25970, .operand = ADDR_MATCH,
        .sig = "55 8b ec 51 6a 00 8d 45 fc c7 45 fc 00 00 00 00 6a 00 50 e8 ?? ?? ?? ?? "
            "83 c4 0c 81 7d fc 00 01 08 00 0f 93 c0 8b e5 5d c3" },

    [AT_SERVICES] = { .name = "service table", .module = IN_POLCORE, .code = IS_DATA, .required = REQUIRED,
        .usual = 0xc4180, .sig_at = 0x51385, .operand = 8,
        .sig = "55 8b ec 8b 45 0c c7 00 ?? ?? ?? ?? 33 c0 5d c2 08 00" },
    [AT_SHA1_INIT] = { .name = "SHA-1 init", .module = IN_POLCORE, .code = IS_CODE, .required = REQUIRED,
        .usual = 0x286a0, .sig_at = 0x286a0, .operand = ADDR_MATCH,
        .sig = "8b 44 24 04 33 c9 c7 00 01 23 45 67 c7 40 04 89 ab cd ef" },
    [AT_SHA1_UPDATE] = { .name = "SHA-1 update", .module = IN_POLCORE, .code = IS_CODE, .required = REQUIRED,
        .usual = 0x286d0, .sig_at = 0x286d0, .operand = ADDR_MATCH,
        .sig = "8b 54 24 0c 53 56 8b 74 24 0c 57 8b 4e 14 8b c1 c1 e8 03 8d 3c d1" },
    [AT_SHA1_FINAL] = { .name = "SHA-1 final", .module = IN_POLCORE, .code = IS_CODE, .required = REQUIRED,
        .usual = 0x28ac0, .sig_at = 0x28ac0, .operand = ADDR_MATCH,
        .sig = "53 55 8b 6c 24 10 56 57 55 e8 ?? ?? ?? ?? 8b 7c 24 18 83 c4 04" },
    [AT_SET_SEA_SECRET] = { .name = "SEA secret setter", .module = IN_POLCORE, .code = IS_CODE, .required = REQUIRED,
        .usual = 0x1a8e0, .sig_at = 0x1a8e0, .operand = ADDR_MATCH,
        .sig = "e8 ?? ?? ?? ?? 8b 4c 24 04 33 c0 3b c8 7c 0b 83 f9 02 7f 06" },
    [AT_SESSION_INIT] = { .name = "session init", .module = IN_POLCORE, .code = IS_CODE, .required = REQUIRED,
        .usual = 0x3f430, .sig_at = 0x3f430, .operand = ADDR_MATCH,
        .sig = "56 57 33 f6 a1 ?? ?? ?? ?? 3b c6 75 11 33 ff 57 e8 ?? ?? ?? ??" },
    [AT_INET_INIT] = { .name = "inet init", .module = IN_POLCORE, .code = IS_CODE, .required = REQUIRED,
        .usual = 0xaa90, .sig_at = 0xaa90, .operand = ADDR_MATCH,
        .sig = "81 ec 90 01 00 00 a1 ?? ?? ?? ?? 85 c0 75 72 6a 00 e8 ?? ?? ?? ??" },
    [AT_CON_OPEN] = { .name = "POLCON_OPEN", .module = IN_POLCORE, .code = IS_CODE, .required = REQUIRED,
        .usual = 0x3f940, .sig_at = 0x3f940, .operand = ADDR_MATCH,
        .sig = "83 ec 14 53 33 db 57 53 e8 ?? ?? ?? ?? a1 ?? ?? ?? ?? 83 c4 04 83 f8 01" },
    [AT_CON_STEP] = { .name = "POLCON_STEP", .module = IN_POLCORE, .code = IS_CODE, .required = REQUIRED,
        .usual = 0x3faf0, .sig_at = 0x3faf0, .operand = ADDR_MATCH,
        .sig = "81 ec 78 01 00 00 53 55 56 57 33 ff 57 e8 ?? ?? ?? ?? 8b 0d ?? ?? ?? ??" },
    [AT_CON_IDLE] = { .name = "POLCON_IDLE", .module = IN_POLCORE, .code = IS_CODE, .required = REQUIRED,
        .usual = 0x40520, .sig_at = 0x40520, .operand = ADDR_MATCH,
        .sig = "51 57 33 ff a1 ?? ?? ?? ?? 3b c7 75 08 b8 01 00 00 00 5f 59 c3" },
    [AT_ENCODE_POLID] = { .name = "ENCODE_POLID", .module = IN_POLCORE, .code = IS_CODE, .required = REQUIRED,
        .usual = 0x41f30, .sig_at = 0x41f10, .operand = ADDR_MATCH,
        .sig = "56 8b 74 24 08 57 b9 10 00 00 00 bf ?? ?? ?? ?? f3 a5 66 a5 5f 5e c3 90 90 90 90 90 90 90 90 90 "
            "8b 44 24 08 8b 4c 24 04 6a 08 50 51 e8 ?? ?? ?? ?? 83 c4 0c c3" },
    [AT_ENCODED_POLID] = { .name = "encoded POL ID", .module = IN_POLCORE, .code = IS_DATA, .required = REQUIRED,
        .usual = 0xed850, .sig_at = 0x41e58, .operand = 9,
        .sig = "8b 74 24 14 56 52 8b 06 a3 ?? ?? ?? ?? 8b 4e 04" },
    [AT_AUTH_STATE] = { .name = "auth state", .module = IN_POLCORE, .code = IS_DATA, .required = REQUIRED,
        .usual = 0xed9d8, .sig_at = 0x3fc7f, .operand = 2,
        .sig = "c7 05 ?? ?? ?? ?? 14 00 00 00 8d 44 24 44" },
    [AT_AUTH_REASON] = { .name = "auth reason", .module = IN_POLCORE, .code = IS_DATA, .required = REQUIRED,
        .usual = 0xed9dc, .sig_at = 0x4029f, .operand = 2,
        .sig = "c7 05 ?? ?? ?? ?? f4 fb ff ff 57 8b f1 e8 ?? ?? ?? ??" },
    [AT_AUTH_RESULT] = { .name = "auth result", .module = IN_POLCORE, .code = IS_DATA, .required = REQUIRED,
        .usual = 0xedc84, .sig_at = 0x3fcbe, .operand = 3,
        .sig = "3b c7 a3 ?? ?? ?? ?? 7c 15 b9 15 00 00 00" },
    [AT_AUTH_ERROR] = { .name = "auth error", .module = IN_POLCORE, .code = IS_DATA, .required = REQUIRED,
        .usual = 0xeefe4, .sig_at = 0x40905, .operand = 14,
        .sig = "83 c4 08 c7 05 ?? ?? ?? ?? ff ff ff ff a3 ?? ?? ?? ??" },
    [AT_LOGIN_FLAGS] = { .name = "login flags", .module = IN_POLCORE, .code = IS_DATA, .required = REQUIRED,
        .usual = 0xed9c4, .sig_at = 0x3fede, .operand = 7,
        .sig = "51 e8 ?? ?? ?? ?? a0 ?? ?? ?? ?? 83 c4 10 84 c3 74 16" },
    [AT_LINK_INDEX] = { .name = "link index", .module = IN_POLCORE, .code = IS_DATA, .required = REQUIRED,
        .usual = 0xed820, .sig_at = 0x40901, .operand = 9,
        .sig = "8b 44 24 0c 83 c4 08 c7 05 ?? ?? ?? ?? ff ff ff ff" },
    [AT_SESSION_READY] = { .name = "session ready", .module = IN_POLCORE, .code = IS_DATA, .required = REQUIRED,
        .usual = 0xed824, .sig_at = 0x409a2, .operand = 6,
        .sig = "85 c0 74 0a c7 05 ?? ?? ?? ?? 00 00 00 00 6a 00" },
    [AT_SESSION_ALIVE] = { .name = "session alive", .module = IN_POLCORE, .code = IS_DATA, .required = REQUIRED,
        .usual = 0xed81c, .sig_at = 0x4043d, .operand = 6,
        .sig = "be 01 00 00 00 a1 ?? ?? ?? ?? 3b c6 75 29" },
    [AT_LINKS] = { .name = "connection records", .module = IN_POLCORE, .code = IS_DATA, .required = REQUIRED,
        .usual = 0x43c340, .sig_at = 0xeed6, .operand = 2,
        .sig = "81 c6 ?? ?? ?? ?? 56 e8 ?? ?? ?? ?? 8b be 18 02 00 00" },
    [AT_IGNORE_ROWS] = { .name = "ignore rows", .module = IN_POLCORE, .code = IS_DATA, .required = REQUIRED,
        .usual = 0x104dc8, .sig_at = 0x1b2a8, .operand = 8,
        .sig = "8d 3c 78 c1 e7 04 81 c7 ?? ?? ?? ?? 0b ca" },
    [AT_FRIENDS_A] = { .name = "friend rows", .module = IN_POLCORE, .code = IS_DATA, .required = REQUIRED,
        .usual = 0x109288, .sig_at = 0x1b681, .operand = 8,
        .sig = "8d 0c 47 c1 e1 04 81 c1 ?? ?? ?? ?? 51 56" },
    [AT_FRIENDS_B] = { .name = "friend extras", .module = IN_POLCORE, .code = IS_DATA, .required = REQUIRED,
        .usual = 0x454de0, .sig_at = 0x1b783, .operand = 8,
        .sig = "89 46 08 89 56 0c 8b 97 ?? ?? ?? ?? 8d 87 ?? ?? ?? ??" },
    [AT_SERVER_STAMPS] = { .name = "server stamps", .module = IN_POLCORE, .code = IS_DATA, .required = REQUIRED,
        .usual = 0x45b500, .sig_at = 0x1ad60, .operand = 7,
        .sig = SIG_STAMPS },
    [AT_LOCAL_STAMPS] = { .name = "local stamps", .module = IN_POLCORE, .code = IS_DATA, .required = REQUIRED,
        .usual = 0x45b50c, .sig_at = 0x1ad60, .operand = 14,
        .sig = SIG_STAMPS },
    [AT_CONTENT_IDS] = { .name = "Content ID records", .module = IN_POLCORE, .code = IS_DATA, .required = REQUIRED,
        .usual = 0x4533e0, .sig_at = 0x1d580, .operand = 3,
        .sig = "8d 34 cd ?? ?? ?? ?? 66 89 56 02 8b 47 04" },
    [AT_HANDLES] = { .name = "handle records", .module = IN_POLCORE, .code = IS_DATA, .required = REQUIRED,
        .usual = 0x45b520, .sig_at = 0x18ccb, .operand = 3,
        .sig = "8b 04 c5 ?? ?? ?? ?? 83 e0 01 0b c1 75 0c" },
    [AT_STATUS_ANSWERS] = { .name = "status answers", .module = IN_POLCORE, .code = IS_DATA, .required = REQUIRED,
        .usual = 0x45c2b8, .sig_at = 0x2060a, .operand = 10,
        .sig = "57 8d 0c 40 8d 14 4e 8b 3c d5 ?? ?? ?? ??" },
    [AT_GCHAT_LINE_HELD] = { .name = "group chat line", .module = IN_POLCORE, .code = IS_DATA, .required = REQUIRED,
        .usual = 0x112de0, .sig_at = 0x21cb5, .operand = 10,
        .sig = "8d 44 24 18 8d 54 09 08 52 68 ?? ?? ?? ??" },
    /* POLCON_POLL is IDLE and then the pointer if it's set. Nothing in polcore sets it, so it's the one place that runs after
       each of the game's pumps without a table slot being swapped */
    [AT_POLL_CALLBACK] = { .name = "POLL callback", .module = IN_POLCORE, .code = IS_DATA, .required = SKIPPABLE,
        .usual = 0x435528, .sig_at = 0x45750, .operand = 9,
        .sig = "56 e8 ?? ?? ?? ?? 8b f0 a1 ?? ?? ?? ?? 85 c0 74 07 6a 00 ff d0 83 c4 04 8b c6 5e c3" },

    /* FFXiMain is packed on disk, so these are only looked for once the game's up */
    [AT_FX_COUNT_CALL] = { .name = "unread count call", .module = IN_FFXI, .code = IS_CODE, .required = SKIPPABLE,
        .usual = 0x201579, .sig_at = 0x201576, .operand = ADDR_MATCH, .sig = SIG_FX_NETSTAT },
    [AT_FX_SOUND_ON] = { .name = "message sound setting", .module = IN_FFXI, .code = IS_CODE, .required = SKIPPABLE,
        .usual = 0x193860, .sig_at = 0x2010b0, .operand = 19, .rel = 1, .sig = SIG_FX_SETTER },
    [AT_FX_SOUND] = { .name = "message sound", .module = IN_FFXI, .code = IS_CODE, .required = SKIPPABLE,
        .usual = 0x38020, .sig_at = 0x2010b0, .operand = 31, .rel = 1, .sig = SIG_FX_SETTER },
    [AT_FX_MARK_CALL] = { .name = "friend mark call", .module = IN_FFXI, .code = IS_CODE, .required = SKIPPABLE,
        .usual = 0x1eb2ef, .sig_at = 0x1eb2ee, .operand = ADDR_MATCH,
        .sig = "55 e8 ?? ?? ?? ?? 83 c4 04 85 c0 74 22 8b 96 84 00 00 00 8b 4c 24 10 68 80 80 80 80 52 6a 0e 6a 00 e8" },
    /* the table's first entry is /ver */
    [AT_FX_LOOKUP_CALL] = { .name = "command lookup call", .module = IN_FFXI, .code = IS_CODE, .required = SKIPPABLE,
        .usual = 0x8102e, .sig_at = 0x81016, .operand = ADDR_MATCH, .sig = SIG_FX_LOOKUP,
        .shows_op = 20, .shows = "2f 76 65 72 00" },
    [AT_FX_EXPANDED] = { .name = "expanded line", .module = IN_FFXI, .code = IS_DATA, .required = SKIPPABLE,
        .usual = 0x354df0, .sig_at = 0x81016, .operand = 61, .sig = SIG_FX_LOOKUP },
    [AT_FX_ADD_LINE] = { .name = "chat add-line", .module = IN_FFXI, .code = IS_CODE, .required = SKIPPABLE,
        .usual = 0x127c10, .sig_at = 0x200e4f, .operand = 56, .rel = 1, .sig = SIG_FX_FROM_LINE },
    [AT_FX_CHAT_LOG] = { .name = "chat log", .module = IN_FFXI, .code = IS_DATA, .required = SKIPPABLE,
        .usual = 0x578198, .sig_at = 0x200e4f, .operand = 32, .sig = SIG_FX_FROM_LINE },
    [AT_FX_WORD_FILTER] = { .name = "word filter", .module = IN_FFXI, .code = IS_CODE, .required = SKIPPABLE,
        .usual = 0x1ba210, .sig_at = 0x200ebf, .operand = 17, .rel = 1,
        .sig = "68 ?? ?? ?? ?? 52 e8 ?? ?? ?? ?? 8d 44 24 20 50 e8 ?? ?? ?? ?? 83 c4 10" },
    [AT_FX_AT_OBJECT] = { .name = "auto-translate object", .module = IN_FFXI, .code = IS_DATA, .required = SKIPPABLE,
        .usual = 0x630260, .sig_at = 0x127cef, .operand = 19, .sig = SIG_FX_EXPAND },
    [AT_FX_EXPANDER] = { .name = "auto-translate expander", .module = IN_FFXI, .code = IS_CODE, .required = SKIPPABLE,
        .usual = 0x1b7270, .sig_at = 0x127cef, .operand = 35, .rel = 1, .sig = SIG_FX_EXPAND },
    /* the lobby's menus, only ever read */
    [AT_FX_MENU_FRONT] = { .name = "menu in front", .module = IN_FFXI, .code = IS_DATA, .required = SKIPPABLE,
        .usual = 0x5eedac, .sig_at = 0x15fdf2, .operand = 16, .sig = SIG_FX_MENU_FRONT },
    [AT_FX_LICENCE_MENU] = { .name = "licence menu", .module = IN_FFXI, .code = IS_DATA, .required = SKIPPABLE,
        .usual = 0x636520, .sig_at = 0x163b75, .operand = 11, .sig = SIG_FX_LICENCE },
    [AT_FX_CONFIRM_MENU] = { .name = "confirm box", .module = IN_FFXI, .code = IS_DATA, .required = SKIPPABLE,
        .usual = 0x636524, .sig_at = 0x163b75, .operand = 0x47, .sig = SIG_FX_LICENCE },
    [AT_FX_LOBBY_MENU] = { .name = "lobby menu", .module = IN_FFXI, .code = IS_DATA, .required = SKIPPABLE,
        .usual = 0x636570, .sig_at = 0x23dc0d, .operand = 5, .sig = SIG_FX_LOBBY },
    [AT_FX_CHAR_LIST_MENU] = { .name = "character list menu", .module = IN_FFXI, .code = IS_DATA, .required = SKIPPABLE,
        .usual = 0x6365a0, .sig_at = 0x23dc0d, .operand = 17, .sig = SIG_FX_LOBBY },
    [AT_FX_CHAR_INDEX] = { .name = "selected character", .module = IN_FFXI, .code = IS_DATA, .required = SKIPPABLE,
        .usual = 0x6365ac, .sig_at = 0x243e8d, .operand = 1, .sig = SIG_FX_CHAR_INDEX },
};

typedef struct {
    BYTE *image;
    DWORD opbase, text, text_end, size;
    IMAGE_SECTION_HEADER *sections;
    WORD section_count;
} addr_module;

static int addr_module_open(addr_module *m, BYTE *image, DWORD opbase)
{
    IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)image;
    IMAGE_NT_HEADERS32 *nt;
    IMAGE_SECTION_HEADER *s;
    DWORD table_end;
    WORD i;
    m->image = image;
    m->opbase = opbase;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0 || dos->e_lfanew > 0x400) return 0;
    nt = (IMAGE_NT_HEADERS32 *)(image + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR32_MAGIC ||
        nt->FileHeader.SizeOfOptionalHeader < sizeof nt->OptionalHeader || nt->FileHeader.NumberOfSections > 32) return 0;
    m->size = nt->OptionalHeader.SizeOfImage;
    s = IMAGE_FIRST_SECTION(nt);
    table_end = (DWORD)((BYTE *)(s + nt->FileHeader.NumberOfSections) - image);
    if (table_end > nt->OptionalHeader.SizeOfHeaders || nt->OptionalHeader.SizeOfHeaders > m->size) return 0;
    m->sections = s;
    m->section_count = nt->FileHeader.NumberOfSections;
    for (i = 0; i < m->section_count; i++, s++)
        if (!memcmp(s->Name, ".text", 6) && (s->Characteristics & IMAGE_SCN_MEM_EXECUTE)) {
            m->text = s->VirtualAddress;
            m->text_end = s->VirtualAddress + s->Misc.VirtualSize;
            return m->text >= 0x1000 && m->text_end > m->text && m->text_end <= m->size;
        }
    return 0;
}

static DWORD addr_sig_len(const char *sig) { return (DWORD)(lstrlenA(sig) + 1) / 3; }
static BYTE addr_hex(char c) { return (BYTE)(c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10); }
static int addr_sig_byte(const char *sig, DWORD k, BYTE *want)
{
    const char *c = sig + k * 3;
    *want = *c == '?' ? 0 : (BYTE)(addr_hex(c[0]) << 4 | addr_hex(c[1]));
    return *c != '?';
}
static void addr_sig_bytes(const char *sig, BYTE *want, BYTE *fixed)
{
    DWORD k, n = addr_sig_len(sig);
    for (k = 0; k < n; k++) fixed[k] = (BYTE)addr_sig_byte(sig, k, &want[k]);
}
static int addr_sig_at(const BYTE *p, const char *sig)
{
    DWORD k, n = addr_sig_len(sig);
    BYTE want;
    for (k = 0; k < n; k++)
        if (addr_sig_byte(sig, k, &want) && p[k] != want) return 0;
    return 1;
}
static int addr_sig_fits(const addr_module *m, DWORD off, const char *sig)
{
    DWORD n = addr_sig_len(sig);
    return off >= m->text && off <= m->text_end && m->text_end - off >= n && addr_sig_at(m->image + off, sig);
}

#define ADDR_PAT_MAX 256
typedef struct {
    BYTE want[ADDR_PAT_MAX], fixed[ADDR_PAT_MAX];
    DWORD n, lead;
} addr_pat;
static int addr_pat_parse(addr_pat *p, const char *sig)
{
    DWORD k;
    p->n = addr_sig_len(sig);
    if (!p->n || p->n > ADDR_PAT_MAX) return 0;
    addr_sig_bytes(sig, p->want, p->fixed);
    for (k = 0; k < p->n && !p->fixed[k]; k++) {}
    p->lead = k;
    return k < p->n;
}
static int addr_pat_at(const addr_pat *p, const BYTE *img)
{
    DWORD k;
    for (k = 0; k < p->n; k++)
        if (p->fixed[k] && img[k] != p->want[k]) return 0;
    return 1;
}

static int addr_in_data(const addr_module *m, DWORD a)
{
    const IMAGE_SECTION_HEADER *s = m->sections;
    WORD i;
    for (i = 0; i < m->section_count; i++, s++) {
        DWORD end = s->VirtualAddress + s->Misc.VirtualSize;
        if (end < s->VirtualAddress || end > m->size) continue;
        if (a >= s->VirtualAddress && a < end)
            return end - a >= 4 && (s->Characteristics & (IMAGE_SCN_MEM_WRITE | IMAGE_SCN_MEM_EXECUTE)) == IMAGE_SCN_MEM_WRITE;
    }
    return 0;
}

static int addr_extras(const struct hook_addr *e, const addr_module *m, DWORD off)
{
    DWORD p;
    if (e->shows) {
        p = *(DWORD *)(m->image + off + e->shows_op) - m->opbase;
        if (p >= m->size || m->size - p < addr_sig_len(e->shows) || !addr_sig_at(m->image + p, e->shows)) return 0;
    }
    if (e->calls) {
        p = off + e->calls_op + 4 + *(DWORD *)(m->image + off + e->calls_op);
        if (!addr_sig_fits(m, p, e->calls)) return 0;
    }
    return 1;
}
static int addr_matches(const struct hook_addr *e, const addr_module *m, DWORD off)
{
    return addr_sig_fits(m, off, e->sig) && addr_extras(e, m, off);
}

static DWORD addr_from(const struct hook_addr *e, const addr_module *m, DWORD off)
{
    DWORD a = e->operand == ADDR_MATCH ? off + (e->usual - e->sig_at)
            : e->rel ? off + e->operand + 4 + *(DWORD *)(m->image + off + e->operand)
                     : *(DWORD *)(m->image + off + e->operand) - m->opbase;
    if (e->code) return a >= m->text && a < m->text_end ? a : 0;
    return addr_in_data(m, a) ? a : 0;
}

static int addr_scan(const struct hook_addr *e, const addr_module *m, DWORD *first)
{
    addr_pat pat;
    int hits = 0, quick = addr_pat_parse(&pat, e->sig);
    DWORD n = quick ? pat.n : addr_sig_len(e->sig), off, last;
    if (m->text_end - m->text < n) return 0;
    last = m->text_end - n;
    for (off = m->text; ; off++) {
        const BYTE *at = m->image + off;
        if (quick ? at[pat.lead] == pat.want[pat.lead] && addr_pat_at(&pat, at) && addr_extras(e, m, off)
                  : addr_matches(e, m, off)) {
            if (!hits) *first = off;
            hits++;
        }
        if (off >= last || hits >= 2) break;
    }
    return hits;
}

static void addr_find(struct hook_addr *e, const addr_module *m)
{
    DWORD off = e->sig_at;
    if (addr_matches(e, m, off)) e->hits = 1;
    else e->hits = addr_scan(e, m, &off);
    e->at = e->hits == 1 ? addr_from(e, m, off) : 0;
}

static const char *addr_why(const struct hook_addr *e)
{
    return e->hits > 1 ? "more than one match" : e->hits ? "its address makes no sense" : "no match";
}

static void addr_list(char *list, int cap, const char *item)
{
    int n = lstrlenA(list);
    if (n >= 3 && !lstrcmpA(list + n - 3, "...")) return;
    if (n + (n ? 2 : 0) + lstrlenA(item) + 6 > cap) { lstrcatA(list, n ? ", ..." : "..."); return; }
    if (n) lstrcatA(list, ", ");
    lstrcatA(list, item);
}

static int addr_resolve(int module, const char *label, BYTE *image, DWORD opbase)
{
    addr_module m;
    char moved[560] = "", skipped[200] = "", item[96], more[80] = "";
    int i, usual = 0, nmoved = 0, nskipped = 0, missing = 0;
    int ok = image && addr_module_open(&m, image, opbase);
    if (!ok) base_log("addresses: %s's headers don't make sense, or it has no .text\r\n", label);
    for (i = 0; i < ADDRESS_COUNT; i++) {
        struct hook_addr *e = &g_addr[i];
        if (e->module != module) continue;
        e->hits = 0;
        e->at = 0;
        if (ok) addr_find(e, &m);
        if (e->at && e->at == e->usual) {
            usual++;
        } else if (e->at) {
            nmoved++;
            wsprintfA(item, "%s +%x -> +%x", e->name, (unsigned)e->usual, (unsigned)e->at);
            addr_list(moved, sizeof moved, item);
        } else if (!e->required) {
            nskipped++;
            addr_list(skipped, sizeof skipped, e->name);
        } else {
            missing++;
            base_log("addresses: %s %s not found (%s)\r\n", label, e->name, addr_why(e));
        }
    }
    if (missing) return 0;
    if (nskipped) wsprintfA(more, ", %d not found and not needed (", nskipped);
    base_log("addresses: %s %d at their usual place, %d moved%s%s%s%s%s%s\r\n", label, usual, nmoved,
        nmoved ? " (" : "", moved, nmoved ? ")" : "", more, skipped, nskipped ? ")" : "");
    return 1;
}

static void addr_require(int module, const char *label, BYTE *image, DWORD opbase)
{
    if (addr_resolve(module, label, image, opbase)) return;
    base_log("addresses: %s isn't a build the hook can use - nothing touched, ending pol.exe (exit %d)\r\n", label,
        ADDRESSES_EXIT);
    base_end_pol(ADDRESSES_EXIT);
}
