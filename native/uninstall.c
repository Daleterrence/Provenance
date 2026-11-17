/* the Add/Remove Programs uninstaller for the POL and FFXI installs Provenance applies itself (no Windows Installer).
   Built twice, once per product with FFXI_UNINSTALL for the game's, so the UAC prompt names the right one.
   Run as <this exe> "<install>\Uninstall\{ProductCode}.txt" [/quiet] [/all]. /quiet asks nothing and keeps the
   player's settings (the product folder's USER or usr, and the K key), /all removes them too.
   The .txt is UTF-16LE, one item per line:
     N <display name>
     A <Uninstall key>                  HKLM, 32-bit view
     S <shortcut file>
     C <key>                            HKLM, 32-bit view, removed with its subkeys (COM classes, ProgIDs)
     K <key>                            the same, for the product's own settings. Only under the L key, and kept
                                        (V lines in it too) unless they're being removed
     T <LIBID> <major> <minor> <lcid>   a type library registration
     V <M|U> <key>\t<value name>        M = HKLM, U = HKCU, 32-bit view. "+" for a key the package made. Keys left
                                        empty go, up to the first one that isn't
     F <file>
     P <folder>\t<pattern>              the package's RemoveFile rules, files only
     I <folder>                         the install root, the R folder's grandparent
     R <folder>                         removed with everything in it, whatever the game or its updates added. A link
                                        in there goes as a link, its target stays
     D <folder>                         removed when empty, deepest first
     L <key>                            one of the three region keys, HKLM, 32-bit view. Removed with its subkeys by the
                                        last product out, bar any settings that were kept
   The .txt can sit somewhere anyone can write, so I, R, K and L only count when this product's InstallFolder under
   the L key (HKLM, admins only) names the R folder exactly. Nothing runs unless only Administrators and SYSTEM own
   or can change the .txt and its folder */
#define UNICODE
#define _UNICODE
#include <windows.h>
#include <winternl.h>
#include <aclapi.h>
#include <oleauto.h>
#include <tlhelp32.h>
#include <wchar.h>
#include <wctype.h>
#include <stdlib.h>
#include "pol_regions.h"

#ifdef FFXI_UNINSTALL
#define TITLE L"Provenance - FINAL FANTASY XI Uninstall"
#define PRODUCT_NAME L"FINAL FANTASY XI"
#define PRODUCT L"0001"
#define FOLDER L"FINAL FANTASY XI folder"
#define KEEP_DIR L"USER"
#define ASK L"Remove %s from this PC?\n\nThis deletes the FINAL FANTASY XI folder and everything game updates added to it. Next you can choose whether to keep your characters' settings and macros, and the game's settings.\n\nFiles in Documents\\My Games\\FINAL FANTASY XI, like screenshots, are kept either way."
#define KEEP_ASK L"Keep your characters' settings and macros, and the game's settings, in case you install FINAL FANTASY XI again?\n\nYes keeps them (in the game folder's USER folder, and in Windows). No deletes them with the rest, and that can't be undone."
#define KEPT_FOLDER L" Your characters' settings and macros are still in %s."
#define KEPT_KEY L" The game's settings are still saved in Windows."
#define OTHER_KEY L"PlayOnlineViewer"
#define OTHER_DIR L"PlayOnlineViewer\\usr"
#define OTHER_KEPT L" The PlayOnline logins and settings you kept when you removed PlayOnline are still in %s, and in Windows."
#else
#define TITLE L"Provenance - PlayOnline Uninstall"
#define PRODUCT_NAME L"PlayOnline Viewer"
#define PRODUCT L"1000"
#define FOLDER L"PlayOnline Viewer folder"
#define KEEP_DIR L"usr"
#define ASK L"Remove %s from this PC?\n\nThis deletes the PlayOnline Viewer folder. Next you can choose whether to keep your saved logins and PlayOnline's settings."
#define KEEP_ASK L"Keep your saved logins and PlayOnline's settings?\n\nYes keeps them (in the PlayOnline Viewer folder's usr folder, and in Windows). No deletes them with the rest, and that can't be undone."
#define KEPT_FOLDER L" Your saved logins and settings are still in %s."
#define KEPT_KEY L" PlayOnline's settings are still saved in Windows."
#define OTHER_KEY L"FinalFantasyXI"
#define OTHER_DIR L"FINAL FANTASY XI\\USER"
#define OTHER_KEPT L" The FINAL FANTASY XI settings and macros you kept when you removed the game are still in %s, and in Windows."
#endif

#define RUNNING L"PlayOnline or FINAL FANTASY XI is running, or Provenance has it open in the background. Exit Provenance and the game, then press Retry."
#define UNPROTECTED_TAIL L" isn't protected, so to be safe nothing was removed.\n\nOpen Provenance and press Repair game files, which saves it again where only administrators can change it. Then try again."
#define UNPROTECTED L"The uninstall information for %s" UNPROTECTED_TAIL
#define UNPROTECTED_NO_NAME L"The uninstall information" UNPROTECTED_TAIL

#define NOT_FOUND(st) ((st) == (NTSTATUS)0xC0000034L || (st) == (NTSTATUS)0xC000003AL)

/* both products share the Uninstall folder, so the last one out takes both programs with it */
static const wchar_t *const g_programs[] = { L"Provenance - PlayOnline Uninstall.exe",
                                             L"Provenance - FINAL FANTASY XI Uninstall.exe" };

