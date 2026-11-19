using System.Xml.Linq;
using Provenance.Common.IO;

namespace Provenance.Common.Game.Windower;

public sealed record WindowerPlugin(string Name, Version? Version, string Description, bool Resources, bool Required, bool Hidden);

public sealed class WindowerManifest
{
    public static WindowerManifest Empty { get; } = new(new Version(), new Version(), []);

    private WindowerManifest(Version launcher, Version hook, IReadOnlyList<WindowerPlugin> plugins)
    {
        LauncherVersion = launcher;
        HookVersion = hook;
        Plugins = plugins;
    }

    public Version LauncherVersion { get; }
    public Version HookVersion { get; }
    public IReadOnlyList<WindowerPlugin> Plugins { get; }

    public static string CachePath(string windowerDir) => Path.Combine(windowerDir, "updates", "manifest.xml");

    public static WindowerManifest? LoadCached(string windowerDir) => Load(CachePath(windowerDir));

    public static WindowerManifest? Load(string file) => TextFiles.LoadXml(file) is { } doc ? Parse(doc) : null;

    // a plugin without a name is dropped, Windower would fall over on it
    public static WindowerManifest Parse(XDocument doc)
    {
        var root = doc.Root;
        if (root is null) return Empty;
        var plugins = new List<WindowerPlugin>();
        foreach (var p in root.Element("plugins")?.Elements("plugin") ?? [])
        {
            var name = ((string?)p.Element("name"))?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            plugins.Add(new WindowerPlugin(name, ParseVersion((string?)p.Element("version")),
                WindowerText.Spaces((string?)p.Element("description") ?? ""),
                Flag(p.Element("resources")), Flag(p.Element("required")), Flag(p.Element("hidden"))));
        }
        return new WindowerManifest(ParseVersion((string?)root.Element("launcher")?.Element("version")) ?? new Version(),
                                    ParseVersion((string?)root.Element("hook")?.Element("version")) ?? new Version(), plugins);
    }

    public WindowerPlugin? Find(string name) => WindowerText.FindByName(Plugins, p => p.Name, name);

    public static IReadOnlyList<string> ParseRes(XDocument doc) => doc.Root?.Elements().Select(e => e.Value).ToList() ?? [];

    public static Version? ParseVersion(string? text) => Version.TryParse(text, out var v) ? v : null;

    // true/false/1/0 in any case like XmlConvert. Anything else is false here, Windower crashes on it every start
    private static bool Flag(XElement? e) => e?.Value.Trim().ToLowerInvariant() is "true" or "1";
}
