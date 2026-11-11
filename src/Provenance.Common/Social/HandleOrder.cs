namespace Provenance.Common.Social;

public sealed record ContentIdOrder(PolContentId Record, int Order);

public static class HandleOrder
{
    // one list for the whole account, every service's Content IDs together. The viewer doesn't say how it breaks
    // a tie, the index is my guess
    public static IReadOnlyList<PolContentId> Listed(IReadOnlyList<PolContentId> contentIds) =>
        contentIds.OrderBy(c => c.Order).ThenBy(c => c.Index).ToList();

    public static IReadOnlyList<ContentIdOrder> Changes(IReadOnlyList<PolContentId> contentIds, IReadOnlyList<string> ffxiKeys)
    {
        var listed = Listed(contentIds);
        var ffxi = listed.Where(c => c.Type == ContentIdType.Ffxi).ToDictionary(c => c.Key);
        if (ffxiKeys.Count != ffxi.Count || ffxiKeys.Distinct().Count() != ffxi.Count || !ffxiKeys.All(ffxi.ContainsKey)) return [];
        int next = 0;
        var wanted = listed.Select(c => c.Type == ContentIdType.Ffxi ? ffxi[ffxiKeys[next++]] : c).ToList();
        // with every byte different the same bytes are handed round. With two the same the lot's numbered from 0,
        // which is what the viewer's list does when it closes
        var bytes = listed.Select(c => c.Order).ToList();
        bool tied = bytes.Distinct().Count() != bytes.Count;
        return wanted.Select((c, i) => new ContentIdOrder(c, tied ? i : bytes[i])).Where(o => o.Order != o.Record.Order).ToList();
    }
}
