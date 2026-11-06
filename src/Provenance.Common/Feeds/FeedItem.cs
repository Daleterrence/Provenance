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
