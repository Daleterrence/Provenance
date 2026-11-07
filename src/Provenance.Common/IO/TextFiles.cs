using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Provenance.Common.IO;

internal static class TextFiles
{
    // a DOCTYPE is skipped and nothing external is ever fetched. null when the file's missing or isn't XML
    public static XDocument? LoadXml(string file)
    {
        try
        {
            using var r = XmlReader.Create(file, XmlSettings());
            return XDocument.Load(r);
        }
        catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    public static XmlReaderSettings XmlSettings(bool ignoreWhitespace = false) =>
        new() { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreWhitespace = ignoreWhitespace };

    // the first maxBytes of a file as UTF-8 (no BOM), or null when it can't be read
    public static string? ReadHead(string path, int maxBytes)
    {
        try
        {
            using var fs = SharedFile.OpenRead(path);
            var buffer = new byte[(int)Math.Min(fs.Length, maxBytes)];
            int read = fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return new UTF8Encoding(false).GetString(buffer, 0, read).TrimStart('\uFEFF');
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
