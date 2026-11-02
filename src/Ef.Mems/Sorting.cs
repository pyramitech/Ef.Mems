using System.Collections.Generic;

namespace Ef.Mems
{
    /// <summary>
    /// Struct key selector for sorted queries: like <see cref="IPredicate{T}"/>,
    /// implementing this on a readonly struct and using the generic
    /// QuerySortCount overloads gives a JIT-specialized scan with the key
    /// extraction inlined (no per-element delegate call).
    /// </summary>
    public interface IKeySelector<in T, out TKey>
    {
        TKey Key(T item);
    }

    /// <summary>
    /// Struct wrapper over <see cref="Comparer{TKey}.Default"/>: inside a
    /// generic instantiation the JIT resolves the comparison statically for
    /// value-type keys, removing the per-comparison interface call.
    /// </summary>
    public readonly struct KeyComparer<TKey> : IComparer<TKey>
    {
        public int Compare(TKey x, TKey y) => Comparer<TKey>.Default.Compare(x, y);
    }

    /// <summary>Folds sort direction into the comparer once, instead of a multiply per comparison at the call sites.</summary>
    internal readonly struct DirectionalComparer<TKey, TCmp> : IComparer<TKey>
        where TCmp : IComparer<TKey>
    {
        private readonly TCmp _cmp;
        private readonly int _dir;

        public DirectionalComparer(TCmp cmp, bool descending)
        {
            _cmp = cmp;
            _dir = descending ? -1 : 1;
        }

        public int Compare(TKey x, TKey y) => _cmp.Compare(x, y) * _dir;
    }
}
