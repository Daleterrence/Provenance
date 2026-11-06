/* pol_hook.c - signs in from inside the real pol.exe, then its own FFXiEntry boots the game (Windower and Ashita
   never notice) */
#include <windows.h>
#include <wincred.h>
#include <string.h>
#include <bcrypt.h>
#include <sddl.h>
#include <tlhelp32.h>
#include "pol_cred.h"
#include "pol_regions.h"
#include "hook_contract.h"
#include "polcore.h"
#include "session.h"
#include "base.c"
#include "pol_exe.c"
#include "credentials.c"
#include "viewer_setup.c"
#include "requests.c"
#include "json.c"
#include "peers.c"
#include "mail_inbox.c"
#include "message_store.c"
#include "friends.c"
#include "profile_db.c"
#include "profiles.c"
#include "mail_send.c"
#include "groups.c"
#include "my_profile.c"
#include "chat_frames.c"
#include "chat_lists.c"
#include "chat_room.c"
#include "group_chat.c"
#include "login.c"
#include "handles.c"
#include "snapshot.c"
#include "hold.c"

/* add-ons and game plugins already unloaded in FFXI's exit. Ashita hooks ExitProcess to unload its POL plugins and
   finish its log, so it gets a clean one, the rest just get more places to crash */
NORETURN static void pol_hook_game_over(UINT code)
{
    if (GetModuleHandleW(L"Ashita.dll")) {
        base_saver_restore();
        ExitProcess(code);
    }
    base_end_pol(code);
}

/* 1.18.15e: the menu session's prologue (push -1; push 0x43fe5a; mov eax, fs:[0]), and the content launcher's
   GameStart call and the empty trace call after it (call eax; push L"Return from FFXI"; mov esi, eax;
   call 0x00412430) */
static const BYTE MENU_PROLOGUE[] = { 0x6a, 0xff, 0x68, 0x5a, 0xfe, 0x43, 0x00, 0x64, 0xa1, 0x00, 0x00, 0x00, 0x00 };
static const BYTE FFXI_RETURN[] = { 0xff, 0xd0, 0x68, 0xa4, 0x2b, 0x44, 0x00, 0x8b, 0xf0, 0xe8, 0x18, 0xf2, 0xff, 0xff };

static int g_game_started;
/* first thing after GameStart returns, ok or not. On a failed one pol.exe would go on to pass the FFXiEntry it's just
   released (NULL) to 0x00408620, which falls over on it :( */
static void __cdecl pol_hook_ffxi_returned(HRESULT hr)
{
    if (!g_game_started) return;
    if (FAILED(hr)) {
        base_log("pol_hook: GameStart failed hr=%08x - ending pol.exe (exit %d)\r\n", (unsigned)hr, GAME_START_EXIT);
        pol_hook_game_over(GAME_START_EXIT);
    } else {
        base_log("pol_hook: content exited (back from FFXI) - ending pol.exe\r\n");
        pol_hook_game_over(0);
    }
}
/* GameStart's HRESULT is in esi by the trace call, and the caller pops the string itself */
static __declspec(naked) void pol_hook_ffxi_returned_thunk(void)
{
    __asm {
        push esi
        call pol_hook_ffxi_returned
        add esp, 4
        ret
    }
}

static int pol_hook_detour_game_end(BYTE *exe)
{
    BYTE *p = exe + 0x1320a;
    DWORD old;
    if (memcmp(p, FFXI_RETURN, sizeof FFXI_RETURN) != 0) {
        base_log("pol_hook: content launcher not as expected (%02x %02x %02x) - game end not hooked\r\n", p[0], p[2],
            p[9]);
        return 0;
    }
    if (!VirtualProtect(p + 9, 5, PAGE_EXECUTE_READWRITE, &old)) {
        base_log("pol_hook: content launcher protect failed - game end not hooked\r\n");
        return 0;
    }
    *(DWORD *)(p + 10) = (DWORD)((BYTE *)pol_hook_ffxi_returned_thunk - (p + 14));
    VirtualProtect(p + 9, 5, old, &old);
    base_log("pol_hook: content launcher's return hooked\r\n");
    return 1;
}

/* the menu session detour, thiscall (this in ecx) + 4 args, callee pops 16 bytes. Returning 1 takes pol.exe's
   dispatch (0x0041406e) down its FFXI case: 0x00413140 CoCreates FFXiEntry {989D790D}, which boots FFXiMain. */
static unsigned __stdcall pol_hook_menu_stub(unsigned a, unsigned b, unsigned c, unsigned d)
{
    (void)a; (void)b; (void)c; (void)d;
    fake_session();
    login_set_cwd_ffxi();
    /* pol.exe 0x00406a10 destroys the hidden splash on the thread that made it */
    ((void (__cdecl *)(void))((BYTE *)GetModuleHandleW(NULL) + 0x6a10))();
    hold_run((BYTE *)viewer_polcore_module());
    base_log("pol_hook: menu session -> content code 1 (FFXiEntry)\r\n");
    g_game_started = 1;   /* from here pol_hook_ffxi_returned ends pol.exe when the game does, so this only runs once */
    return 1;
}

