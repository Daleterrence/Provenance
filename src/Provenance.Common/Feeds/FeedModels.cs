using Provenance.Common.PolFormat;

namespace Provenance.Common.Feeds;

// News is the Topics feed (lots of images), Status the text-only Information feed
public enum FeedKind { News, Status }

public sealed record FeedItem(string Title, string Url, DateTimeOffset? Date, string? ImageUrl, string Summary,
                              InfoCategory Category = InfoCategory.None, InfoBadge Badge = InfoBadge.None)
{
    public string CategoryLabel => Category switch
    {
        InfoCategory.Important => "Important",
        InfoCategory.Maintenance => "Maintenance",
        InfoCategory.General => "General",
        InfoCategory.Events => "Event",
        InfoCategory.Updates => "Update",
        _ => "",
    };

    public bool HasCategory => Category != InfoCategory.None;

    public string BadgeLabel => Badge switch
    {
        InfoBadge.Outage => "Difficulties",
        InfoBadge.Maintenance => "Maintenance",
        InfoBadge.Recovery => "Recovery",
        InfoBadge.FollowUp => "Follow-up",
        _ => "",
    };
}

public sealed record FeedResult(PolRegion Region, FeedKind Kind, IReadOnlyList<FeedItem> Items);

// At is when the worker last answered for these (the saved copy's time when Stale), null when there's nothing
public sealed record FeedFetch<T>(IReadOnlyList<T> Items, DateTime? At, bool Stale)
{
    internal static readonly FeedFetch<T> None = new([], null, false);
}

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
