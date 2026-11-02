using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace Ef.Mems.Benchmarks
{
    /// <summary>
    /// Delegate predicates vs struct predicates over a fully cached store.
    /// Number is Id % 1000, so MinMatchHalf (500) matches ~half the rows and
    /// NoMatch (1000) matches none (pure scan, no result materialization).
    /// </summary>
    [MemoryDiagnoser]
    [GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
    [CategoriesColumn]
    public class PredicateBenchmarks
    {
        public const int N = 1_000_000;
        private const int MinMatchHalf = 500;
        private const int NoMatch = 1000;

        private BenchStore _store;
        private int _min = MinMatchHalf;

        [GlobalSetup]
        public void Setup()
        {
            var dbPath = BenchSeeder.DbPath(N);
            BenchSeeder.EnsureSeeded(dbPath, N);
            _store = new BenchStore(dbPath);
        }

        // A hand-written predicate, exercising the generic struct overloads
        // (the generated MinNumberPredicate goes through the named methods).
        private readonly struct MinNumberStruct : IPredicate<BenchEntityReadOnly>
        {
            private readonly int _min;
            public MinNumberStruct(int min) => _min = min;
            public bool Invoke(BenchEntityReadOnly item) => item.Number >= _min;
        }

        // ---- Full scan, count only (take: 0), ~half the rows match --------

        [BenchmarkCategory("Count full scan"), Benchmark(Baseline = true)]
        public async Task<int> Count_Delegate()
            => (await _store.BenchEntities.QueryCount(x => x.Number >= _min, 0, 0)).MatchCount;

        [BenchmarkCategory("Count full scan"), Benchmark]
        public async Task<int> Count_StructNamed()
            => (await _store.BenchEntities.QueryCountMinNumber(_min, 0, 0)).MatchCount;

        [BenchmarkCategory("Count full scan"), Benchmark]
        public async Task<int> Count_StructHandwritten()
            => (await _store.BenchEntities.QueryCount(new MinNumberStruct(_min), 0, 0)).MatchCount;

        // ---- Full scan, no matches (pure predicate cost) ------------------

        [BenchmarkCategory("FirstOrDefault no match"), Benchmark(Baseline = true)]
        public async Task<BenchEntityReadOnly> First_Delegate()
            => await _store.BenchEntities.FirstOrDefault(x => x.Number >= NoMatch);

        [BenchmarkCategory("FirstOrDefault no match"), Benchmark]
        public async Task<BenchEntityReadOnly> First_StructNamed()
            => await _store.BenchEntities.FirstOrDefaultMinNumber(NoMatch);

        // ---- Paged query (skip 500 matches, take 64) ----------------------

        [BenchmarkCategory("Paged query"), Benchmark(Baseline = true)]
        public async Task<int> Page_Delegate()
            => (await _store.BenchEntities.Query(x => x.Number >= _min, skip: 500, take: 64)).Count;

        [BenchmarkCategory("Paged query"), Benchmark]
        public async Task<int> Page_StructNamed()
            => (await _store.BenchEntities.QueryMinNumber(_min, skip: 500, take: 64)).Count;

        // ---- Full scan with a polymorphic call site ------------------------
        // Four syntactically distinct lambdas (identical bodies, so identical
        // per-element cost) all funnel through the single shared scan-loop
        // instantiation. The delegate call site inside it sees four targets at
        // ~25% each, below PGO's guarded-devirtualization likelihood threshold,
        // so the JIT leaves the raw indirect call - like a real store where
        // many different predicates share the loop. The struct side uses four
        // distinct predicate types: each gets its own specialized inlined loop.

        private readonly struct MinA : IPredicate<BenchEntityReadOnly>
        {
            private readonly int _m;
            public MinA(int m) => _m = m;
            public bool Invoke(BenchEntityReadOnly item) => item.Number >= _m;
        }

        private readonly struct MinB : IPredicate<BenchEntityReadOnly>
        {
            private readonly int _m;
            public MinB(int m) => _m = m;
            public bool Invoke(BenchEntityReadOnly item) => item.Number >= _m;
        }

        private readonly struct MinC : IPredicate<BenchEntityReadOnly>
        {
            private readonly int _m;
            public MinC(int m) => _m = m;
            public bool Invoke(BenchEntityReadOnly item) => item.Number >= _m;
        }

        private readonly struct MinD : IPredicate<BenchEntityReadOnly>
        {
            private readonly int _m;
            public MinD(int m) => _m = m;
            public bool Invoke(BenchEntityReadOnly item) => item.Number >= _m;
        }

        [BenchmarkCategory("Count polymorphic"), Benchmark(Baseline = true, OperationsPerInvoke = 4)]
        public async Task<int> CountPoly_Delegate()
        {
            var s = _store.BenchEntities;
            var total = (await s.QueryCount(x => x.Number >= _min, 0, 0)).MatchCount;
            total += (await s.QueryCount(x => x.Number >= _min, 0, 0)).MatchCount;
            total += (await s.QueryCount(x => x.Number >= _min, 0, 0)).MatchCount;
            total += (await s.QueryCount(x => x.Number >= _min, 0, 0)).MatchCount;
            return total;
        }

        [BenchmarkCategory("Count polymorphic"), Benchmark(OperationsPerInvoke = 4)]
        public async Task<int> CountPoly_Struct()
        {
            var s = _store.BenchEntities;
            var total = (await s.QueryCount(new MinA(_min), 0, 0)).MatchCount;
            total += (await s.QueryCount(new MinB(_min), 0, 0)).MatchCount;
            total += (await s.QueryCount(new MinC(_min), 0, 0)).MatchCount;
            total += (await s.QueryCount(new MinD(_min), 0, 0)).MatchCount;
            return total;
        }
    }
}
