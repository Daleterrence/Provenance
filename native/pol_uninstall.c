/* pol_uninstall.c - the Add/Remove Programs uninstaller for the POL and FFXI installs Provenance applies itself (no
   Windows Installer, so no MsiExec /X). Run as uninstall.exe "<install>\Uninstall\{ProductCode}.txt" [/quiet]
   The .txt is UTF-16LE, one item per line:
     N <display name>
     A <Uninstall key>                  HKLM, 32-bit view
     S <shortcut file>
     C <key>                            HKLM, 32-bit view, removed with its subkeys (COM classes, ProgIDs)
     T <LIBID> <major> <minor> <lcid>   a type library registration
     V <M|U> <key>\t<value name>        M = HKLM, U = HKCU, 32-bit view. "+" for a key the package made. Keys left
                                        empty go, up to the first one that isn't
     F <file>
     P <folder>\t<pattern>              the package's RemoveFile rules, files only
     D <folder>                         removed when empty, deepest first
   Files the game's updates added stay (bar the P patterns), same as the official uninstall */
#define UNICODE
#define _UNICODE
#include <windows.h>
#include <oleauto.h>
#include <tlhelp32.h>
#include <wchar.h>
#include <stdlib.h>

static HWND g_label;
static wchar_t g_name[256] = L"FINAL FANTASY XI";

static void status(const wchar_t *text)
{
    MSG m;
    if (!g_label) return;
    SetWindowTextW(g_label, text);
    while (PeekMessageW(&m, NULL, 0, 0, PM_REMOVE)) { TranslateMessage(&m); DispatchMessageW(&m); }
}

/* deletes a key with no values and no subkeys, then its parent the same way */
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
        if (!_wcsicmp(key, L"SOFTWARE") || !_wcsicmp(key, L"SOFTWARE\\Classes") ||
            !_wcsicmp(key, L"Software\\Microsoft") || !_wcsicmp(key, L"SOFTWARE\\Classes\\CLSID")) return;
    }
}

static void delete_file(const wchar_t *path)
{
    DWORD a = GetFileAttributesW(path);
    if (a == INVALID_FILE_ATTRIBUTES || (a & FILE_ATTRIBUTE_DIRECTORY)) return;
    if (a & FILE_ATTRIBUTE_READONLY) SetFileAttributesW(path, a & ~FILE_ATTRIBUTE_READONLY);
    DeleteFileW(path);
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

static int run(wchar_t *text, DWORD chars, wchar_t kind)
{
    wchar_t *p = text + (chars > 0 && text[0] == 0xFEFF), *end = text + chars;   /* skip the byte-order mark */
    int n = 0;
    while (p < end) {
        wchar_t *eol = p, *arg, *tab;
        while (eol < end && *eol != L'\n' && *eol != L'\r') eol++;
        if (eol - p >= 2 && p[0] == kind && p[1] == L' ') {
            wchar_t save = *eol;
            *eol = 0;
            arg = p + 2;
            switch (kind) {
            case L'S': case L'F':
                delete_file(arg);
                if (kind == L'F' && (++n % 500) == 0) {
                    wchar_t msg[160];
                    wsprintfW(msg, L"Removing %s: %d files", g_name, n);
                    status(msg);
                }
                break;
            case L'A': case L'C': {
                HKEY k;
                if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, arg, 0, KEY_ALL_ACCESS | KEY_WOW64_32KEY, &k) == ERROR_SUCCESS) {
                    RegDeleteTreeW(k, NULL);
                    RegCloseKey(k);
                    RegDeleteKeyExW(HKEY_LOCAL_MACHINE, arg, KEY_WOW64_32KEY, 0);
                }
                break;
            }
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
                    *tab = 0;
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
                    WIN32_FIND_DATAW fd;
                    HANDLE h;
                    *tab = 0;
                    wsprintfW(pattern, L"%s\\%s", arg, tab + 1);
                    h = FindFirstFileW(pattern, &fd);
                    if (h != INVALID_HANDLE_VALUE) {
                        do {
                            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
                            wsprintfW(file, L"%s\\%s", arg, fd.cFileName);
                            delete_file(file);
                        } while (FindNextFileW(h, &fd));
                        FindClose(h);
                    }
                }
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
        p = eol;
        while (p < end && (*p == L'\n' || *p == L'\r')) p++;
    }
    return n;
}

