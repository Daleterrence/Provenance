using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32;
using Provenance.Common.Security;
using Provenance.Common.Win32;

namespace Provenance.Common.Install;

internal static class ComRegistration
{
    // RegisterClassInfo and RegisterProgIdInfo, minus the descriptor value that'd send COM off to Windows Installer
    public static (int Classes, int ProgIds) RegisterClasses(MsiDatabase db, Func<string, bool> on, Func<string, string> serverOf,
                                                             MsiPackage.Sandbox? sandbox, bool keepExisting)
    {
        var progIds = db.Query("SELECT `ProgId`,`ProgId_Parent`,`Class_`,`Description` FROM `ProgId`");
        int classes = 0, progIdsWritten = 0;
        using var hklm = MsiRegistry.Hive(sandbox);
        foreach (var r in db.Query("SELECT `CLSID`,`Context`,`Component_`,`ProgId_Default`,`Description` FROM `Class`"))
        {
            if (!on(r[2])) continue;
            var server = serverOf(r[2]);   // Supported() checked it's a file
            if (keepExisting && ClassIntact(hklm, r[0], r[1], server)) continue;
            using var cls = hklm.CreateSubKey($@"SOFTWARE\Classes\CLSID\{r[0]}", true);
            if (r[4].Length > 0) cls.SetValue("", r[4]);
            using (var ctx = cls.CreateSubKey(r[1], true)) ctx.SetValue("", server);
            if (r[3].Length > 0)
            {
                using (var p = cls.CreateSubKey("ProgID", true)) p.SetValue("", r[3]);
                if (progIds.FirstOrDefault(x => x[1] == r[3]) is { } vi)
                    using (var v = cls.CreateSubKey("VersionIndependentProgID", true)) v.SetValue("", vi[0]);
            }
            classes++;
        }
        foreach (var p in progIds)
        {
            var clsid = p[2].Length > 0 ? p[2] : progIds.FirstOrDefault(x => x[0] == p[1])?[2] ?? "";
            if (keepExisting)
            {
                using var had = hklm.OpenSubKey($@"SOFTWARE\Classes\{p[0]}");
                using var hadClsid = had?.OpenSubKey("CLSID");
                if (had is not null && (clsid.Length == 0 || string.Equals(hadClsid?.GetValue("") as string, clsid, StringComparison.OrdinalIgnoreCase)))
                    continue;
            }
            using var k = hklm.CreateSubKey($@"SOFTWARE\Classes\{p[0]}", true);
            if (p[3].Length > 0) k.SetValue("", p[3]);
            if (clsid.Length > 0) using (var c = k.CreateSubKey("CLSID", true)) c.SetValue("", clsid);
            if (p[1].Length > 0) using (var c = k.CreateSubKey("CurVer", true)) c.SetValue("", p[1]);
            progIdsWritten++;
        }
        return (classes, progIdsWritten);
    }

    public static int RegisterTypeLibs(MsiDatabase db, Func<string, bool> on, Func<string, string> libOf, MsiPackage.Sandbox? sandbox,
                                       bool keepExisting)
    {
        int typeLibs = 0;
        foreach (var r in db.Query("SELECT `LibID`,`Component_` FROM `TypeLib`"))
        {
            if (!on(r[1])) continue;
            var lib = libOf(r[1]);
            if (sandbox != null) { if (!keepExisting) typeLibs++; continue; }   // a sandbox can't register one, or check it
            if (keepExisting && TypeLibIntact(lib)) continue;
            Marshal.ThrowExceptionForHR(Oleaut32.LoadTypeLibEx(lib, 1 /* REGKIND_REGISTER */, out var tl));
            if (tl != null) Marshal.ReleaseComObject(tl);
            // REGKIND_REGISTER says S_OK even when the registry write was refused, so a repair looks for itself
            if (keepExisting && !TypeLibIntact(lib))
                throw Shown.Mark(new InvalidOperationException(
                    $"Windows didn't register {Path.GetFileName(lib)}, which the game needs. Run the repair again."));
            typeLibs++;
        }
        return typeLibs;
    }

    // the library's own id and version, read from the file without registering it
    public static TYPELIBATTR? ReadTypeLib(string path)
    {
        if (Oleaut32.LoadTypeLibEx(path, 2 /* REGKIND_NONE */, out var tl) != 0 || tl is not ITypeLib typeLib) return null;
        try
        {
            typeLib.GetLibAttr(out var pa);
            var a = Marshal.PtrToStructure<TYPELIBATTR>(pa);
            typeLib.ReleaseTLibAttr(pa);
            return a;
        }
        finally { Marshal.ReleaseComObject(typeLib); }
    }

    private static bool ClassIntact(RegistryKey hklm, string clsid, string context, string server)
    {
        using var k = hklm.OpenSubKey($@"SOFTWARE\Classes\CLSID\{clsid}\{context}");
        return k?.GetValue("") is string named && SamePath(Unquote(named), server) && File.Exists(Unquote(named));
    }

    private static bool TypeLibIntact(string path)
    {
        if (ReadTypeLib(path) is not { } a) return false;
        using var hklm = MsiRegistry.Hive(null);
        using var k = hklm.OpenSubKey($@"SOFTWARE\Classes\TypeLib\{a.guid:B}\{a.wMajorVerNum:x}.{a.wMinorVerNum:x}\{a.lcid:x}\win32");
        return k?.GetValue("") is string named && SamePath(Unquote(named), path) && File.Exists(Unquote(named));
    }

    private static string Unquote(string s) => s.Trim().Trim('"');

    private static bool SamePath(string a, string b)
    {
        try { return PathInside.Same(a, b); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
