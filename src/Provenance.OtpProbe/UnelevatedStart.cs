using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Provenance.OtpProbe.Win32;

namespace Provenance.OtpProbe;

internal static class UnelevatedStart
{
    private const uint ProcessCreateProcess = 0x0080, ProcessQueryLimitedInformation = 0x1000;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint TokenDuplicate = 0x0002, MaximumAllowed = 0x02000000;
    private const int SecurityImpersonation = 2, TokenPrimary = 1;
    private static readonly IntPtr ParentProcessAttribute = 0x00020000;

    internal static IntPtr UnderExplorerParent(string commandLine, string directory)
    {
        using var explorer = FindExplorer();
        var parent = Kernel32.OpenProcess(ProcessCreateProcess, false, (uint)explorer.Id);
        if (parent == IntPtr.Zero) throw new Win32Exception();
        var size = IntPtr.Zero;
        Kernel32.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var list = Marshal.AllocHGlobal(size);
        var value = Marshal.AllocHGlobal(IntPtr.Size);
        var initialised = false;
        try
        {
            Marshal.WriteIntPtr(value, parent);
            if (!Kernel32.InitializeProcThreadAttributeList(list, 1, 0, ref size)) throw new Win32Exception();
            initialised = true;
            if (!Kernel32.UpdateProcThreadAttribute(list, 0, ParentProcessAttribute, value, IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
            var startup = new StartupInfoEx { Startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>() }, AttributeList = list };
            if (!Kernel32.CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, ExtendedStartupInfoPresent,
                                         IntPtr.Zero, directory, ref startup, out var information)) throw new Win32Exception();
            Kernel32.CloseHandle(information.Thread);
            return information.Process;
        }
        finally
        {
            if (initialised) Kernel32.DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
            Marshal.FreeHGlobal(value);
            Kernel32.CloseHandle(parent);
        }
    }

    internal static IntPtr WithExplorerToken(string commandLine, string directory)
    {
        using var explorer = FindExplorer();
        var process = Kernel32.OpenProcess(ProcessQueryLimitedInformation, false, (uint)explorer.Id);
        if (process == IntPtr.Zero) throw new Win32Exception();
        var token = IntPtr.Zero;
        var primary = IntPtr.Zero;
        try
        {
            if (!Advapi32.OpenProcessToken(process, TokenDuplicate, out token)) throw new Win32Exception();
            if (!Advapi32.DuplicateTokenEx(token, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primary)) throw new Win32Exception();
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
            if (!Advapi32.CreateProcessWithTokenW(primary, 0, null, new StringBuilder(commandLine), 0, IntPtr.Zero, directory,
                                                  ref startup, out var information)) throw new Win32Exception();
            Kernel32.CloseHandle(information.Thread);
            return information.Process;
        }
        finally
        {
            if (primary != IntPtr.Zero) Kernel32.CloseHandle(primary);
            if (token != IntPtr.Zero) Kernel32.CloseHandle(token);
            Kernel32.CloseHandle(process);
        }
    }

    private static Process FindExplorer()
    {
        using var me = Process.GetCurrentProcess();
        var candidates = Process.GetProcessesByName("explorer");
        var found = candidates.FirstOrDefault(p => p.SessionId == me.SessionId) ?? throw new InvalidOperationException("No explorer.exe in this session.");
        foreach (var other in candidates.Where(p => p != found)) other.Dispose();
        return found;
    }
}
