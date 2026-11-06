/* "Provenance Watcher.exe" <pol.exe> <hook dll>: pol.exe starts suspended, gets the hook, then resumes.
   --attach <pid> <hook dll> is for a pol.exe Windower or Ashita started, which it never ends.
   --windower <Hook.dll> <pol.exe> <hook dll> puts Windower's Hook.dll in before the hook, the way Windower.exe does */
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>
#include "hook/hook_contract.h"

/* the app reads this output and the exit codes, so they don't change */
static HANDLE g_child;   /* the suspended pol.exe, killed on any later failure */
static void die(const char *m)
{
    fprintf(stderr, "watcher: %s (err=%lu)\n", m, GetLastError());
    if (g_child) TerminateProcess(g_child, 1);
    exit(1);
}

static void usage(void)
{
    fprintf(stderr, "watcher: usage: <pol.exe> <hook dll> | --attach <pid> <hook dll> | "
                    "--windower <Hook.dll> <pol.exe> <hook dll>\n");
    exit(1);
}

static const char *utf8(const WCHAR *w)
{
    static char buf[MAX_PATH * 4];
    if (!WideCharToMultiByte(CP_UTF8, 0, w, -1, buf, sizeof buf, NULL, NULL)) lstrcpyA(buf, "(too long to show)");
    return buf;
}

static DWORD remote_run(HANDLE p, FARPROC fn, void *arg, DWORD wait_ms, const char *create_msg, const char *timeout_msg)
{
    DWORD code = 0;
    HANDLE th = CreateRemoteThread(p, NULL, 0, (LPTHREAD_START_ROUTINE)fn, arg, 0, NULL);
    if (!th) die(create_msg);
    if (WaitForSingleObject(th, wait_ms) != WAIT_OBJECT_0) { CloseHandle(th); die(timeout_msg); }
    GetExitCodeThread(th, &code);
    CloseHandle(th);
    return code;
}

static DWORD remote_call(HANDLE p, FARPROC fn, void *arg, const char *what)
{
    return remote_run(p, fn, arg, 10000, what, what);
}

/* data goes into the process and loader runs on it in a thread there, giving back what that returns */
static DWORD remote_load(HANDLE process, FARPROC loader, const void *data, DWORD size, DWORD wait_ms, const char *timeout_msg)
{
    DWORD code;
    void *remote = VirtualAllocEx(process, NULL, size, MEM_COMMIT, PAGE_READWRITE);
    if (!remote) die("VirtualAllocEx");
    if (!WriteProcessMemory(process, remote, data, size, NULL)) die("WriteProcessMemory");
    code = remote_run(process, loader, remote, wait_ms, "CreateRemoteThread", timeout_msg);
    VirtualFreeEx(process, remote, 0, MEM_RELEASE);
    return code;
}

static int attach(DWORD pid, const WCHAR *dll)
{
    HMODULE k32 = GetModuleHandleA("kernel32.dll");
    FARPROC set_error_mode = GetProcAddress(k32, "SetErrorMode");
    FARPROC load_library = GetProcAddress(k32, "LoadLibraryW");
    HANDLE p;
    DWORD old, code;

    if (GetFileAttributesW(dll) == INVALID_FILE_ATTRIBUTES) die("pol_hook.dll not found");
    p = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION |
                    PROCESS_VM_WRITE | PROCESS_VM_READ | SYNCHRONIZE, FALSE, pid);
    if (!p) die("cannot open pol.exe to attach");
    /* no critical-error boxes while the hook loads: a Smart App Control refusal pops a "Bad Image" box that holds the
       load until someone clicks OK. Great feature :D */
    old = remote_call(p, set_error_mode, (void *)(UINT_PTR)(SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX),
                      "setting the error mode timed out");
    code = remote_load(p, load_library, dll, (lstrlenW(dll) + 1) * sizeof(WCHAR), 10000, "hook dll load timed out in target");
    remote_call(p, set_error_mode, (void *)(UINT_PTR)old, "restoring the error mode timed out");
    CloseHandle(p);
    printf("watcher: attach, LoadLibraryW -> %08lx\n", code);
    if (!code) die("hook dll was refused or failed to load in target");
    printf("watcher: " LOG_WATCHER_ATTACHED " (pid %lu)\n", pid);
    return 0;
}