static const wchar_t *const g_regions[] = { L"SOFTWARE\\" WIDE(POL_HIVE_JP), L"SOFTWARE\\" WIDE(POL_HIVE_US),
                                            L"SOFTWARE\\" WIDE(POL_HIVE_EU) };

static HWND g_label;
static const wchar_t *g_product = PRODUCT, *g_keep_dir = KEEP_DIR, *g_other_key = OTHER_KEY, *g_other_dir = OTHER_DIR;
static wchar_t g_other[MAX_PATH * 2];   /* the other product's kept folder, when this one left it */
static int g_keep = 1, g_depth, g_skipped;
static wchar_t g_settings[600];   /* the K line's key, which keeping leaves alone */
static wchar_t g_region[64], g_folder[MAX_PATH * 2];   /* only set once HKLM agrees with the record */
static int g_unconfirmed, g_unprotected;
static wchar_t g_name[256] = PRODUCT_NAME;
static wchar_t g_root[MAX_PATH * 2];
static wchar_t g_tree[MAX_PATH * 2], g_kept[MAX_PATH * 2];
static wchar_t g_path[32768];   /* what remove_entry's on, for the restart list */
static int g_count, g_later, g_pending;

static void status(const wchar_t *text)
{
    MSG m;
    if (!g_label) return;
    SetWindowTextW(g_label, text);
    while (PeekMessageW(&m, NULL, 0, 0, PM_REMOVE)) { TranslateMessage(&m); DispatchMessageW(&m); }
}

static void counted(void)
{
    wchar_t msg[300];
    if (++g_count % 500) return;
    _snwprintf_s(msg, _countof(msg), _TRUNCATE, L"Removing %s: %d files", g_name, g_count);
    status(msg);
}

static int shared_root(const wchar_t *key)
{
    return !wcschr(key, L'\\') || !_wcsicmp(key, L"SOFTWARE\\Classes") || !_wcsicmp(key, L"Software\\Microsoft") ||
           !_wcsicmp(key, L"SOFTWARE\\Classes\\CLSID");
}

static void prune(HKEY root, wchar_t *key)
{
    for (;;) {
        HKEY k;
        DWORD subkeys = 0, values = 0;
        wchar_t *slash;
        if (RegOpenKeyExW(root, key, 0, KEY_READ | KEY_WOW64_32KEY, &k) != ERROR_SUCCESS) return;
        RegQueryInfoKeyW(k, NULL, NULL, NULL, &subkeys, NULL, NULL, &values, NULL, NULL, NULL, NULL);
        RegCloseKey(k);
        if (subkeys || values) return;
        if (RegDeleteKeyExW(root, key, KEY_WOW64_32KEY, 0) != ERROR_SUCCESS) return;
        slash = wcsrchr(key, L'\\');
        if (!slash) return;
        *slash = 0;
        /* never climb into the shared roots */
        if (shared_root(key)) return;
    }
}

static void delete_key(const wchar_t *key)
{
    HKEY k;
    size_t n = wcslen(key);
    /* an empty or cut-short key would open the one above it */
    if (!n || key[n - 1] == L'\\' || shared_root(key)) return;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, key, 0, KEY_ALL_ACCESS | KEY_WOW64_32KEY, &k) != ERROR_SUCCESS) return;
    RegDeleteTreeW(k, NULL);
    RegCloseKey(k);
    RegDeleteKeyExW(HKEY_LOCAL_MACHINE, key, KEY_WOW64_32KEY, 0);
}

static void delete_file(const wchar_t *path)
{
    DWORD a = GetFileAttributesW(path);
    if (a == INVALID_FILE_ATTRIBUTES || (a & FILE_ATTRIBUTE_DIRECTORY)) return;
    if (a & FILE_ATTRIBUTE_READONLY) SetFileAttributesW(path, a & ~FILE_ATTRIBUTE_READONLY);
    DeleteFileW(path);
}

/* X:\ and at least one folder, nothing relative, no network paths or streams */
static int local_path(const wchar_t *p)
{
    return iswalpha(p[0]) && p[1] == L':' && p[2] == L'\\' && p[3] && p[3] != L'\\' && !wcschr(p + 2, L':');
}

static void kept(void)
{
    if (!g_kept[0]) lstrcpynW(g_kept, g_tree, _countof(g_kept));
}

/* relative to a folder already open, so nothing on the way can be swapped for a link, and the entry itself is opened
   rather than anything it points at */
static HANDLE open_at(HANDLE dir, const wchar_t *name, size_t len, ACCESS_MASK access, NTSTATUS *st)
{
    UNICODE_STRING us;
    OBJECT_ATTRIBUTES oa;
    IO_STATUS_BLOCK io;
    HANDLE h;
    us.Buffer = (PWSTR)name;
    us.Length = us.MaximumLength = (USHORT)(len * sizeof(wchar_t));
    InitializeObjectAttributes(&oa, &us, OBJ_CASE_INSENSITIVE, dir, NULL);
    *st = NtCreateFile(&h, access | SYNCHRONIZE, &oa, &io, NULL, 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                       FILE_OPEN, FILE_OPEN_REPARSE_POINT | FILE_OPEN_FOR_BACKUP_INTENT | FILE_SYNCHRONOUS_IO_NONALERT, NULL, 0);
    return NT_SUCCESS(*st) ? h : INVALID_HANDLE_VALUE;
}

