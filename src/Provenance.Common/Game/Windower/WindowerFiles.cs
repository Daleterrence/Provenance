using System.IO.Compression;
using System.Reflection;
using Provenance.Common.IO;
using Provenance.Common.Security;

namespace Provenance.Common.Game.Windower;

internal static class WindowerFiles
{
    public static Version FileVersion(string file) =>
        File.Exists(file) ? PeFile.ReadFileVersion(file) ?? new Version(0, 0, 0, 0) : new Version();

    // Windower compares (and builds its User-Agent from) its assembly version. Only the metadata's read, nothing's loaded
    public static Version LauncherVersion(string exe)
    {
        if (!File.Exists(exe)) return new Version();
        try { return AssemblyName.GetAssemblyName(exe).Version ?? FileVersion(exe); }
        catch (Exception e) when (e is BadImageFormatException or FileLoadException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return FileVersion(exe);
        }
    }

    // a captive portal's 200 shouldn't end up as Hook.dll
    public static bool LooksLikePe(string file)
    {
        try
        {
            using var f = File.OpenRead(file);
            return f.ReadByte() == 'M' && f.ReadByte() == 'Z';
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public static bool IsPlainName(string name) => FileNames.IsPlainFileName(name) && name.Trim() == name;

    // a OneDrive file has the reparse point attribute too, only a junction or symlink counts
    public static bool IsLinkOrFolder(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.Directory) || LinkCheck.IsRedirect(path); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    public static bool IsZip(string file)
    {
        try
        {
            using var z = ZipFile.OpenRead(file);
            _ = z.Entries.Count;
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { return false; }
    }
}
