using Provenance.Common.Win32;

namespace Provenance.Common.Install;

public static class MsiInstaller
{
    // INSTALLSTATE_DEFAULT
    public static bool IsInstalled(string productCode) => Msi.MsiQueryProductStateW(productCode) == 5;
}