static DWORD attrs(HANDLE h)
{
    FILE_ATTRIBUTE_TAG_INFO t;
    return GetFileInformationByHandleEx(h, FileAttributeTagInfo, &t, sizeof t) ? t.FileAttributes : INVALID_FILE_ATTRIBUTES;
}

static int plain_folder(HANDLE h)
{
    DWORD a = attrs(h);
    return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY) && !(a & FILE_ATTRIBUTE_REPARSE_POINT);
}

static int delete_open(HANDLE h)
{
    FILE_DISPOSITION_INFO_EX ex = { FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS |
                                    FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE };
    FILE_DISPOSITION_INFO yes = { TRUE };
    BY_HANDLE_FILE_INFORMATION bi;
    if (SetFileInformationByHandle(h, FileDispositionInfoEx, &ex, sizeof ex)) return 1;
    /* older Windows: read-only cleared first, but not on a file with other hard links, that'd change theirs too */
    if (GetFileInformationByHandle(h, &bi) && (bi.dwFileAttributes & FILE_ATTRIBUTE_READONLY) && bi.nNumberOfLinks == 1) {
        FILE_BASIC_INFO basic = { 0 };
        basic.FileAttributes = bi.dwFileAttributes & ~FILE_ATTRIBUTE_READONLY;
        if (!basic.FileAttributes) basic.FileAttributes = FILE_ATTRIBUTE_NORMAL;
        SetFileInformationByHandle(h, FileBasicInfo, &basic, sizeof basic);
    }
    return SetFileInformationByHandle(h, FileDispositionInfo, &yes, sizeof yes);
}

/* in use, so it goes at the next restart, renamed aside first so a reinstall doesn't lose its fresh copy.
   Into dir's real path, a handle-relative rename is denied */
