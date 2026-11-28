using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Provenance.Common.Win32;

namespace Provenance.Common.Otp;

internal sealed class ShellToken : IDisposable
{
    private const uint ProcessQueryLimitedInformation = 0x1000, TokenDuplicate = 0x0002, MaximumAllowed = 0x02000000, CreateUnicodeEnvironment = 0x0400;
    private const int SecurityImpersonation = 2, TokenPrimary = 1;

    private readonly SafeAccessTokenHandle _token;

    private ShellToken(SafeAccessTokenHandle token, SecurityIdentifier user)
    {
        _token = token;
        User = user;
    }

    internal SecurityIdentifier User { get; }

    internal static ShellToken Open()
    {
        using var me = Process.GetCurrentProcess();
        var candidates = Process.GetProcessesByName("explorer");
        try
        {
            foreach (var candidate in candidates)
                if (candidate.SessionId == me.SessionId && TryOpen(candidate.Id) is { } shell) return shell;
        }
        finally
        {
            foreach (var candidate in candidates) candidate.Dispose();
        }
        throw new InvalidOperationException("There is no explorer.exe in this session to take the user's token from.");
    }

    internal LaunchedProcess Start(string commandLine, string directory)
    {
        if (!Userenv.CreateEnvironmentBlock(out var environment, _token, false)) throw new Win32Exception();
        try
        {
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
            if (!Advapi32.CreateProcessWithTokenW(_token, 0, null, new StringBuilder(commandLine), CreateUnicodeEnvironment, environment, directory,
                                                  ref startup, out var information)) throw new Win32Exception();
            Kernel32.CloseHandle(information.Thread);
            return new LaunchedProcess(information.Process, information.ProcessId);
        }
        finally { Userenv.DestroyEnvironmentBlock(environment); }
    }

    public void Dispose() => _token.Dispose();

    private static ShellToken? TryOpen(int processId)
    {
        var process = Kernel32.OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            if (!IsWindowsExplorer(process)) return null;
            if (!Advapi32.OpenProcessToken(process, TokenDuplicate, out var token)) return null;
            using (token)
            {
                if (!Advapi32.DuplicateTokenEx(token, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var primary)) return null;
                try
                {
                    using var identity = new WindowsIdentity(primary.DangerousGetHandle());
                    return identity.User is { } user ? new ShellToken(primary, user) : Discard(primary);
                }
                catch
                {
                    primary.Dispose();
                    throw;
                }
            }
        }
        finally { Kernel32.CloseHandle(process); }
    }

    private static ShellToken? Discard(SafeAccessTokenHandle token)
    {
        token.Dispose();
        return null;
    }

    private static bool IsWindowsExplorer(IntPtr process)
    {
        var path = new StringBuilder(1024);
        var size = (uint)path.Capacity;
        return Kernel32.QueryFullProcessImageNameW(process, 0, path, ref size)
            && string.Equals(path.ToString(), AppPaths.ExplorerExe, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class LaunchedProcess(IntPtr handle, int id) : IDisposable
{
    private IntPtr _handle = handle;

    internal int Id { get; } = id;

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        Kernel32.CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }
}
