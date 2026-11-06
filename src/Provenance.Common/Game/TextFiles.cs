using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Provenance.Common.Game;

internal static partial class TextFiles
{
    // a DOCTYPE is skipped and nothing external is ever fetched. null when the file's missing or isn't XML
    public static XDocument? LoadXml(string file)
    {
        try
        {
            using var r = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            return XDocument.Load(r);
        }
        catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    // the first maxBytes of a file as UTF-8 (no BOM), or null when it can't be read
    public static string? ReadHead(string path, int maxBytes)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[(int)Math.Min(fs.Length, maxBytes)];
            int read = fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return new UTF8Encoding(false).GetString(buffer, 0, read).TrimStart('\uFEFF');
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static string Spaces(string text) => Whitespace().Replace(text, " ").Trim();

    // the exact name first, then any case
    public static T? FindByName<T>(IEnumerable<T> items, Func<T, string> nameOf, string name) where T : class =>
        items.FirstOrDefault(i => nameOf(i) == name) ?? items.FirstOrDefault(i => nameOf(i).Equals(name, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
