namespace Provenance.Common.Settings;

// from 10 so the viewer's own 01-08 never get touched, and never 84
public static class HomeSlots
{
    public const int First = 10, Last = 99, Shared = 84;

    public static bool Valid(int slot) => slot is >= First and <= Last && slot != Shared;

    public static bool Assign(LauncherSettings s, LauncherSettings? saved = null)
    {
        bool changed = false;
        var taken = new HashSet<int>();
        int last = Math.Max(s.LastHomeSlot, saved?.LastHomeSlot ?? 0);
        foreach (var p in s.Profiles)
        {
            if (Valid(p.HomeSlot) && taken.Add(p.HomeSlot)) continue;
            if (p.HomeSlot == 0) continue;
            p.HomeSlot = 0;
            changed = true;
        }
        var blocked = new HashSet<int>(taken);
        foreach (var o in saved?.Profiles ?? [])
            if (Valid(o.HomeSlot) && s.Profiles.All(p => p.Id != o.Id)) blocked.Add(o.HomeSlot);
        last = Math.Max(last, blocked.DefaultIfEmpty(0).Max());
        foreach (var p in s.Profiles)
        {
            if (p.HomeSlot != 0) continue;
            int slot = Next(ref last, blocked);
            if (slot == 0) break;   // 89 accounts at once, the rest stay in home84
            p.HomeSlot = slot;
            blocked.Add(slot);
            changed = true;
        }
        if (s.LastHomeSlot != last)
        {
            s.LastHomeSlot = last;
            changed = true;
        }
        return changed;
    }

    private static int Next(ref int last, HashSet<int> blocked)
    {
        for (int c = Math.Max(last + 1, First); c <= Last; c++)
            if (Valid(c) && !blocked.Contains(c))
            {
                last = c;
                return c;
            }
        for (int c = First; c <= Last; c++)
            if (Valid(c) && !blocked.Contains(c)) return c;
        return 0;
    }
}