static int hook_and_resume(PROCESS_INFORMATION *pi, const void *dll, DWORD size, UINT errmode, FARPROC load_library,
                           const char *how)
{
    FARPROC set_error_mode = GetProcAddress(GetModuleHandleA("kernel32.dll"), "SetErrorMode");
    char noresume[4] = {0};
    DWORD code = remote_load(pi->hProcess, load_library, dll, size, 10000, "hook dll load timed out in target");

    printf("watcher: injected, %s -> %08x\n", how, code);
    if (!code || code == STILL_ACTIVE) die("hook dll was refused or failed to load in target");
    remote_call(pi->hProcess, set_error_mode, (void *)(UINT_PTR)errmode, "restoring the error mode timed out");
    /* PROVENANCE_WATCHER_NORESUME=1 loads the hook, then ends pol.exe unresumed (checks the injection without
       starting POL) */
    if (GetEnvironmentVariableA("PROVENANCE_WATCHER_NORESUME", noresume, sizeof noresume) && noresume[0] == '1') {
        printf("watcher: hook loaded; PROVENANCE_WATCHER_NORESUME set, pol.exe ended unresumed\n");
        TerminateProcess(pi->hProcess, 0);
        return 0;
    }
    ResumeThread(pi->hThread);
    g_child = NULL;
    printf("watcher: " LOG_WATCHER_RUNNING "%lu)\n", pi->dwProcessId);
    CloseHandle(pi->hThread);
    CloseHandle(pi->hProcess);
    return 0;
}

static WCHAR *full_path(const WCHAR *path, const char *what)
{
    DWORD n = GetFullPathNameW(path, 0, NULL, NULL);
    WCHAR *full = n ? malloc(n * sizeof(WCHAR)) : NULL;
    if (!full || !GetFullPathNameW(path, n, full, NULL)) die(what);
    return full;
}

/* pol.exe suspended, in its own folder. FFXiEntry resolves the install paths from the cwd, and kiyaku=true skips the EULA
   (no /game means the hook hands back content code 1). pol.exe inherits the error mode, so it gets the same no-boxes
   rule as attach. Gives back the error mode to put back in it once the hook's in */
static UINT start_suspended(const WCHAR *pol, PROCESS_INFORMATION *pi)
{
    STARTUPINFOW si = { sizeof si };
    WCHAR *cmd, *cwd, *slash;
    DWORD n = lstrlenW(pol);
    UINT errmode;

    cmd = malloc((n + 16) * sizeof(WCHAR));
    cwd = malloc((n + 1) * sizeof(WCHAR));
    if (!cmd || !cwd) die("out of memory");
    lstrcpyW(cwd, pol);
    slash = wcsrchr(cwd, L'\\');
    if (slash) *slash = 0;
    printf("watcher: cwd=%s\n", utf8(cwd));
    cmd[0] = L'"';
    lstrcpyW(cmd + 1, pol);
    lstrcatW(cmd, L"\" kiyaku=true");
    errmode = SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX);
    SetErrorMode(errmode | SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX);
    if (!CreateProcessW(pol, cmd, NULL, NULL, FALSE, CREATE_SUSPENDED, NULL, slash ? cwd : NULL, &si, pi))
        die("CreateProcess pol.exe");
    SetErrorMode(errmode);
    g_child = pi->hProcess;
    free(cmd);
    free(cwd);
    return errmode;
}

/* the app makes Windower's mapping and event before it runs this. Hook.dll throws on its install thread when the
   mapping's missing, so that's refused before pol.exe even starts */
