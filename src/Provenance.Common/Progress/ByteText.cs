using System.Globalization;
using static System.FormattableString;

namespace Provenance.Common.Progress;

public static class ByteText
{
    private const double KB = 1024, MB = KB * 1024, GB = MB * 1024;

    public static string Gb(long bytes, int decimals = 1) => GbNumber(bytes, decimals) + " GB";

    public static string GbNumber(long bytes, int decimals = 1) => Number(bytes / GB, decimals);

    public static string Size(long bytes, int gbDecimals = 1, int mbDecimals = 1, bool kb = false) =>
        bytes >= GB ? Number(bytes / GB, gbDecimals) + " GB"
        : bytes >= MB || !kb ? Number(bytes / MB, mbDecimals) + " MB"
        : bytes < KB ? "less than 1 KB"
        : Invariant($"{(bytes + 1023) / 1024} KB");

    public static string Mb(long bytes, string format) => (bytes / MB).ToString(format, CultureInfo.InvariantCulture);

    private static string Number(double value, int decimals) => value.ToString("F" + decimals, CultureInfo.InvariantCulture);
}
