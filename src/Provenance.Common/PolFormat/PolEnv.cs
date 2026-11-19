using System.Globalization;
using System.Text;

namespace Provenance.Common.PolFormat;

// "NAME,value" lines in POL's file cipher
public static class PolEnv
{
    private static readonly byte[] Key = [0xfd, 0x31, 0x42, 0x53, 0x64, 0x75, 0x86, 0x97];

    public static string? Value(string path, string name)
    {
        try { return Value(File.ReadAllBytes(path), name); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static string? Value(ReadOnlySpan<byte> blob, string name)
    {
        if (new PolFileCipher(Key).Decrypt(blob) is not { } r) return null;
        foreach (var raw in Encoding.Latin1.GetString(r.Data).TrimEnd('\0').Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > name.Length && line[name.Length] == ',' && line.StartsWith(name, StringComparison.Ordinal))
                return line[(name.Length + 1)..];
        }
        return null;
    }

    internal static byte[] Encode(string text)
    {
        var data = Encoding.Latin1.GetBytes(text);
        Array.Resize(ref data, (data.Length + 7) / 8 * 8);
        return new PolFileCipher(Key).Encrypt(data, (uint)text.Length);
    }

    // EU\env.dat when there is one, data\utf\env.dat otherwise (an EU install has both and the utf one says US)
    private static string? FileIn(string viewerDir) =>
        new[] { Path.Combine("EU", "env.dat"), Path.Combine("data", "utf", "env.dat") }
            .Select(rel => Path.Combine(viewerDir, rel)).FirstOrDefault(File.Exists);

    public static string? ValueIn(string viewerDir, string name) => FileIn(viewerDir) is { } path ? Value(path, name) : null;

    private static readonly PolRegion[] ByArea = [PolRegion.Japan, PolRegion.UnitedStates, PolRegion.Europe];

    public static int AreaIndex(PolRegion region) => Array.IndexOf(ByArea, region);

    public static PolRegion? Region(string viewerDir) =>
        int.TryParse(ValueIn(viewerDir, "POL_UCS_AREA_KBN"), NumberStyles.None, CultureInfo.InvariantCulture, out var area)
            && area < ByArea.Length ? ByArea[area] : null;
}
