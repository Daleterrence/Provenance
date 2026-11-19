using System.Text.RegularExpressions;

namespace Provenance.Common.IO;

public static partial class FileNames
{
    public static bool IsPlainFileName(string name) =>
        name.Length > 0 && name != "." && name != ".." && name == Path.GetFileName(name)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.EndsWith('.') && !name.EndsWith(' ');

    public static bool IsDeviceName(string name) => DeviceName().IsMatch(name);

    [GeneratedRegex(@"^(con|prn|aux|nul|com\d|lpt\d)(\.|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceName();
}
