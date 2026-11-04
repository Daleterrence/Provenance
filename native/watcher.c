/* "Provenance Watcher.exe" <pol.exe> <hook dll>: pol.exe starts suspended, gets the hook, then resumes.
   --attach <pid> <hook dll> is for a pol.exe Windower or Ashita started, which it never ends.
   --windower <Hook.dll> <pol.exe> <hook dll> puts Windower's Hook.dll in before the hook, the way Windower.exe does */
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>

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

static DWORD remote_call(HANDLE p, FARPROC fn, void *arg, const char *what)
{
    DWORD code = 0;
    HANDLE th = CreateRemoteThread(p, NULL, 0, (LPTHREAD_START_ROUTINE)fn, arg, 0, NULL);
    if (!th) die(what);
    if (WaitForSingleObject(th, 10000) != WAIT_OBJECT_0) { CloseHandle(th); die(what); }
    GetExitCodeThread(th, &code);
    CloseHandle(th);
    return code;
}

static int attach(DWORD pid, const WCHAR *dll)
{
    HMODULE k32 = GetModuleHandleA("kernel32.dll");
    FARPROC set_error_mode = GetProcAddress(k32, "SetErrorMode");
    FARPROC load_library = GetProcAddress(k32, "LoadLibraryW");
    HANDLE p;
    void *remote;
    DWORD old, code, size = (lstrlenW(dll) + 1) * sizeof(WCHAR);

    if (GetFileAttributesW(dll) == INVALID_FILE_ATTRIBUTES) die("pol_hook.dll not found");
    p = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION |
                    PROCESS_VM_WRITE | PROCESS_VM_READ | SYNCHRONIZE, FALSE, pid);
    if (!p) die("cannot open pol.exe to attach");
    /* no critical-error boxes while the hook loads: a Smart App Control refusal pops a "Bad Image" box that holds the
       load until someone clicks OK. Great feature :D */
    old = remote_call(p, set_error_mode, (void *)(UINT_PTR)(SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX),
                      "setting the error mode timed out");
    remote = VirtualAllocEx(p, NULL, size, MEM_COMMIT, PAGE_READWRITE);
    if (!remote) die("VirtualAllocEx");
    if (!WriteProcessMemory(p, remote, dll, size, NULL)) die("WriteProcessMemory");
    code = remote_call(p, load_library, remote, "hook dll load timed out in target");
    remote_call(p, set_error_mode, (void *)(UINT_PTR)old, "restoring the error mode timed out");
    VirtualFreeEx(p, remote, 0, MEM_RELEASE);
    CloseHandle(p);
    printf("watcher: attach, LoadLibraryW -> %08lx\n", code);
    if (!code) die("hook dll was refused or failed to load in target");
    printf("watcher: attached to pol.exe (pid %lu)\n", pid);
    return 0;
}

static int hook_and_resume(PROCESS_INFORMATION *pi, const void *dll, DWORD size, UINT errmode, FARPROC load_library,
                           const char *how)
{
    FARPROC set_error_mode = GetProcAddress(GetModuleHandleA("kernel32.dll"), "SetErrorMode");
    char noresume[4] = {0};
    void *remote;
    HANDLE th;
    DWORD code;

    remote = VirtualAllocEx(pi->hProcess, NULL, size, MEM_COMMIT, PAGE_READWRITE);
    if (!remote) die("VirtualAllocEx");
    if (!WriteProcessMemory(pi->hProcess, remote, dll, size, NULL))
        die("WriteProcessMemory");
    th = CreateRemoteThread(pi->hProcess, NULL, 0, (LPTHREAD_START_ROUTINE)load_library, remote, 0, NULL);
    if (!th) die("CreateRemoteThread");
    if (WaitForSingleObject(th, 10000) != WAIT_OBJECT_0)
        die("hook dll load timed out in target");
    GetExitCodeThread(th, &code);
    printf("watcher: injected, %s -> %08x\n", how, code);
    if (!code || code == STILL_ACTIVE) die("hook dll was refused or failed to load in target");
    CloseHandle(th);
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
    printf("watcher: pol.exe running (pid %lu)\n", pi->dwProcessId);
    CloseHandle(pi->hThread);
    CloseHandle(pi->hProcess);
    return 0;
}

