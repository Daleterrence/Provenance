using System.Runtime.InteropServices;

namespace Provenance.Common.Game.Windower;

// Windower sorts addons with .NET Framework's culture compare, which is Windows' own (NLS). .NET now uses ICU, which
// puts co-op before cob, so this asks Windows directly
internal static class NlsSort
{
    private const uint LinguisticCasing = 0x08000000;

    public static readonly IComparer<string> UserDefault = Comparer<string>.Create((a, b) =>
    {
        int r = CompareStringEx(null, LinguisticCasing, a, a.Length, b, b.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return r == 0 ? string.CompareOrdinal(a, b) : r - 2;
    });

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CompareStringEx(string? locale, uint flags, string a, int aLength, string b, int bLength,
                                              IntPtr version, IntPtr reserved, IntPtr param);
}
