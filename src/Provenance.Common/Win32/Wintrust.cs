using System.Runtime.InteropServices;

namespace Provenance.Common.Win32;

internal static partial class Wintrust
{
    [DllImport("wintrust.dll", ExactSpelling = true)]
    internal static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    internal static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint cert);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    internal static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provData, uint signer, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    internal static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WinTrustFileInfo
{
    public uint cbStruct;
    [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
    public IntPtr hFile;
    public IntPtr pgKnownSubject;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WinTrustData
{
    public uint cbStruct;
    public IntPtr pPolicyCallbackData;
    public IntPtr pSIPClientData;
    public uint dwUIChoice;
    public uint fdwRevocationChecks;
    public uint dwUnionChoice;
    public IntPtr pFile;
    public uint dwStateAction;
    public IntPtr hWVTStateData;
    public IntPtr pwszURLReference;
    public uint dwProvFlags;
    public uint dwUIContext;
    public IntPtr pSignatureSettings;
}
