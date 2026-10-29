using System.Runtime.InteropServices;

namespace Provenance.Common.Credentials;

// current-user DPAPI, only this Windows account on this PC can open it
public static class Dpapi
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    public static byte[]? Protect(byte[] plain, byte[] entropy) => Run(plain, entropy, protect: true);

    public static byte[]? Unprotect(byte[] cipher, byte[] entropy) => Run(cipher, entropy, protect: false);

    private static byte[]? Run(byte[] input, byte[] entropy, bool protect)
    {
        var outBlob = new DataBlob();
        var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entHandle = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        try
        {
            var inBlob = new DataBlob { cbData = input.Length, pbData = inHandle.AddrOfPinnedObject() };
            var entBlob = new DataBlob { cbData = entropy.Length, pbData = entHandle.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref inBlob, null, ref entBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob);
            if (!ok || outBlob.pbData == IntPtr.Zero) return null;
            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            inHandle.Free();
            entHandle.Free();
        }
    }

    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr, ref DataBlob pOptionalEntropy,
                                                IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
                                                  IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
