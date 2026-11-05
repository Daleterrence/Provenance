using System.Runtime.InteropServices;

namespace Provenance.Wpf.Win32;

internal static partial class Advapi32
{
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, IntPtr size);
}