NORETURN static void pol_hook_not_hooked(void)
{
    base_log("pol_hook: menu session not detoured - ending pol.exe (exit %d)\r\n", NOT_HOOKED_EXIT);
    base_end_pol(NOT_HOOKED_EXIT);
}

static void pol_hook_install(void)
{
    HMODULE exe = GetModuleHandleW(NULL);
    DWORD old, rel;
    /* WinMain reaches CreateMutexW and OpenMutexW through pol.exe's MSLU slots, not the IAT. In a Windower or
       Ashita pol.exe they may already exist, and renaming would make later opens miss. */
    if (base_getenv_set(ENV_LAUNCH_ID)) {
        base_log("pol_hook: attached to a third-party hook's pol.exe; mutex names left to it\r\n");
    } else {
        HMODULE k32 = GetModuleHandleW(L"kernel32.dll");
        g_pCreateMutexW = GetProcAddress(k32, "CreateMutexW");
        g_pOpenMutexW   = GetProcAddress(k32, "OpenMutexW");
        if (g_pCreateMutexW) pol_exe_hook_slot(exe, 0x5c7c4, 0x15674, (void *)pol_exe_my_CreateMutexW, "CreateMutexW");
        if (g_pOpenMutexW)   pol_exe_hook_slot(exe, 0x5c834, 0x15b39, (void *)pol_exe_my_OpenMutexW,   "OpenMutexW");
    }
    pol_exe_hook_slot(exe, 0x5c864, 0x15493, (void *)pol_exe_my_CreateDialogParamW, "CreateDialogParamW");
    /* an E9 jmp over the menu session (pol.exe 0x00413710), only in the 1.18.15e pol.exe I wrote this against.
       Other builds (1.18.00n for one) have other code here. */
    BYTE *p = (BYTE *)exe + 0x13710;
    if (memcmp(p, MENU_PROLOGUE, sizeof MENU_PROLOGUE) != 0) {
        base_log("pol_hook: pol.exe is not the 1.18.15e build (menu session %02x %02x %02x) - not hooked\r\n",
           p[0], p[1], p[2]);
        pol_hook_not_hooked();
    }
    /* the game's end is only ever caught at the content launcher's return, so no detour without that hook */
    if (!pol_hook_detour_game_end((BYTE *)exe) || !VirtualProtect(p, 5, PAGE_EXECUTE_READWRITE, &old))
        pol_hook_not_hooked();
    rel = (DWORD)((BYTE *)pol_hook_menu_stub - (p + 5));
    p[0] = 0xE9;
    *(DWORD *)(p + 1) = rel;
    VirtualProtect(p, 5, old, &old);
    base_log("pol_hook: menu session detoured -> %08x\r\n", (unsigned)(ULONG_PTR)pol_hook_menu_stub);
}

/* PROVENANCE_LAUNCH_ID ends up in a file name in base_ipc_dir, so only what Provenance makes gets through: 1 to 32
   characters of 0-9 a-f (n = what GetEnvironmentVariableA returned) */
static int pol_hook_launch_id_ok(const char *id, DWORD n)
{
    if (n < 1 || n > 32) return 0;
    for (DWORD i = 0; i < n; i++)
        if (!((id[i] >= '0' && id[i] <= '9') || (id[i] >= 'a' && id[i] <= 'f'))) return 0;
    return 1;
}

BOOL WINAPI DllMain(HINSTANCE h, DWORD why, void *r)
{
    char id[64], path[MAX_PATH + 96], num[16];
    DWORD n, idn;
    HANDLE f;
    SYSTEM_INFO si;
    (void)r;
    if (why != DLL_PROCESS_ATTACH) return TRUE;
    DisableThreadLibraryCalls(h);
    base_saver_save();
    InitializeCriticalSection(&g_lgcs);
    g_lgcs_ready = 1;
    /* shows whether Large Address Aware took: 0xfffeffff with the bit on pol.exe, 0x7ffeffff without. Keep it the
       first line, the app reads a reused pid's log from the last one on */
    GetSystemInfo(&si);
    base_log(LOG_FIRST_LINE " %08x\r\n", (unsigned)(ULONG_PTR)si.lpMaximumApplicationAddress);
    base_env_check();
    base_owner_open();
    pol_hook_install();
    idn = GetEnvironmentVariableA(ENV_LAUNCH_ID, id, sizeof id);
    if (idn > 0 && !pol_hook_launch_id_ok(id, idn)) {
        base_log("pol_hook: third-party hook launch id refused\r\n");
    } else if (idn > 0) {
        base_ipc_dir(path);
        wsprintfA(path + lstrlenA(path), IPC_PID_FILE, id);
        f = CreateFileA(path, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
        if (f != INVALID_HANDLE_VALUE) {
            wsprintfA(num, "%u", GetCurrentProcessId());
            WriteFile(f, num, lstrlenA(num), &n, NULL);
            CloseHandle(f);
        }
        base_log("pol_hook: third-party hook launch %s (pid file written)\r\n", id);
    }
    return TRUE;
}
