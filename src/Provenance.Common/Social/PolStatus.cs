namespace Provenance.Common.Social;

public enum PolStatus { Online, Away, Busy, AppearOffline }

public static class PolStatusText
{
    public static PolStatus Parse(string? s) => s switch
    {
        "online" => PolStatus.Online,
        "away" => PolStatus.Away,
        "busy" => PolStatus.Busy,
        _ => PolStatus.AppearOffline,
    };

    public static string Label(PolStatus s) => s switch
    {
        PolStatus.Online => "Online",
        PolStatus.Away => "Away",
        PolStatus.Busy => "Busy",
        _ => "Appear offline",
    };

    // the digit the hook's status file wants
    public static int Code(PolStatus s) =>
        s switch { PolStatus.Online => 0, PolStatus.Away => 1, PolStatus.Busy => 2, _ => 4 };
}
