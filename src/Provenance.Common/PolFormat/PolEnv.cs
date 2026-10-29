using System.Globalization;
using System.Text;
using Provenance.Common.Patcher;

namespace Provenance.Common.PolFormat;

// env.dat is "NAME,value" lines in POL's file cipher, under polcore's built-in key (0x1009af90)
public static class PolEnv
{
    private static readonly byte[] Key = [0xfd, 0x31, 0x42, 0x53, 0x64, 0x75, 0x86, 0x97];

    // null when the file can't be read or decoded, or has no such line
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

    // pol.exe's own answer (WinMain 0x00414560 through 0x0040f5c0): POL_UCS_AREA_KBN from EU\env.dat when there is
    // one, data\utf\env.dat otherwise. An EU install has both and the utf one says US, so a bad EU file is no answer
    public static PolRegion? Region(string viewerDir)
    {
        foreach (var rel in new[] { Path.Combine("EU", "env.dat"), Path.Combine("data", "utf", "env.dat") })
        {
            var path = Path.Combine(viewerDir, rel);
            if (!File.Exists(path)) continue;
            return int.TryParse(Value(path, "POL_UCS_AREA_KBN"), NumberStyles.None, CultureInfo.InvariantCulture, out var area)
                ? area switch { 0 => PolRegion.Japan, 1 => PolRegion.UnitedStates, 2 => PolRegion.Europe, _ => null }
                : null;
        }
        return null;
    }
}