static void later(HANDLE dir, HANDLE h)
{
    struct { FILE_RENAME_INFO i; wchar_t more[MAX_PATH * 2 + 64]; } r;
    const wchar_t *path = g_path;
    DWORD n = 0;
    int len;
    ZeroMemory(&r, sizeof r);
    if (h != INVALID_HANDLE_VALUE)
        n = GetFinalPathNameByHandleW(dir, r.i.FileName, MAX_PATH * 2, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    if (n && n < MAX_PATH * 2 &&
        (len = _snwprintf_s(r.i.FileName + n, MAX_PATH * 2 + 64 - n, _TRUNCATE, L"\\uninstall-%lu-%d.old",
                            GetCurrentProcessId(), g_later++)) > 0) {
        r.i.FileNameLength = (n + len) * sizeof(wchar_t);
        if (SetFileInformationByHandle(h, FileRenameInfo, &r, sizeof r)) path = r.i.FileName;
    }
    if (MoveFileExW(path, NULL, MOVEFILE_DELAY_UNTIL_REBOOT)) g_pending = 1;
    else kept();
}

static void remove_entry(HANDLE dir, const wchar_t *name, size_t len, int folder);

typedef struct batch { LONGLONG data[8192]; struct batch *next; } batch;

static void empty_folder(HANDLE h)
{
    batch *first = NULL, **tail = &first, *b;
    FILE_INFO_BY_HANDLE_CLASS kind = FileFullDirectoryRestartInfo;
    /* the whole listing first, something renamed for the restart mustn't turn up in it again */
    for (;;) {
        if (!(b = (batch *)malloc(sizeof *b))) { kept(); break; }
        b->next = NULL;
        if (!GetFileInformationByHandleEx(h, kind, b->data, sizeof b->data)) { free(b); break; }
        *tail = b;
        tail = &b->next;
        kind = FileFullDirectoryInfo;
    }
    while ((b = first) != NULL) {
        FILE_FULL_DIR_INFO *e = (FILE_FULL_DIR_INFO *)b->data;
        for (;;) {
            size_t len = e->FileNameLength / sizeof(wchar_t);
            if (g_depth == 1 && g_keep && len == wcslen(g_keep_dir) && !_wcsnicmp(e->FileName, g_keep_dir, len)) g_skipped = 1;
            else if (!(len == 1 && e->FileName[0] == L'.') && !(len == 2 && e->FileName[0] == L'.' && e->FileName[1] == L'.'))
                remove_entry(h, e->FileName, len,
                             (e->FileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) == FILE_ATTRIBUTE_DIRECTORY);
            if (!e->NextEntryOffset) break;
            e = (FILE_FULL_DIR_INFO *)((BYTE *)e + e->NextEntryOffset);
        }
        first = b->next;
        free(b);
    }
}

static void remove_entry(HANDLE dir, const wchar_t *name, size_t len, int folder)
{
    size_t at = wcslen(g_path) + 1;
    NTSTATUS st;
    HANDLE h;
    if (at + len >= _countof(g_path)) { kept(); return; }
    g_path[at - 1] = L'\\';
    wmemcpy(g_path + at, name, len);
    g_path[at + len] = 0;
    h = open_at(dir, name, len, DELETE | FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES | (folder ? FILE_LIST_DIRECTORY : 0), &st);
    if (h == INVALID_HANDLE_VALUE && folder && !NOT_FOUND(st))   /* it can't go yet, what's in it still can */
        h = open_at(dir, name, len, FILE_READ_ATTRIBUTES | FILE_LIST_DIRECTORY, &st);
    if (h == INVALID_HANDLE_VALUE) {
        if (!NOT_FOUND(st)) later(dir, h);
    } else {
        int tree = folder && plain_folder(h), top = g_depth == 0;
        if (tree) {
            g_depth++;
            empty_folder(h);
            g_depth--;
        } else counted();
        /* a folder keeps its name, what's still in it is already down for the restart by its path. The product's
           folder just stays when its settings did */
        if (!(top && g_skipped) && !delete_open(h)) later(dir, tree ? INVALID_HANDLE_VALUE : h);
        CloseHandle(h);
    }
    g_path[at - 1] = 0;
}

/* an R line. The root must be a real folder at the I line's path (no link or SUBST drive),
   and the target strictly inside it */
static void remove_tree(const wchar_t *target)
{
    wchar_t full[MAX_PATH * 2], real[MAX_PATH * 2 + 8], want[MAX_PATH * 2 + 8];
    const wchar_t *part, *slash;
    size_t root = wcslen(g_root);
    DWORD n;
    NTSTATUS st;
    HANDLE dir, h;
    lstrcpynW(g_tree, target, _countof(g_tree));
    g_depth = g_skipped = 0;
    n = GetFullPathNameW(target, _countof(full), full, NULL);
    if (!root || !local_path(target) || !n || n >= _countof(full)) { kept(); return; }
    while (n > 3 && full[n - 1] == L'\\') full[--n] = 0;
    if (_wcsnicmp(full, g_root, root) || full[root] != L'\\' || !full[root + 1]) { kept(); return; }
    if (!g_folder[0] || _wcsicmp(full, g_folder)) return;

    _snwprintf_s(g_path, _countof(g_path), _TRUNCATE, L"\\\\?\\%s", g_root);
    dir = CreateFileW(g_path, FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                      NULL, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, NULL);
    if (dir == INVALID_HANDLE_VALUE) {
        n = GetLastError();
        if (n != ERROR_FILE_NOT_FOUND && n != ERROR_PATH_NOT_FOUND) kept();
        return;
    }
    n = GetLongPathNameW(g_path, want, _countof(want));
    if (!plain_folder(dir) || !n || n >= _countof(want) ||
        GetFinalPathNameByHandleW(dir, real, _countof(real), FILE_NAME_NORMALIZED | VOLUME_NAME_DOS) - 1 >= _countof(real) - 1 ||
        _wcsicmp(real, want)) {
        CloseHandle(dir);
        kept();
        return;
    }
    for (part = full + root + 1; (slash = wcschr(part, L'\\')) != NULL; part = slash + 1) {
        h = open_at(dir, part, slash - part, FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES, &st);
        CloseHandle(dir);
        if (h == INVALID_HANDLE_VALUE) {
            if (!NOT_FOUND(st)) kept();
            return;
        }
        dir = h;
        if (!plain_folder(dir)) { CloseHandle(dir); kept(); return; }
        n = (DWORD)wcslen(g_path);
        _snwprintf_s(g_path + n, _countof(g_path) - n, _TRUNCATE, L"\\%.*s", (int)(slash - part), part);
    }
    remove_entry(dir, part, wcslen(part), 1);
    CloseHandle(dir);
}

static int keep_settings(HKEY k);

static void delete_values(HKEY k)
{
    wchar_t name[256];
    DWORD n;
    for (;;) {
        n = _countof(name);
        if (RegEnumValueW(k, 0, name, &n, NULL, NULL, NULL, NULL) != ERROR_SUCCESS || RegDeleteValueW(k, name) != ERROR_SUCCESS) break;
    }
}

static void clear_but(HKEY k, const wchar_t *only)
{
    wchar_t name[256];
    DWORD n, i;
    delete_values(k);
    for (i = 0;; ) {
        n = _countof(name);
        if (RegEnumKeyExW(k, i, name, &n, NULL, NULL, NULL, NULL) != ERROR_SUCCESS) break;
        if (!_wcsicmp(name, only) || RegDeleteTreeW(k, name) != ERROR_SUCCESS) i++;
    }
}

/* the other product's kept settings: record gone (I'm the last out), key and kept folder both still there.
   A link in place of the folder doesn't count */
static int other_kept(const wchar_t *region)
{
    wchar_t key[600], folder[MAX_PATH * 2];
    DWORD a;
    HKEY k;
    int i, found = 0;
    for (i = 0; i < 2 && !found; i++) {
        _snwprintf_s(key, _countof(key), _TRUNCATE, L"%s\\%s\\%s", region, i ? L"SQUARE" : L"SquareEnix", g_other_key);
        if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, key, 0, KEY_READ | KEY_WOW64_32KEY, &k) == ERROR_SUCCESS) {
            RegCloseKey(k);
            found = 1;
        }
    }
    if (!found || !g_root[0] || _snwprintf_s(folder, _countof(folder), _TRUNCATE, L"%s\\SquareEnix\\%s", g_root, g_other_dir) < 0) return 0;
    a = GetFileAttributesW(folder);
    if (a == INVALID_FILE_ATTRIBUTES || !(a & FILE_ATTRIBUTE_DIRECTORY) || (a & FILE_ATTRIBUTE_REPARSE_POINT)) return 0;
    lstrcpynW(g_other, folder, _countof(g_other));
    return 1;
}

/* what's in it bar the SquareEnix (or SQUARE) settings, or only the one named in them, then any key left empty in
   those */
