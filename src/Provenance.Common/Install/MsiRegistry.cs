using Microsoft.Win32;

namespace Provenance.Common.Install;

internal static class MsiRegistry
{
    public const string SharedDlls = @"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs";

    public static RegistryKey Hive(MsiPackage.Sandbox? sandbox, bool user = false) => sandbox != null
        ? Registry.CurrentUser.CreateSubKey(sandbox.RegistryKey + (user ? @"\HKCU" : @"\HKLM"), true)
        : RegistryKey.OpenBaseKey(user ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry32);
}
