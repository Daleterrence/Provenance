using System.Text;
using Provenance.Common.IO;
using Provenance.Common.Security;
using Provenance.Common.Win32;

namespace Provenance.Common.Install;

public sealed class MsiDatabase : IDisposable
{
    private readonly IntPtr _db;
    public string Location { get; }

    public MsiDatabase(string path)
    {
        Location = Path.GetFullPath(path);
        if (Msi.MsiOpenDatabaseW(Location, IntPtr.Zero /* MSIDBOPEN_READONLY */, out _db) != 0)
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a readable installer package.");
    }

    public void Dispose() => Msi.MsiCloseHandle(_db);

    public List<string[]> Query(string sql)
    {
        var rows = new List<string[]>();
        uint r = Msi.MsiDatabaseOpenViewW(_db, sql, out var view);
        if (r == 1615 /* ERROR_BAD_QUERY_SYNTAX: no such table in this package */) return rows;
        if (r != 0) throw Unreadable(r);
        try
        {
            if ((r = Msi.MsiViewExecute(view, IntPtr.Zero)) != 0) throw Unreadable(r);
            while ((r = Msi.MsiViewFetch(view, out var rec)) != 259 /* ERROR_NO_MORE_ITEMS */)
            {
                if (r != 0) throw Unreadable(r);
                try
                {
                    var row = new string[Msi.MsiRecordGetFieldCount(rec)];
                    for (int i = 0; i < row.Length; i++) row[i] = RecordString(rec, (uint)i + 1);
                    rows.Add(row);
                }
                finally { Msi.MsiCloseHandle(rec); }
            }
        }
        finally { Msi.MsiCloseHandle(view); }
        return rows;
    }

    private static InvalidDataException Unreadable(uint error) =>
        new($"The package could not be read (error {error}).");

    internal static string LongName(string s)
    {
        int colon = s.IndexOf(':');
        if (colon >= 0) s = s[..colon];
        int bar = s.IndexOf('|');
        return bar >= 0 ? s[(bar + 1)..] : s;
    }

    internal bool CopyStream(string sql, Stream dest)
    {
        if (Msi.MsiDatabaseOpenViewW(_db, sql, out var view) != 0) return false;
        try
        {
            if (Msi.MsiViewExecute(view, IntPtr.Zero) != 0 || Msi.MsiViewFetch(view, out var rec) != 0) return false;
            try
            {
                var buf = new byte[1 << 16];
                while (true)
                {
                    uint n = (uint)buf.Length;
                    if (Msi.MsiRecordReadStream(rec, 1, buf, ref n) != 0) throw Shown.Mark(new IOException("Provenance couldn't read part of an installer package. Try again."));
                    if (n == 0) return true;
                    dest.Write(buf, 0, (int)n);
                }
            }
            finally { Msi.MsiCloseHandle(rec); }
        }
        finally { Msi.MsiCloseHandle(view); }
    }

    internal string ExtractStream(string name, List<string> temps)
    {
        var path = Path.Combine(Path.GetDirectoryName(Location)!, name + ".provenance");
        temps.Add(path);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        if (!CopyStream($"SELECT `Data` FROM `_Streams` WHERE `Name`='{name.Replace("'", "")}'", fs))
            throw new InvalidDataException($"The package has no embedded cabinet {name}.");
        return path;
    }

    public static void WithoutEmbeddedCabinets(string sourceMsi, string targetMsi)
    {
        List<string> embedded;
        using (var src = new MsiDatabase(sourceMsi))
            embedded = src.Query("SELECT `Cabinet` FROM `Media`").Select(r => r[0]).Where(c => c.StartsWith('#')).Select(c => c[1..])
                          .Distinct(StringComparer.Ordinal).ToList();
        var target = Path.GetFullPath(targetMsi);
        var folder = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(folder);
        // a copy left by a crash is a whole installer, so it goes
        foreach (var old in Directory.EnumerateFiles(folder, Path.GetFileName(target) + ".*.tmp").Where(f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)))
            FileDelete.Try(old);
        AtomicFile.Build(target, tmp =>
        {
            File.Copy(sourceMsi, tmp);
            AtomicFile.ClearReadOnly(tmp);
            Check(Msi.MsiOpenDatabaseW(tmp, (IntPtr)1 /* MSIDBOPEN_TRANSACT */, out var db), "open");
            try
            {
                foreach (var name in embedded)
                {
                    Check(Msi.MsiDatabaseOpenViewW(db, "SELECT `Name`,`Data` FROM `_Streams` WHERE `Name`=?", out var view), "view");
                    var arg = Msi.MsiCreateRecord(1);
                    try
                    {
                        Check(Msi.MsiRecordSetStringW(arg, 1, name), "name");
                        Check(Msi.MsiViewExecute(view, arg), "query");
                        uint r = Msi.MsiViewFetch(view, out var rec);
                        if (r == 259 /* ERROR_NO_MORE_ITEMS */) throw new InvalidDataException($"The package has no embedded cabinet {name}.");
                        Check(r, "fetch");
                        try { Check(Msi.MsiViewModify(view, 6 /* MSIMODIFY_DELETE */, rec), "delete " + name); }
                        finally { Msi.MsiCloseHandle(rec); }
                    }
                    finally { Msi.MsiCloseHandle(arg); Msi.MsiCloseHandle(view); }
                }
                Check(Msi.MsiDatabaseCommit(db), "commit");
            }
            finally { Msi.MsiCloseHandle(db); }
        });

        static void Check(uint r, string step)
        {
            if (r != 0) throw new InvalidDataException($"The package's cabinets could not be removed ({step}: error {r}).");
        }
    }

    private static string RecordString(IntPtr rec, uint field)
    {
        uint len = 0;
        var sb = new StringBuilder(1);
        uint r = Msi.MsiRecordGetStringW(rec, field, sb, ref len);
        if (r == 234 /* ERROR_MORE_DATA */)
        {
            sb = new StringBuilder((int)++len);
            r = Msi.MsiRecordGetStringW(rec, field, sb, ref len);
        }
        return r == 0 ? sb.ToString() : "";
    }
}