static void clear_except_settings(HKEY k, const wchar_t *only)
{
    wchar_t name[256];
    DWORD n, i;
    delete_values(k);
    for (i = 0;; ) {
        HKEY sub;
        int left = 1;
        n = _countof(name);
        if (RegEnumKeyExW(k, i, name, &n, NULL, NULL, NULL, NULL) != ERROR_SUCCESS) break;
        if (_wcsicmp(name, L"SquareEnix") && _wcsicmp(name, L"SQUARE")) left = RegDeleteTreeW(k, name) != ERROR_SUCCESS;
        else if (RegOpenKeyExW(k, name, 0, KEY_ALL_ACCESS | KEY_WOW64_32KEY, &sub) == ERROR_SUCCESS) {
            if (only) clear_but(sub, only);
            left = keep_settings(sub) || RegDeleteKeyExW(k, name, KEY_WOW64_32KEY, 0) != ERROR_SUCCESS;
            RegCloseKey(sub);
        }
        if (left) i++;
    }
}

static int keep_settings(HKEY k)
{
    wchar_t name[256];
    DWORD n, i, subkeys = 0, values = 0;
    for (i = 0;; ) {
        HKEY sub;
        int left = 1;
        n = _countof(name);
        if (RegEnumKeyExW(k, i, name, &n, NULL, NULL, NULL, NULL) != ERROR_SUCCESS) break;
        if (RegOpenKeyExW(k, name, 0, KEY_ALL_ACCESS | KEY_WOW64_32KEY, &sub) == ERROR_SUCCESS) {
            left = keep_settings(sub);
            RegCloseKey(sub);
        }
        if (left || RegDeleteKeyExW(k, name, KEY_WOW64_32KEY, 0) != ERROR_SUCCESS) i++;
    }
    RegQueryInfoKeyW(k, NULL, NULL, NULL, &subkeys, NULL, NULL, &values, NULL, NULL, NULL, NULL);
    return subkeys || values;
}

/* the region's whole key, unless another product's still installed in it */
static void remove_region(const wchar_t *key)
{
    wchar_t sub[600];
    DWORD values = 0;
    HKEY k;
    LSTATUS r;
    if (shared_root(key) || _snwprintf_s(sub, _countof(sub), _TRUNCATE, L"%s\\InstallFolder", key) < 0) return;
    r = RegOpenKeyExW(HKEY_LOCAL_MACHINE, sub, 0, KEY_READ | KEY_WOW64_32KEY, &k);
    if (r == ERROR_SUCCESS) {
        if (RegQueryInfoKeyW(k, NULL, NULL, NULL, NULL, NULL, NULL, &values, NULL, NULL, NULL, NULL) != ERROR_SUCCESS) values = 1;
        RegCloseKey(k);
    } else if (r != ERROR_FILE_NOT_FOUND) return;
    if (values) return;
    if (!g_keep && !other_kept(key)) delete_key(key);
    else if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, key, 0, KEY_ALL_ACCESS | KEY_WOW64_32KEY, &k) == ERROR_SUCCESS) {
        clear_except_settings(k, g_keep ? NULL : g_other_key);
        RegCloseKey(k);
        prune(HKEY_LOCAL_MACHINE, lstrcpynW(sub, key, _countof(sub)));
    }
}

static int full_path(const wchar_t *in, wchar_t *out, DWORD size)
{
    DWORD n = local_path(in) ? GetFullPathNameW(in, size, out, NULL) : 0;
    if (!n || n >= size) return 0;
    while (n > 3 && out[n - 1] == L'\\') out[--n] = 0;
    return local_path(out);
}

typedef struct { wchar_t *p, *end; } cursor;

/* skips the byte-order mark */
static cursor lines_of(wchar_t *text, DWORD chars)
{
    cursor c = { text + (chars > 0 && text[0] == 0xFEFF), text + chars };
    return c;
}

static int next_line(cursor *c, wchar_t *kind, wchar_t **arg, wchar_t **eol)
{
    while (c->p < c->end) {
        wchar_t *p = c->p, *e = p;
        while (e < c->end && *e != L'\n' && *e != L'\r') e++;
        c->p = e;
        while (c->p < c->end && (*c->p == L'\n' || *c->p == L'\r')) c->p++;
        if (e - p >= 2 && p[1] == L' ') {
            *kind = p[0];
            *arg = p + 2;
            *eol = e;
            return 1;
        }
    }
    return 0;
}

static int first(wchar_t *text, DWORD chars, wchar_t kind, wchar_t *out, size_t size)
{
    cursor c = lines_of(text, chars);
    wchar_t k, *arg, *eol;
    while (next_line(&c, &k, &arg, &eol)) {
        size_t n = eol - arg;
        if (k != kind) continue;
        if (n >= size) return 0;
        wmemcpy(out, arg, n);
        out[n] = 0;
        return 1;
    }
    return 0;
}

static int admin_sid(PSID sid)
{
    return sid && (IsWellKnownSid(sid, WinBuiltinAdministratorsSid) || IsWellKnownSid(sid, WinLocalSystemSid));
}

