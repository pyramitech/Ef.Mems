namespace Ef.Mems
{
    /// <summary>
    /// A predicate implementable by a struct. When passed to the struct-generic
    /// Query overloads, the JIT compiles a specialized scan loop per predicate
    /// type with <see cref="Invoke"/> inlined into it - no delegate indirection
    /// and no closure allocations. The <see cref="System.Predicate{T}"/> based
    /// overloads remain available for ad-hoc/dynamic queries.
    /// </summary>
    public interface IPredicate<in T>
    {
        bool Invoke(T item);
    }

    /// <summary>Matches everything; the no-predicate form for generic query paths.</summary>
    public readonly struct MatchAll<T> : IPredicate<T>
    {
        public bool Invoke(T item) => true;
    }

    /// <summary>Adapts a delegate predicate into the struct-generic paths (keeps the delegate's cost, nothing more).</summary>
    public readonly struct DelegatePredicate<T> : IPredicate<T>
    {
        private readonly System.Predicate<T> _predicate;
        public DelegatePredicate(System.Predicate<T> predicate) => _predicate = predicate;
        public bool Invoke(T item) => _predicate(item);
    }
}
