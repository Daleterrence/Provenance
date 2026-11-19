using Provenance.Common.Win32;

namespace Provenance.Common.Game.Windower;

// Windower sorts addons with .NET Framework's culture compare (NLS), .NET's ICU puts co-op before cob
internal static class NlsSort
{
    private const uint LinguisticCasing = 0x08000000;

    public static readonly IComparer<string> UserDefault = Comparer<string>.Create((a, b) =>
    {
        int r = Kernel32.CompareStringEx(null, LinguisticCasing, a, a.Length, b, b.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return r == 0 ? string.CompareOrdinal(a, b) : r - 2;
    });
}
