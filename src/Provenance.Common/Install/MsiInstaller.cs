using System.Runtime.InteropServices;

namespace Provenance.Common.Install;

public static class MsiInstaller
{
    // INSTALLSTATE_DEFAULT
    public static bool IsInstalled(string productCode) => MsiQueryProductStateW(productCode) == 5;

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiQueryProductStateW(string productCode);
}