static const char *utf8(const WCHAR *w)
{
    static char buf[MAX_PATH * 4];
    if (!WideCharToMultiByte(CP_UTF8, 0, w, -1, buf, sizeof buf, NULL, NULL)) lstrcpyA(buf, "(too long to show)");
    return buf;
}

static WCHAR *full_path(const WCHAR *path, const char *what)
{
    DWORD n = GetFullPathNameW(path, 0, NULL, NULL);
    WCHAR *full = n ? malloc(n * sizeof(WCHAR)) : NULL;
    if (!full || !GetFullPathNameW(path, n, full, NULL)) die(what);
    return full;
}

/* the app makes Windower's mapping and event before it runs this. Hook.dll throws on its install thread when the
   mapping's missing, so that's refused before pol.exe even starts */
static int windower(int argc, WCHAR **arg)
{
    WCHAR *hook, *pol, *polhook, *cmd, *cwd, *slash;
    char dll[MAX_PATH] = {0}, wait_s[16] = {0};
    STARTUPINFOW si = { sizeof si };
    PROCESS_INFORMATION pi;
    HANDLE settings, ready, th, waits[2];
    HMODULE kernelbase;
    FARPROC load_library = NULL;
    void *remote;
    DWORD code, n, size, wait_ms = 30000;
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
    printf("hook =%s\n", dll);
    printf("watcher: windower=%s\n", utf8(hook));
    n = lstrlenW(pol);
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
    /* Windower.exe sets it on itself for pol.exe to inherit, so same here */
    SetEnvironmentVariableW(L"__COMPAT_LAYER", L"HighDpiAware");
    errmode = SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX);
    SetErrorMode(errmode | SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX);
    if (!CreateProcessW(pol, cmd, NULL, NULL, FALSE, CREATE_SUSPENDED, NULL, slash ? cwd : NULL, &si, &pi))
        die("CreateProcess pol.exe");
    SetErrorMode(errmode);
    g_child = pi.hProcess;

    /* a signal left over from some earlier launch would let pol.exe resume before the hook's in */
    WaitForSingleObject(ready, 0);
    size = (lstrlenW(hook) + 1) * sizeof(WCHAR);
    remote = VirtualAllocEx(pi.hProcess, NULL, size, MEM_COMMIT, PAGE_READWRITE);
    if (!remote) die("VirtualAllocEx");
    if (!WriteProcessMemory(pi.hProcess, remote, hook, size, NULL)) die("WriteProcessMemory");
    th = CreateRemoteThread(pi.hProcess, NULL, 0,
        (LPTHREAD_START_ROUTINE)GetProcAddress(GetModuleHandleA("kernel32.dll"), "LoadLibraryW"), remote, 0, NULL);
    if (!th) die("CreateRemoteThread");
    /* Windower.exe waits as long as it takes here, and a cold start with antivirus poking at it can be slow */
    if (WaitForSingleObject(th, wait_ms) != WAIT_OBJECT_0) die("Windower's Hook.dll load timed out in target");
    GetExitCodeThread(th, &code);
    CloseHandle(th);
    VirtualFreeEx(pi.hProcess, remote, 0, MEM_RELEASE);
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
    WCHAR *pol, *dll, *cmd, *cwd, *slash;
    STARTUPINFOW si = { sizeof si };
    PROCESS_INFORMATION pi;
    UINT errmode;
    DWORD n;

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
    printf("hook =%s\n", utf8(dll));
    /* kiyaku=true skips the EULA, no /game means the hook hands back content code 1. FFXiEntry resolves the install
       paths from the cwd, so that's POL's folder */
    n = lstrlenW(pol);
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
    /* pol.exe inherits the error mode, so the same no-boxes rule as attach */
    errmode = SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX);
    SetErrorMode(errmode | SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX);
    if (!CreateProcessW(pol, cmd, NULL, NULL, FALSE, CREATE_SUSPENDED, NULL, slash ? cwd : NULL, &si, &pi))
        die("CreateProcess pol.exe");
    SetErrorMode(errmode);
    g_child = pi.hProcess;
    return hook_and_resume(&pi, dll, (lstrlenW(dll) + 1) * sizeof(WCHAR), errmode,
                           GetProcAddress(GetModuleHandleA("kernel32.dll"), "LoadLibraryW"), "LoadLibraryW");
}
