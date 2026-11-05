using System.Runtime.InteropServices;

namespace Provenance.Common.Win32;

internal static partial class Oleaut32
{
    [DllImport("oleaut32.dll", CharSet = CharSet.Unicode)]
    internal static extern int LoadTypeLibEx(string file, int regKind, [MarshalAs(UnmanagedType.Interface)] out object? typeLib);
}
