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
#include "addresses.c"
#include "pol_exe.c"
#include "redirector.c"
#include "polcore_sockets.c"
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
#include "chat_room_master.c"
#include "group_chat.c"
#include "request_table.c"
#include "login.c"
#include "handles.c"
#include "handle_edits.c"
#include "snapshot.c"
#include "ingame.c"
#include "ffxi.c"
#include "lobby.c"
#include "hold.c"

/* add-ons and game plugins are already unloaded in FFXI's exit. Ashita hooks ExitProcess to unload its POL plugins
   and finish its log, so it gets a clean one */
NORETURN static void pol_hook_game_over(UINT code)
{
    ingame_stop();
    if (GetModuleHandleW(L"Ashita.dll")) {
        base_saver_restore();
        ExitProcess(code);
    }
    base_end_pol(code);
}

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
static __declspec(naked) void pol_hook_ffxi_returned_thunk(void)
{
    __asm {
        push edi
        call pol_hook_ffxi_returned
        add esp, 4
        ret
    }
}

static int pol_hook_detour_game_end(BYTE *p)
{
    DWORD old;
    if (!VirtualProtect(p, 5, PAGE_EXECUTE_READWRITE, &old)) {
        base_log("pol_hook: content launcher protect failed - game end not hooked\r\n");
        return 0;
    }
    *(DWORD *)(p + 1) = (DWORD)((BYTE *)pol_hook_ffxi_returned_thunk - (p + 5));
    VirtualProtect(p, 5, old, &old);
    base_log("pol_hook: content launcher's return hooked\r\n");
    return 1;
}

static unsigned __stdcall pol_hook_menu_stub(unsigned a, unsigned b, unsigned c, unsigned d)
{
    (void)a; (void)b; (void)c; (void)d;
    fake_session();
    login_set_cwd_ffxi();
    ((void (__cdecl *)(void))pc_addr_of((BYTE *)GetModuleHandleW(NULL), AT_SPLASH_CLOSE))();
    hold_run((BYTE *)viewer_polcore_module());
    base_log("pol_hook: menu session -> content code 1 (FFXiEntry)\r\n");
    g_game_started = 1;
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
    BYTE *p;
    DWORD old, rel;
    addr_require(IN_EXE, "pol.exe", (BYTE *)exe, (DWORD)(ULONG_PTR)exe);
    if (base_getenv_set(ENV_LAUNCH_ID)) {
        base_log("pol_hook: attached to a Windower/Ashita pol.exe; mutex names left to it\r\n");
    } else {
        HMODULE k32 = GetModuleHandleW(L"kernel32.dll");
        g_pCreateMutexW = GetProcAddress(k32, "CreateMutexW");
        g_pOpenMutexW   = GetProcAddress(k32, "OpenMutexW");
        if (g_pCreateMutexW) pol_exe_hook_slot(exe, AT_CREATE_MUTEX_SLOT, (void *)pol_exe_my_CreateMutexW, "CreateMutexW");
        if (g_pOpenMutexW) pol_exe_hook_slot(exe, AT_OPEN_MUTEX_SLOT, (void *)pol_exe_my_OpenMutexW, "OpenMutexW");
    }
    pol_exe_hook_slot(exe, AT_CREATE_DIALOG_SLOT, (void *)pol_exe_my_CreateDialogParamW, "CreateDialogParamW");
    pol_exe_skip_dx_check((BYTE *)exe);
    p = pc_addr_of((BYTE *)exe, AT_MENU_SESSION);
    if (!pol_hook_detour_game_end(pc_addr_of((BYTE *)exe, AT_GAME_RETURN))
        || !VirtualProtect(p, 5, PAGE_EXECUTE_READWRITE, &old))
        pol_hook_not_hooked();
    rel = (DWORD)((BYTE *)pol_hook_menu_stub - (p + 5));
    p[0] = 0xE9;
    *(DWORD *)(p + 1) = rel;
    VirtualProtect(p, 5, old, &old);
    base_log("pol_hook: menu session detoured -> %08x\r\n", (unsigned)(ULONG_PTR)pol_hook_menu_stub);
}

/* PROVENANCE_LAUNCH_ID ends up in a file name in base_ipc_dir, so only what Provenance makes gets through: 1 to 32 characters of 0-9 a-f */
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
        base_log("pol_hook: Windower/Ashita launch id refused\r\n");
        return TRUE;
    }
    redirector_start();
    if (idn > 0) {
        base_ipc_dir(path);
        wsprintfA(path + lstrlenA(path), IPC_PID_FILE, id);
        f = CreateFileA(path, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
        if (f != INVALID_HANDLE_VALUE) {
            wsprintfA(num, "%u", GetCurrentProcessId());
            WriteFile(f, num, lstrlenA(num), &n, NULL);
            CloseHandle(f);
        }
        base_log("pol_hook: Windower/Ashita launch %s (pid file written)\r\n", id);
    }
    return TRUE;
}
