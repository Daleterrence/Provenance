/* pol_launch.exe [pol.exe] [hook dll]: pol.exe starts suspended, gets the hook, then resumes. --attach <pid> [hook dll]
   is for a pol.exe Windower or Ashita started, which it never ends */
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>

/* the app reads this output and the exit codes, so they don't change */
static HANDLE g_child;   /* the suspended pol.exe, killed on any later failure */
static void die(const char *m)
{
    fprintf(stderr, "pol_launch: %s (err=%lu)\n", m, GetLastError());
    if (g_child) TerminateProcess(g_child, 1);
    exit(1);
}

static int get_pol_path(char *out, int cap)
{
    static const char *const keys[] = { "SOFTWARE\\WOW6432Node\\PlayOnlineUS\\InstallFolder",
                                        "SOFTWARE\\WOW6432Node\\PlayOnlineEU\\InstallFolder",
                                        "SOFTWARE\\WOW6432Node\\PlayOnline\\InstallFolder" };
    HKEY k;
    DWORD n = 0, t;
    int i, found = 0;
    for (i = 0; i < 3 && !found; i++) {
        if (RegOpenKeyExA(HKEY_LOCAL_MACHINE, keys[i], 0, KEY_READ, &k)) continue;
        n = cap;
        found = !RegQueryValueExA(k, "1000", NULL, &t, (BYTE *)out, &n) && t == REG_SZ;
        RegCloseKey(k);
    }
    if (!found) return 0;
    if (n < 5 || n > (DWORD)cap) return 0;
    if (out[n - 2] != '\\') { out[n - 1] = '\\'; out[n] = 0; }
    lstrcatA(out, "pol.exe");
    return 1;
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

static int attach(DWORD pid, const char *dll)
{
    HMODULE k32 = GetModuleHandleA("kernel32.dll");
    FARPROC set_error_mode = GetProcAddress(k32, "SetErrorMode");
    FARPROC load_library = GetProcAddress(k32, "LoadLibraryA");
    HANDLE p;
    void *remote;
    DWORD old, code;

    if (GetFileAttributesA(dll) == INVALID_FILE_ATTRIBUTES) die("pol_hook.dll not found");
    p = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION |
                    PROCESS_VM_WRITE | PROCESS_VM_READ | SYNCHRONIZE, FALSE, pid);
    if (!p) die("cannot open pol.exe to attach");
    /* no critical-error boxes while the hook loads: a Smart App Control refusal pops a "Bad Image" box that holds the
       load until someone clicks OK. Great feature :D */
    old = remote_call(p, set_error_mode, (void *)(UINT_PTR)(SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX),
                      "setting the error mode timed out");
    remote = VirtualAllocEx(p, NULL, MAX_PATH, MEM_COMMIT, PAGE_READWRITE);
    if (!remote) die("VirtualAllocEx");
    if (!WriteProcessMemory(p, remote, dll, lstrlenA(dll) + 1, NULL)) die("WriteProcessMemory");
    code = remote_call(p, load_library, remote, "hook dll load timed out in target");
    remote_call(p, set_error_mode, (void *)(UINT_PTR)old, "restoring the error mode timed out");
    VirtualFreeEx(p, remote, 0, MEM_RELEASE);
    CloseHandle(p);
    printf("pol_launch: attach, LoadLibraryA -> %08lx\n", code);
    if (!code) die("hook dll was refused or failed to load in target");
    printf("pol_launch: attached to pol.exe (pid %lu)\n", pid);
    return 0;
}