static int windower(int argc, WCHAR **arg)
{
    WCHAR *hook, *pol, *polhook;
    char dll[MAX_PATH] = {0}, wait_s[16] = {0};
    PROCESS_INFORMATION pi;
    HANDLE settings, ready, waits[2];
    HMODULE kernelbase;
    FARPROC load_library = NULL;
    DWORD code, n, wait_ms = 30000;
    UINT errmode;
    BOOL lossy = FALSE;

    if (argc < 5) usage();
    pol = arg[3];
    /* full paths, so what gets checked here is what pol.exe loads from its own working folder */
    hook = full_path(arg[2], "Windower's Hook.dll path is no good");
    polhook = full_path(arg[4], "pol_hook.dll path is no good");
    if (!WideCharToMultiByte(CP_ACP, WC_NO_BEST_FIT_CHARS, polhook, -1, dll, MAX_PATH, NULL, &lossy) || lossy)
        die("pol_hook.dll path won't go into ANSI");
    if (GetFileAttributesW(hook) == INVALID_FILE_ATTRIBUTES) die("Windower's Hook.dll not found");
    if (GetFileAttributesW(pol) == INVALID_FILE_ATTRIBUTES) die("pol.exe not found");
    if (GetFileAttributesA(dll) == INVALID_FILE_ATTRIBUTES) die("pol_hook.dll not found");
    settings = OpenFileMappingW(FILE_MAP_READ, FALSE, L"WindowerMMFSettingsHandler");
    if (!settings) die("Windower's settings mapping isn't there");
    ready = OpenEventW(SYNCHRONIZE, FALSE, L"windower.signal-injection");
    if (!ready) die("Windower's injection event isn't there");
    n = GetEnvironmentVariableA("PROVENANCE_WATCHER_WINDOWER_WAIT_MS", wait_s, sizeof wait_s);
    if (n && n < sizeof wait_s && strtoul(wait_s, NULL, 10)) wait_ms = strtoul(wait_s, NULL, 10);

    printf("watcher: host=%s\n", utf8(pol));
    printf("watcher: hook=%s\n", dll);
    printf("watcher: windower=%s\n", utf8(hook));
    /* Windower.exe sets it on itself for pol.exe to inherit, so same here */
    SetEnvironmentVariableW(L"__COMPAT_LAYER", L"HighDpiAware");
    errmode = start_suspended(pol, &pi);

    /* a signal left over from some earlier launch would let pol.exe resume before the hook's in */
    WaitForSingleObject(ready, 0);
    /* Windower.exe waits as long as it takes here, and a cold start with antivirus poking at it can be slow */
    code = remote_load(pi.hProcess, GetProcAddress(GetModuleHandleA("kernel32.dll"), "LoadLibraryW"), hook,
                       (lstrlenW(hook) + 1) * sizeof(WCHAR), wait_ms, "Windower's Hook.dll load timed out in target");
    printf("watcher: windower, LoadLibraryW -> %08lx\n", code);
    if (!code) die("Windower's Hook.dll was refused or failed to load in target");
    /* the event comes once every one of its hooks is in, pol.exe stays suspended till then */
    waits[0] = ready;
    waits[1] = pi.hProcess;
    SetLastError(0);
    n = WaitForMultipleObjects(2, waits, FALSE, wait_ms);
    if (n == WAIT_OBJECT_0 + 1) die("pol.exe ended before Windower's hook reported in");
    if (n != WAIT_OBJECT_0) die("Windower's hook didn't report in");
    printf("watcher: windower hook reported in\n");
    /* Windower's Hook.dll detours kernel32's LoadLibraryA to refuse anything named like it, and pol_hook.dll only
       gets past on a quirk in its check (not one I'd bet on lasting). KernelBase's export goes round the detour */
    kernelbase = GetModuleHandleA("kernelbase.dll");
    if (kernelbase) load_library = GetProcAddress(kernelbase, "LoadLibraryA");
    if (!load_library) load_library = GetProcAddress(GetModuleHandleA("kernel32.dll"), "LoadLibraryA");
    return hook_and_resume(&pi, dll, lstrlenA(dll) + 1, errmode, load_library, "LoadLibraryA");
}

/* wide all the way, Provenance's folder has the Windows user name in it and that can be anything */
int wmain(int argc, WCHAR **argv)
{
    WCHAR *pol, *dll;
    PROCESS_INFORMATION pi;
    UINT errmode;

    int attaching = argc > 1 && lstrcmpW(argv[1], L"--attach") == 0;
    int dllarg = attaching ? 3 : 2;

    if (argc > 1 && lstrcmpW(argv[1], L"--windower") == 0) return windower(argc, argv);
    if (argc <= dllarg) usage();
    /* pol.exe would look for a relative one from its own working folder */
    dll = full_path(argv[dllarg], "pol_hook.dll path is no good");
    if (attaching) {
        DWORD pid = (DWORD)wcstoul(argv[2], NULL, 10);
        if (!pid) die("--attach needs a pid");
        return attach(pid, dll);
    }
    pol = full_path(argv[1], "pol.exe path is no good");
    if (GetFileAttributesW(pol) == INVALID_FILE_ATTRIBUTES) die("pol.exe not found");
    if (GetFileAttributesW(dll) == INVALID_FILE_ATTRIBUTES) die("pol_hook.dll not found");

    printf("watcher: host=%s\n", utf8(pol));
    printf("watcher: hook=%s\n", utf8(dll));
    errmode = start_suspended(pol, &pi);
    return hook_and_resume(&pi, dll, (lstrlenW(dll) + 1) * sizeof(WCHAR), errmode,
                           GetProcAddress(GetModuleHandleA("kernel32.dll"), "LoadLibraryW"), "LoadLibraryW");
}
