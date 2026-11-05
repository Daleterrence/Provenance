using System.Runtime.InteropServices;

namespace Provenance.Common.Win32;

internal static partial class Crypt32
{
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr, ref DataBlob pOptionalEntropy,
                                                 IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
                                                   IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);
}

internal struct DataBlob
{
    public int cbData;
    public IntPtr pbData;
}