/* owned by someone else than Administrators or SYSTEM, a link, or someone else can write, delete or re-permission it */
static int open_to_others(const wchar_t *path, DWORD flags)
{
    const DWORD writes = FILE_WRITE_DATA | FILE_APPEND_DATA | FILE_WRITE_EA | FILE_DELETE_CHILD | FILE_WRITE_ATTRIBUTES | DELETE |
                         WRITE_DAC | WRITE_OWNER | GENERIC_WRITE | GENERIC_ALL;
    HANDLE h = CreateFileW(path, READ_CONTROL | FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL,
                           OPEN_EXISTING, flags | FILE_FLAG_OPEN_REPARSE_POINT, NULL);
    PSECURITY_DESCRIPTOR sd = NULL;
    PSID owner = NULL;
    PACL dacl = NULL;
    DWORD a, i;
    int weak = 1;
    if (h == INVALID_HANDLE_VALUE) return 1;
    a = attrs(h);
    if (a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_REPARSE_POINT) &&
        GetSecurityInfo(h, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION, &owner, NULL, &dacl, NULL, &sd) == ERROR_SUCCESS &&
        dacl) {   /* no DACL at all lets anyone in */
        weak = !admin_sid(owner);
        for (i = 0; !weak && i < dacl->AceCount; i++) {
            ACE_HEADER *ace;
            if (!GetAce(dacl, i, (void **)&ace)) weak = 1;
            else if (ace->AceFlags & INHERIT_ONLY_ACE) continue;
            else if (ace->AceType == ACCESS_DENIED_ACE_TYPE || ace->AceType == ACCESS_DENIED_OBJECT_ACE_TYPE ||
                     ace->AceType == ACCESS_DENIED_CALLBACK_ACE_TYPE || ace->AceType == ACCESS_DENIED_CALLBACK_OBJECT_ACE_TYPE) continue;
            else if (ace->AceType != ACCESS_ALLOWED_ACE_TYPE) weak = 1;   /* an allow with conditions, can't tell who it lets in */
            else if ((((ACCESS_ALLOWED_ACE *)ace)->Mask & writes) && !admin_sid(&((ACCESS_ALLOWED_ACE *)ace)->SidStart)) weak = 1;
        }
    }
    LocalFree(sd);
    CloseHandle(h);
    return weak;
}

static int unprotected(const wchar_t *record)
{
    wchar_t folder[MAX_PATH * 2], *name;
    DWORD n = GetFullPathNameW(record, _countof(folder), folder, &name);
    if (!n || n >= _countof(folder) || !name || name == folder) return 1;
    if (open_to_others(record, 0)) return 1;
    name[-1] = 0;
    return open_to_others(folder, FILE_FLAG_BACKUP_SEMANTICS);
}

/* runs before anything's removed, the V lines take the InstallFolder value this checks against */
static void confirm(wchar_t *text, DWORD chars)
{
    wchar_t region[64], line[MAX_PATH * 2], tree[MAX_PATH * 2], value[MAX_PATH * 2], folder[MAX_PATH * 2], *slash;
    DWORD size = sizeof value;
    HKEY k;
    int i, ok = 0;
    g_region[0] = g_folder[0] = 0;
    g_unconfirmed = 0;
    if (!first(text, chars, L'K', g_settings, _countof(g_settings))) g_settings[0] = 0;
    if (!first(text, chars, L'R', line, _countof(line)) && !first(text, chars, L'K', line, _countof(line)) &&
        !first(text, chars, L'L', line, _countof(line))) return;
    if (first(text, chars, L'L', region, _countof(region)))
        for (i = 0; i < (int)_countof(g_regions); i++) ok |=!_wcsicmp(region, g_regions[i]);
    if (ok && _snwprintf_s(line, _countof(line), _TRUNCATE, L"%s\\InstallFolder", region) > 0 &&
        RegOpenKeyExW(HKEY_LOCAL_MACHINE, line, 0, KEY_QUERY_VALUE | KEY_WOW64_32KEY, &k) == ERROR_SUCCESS) {
        ok = RegGetValueW(k, NULL, g_product, RRF_RT_REG_SZ, NULL, value, &size) == ERROR_SUCCESS;
        RegCloseKey(k);
    } else ok = 0;
    ok = ok && full_path(value, folder, _countof(folder)) && first(text, chars, L'R', line, _countof(line)) &&
         full_path(line, tree, _countof(tree)) && !_wcsicmp(tree, folder);
    /* <root>\SquareEnix\<product> */
    for (i = 0; ok && i < 2; i++) ok = (slash = wcsrchr(tree, L'\\')) != NULL && (*slash = 0, local_path(tree));
    if (ok && g_root[0] && !_wcsicmp(tree, g_root)) {
        lstrcpynW(g_region, region, _countof(g_region));
        lstrcpynW(g_folder, folder, _countof(g_folder));
        return;
    }
    g_unconfirmed = 1;
    g_root[0] = 0;
}

static int running(const wchar_t *exe)
{
    PROCESSENTRY32W pe;
    int found = 0;
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return 0;
    pe.dwSize = sizeof pe;
    if (Process32FirstW(snap, &pe))
        do { if (!_wcsicmp(pe.szExeFile, exe)) found = 1; } while (!found && Process32NextW(snap, &pe));
    CloseHandle(snap);
    return found;
}

static wchar_t *load(const wchar_t *path, DWORD *chars)
{
    HANDLE f = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    DWORD size, got;
    wchar_t *buf;
    if (f == INVALID_HANDLE_VALUE) return NULL;
    size = GetFileSize(f, NULL);
    buf = (wchar_t *)calloc(size / 2 + 2, sizeof(wchar_t));
    if (!buf || !ReadFile(f, buf, size, &got, NULL)) { CloseHandle(f); free(buf); return NULL; }
    CloseHandle(f);
    *chars = got / 2;
    buf[*chars] = 0;
    return buf;
}

