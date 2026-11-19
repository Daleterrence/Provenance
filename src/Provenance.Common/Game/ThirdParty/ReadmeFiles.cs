using System.Text;
using Provenance.Common.IO;

namespace Provenance.Common.Game.ThirdParty;

public static class ReadmeFiles
{
    private const long MaxBytes = 1 << 20;

    private static readonly (string Name, AddonDocsKind Kind)[] Names =
    [
        ("readme.md", AddonDocsKind.Text), ("readme.txt", AddonDocsKind.Text), ("readme", AddonDocsKind.Text),
        ("readme.html", AddonDocsKind.Page), ("readme.htm", AddonDocsKind.Page),
    ];

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

    // only a .md gets laid out. A readme.txt is often hand-spaced columns, which Markdown would run together
    public static bool IsMarkdown(string path) => Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase);

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
