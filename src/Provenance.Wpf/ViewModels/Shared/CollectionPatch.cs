using System.Collections.ObjectModel;

namespace Provenance.Wpf.ViewModels.Shared;

public static class CollectionPatch
{
    // brings list to match next in place rather than clearing it, so it keeps its scroll position and selection. sameRow says
    // two items are one row, equal that nothing about it changed
    public static void Patch<T>(this ObservableCollection<T> list, IReadOnlyList<T> next, Func<T, T, bool> sameRow,
                                Func<T, T, bool> equal, Action<int, T>? replace = null)
    {
        for (int i = 0; i < next.Count; i++)
        {
            int j = -1;
            for (int k = i; k < list.Count && j < 0; k++)
                if (sameRow(list[k], next[i])) j = k;
            if (j < 0)
            {
                list.Insert(i, next[i]);
                continue;
            }
            if (j != i) list.Move(j, i);
            if (equal(list[i], next[i])) continue;
            if (replace is null) list[i] = next[i];
            else replace(i, next[i]);
        }
        while (list.Count > next.Count) list.RemoveAt(list.Count - 1);
    }
}
