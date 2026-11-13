using Provenance.Common.Game.ThirdParty;

namespace Provenance.Common.Game.Ashita;

public static class AshitaDocs
{
    // docs\<name>\ with a readme inside, <name> being the addon's or plugin's in any case (docs\Packetflow for
    // PacketFlow.dll)
    public static IReadOnlyDictionary<string, AddonDocs> Scan(string ashitaDir)
    {
        var map = new Dictionary<string, AddonDocs>(StringComparer.OrdinalIgnoreCase);
        List<string> folders;
        try { folders = Directory.EnumerateDirectories(Path.Combine(ashitaDir, "docs")).ToList(); }
        // no docs folder, or one that won't open, means no docs
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return map; }
        foreach (var folder in folders)
            if (ReadmeFiles.Find(folder) is { } docs) map.TryAdd(Path.GetFileName(folder), docs);
        return map;
    }
}