int main(int argc, char **argv)
{
    char pol[MAX_PATH] = {0}, dll[MAX_PATH] = {0}, cmd[MAX_PATH + 64], cwd[MAX_PATH];
    STARTUPINFOA si = { sizeof si };
    PROCESS_INFORMATION pi;
    void *remote;
    HANDLE th;
    DWORD code;
    UINT errmode;
    char noresume[4] = {0};
    FARPROC set_error_mode = GetProcAddress(GetModuleHandleA("kernel32.dll"), "SetErrorMode");

    int attaching = argc > 2 && lstrcmpA(argv[1], "--attach") == 0;
    int dllarg = attaching ? 3 : 2;

    if (attaching) lstrcpynA(pol, "(attach)", MAX_PATH);
    else if (argc > 1) lstrcpynA(pol, argv[1], MAX_PATH);
    else if (!get_pol_path(pol, MAX_PATH - 16)) die("cannot resolve pol.exe (pass path as arg1)");
    if (argc > dllarg) lstrcpynA(dll, argv[dllarg], MAX_PATH);
    else {
        char *slash;
        GetModuleFileNameA(NULL, dll, MAX_PATH);
        slash = dll + lstrlenA(dll);
        while (slash > dll && *slash != '\\' && *slash != '/') slash--;
        if (*slash == '\\' || *slash == '/') slash++;
        lstrcpyA(slash, "pol_hook.dll");
        if (GetFileAttributesA(dll) == INVALID_FILE_ATTRIBUTES)
            lstrcpyA(dll, "pol_hook.dll");
    }
    if (attaching) {
        DWORD pid = (DWORD)strtoul(argv[2], NULL, 10);
        if (!pid) die("--attach needs a pid");
        return attach(pid, dll);
    }
    if (GetFileAttributesA(pol) == INVALID_FILE_ATTRIBUTES) die("pol.exe not found");
    if (GetFileAttributesA(dll) == INVALID_FILE_ATTRIBUTES) die("pol_hook.dll not found");

    printf("pol_launch: host=%s\nhook =%s\n", pol, dll);
    /* kiyaku=true skips the EULA, no /game means the hook hands back content code 1. FFXiEntry resolves the install
       paths from the cwd, so that's POL's folder */
    lstrcpynA(cwd, pol, MAX_PATH);
    for (char *e = cwd + lstrlenA(cwd); e > cwd && *e != '\\'; e--) *e = 0;
    if (cwd[lstrlenA(cwd) - 1] == '\\') cwd[lstrlenA(cwd) - 1] = 0;
    printf("pol_launch: cwd=%s\n", cwd);
    wsprintfA(cmd, "\"%s\" kiyaku=true", pol);
    /* pol.exe inherits the error mode, so the same no-boxes rule as attach */
    errmode = SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX);
    SetErrorMode(errmode | SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX);
    if (!CreateProcessA(pol, cmd, NULL, NULL, FALSE, CREATE_SUSPENDED, NULL, cwd, &si, &pi))
        die("CreateProcess pol.exe");
    SetErrorMode(errmode);
    g_child = pi.hProcess;

    remote = VirtualAllocEx(pi.hProcess, NULL, MAX_PATH, MEM_COMMIT, PAGE_READWRITE);
    if (!remote) die("VirtualAllocEx");
    if (!WriteProcessMemory(pi.hProcess, remote, dll, lstrlenA(dll) + 1, NULL))
        die("WriteProcessMemory");
    /* it's an ANSI path, so LoadLibraryA */
    th = CreateRemoteThread(pi.hProcess, NULL, 0,
        (LPTHREAD_START_ROUTINE)GetProcAddress(GetModuleHandleA("kernel32.dll"), "LoadLibraryA"),
        remote, 0, NULL);
    if (!th) die("CreateRemoteThread");
    if (WaitForSingleObject(th, 10000) != WAIT_OBJECT_0)
        die("hook dll load timed out in target");
    GetExitCodeThread(th, &code);
    printf("pol_launch: injected, LoadLibraryA -> %08x\n", code);
    if (!code || code == STILL_ACTIVE) die("hook dll was refused or failed to load in target");
    CloseHandle(th);
    remote_call(pi.hProcess, set_error_mode, (void *)(UINT_PTR)errmode, "restoring the error mode timed out");
    /* POL_LAUNCH_NORESUME=1 loads the hook, then ends pol.exe unresumed (checks the injection without starting POL) */
    if (GetEnvironmentVariableA("POL_LAUNCH_NORESUME", noresume, sizeof noresume) && noresume[0] == '1') {
        printf("pol_launch: hook loaded; POL_LAUNCH_NORESUME set, pol.exe ended unresumed\n");
        TerminateProcess(pi.hProcess, 0);
        return 0;
    }
    ResumeThread(pi.hThread);
    g_child = NULL;
    printf("pol_launch: pol.exe running (pid %lu)\n", pi.dwProcessId);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return 0;
}
