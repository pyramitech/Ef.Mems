using System;
using System.Collections.Generic;

namespace Ef.Mems
{
    /// <summary>
    /// Runtime helpers backing the generated {Name}Union read views
    /// (UseUnion). Union reads compose the member stores' own query methods,
    /// so they inherit each member's locking/snapshot discipline; they are
    /// stale-ok and never a consistent cross-store snapshot.
    /// </summary>
    public static class UnionQuery
    {
        /// <summary>
        /// K-way merges per-member windows that are already sorted by
        /// <paramref name="keySelector"/> in the requested direction, then
        /// applies skip/take. Windows must each hold at least the first
        /// skip+take matches of their member (the generated callers request
        /// exactly that).
        /// </summary>
        public static List<T> MergeSorted<T, TKey>(IReadOnlyList<T>[] sorted, Func<T, TKey> keySelector,
            IComparer<TKey>? comparer, bool descending, int skip, int take)
        {
            if (sorted is null) throw new ArgumentNullException(nameof(sorted));
            if (keySelector is null) throw new ArgumentNullException(nameof(keySelector));
            if (take <= 0) return new List<T>(0);
            if (skip < 0) skip = 0;

            var cmp = comparer ?? Comparer<TKey>.Default;
            int dir = descending ? -1 : 1;

            var result = new List<T>(take);
            var idx = new int[sorted.Length];
            int skipped = 0;

            while (result.Count < take)
            {
                int best = -1;
                TKey bestKey = default!;
                for (int i = 0; i < sorted.Length; i++)
                {
                    var list = sorted[i];
                    if (list is null || idx[i] >= list.Count) continue;
                    var key = keySelector(list[idx[i]]);
                    if (best < 0 || cmp.Compare(key, bestKey) * dir < 0)
                    {
                        best = i;
                        bestKey = key;
                    }
                }
                if (best < 0) break;

                var item = sorted[best][idx[best]++];
                if (skipped < skip) { skipped++; continue; }
                result.Add(item);
            }

            return result;
        }
    }
}
