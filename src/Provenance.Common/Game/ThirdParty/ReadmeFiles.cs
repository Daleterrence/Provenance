using System.Text;
using Provenance.Common.IO;

namespace Provenance.Common.Game.ThirdParty;

public static class ReadmeFiles
{
    // the biggest readme I've seen is 44 KB, so anything over this isn't one
    private const long MaxBytes = 1 << 20;

    private static readonly (string Name, AddonDocsKind Kind)[] Names =
    [
        ("readme.md", AddonDocsKind.Text), ("readme.txt", AddonDocsKind.Text), ("readme", AddonDocsKind.Text),
        ("readme.html", AddonDocsKind.Page), ("readme.htm", AddonDocsKind.Page),
    ];

    // the first of those straight inside the folder, in any case. null for a folder that's missing or won't read
    public static AddonDocs? Find(string folder)
    {
        List<string> files;
        try { files = Directory.EnumerateFiles(folder, "readme*").ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        foreach (var (name, kind) in Names)
            if (files.FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase)) is { } file && SizeOk(file))
                return new AddonDocs(kind, file);
        return null;
    }

    // UTF-8 unless a BOM says UTF-16, which is what Notepad's Unicode writes. null when it can't be read
    public static string? ReadText(string path)
    {
        try
        {
            using var fs = SharedFile.OpenRead(path);
            if (fs.Length > MaxBytes) return null;
            using var reader = new StreamReader(fs, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool SizeOk(string file)
    {
        try { return new FileInfo(file).Length is > 0 and <= MaxBytes; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
