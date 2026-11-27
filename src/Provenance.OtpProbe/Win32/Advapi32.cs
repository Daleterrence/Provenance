using System.Runtime.InteropServices;
using System.Text;

namespace Provenance.OtpProbe.Win32;

internal static class Advapi32
{
    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int impersonationLevel, int tokenType, out IntPtr duplicate);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string? application, StringBuilder commandLine, uint flags,
                                                        IntPtr environment, string? directory, ref StartupInfo startup,
                                                        out ProcessInformation information);
}
