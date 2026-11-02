using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace Ef.Mems.Benchmarks
{
    /// <summary>
    /// QuerySortCount at production scale (4M rows): the delegate baseline
    /// (Func keySelector + IComparer + closure sort) vs the fully
    /// JIT-specialized generic overload (struct predicate + IKeySelector +
    /// KeyComparer, direction folded). "ByIdDesc" is the "latest N" shape -
    /// the baseline for the future order-by-sequential-index fast path.
    /// </summary>
    [MemoryDiagnoser]
    [GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
    [CategoriesColumn]
    public class SortBenchmarks
    {
        public const int N = 4_000_000;

        private BenchStore _store;

        [GlobalSetup]
        public void Setup()
        {
            var dbPath = BenchSeeder.DbPath(N);
            BenchSeeder.EnsureSeeded(dbPath, N);
            _store = new BenchStore(dbPath);
        }

        private readonly struct NumberSelector : IKeySelector<BenchEntityReadOnly, int>
        {
            public int Key(BenchEntityReadOnly item) => item.Number;
        }

        private readonly struct IdSelector : IKeySelector<BenchEntityReadOnly, int>
        {
            public int Key(BenchEntityReadOnly item) => item.Id;
        }

        // ---- Top 50 by Number, ~half the rows match ------------------------

        [BenchmarkCategory("Sort top50"), Benchmark(Baseline = true)]
        public async Task<int> Top50_Delegate()
            => (await _store.BenchEntities.QuerySortCount(x => x.Number >= 500, 0, 50, x => x.Number)).Items.Count;

        [BenchmarkCategory("Sort top50"), Benchmark]
        public async Task<int> Top50_Struct()
            => (await _store.BenchEntities.QuerySortCount<int, BenchEntityEntityStore.MinNumberPredicate, NumberSelector, KeyComparer<int>>(
                new BenchEntityEntityStore.MinNumberPredicate(500), new NumberSelector(), new KeyComparer<int>(), 0, 50)).Items.Count;

        // ---- Deep page: skip 100k, take 50 (window = 100 050) ---------------

        [BenchmarkCategory("Sort deep page"), Benchmark(Baseline = true)]
        public async Task<int> DeepPage_Delegate()
            => (await _store.BenchEntities.QuerySortCount(x => x.Number >= 500, 100_000, 50, x => x.Number)).Items.Count;

        [BenchmarkCategory("Sort deep page"), Benchmark]
        public async Task<int> DeepPage_Struct()
            => (await _store.BenchEntities.QuerySortCount<int, BenchEntityEntityStore.MinNumberPredicate, NumberSelector, KeyComparer<int>>(
                new BenchEntityEntityStore.MinNumberPredicate(500), new NumberSelector(), new KeyComparer<int>(), 100_000, 50)).Items.Count;

        // ---- Latest 50 by Id: heap sort vs the seq-order descending walk ----

        [BenchmarkCategory("Sort by Id desc"), Benchmark(Baseline = true)]
        public async Task<int> ByIdDesc_Delegate()
            => (await _store.BenchEntities.QuerySortCount(x => x.Number >= 500, 0, 50, x => x.Id, descending: true)).Items.Count;

        [BenchmarkCategory("Sort by Id desc"), Benchmark]
        public async Task<int> ByIdDesc_Struct()
            => (await _store.BenchEntities.QuerySortCount<int, BenchEntityEntityStore.MinNumberPredicate, IdSelector, KeyComparer<int>>(
                new BenchEntityEntityStore.MinNumberPredicate(500), new IdSelector(), new KeyComparer<int>(), 0, 50, descending: true)).Items.Count;

        [BenchmarkCategory("Sort by Id desc"), Benchmark]
        public async Task<int> ByIdDesc_SeqWalk()
            => (await _store.BenchEntities.Query(
                new BenchEntityEntityStore.MinNumberPredicate(500), 0, 50, descending: true)).Count;

        // ---- Named sorts: scan (specialized) vs materialized snapshot -------

        [BenchmarkCategory("Named sort top50"), Benchmark(Baseline = true)]
        public async Task<int> NamedSort_Scan()
            => (await _store.BenchEntities.QuerySortCountByNumber(
                new BenchEntityEntityStore.MinNumberPredicate(500), 0, 50)).Items.Count;

        [BenchmarkCategory("Named sort top50"), Benchmark]
        public async Task<int> NamedSort_Materialized()
            => (await _store.BenchEntities.QuerySortCountNumberMat(
                new BenchEntityEntityStore.MinNumberPredicate(500), 0, 50)).Items.Count;

        [BenchmarkCategory("Named sort top50"), Benchmark]
        public async Task<int> NamedSort_MaterializedNoCount()
            => (await _store.BenchEntities.QuerySortNumberMat(
                new BenchEntityEntityStore.MinNumberPredicate(500), 0, 50)).Count;

        // Direction-aligned window: descending over the ascending-sorted
        // array starts at the matching suffix, so the early exit fires after
        // ~50 elements instead of scanning the 2M non-matching prefix.
        [BenchmarkCategory("Named sort desc top50"), Benchmark(Baseline = true)]
        public async Task<int> NamedSortDesc_Scan()
            => (await _store.BenchEntities.QuerySortCountByNumber(
                new BenchEntityEntityStore.MinNumberPredicate(500), 0, 50, descending: true)).Items.Count;

        [BenchmarkCategory("Named sort desc top50"), Benchmark]
        public async Task<int> NamedSortDesc_MaterializedNoCount()
            => (await _store.BenchEntities.QuerySortNumberMat(
                new BenchEntityEntityStore.MinNumberPredicate(500), 0, 50, descending: true)).Count;
    }
}
