namespace Provenance.Common.Feeds;

public enum InfoCategory { None, Important, Maintenance, General, Events, Updates }

public enum InfoBadge { None, Outage, Maintenance, Recovery, FollowUp }

public sealed record WorldStatus(string Name, string Status)
{
    public string Label => Status switch
    {
        "up" => "Up",
        "partially-down" => "Partially down",
        "down" => "Down",
        "partial-maintenance" => "Partial maintenance",
        "maintenance" => "Maintenance",
        _ => "Unknown",
    };

    public bool IsUp => Status == "up";
}

// the feed worker's category and badge keys
public static class PlayOnlineInfo
{
    public static InfoCategory CategoryFromKey(string? key) => key switch
    {
        "important" => InfoCategory.Important, "maintenance" => InfoCategory.Maintenance,
        "general" => InfoCategory.General, "events" => InfoCategory.Events, "updates" => InfoCategory.Updates,
        _ => InfoCategory.None,
    };

    public static InfoBadge BadgeFromKey(string? key) => key switch
    {
        "outage" => InfoBadge.Outage, "maintenance" => InfoBadge.Maintenance,
        "recovery" => InfoBadge.Recovery, "followup" => InfoBadge.FollowUp,
        _ => InfoBadge.None,
    };
}