static void run(wchar_t *text, DWORD chars, wchar_t kind)
{
    cursor c = lines_of(text, chars);
    wchar_t got, *arg, *eol, *tab, save;
    while (next_line(&c, &got, &arg, &eol)) {
        if (got != kind) continue;
        save = *eol;
        *eol = 0;
        switch (kind) {
        case L'S':
            delete_file(arg);
            break;
        case L'F':
            delete_file(arg);
            counted();
            break;
        case L'A': case L'C':
            delete_key(arg);
            break;
        case L'K': {
            size_t n = wcslen(g_region);
            if (!g_keep && n && !_wcsnicmp(arg, g_region, n) && arg[n] == L'\\' && !shared_root(arg)) delete_key(arg);
            break;
        }
        case L'L':
            if (g_region[0] && !_wcsicmp(arg, g_region)) remove_region(arg);
            break;
        case L'T': {
            wchar_t libid[64];
            int major = 0, minor = 0, lcid = 0;
            GUID g;
            if (swscanf_s(arg, L"%63s %d %d %d", libid, (unsigned)_countof(libid), &major, &minor, &lcid) == 4 &&
                SUCCEEDED(CLSIDFromString(libid, &g)))
                UnRegisterTypeLib(&g, (WORD)major, (WORD)minor, (LCID)lcid, SYS_WIN32);
            break;
        }
        case L'V':
            if (arg[0] && arg[1] == L' ' && (tab = wcschr(arg + 2, L'\t')) != NULL) {
                HKEY root = arg[0] == L'U' ? HKEY_CURRENT_USER : HKEY_LOCAL_MACHINE, k;
                size_t n = wcslen(g_settings);
                *tab = 0;
                if (g_keep && n && root == HKEY_LOCAL_MACHINE && !_wcsnicmp(arg + 2, g_settings, n) && (!arg[2 + n] || arg[2 + n] == L'\\')) {
                    *tab = L'\t';
                    break;
                }
                if (wcscmp(tab + 1, L"+") != 0 &&
                    RegOpenKeyExW(root, arg + 2, 0, KEY_SET_VALUE | KEY_WOW64_32KEY, &k) == ERROR_SUCCESS) {
                    RegDeleteValueW(k, tab[1] ? tab + 1 : NULL);
                    RegCloseKey(k);
                }
                prune(root, arg + 2);
            }
            break;
        case L'P':
            if ((tab = wcschr(arg, L'\t')) != NULL) {
                wchar_t pattern[MAX_PATH * 2], file[MAX_PATH * 2];
                WIN32_FIND_DATAW fd = { 0 };
                HANDLE h;
                *tab = 0;
                /* a line too long for these is skipped, a cut-off path could be some other file */
                h = _snwprintf_s(pattern, _countof(pattern), _TRUNCATE, L"%s\\%s", arg, tab + 1) < 0
                    ? INVALID_HANDLE_VALUE : FindFirstFileW(pattern, &fd);
                if (h != INVALID_HANDLE_VALUE) {
                    do {
                        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
                        if (_snwprintf_s(file, _countof(file), _TRUNCATE, L"%s\\%s", arg, fd.cFileName) >= 0)
                            delete_file(file);
                    } while (FindNextFileW(h, &fd));
                    FindClose(h);
                }
            }
            break;
        case L'I':
            if (!full_path(arg, g_root, _countof(g_root))) g_root[0] = 0;
            break;
        case L'R':
            remove_tree(arg);
            break;
        case L'D':
            RemoveDirectoryW(arg);
            break;
        case L'N':
            lstrcpynW(g_name, arg, 256);
            break;
        }
        *eol = save;
    }
}

static void remove_all(wchar_t *text, DWORD chars, const wchar_t *record, const wchar_t *dir)
{
    wchar_t pattern[MAX_PATH + 8];
    WIN32_FIND_DATAW fd;
    HANDLE h;
    int i;
    if (unprotected(record)) {
        g_unprotected = 1;
        return;
    }
    run(text, chars, L'I');
    confirm(text, chars);
    status(L"Removing shortcuts and registry entries");
    run(text, chars, L'S');
    run(text, chars, L'C');
    run(text, chars, L'K');
    run(text, chars, L'T');
    run(text, chars, L'F');
    run(text, chars, L'P');
    run(text, chars, L'R');
    run(text, chars, L'V');
    run(text, chars, L'D');
    run(text, chars, L'A');
    DeleteFileW(record);
    /* the last product gone: the region's settings, both programs and their folder go */
    _snwprintf_s(pattern, _countof(pattern), _TRUNCATE, L"%s\\*.txt", dir);
    h = FindFirstFileW(pattern, &fd);
    if (h != INVALID_HANDLE_VALUE) { FindClose(h); return; }
    run(text, chars, L'L');
    for (i = 0; i < 2; i++) {
        wchar_t path[MAX_PATH + 64], old[MAX_PATH + 32];
        _snwprintf_s(path, _countof(path), _TRUNCATE, L"%s\\%s", dir, g_programs[i]);
        if (DeleteFileW(path) || GetFileAttributesW(path) == INVALID_FILE_ATTRIBUTES) continue;
        /* this one's running, so it goes at the next restart. Renamed out of the way first, or a reinstall
           before that restart gets its fresh copy deleted too (whoops, that one bit) */
        _snwprintf_s(old, _countof(old), _TRUNCATE, L"%s\\uninstall-%lu-%d.old", dir, GetCurrentProcessId(), i);
        MoveFileExW(MoveFileExW(path, old, MOVEFILE_REPLACE_EXISTING) ? old : path, NULL, MOVEFILE_DELAY_UNTIL_REBOOT);
    }
    MoveFileExW(dir, NULL, MOVEFILE_DELAY_UNTIL_REBOOT);
    /* only goes if it's empty by then */
    if (g_root[0]) MoveFileExW(g_root, NULL, MOVEFILE_DELAY_UNTIL_REBOOT);
}

