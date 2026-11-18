using System.Globalization;

namespace Provenance.Common.Social;

public static class NotificationBadge
{
    public static int Count(int unread, int waiting) => Math.Max(0, unread) + Math.Max(0, waiting);

    public static string Label(int count) =>
        count <= 0 ? "" : count > 9 ? "9+" : count.ToString(CultureInfo.InvariantCulture);

    public static string Description(int count) =>
        count <= 0 ? "" : count == 1 ? "1 notification" : $"{count.ToString(CultureInfo.InvariantCulture)} notifications";
}