int WINAPI wWinMain(HINSTANCE inst, HINSTANCE prev, PWSTR cmd, int show)
{
    int argc;
    wchar_t **argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    wchar_t *text, ask[512], self[MAX_PATH], dir[MAX_PATH], *slash, pattern[MAX_PATH + 8];
    DWORD chars;
    WIN32_FIND_DATAW fd;
    HANDLE h;
    int quiet;
    (void)prev; (void)cmd; (void)show;
    quiet = argc >= 3 && !_wcsicmp(argv[2], L"/quiet");
    if (argc < 2 || !(text = load(argv[1], &chars))) {
        MessageBoxW(NULL, L"The uninstall information was not found.", L"Uninstall", MB_ICONERROR);
        return 1;
    }
    run(text, chars, L'N');
    wsprintfW(ask, L"Remove %s from this computer?\n\nYour FINAL FANTASY XI settings and the files that game updates added are kept, as with Square Enix's own uninstall.", g_name);
    if (!quiet && MessageBoxW(NULL, ask, L"Uninstall", MB_YESNO | MB_ICONQUESTION) != IDYES) return 2;
    while (running(L"pol.exe") || running(L"polboot.exe")) {
        if (quiet || MessageBoxW(NULL, L"PlayOnline or FINAL FANTASY XI is running. Close it, then press Retry.", L"Uninstall",
                                 MB_RETRYCANCEL | MB_ICONWARNING) != IDRETRY) return 3;
    }
    CoInitialize(NULL);
    g_label = CreateWindowExW(WS_EX_TOPMOST, L"STATIC", L"", WS_POPUP | WS_BORDER | WS_VISIBLE | SS_CENTER | SS_CENTERIMAGE,
                              (GetSystemMetrics(SM_CXSCREEN) - 420) / 2, (GetSystemMetrics(SM_CYSCREEN) - 90) / 2, 420, 90,
                              NULL, NULL, inst, NULL);
    status(L"Removing shortcuts and registry entries");
    run(text, chars, L'S');
    run(text, chars, L'V');
    run(text, chars, L'C');
    run(text, chars, L'T');
    run(text, chars, L'F');
    run(text, chars, L'P');
    run(text, chars, L'D');
    run(text, chars, L'A');
    DestroyWindow(g_label);
    g_label = NULL;
    DeleteFileW(argv[1]);
    /* the last product gone: this program and its folder go at the next restart */
    GetModuleFileNameW(NULL, self, MAX_PATH);
    lstrcpynW(dir, self, MAX_PATH);
    if ((slash = wcsrchr(dir, L'\\')) != NULL) *slash = 0;
    wsprintfW(pattern, L"%s\\*.txt", dir);
    h = FindFirstFileW(pattern, &fd);
    if (h != INVALID_HANDLE_VALUE) FindClose(h);
    else {
        /* renamed out of the way first, or a reinstall before that restart gets its fresh uninstall.exe deleted too
           (whoops, that one bit) */
        wchar_t old[MAX_PATH + 32];
        wsprintfW(old, L"%s\\uninstall-%lu.old", dir, GetCurrentProcessId());
        MoveFileExW(MoveFileExW(self, old, MOVEFILE_REPLACE_EXISTING) ? old : self, NULL, MOVEFILE_DELAY_UNTIL_REBOOT);
        MoveFileExW(dir, NULL, MOVEFILE_DELAY_UNTIL_REBOOT);
    }
    wsprintfW(ask, L"%s was removed.", g_name);
    if (!quiet) MessageBoxW(NULL, ask, L"Uninstall", MB_ICONINFORMATION);
    CoUninitialize();
    return 0;
}
