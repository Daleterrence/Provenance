using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Provenance.Wpf.Win32;

// Only the calls CloseRequest makes, Common's own declarations of them aren't visible from here
internal static partial class Kernel32
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeWaitHandle CreateEventW(ref SecurityAttributes attributes, bool manualReset, bool initialState, string name);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr memory);
}

[StructLayout(LayoutKind.Sequential)]
internal struct SecurityAttributes
{
    public int Length;
    public IntPtr Descriptor;
    public int InheritHandle;
}
