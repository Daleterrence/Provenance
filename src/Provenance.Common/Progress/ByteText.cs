using System.Globalization;
using static System.FormattableString;

namespace Provenance.Common.Progress;

// 1024 maths with GB, MB and KB labels, which is what Explorer shows. Invariant, a German PC would put "6,60 GB" in an
// English line
public static class ByteText
{
    private const double KB = 1024, MB = KB * 1024, GB = MB * 1024;

    public static string Gb(long bytes, int decimals = 1) => GbNumber(bytes, decimals) + " GB";

    public static string GbNumber(long bytes, int decimals = 1) => Number(bytes / GB, decimals);

    // GB from a gigabyte up, MB below that, KB only when asked for (a small file shouldn't read "0.0 MB").
    // KB rounds up, nothing under 1 KB shown as a number
    public static string Size(long bytes, int gbDecimals = 1, int mbDecimals = 1, bool kb = false) =>
        bytes >= GB ? Number(bytes / GB, gbDecimals) + " GB"
        : bytes >= MB || !kb ? Number(bytes / MB, mbDecimals) + " MB"
        : bytes < KB ? "less than 1 KB"
        : Invariant($"{(bytes + 1023) / 1024} KB");

    private static string Number(double value, int decimals) => value.ToString("F" + decimals, CultureInfo.InvariantCulture);
}
