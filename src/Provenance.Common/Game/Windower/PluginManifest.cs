using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Provenance.Common.Game.Windower;

internal sealed partial class PluginManifest
{
    public Version? LauncherVersion { get; private init; }
    public List<(string Name, Version? Version, string Description, bool HasResources, bool Required, bool Hidden)> Plugins { get; } = [];

    // missing or not XML is no manifest, like Windower
    public static PluginManifest? Load(string windowerDir)
    {
        var path = Path.Combine(windowerDir, "updates", "manifest.xml");
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var r = XmlReader.Create(fs, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreWhitespace = true });
            return Parse(XDocument.Load(r));
        }
        catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException) { return null; }
    }

    // Windower throws on a flag it can't read, here it's just off
    internal static PluginManifest Parse(XDocument doc)
    {
        var root = doc.Root!;
        var m = new PluginManifest { LauncherVersion = Version.TryParse((string?)root.Element("launcher")?.Element("version"), out var v) ? v : null };
        foreach (var p in root.Element("plugins")?.Elements("plugin") ?? [])
        {
            var name = ((string?)p.Element("name"))?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            bool Flag(string n) => ((string?)p.Element(n))?.Trim().ToLowerInvariant() is "true" or "1";
            m.Plugins.Add((name, Version.TryParse((string?)p.Element("version"), out var pv) ? pv : null,
                           Spaces().Replace((string?)p.Element("description") ?? "", " ").Trim(),
                           Flag("resources"), Flag("required"), Flag("hidden")));
        }
        return m;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
