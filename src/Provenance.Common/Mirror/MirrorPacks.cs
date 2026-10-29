namespace Provenance.Common.Mirror;

public sealed record MirrorSpan(string Pack, long Start, long Length, IReadOnlyList<MirrorEntry> Entries);

public static class MirrorPacks
{
    public const long TargetSize = 64L * 1024 * 1024;

    // wanted ranges closer than this go in one request, gap bytes and all
    public const long MaxGap = 1048576L;

    // a file that'd take a non-empty pack past targetSize starts a new one, so a bigger file gets its own
    public static IReadOnlyList<(int First, int Count)> Group(IReadOnlyList<long> compressedSizes, long targetSize = TargetSize)
    {
        var groups = new List<(int First, int Count)>();
        int first = 0;
        long sum = 0;
        for (int i = 0; i < compressedSizes.Count; i++)
        {
            if (i > first && sum + compressedSizes[i] > targetSize)
            {
                groups.Add((first, i - first));
                first = i;
                sum = 0;
            }
            sum += compressedSizes[i];
        }
        if (compressedSizes.Count > first) groups.Add((first, compressedSizes.Count - first));
        return groups;
    }

    public static IReadOnlyList<MirrorSpan> Plan(IEnumerable<MirrorEntry> entries, long maxGap = MaxGap)
    {
        var spans = new List<MirrorSpan>();
        foreach (var pack in entries.GroupBy(e => e.Pack ?? throw new InvalidDataException("entry " + e.Path + " has no pack"))
                                    .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            long start = 0, end = 0;
            List<MirrorEntry>? cur = null;
            foreach (var loc in pack.GroupBy(e => e.Offset).OrderBy(g => g.Key))
            {
                var members = loc.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
                long locEnd = loc.Key + members.Max(e => e.CompressedSize);
                if (cur is not null && loc.Key - end <= maxGap)
                {
                    cur.AddRange(members);
                    end = Math.Max(end, locEnd);
                    continue;
                }
                if (cur is not null) spans.Add(new MirrorSpan(pack.Key, start, end - start, cur));
                (start, end, cur) = (loc.Key, locEnd, members);
            }
            if (cur is not null) spans.Add(new MirrorSpan(pack.Key, start, end - start, cur));
        }
        return spans.OrderByDescending(s => s.Length).ThenBy(s => s.Pack, StringComparer.Ordinal).ThenBy(s => s.Start).ToList();
    }
}