static void options(int argc, wchar_t **argv, int *quiet)
{
    int i;
    *quiet = 0;
    g_keep = 1;
    for (i = 2; i < argc; i++) {
        if (!_wcsicmp(argv[i], L"/quiet")) *quiet = 1;
        else if (!_wcsicmp(argv[i], L"/all")) g_keep = 0;
    }
}

static void kept_text(wchar_t *out, size_t size)
{
    size_t n;
    HKEY k;
    if (g_unprotected || g_unconfirmed) return;
    if (g_other[0]) {
        n = wcslen(out);
        _snwprintf_s(out + n, size - n, _TRUNCATE, OTHER_KEPT, g_other);
    }
    if (!g_keep) return;
    if (g_skipped) {
        wchar_t where[MAX_PATH * 2 + 8];
        _snwprintf_s(where, _countof(where), _TRUNCATE, L"%s\\%s", g_folder, g_keep_dir);
        n = wcslen(out);
        _snwprintf_s(out + n, size - n, _TRUNCATE, KEPT_FOLDER, where);
    }
    if (g_settings[0] && RegOpenKeyExW(HKEY_LOCAL_MACHINE, g_settings, 0, KEY_READ | KEY_WOW64_32KEY, &k) == ERROR_SUCCESS) {
        RegCloseKey(k);
        n = wcslen(out);
        _snwprintf_s(out + n, size - n, _TRUNCATE, L"%s", KEPT_KEY);
    }
}

int WINAPI wWinMain(HINSTANCE inst, HINSTANCE prev, PWSTR cmd, int show)
{
    int argc;
    wchar_t **argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    wchar_t *text, ask[1024], dir[MAX_PATH], *slash;
    DWORD chars;
    int quiet;
    (void)prev; (void)cmd; (void)show;
    options(argc, argv, &quiet);
    if (argc < 2 || GetFileAttributesW(argv[1]) == INVALID_FILE_ATTRIBUTES) {
        MessageBoxW(NULL, L"The uninstall information was not found.", TITLE, MB_ICONERROR);
        return 1;
    }
    /* nothing in the record is read before it's known only admins can change it, so its name isn't known yet */
    if (unprotected(argv[1])) {
        if (!quiet) MessageBoxW(NULL, UNPROTECTED_NO_NAME, TITLE, MB_ICONERROR);
        return 4;
    }
    if (!(text = load(argv[1], &chars))) {
        MessageBoxW(NULL, L"The uninstall information was not found.", TITLE, MB_ICONERROR);
        return 1;
    }
    run(text, chars, L'N');
    _snwprintf_s(ask, _countof(ask), _TRUNCATE, ASK, g_name);
    if (!quiet && MessageBoxW(NULL, ask, TITLE, MB_YESNO | MB_ICONWARNING | MB_DEFBUTTON2) != IDYES) return 2;
    if (!quiet && g_keep) g_keep = MessageBoxW(NULL, KEEP_ASK, TITLE, MB_YESNO | MB_ICONQUESTION | MB_DEFBUTTON1) == IDYES;
    while (running(L"pol.exe") || running(L"polboot.exe")) {
        if (quiet || MessageBoxW(NULL, RUNNING, TITLE,
                                 MB_RETRYCANCEL | MB_ICONWARNING) != IDRETRY) return 3;
    }
    CoInitialize(NULL);
    g_label = CreateWindowExW(WS_EX_TOPMOST, L"STATIC", L"", WS_POPUP | WS_BORDER | WS_VISIBLE | SS_CENTER | SS_CENTERIMAGE,
                              (GetSystemMetrics(SM_CXSCREEN) - 420) / 2, (GetSystemMetrics(SM_CYSCREEN) - 90) / 2, 420, 90,
                              NULL, NULL, inst, NULL);
    GetModuleFileNameW(NULL, dir, MAX_PATH);
    if ((slash = wcsrchr(dir, L'\\')) != NULL) *slash = 0;
    remove_all(text, chars, argv[1], dir);
    DestroyWindow(g_label);
    g_label = NULL;
    if (g_unprotected)
        _snwprintf_s(ask, _countof(ask), _TRUNCATE, UNPROTECTED, g_name);
    else if (g_unconfirmed)
        _snwprintf_s(ask, _countof(ask), _TRUNCATE,
                     L"%s was removed, but the " FOLDER L" was left where it is, because it couldn't be confirmed as this install's. Check it and delete it yourself if you don't need it.",
                     g_name);
    else if (g_kept[0])
        _snwprintf_s(ask, _countof(ask), _TRUNCATE,
                     L"%s was removed, but some of its files couldn't be deleted. You can delete what's left in %s yourself.",
                     g_name, g_kept);
    else if (g_pending)
        _snwprintf_s(ask, _countof(ask), _TRUNCATE,
                     L"%s was removed. Some of its files were in use, so they'll be deleted when Windows next starts.", g_name);
    else _snwprintf_s(ask, _countof(ask), _TRUNCATE, L"%s was removed.", g_name);
    kept_text(ask, _countof(ask));
    if (!quiet) MessageBoxW(NULL, ask, TITLE, MB_ICONINFORMATION);
    CoUninitialize();
    return 0;
}
