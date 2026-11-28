using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Provenance.Common.Win32;

internal static class Userenv
{
    [DllImport("userenv.dll", SetLastError = true)]
    internal static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    internal static extern bool DestroyEnvironmentBlock(IntPtr environment);
}
