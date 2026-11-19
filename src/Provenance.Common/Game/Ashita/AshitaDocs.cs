using Provenance.Common.Game.ThirdParty;

namespace Provenance.Common.Game.Ashita;

public static class AshitaDocs
{
    public static IReadOnlyDictionary<string, AddonDocs> Scan(string ashitaDir)
    {
        var map = new Dictionary<string, AddonDocs>(StringComparer.OrdinalIgnoreCase);
        List<string> folders;
        try { folders = Directory.EnumerateDirectories(Path.Combine(ashitaDir, "docs")).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return map; }
        foreach (var folder in folders)
            if (ReadmeFiles.Find(folder) is { } docs) map.TryAdd(Path.GetFileName(folder), docs);
        return map;
    }
}
