using System.Text.RegularExpressions;

namespace Provenance.Common.IO;

public static partial class FileNames
{
    // one file name, no folder or drive in it, nothing Windows would change on its way to disk
    public static bool IsPlainFileName(string name) =>
        name.Length > 0 && name != "." && name != ".." && name == Path.GetFileName(name)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.EndsWith('.') && !name.EndsWith(' ');

    // con, nul, com1 and the like, which open a device rather than a file, with or without an extension
    public static bool IsDeviceName(string name) => DeviceName().IsMatch(name);

    [GeneratedRegex(@"^(con|prn|aux|nul|com\d|lpt\d)(\.|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceName();
}
