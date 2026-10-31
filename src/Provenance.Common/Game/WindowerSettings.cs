using System.Text;
using System.Xml;
using System.Xml.Linq;
using Provenance.Common.Locks;

namespace Provenance.Common.Game;

public static class WindowerSettings
{
    private const string CopySuffix = " (Provenance)";
    private const string DefaultCopy = "(Provenance)";

    // fast login installs Windower's POL Proxy, which replaces my sign-in, and the other two change pol.exe too
    public static readonly IReadOnlyList<string> OffOptions = ["enablefastlogin", "increaseaccountlimit", "preventbackgroundthrottling"];

    public static string CopyName(string profile) => profile.Length == 0 ? DefaultCopy : profile + CopySuffix;

    public static bool IsCopy(string name) => name == DefaultCopy || name.EndsWith(CopySuffix, StringComparison.Ordinal);

    public static IReadOnlyList<string> Profiles(string file)
    {
        if (!File.Exists(file)) return [];
        return Load(file).Root?.Elements("profile")
                   .Select(p => (string?)p.Attribute("name") ?? "").Where(n => !IsCopy(n)).Distinct().ToList()
               ?? (IReadOnlyList<string>)[];
    }

    // Windower only saves options that differ from its defaults, so it drops a copy's falses and missing means off
    private static bool IsOff(XElement profile) =>
        OffOptions.All(o => profile.Element(o) is not { } e || e.Value == "false");

    private static XElement WithoutOffOptions(XElement profile)
    {
        var c = new XElement(profile);
        foreach (var o in OffOptions) c.Element(o)?.Remove();
        return c;
    }

    public sealed record CopyResult(string Name, string Source, bool SourceFound, bool Wrote);

    // Windower has no switches for these and rewrites settings.xml on exit. Only a separate profile sticks, so yes, a
    // whole profile copy just to turn three options off
    public static CopyResult PrepareCopy(string file, string? profile)
    {
        var source = profile ?? "";
        if (source == DefaultCopy) source = "";
        else if (source.EndsWith(CopySuffix, StringComparison.Ordinal)) source = source[..^CopySuffix.Length];
        var name = CopyName(source);
        var doc = File.Exists(file) ? Load(file) : new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("settings"));
        var root = doc.Root ?? throw new InvalidDataException("Windower's settings.xml has no settings element.");
        var src = root.Elements("profile").FirstOrDefault(p => ((string?)p.Attribute("name") ?? "") == source);
        var copy = src is null ? new XElement("profile") : new XElement(src);
        copy.SetAttributeValue("name", name);
        foreach (var option in OffOptions) copy.SetElementValue(option, "false");
        var old = root.Elements("profile").Where(p => (string?)p.Attribute("name") == name).ToList();
        if (old.Count == 1 && IsOff(old[0]) && XNode.DeepEquals(WithoutOffOptions(old[0]), WithoutOffOptions(copy)))
            return new CopyResult(name, source, src is not null, false);
        old.ForEach(o => o.Remove());
        if (src is not null) src.AddAfterSelf(copy);
        else root.Add(copy);
        // if the lock doesn't come it writes anyway, the move swaps settings.xml whole
        IDisposable? gate = null;
        try
        {
            try { gate = AdminLock.TryAcquire("windower-settings", TimeSpan.FromSeconds(5)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            var tmp = $"{file}.provenance.{Guid.NewGuid():N}.tmp";
            var ws = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, IndentChars = "  " };
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var w = XmlWriter.Create(fs, ws))
                    doc.Save(w);
                File.Move(tmp, file, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        finally { gate?.Dispose(); }
        return new CopyResult(name, source, src is not null, true);
    }

    // no DTDs, so no entity tricks in a file other programs can edit
    private static XDocument Load(string file)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true,
        };
        using var r = XmlReader.Create(file, settings);
        return XDocument.Load(r);
    }
}
