using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace Ef.Mems.Benchmarks
{
    /// <summary>
    /// SearchQuery engine comparison at production scale (4M rows, the
    /// largest real table): per-token ordinal string.Contains (the original
    /// engine, kept here as the baseline) vs per-token single-string
    /// SearchValues, which precomputes rare-character anchors once per query
    /// and won ~2x - it is what SearchQuery ships with now. All benchmarks
    /// are QueryCount with take: 0 (count only) so result materialization
    /// never enters the measurement. SearchField shape: "{color} {noun}
    /// {extra} unit {n}" - see BenchSeeder.
    /// </summary>
    [MemoryDiagnoser]
    [GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
    [CategoriesColumn]
    public class SearchBenchmarks
    {
        // Production spread: the hottest table has ~4M rows, most others a
        // few thousand.
        [Params(4_000, 4_000_000)]
        public int N;

        private BenchStore _store;

        // "xylophone" never occurs; "rotor" hits 1 noun in 25 (~160k rows);
        // "crimson rotor" additionally needs 1 color in 20 (~8k rows).
        private ContainsPredicate _noMatchC, _commonC, _twoTokensC;
        private SearchQueryPredicate _noMatchSv, _commonSv, _twoTokensSv;

        [GlobalSetup]
        public void Setup()
        {
            var dbPath = BenchSeeder.DbPath(N);
            BenchSeeder.EnsureSeeded(dbPath, N);
            _store = new BenchStore(dbPath);

            _noMatchC = new ContainsPredicate("xylophone");
            _commonC = new ContainsPredicate("rotor");
            _twoTokensC = new ContainsPredicate("crimson", "rotor");
            _noMatchSv = new SearchQueryPredicate(SearchQuery.Parse("xylophone"));
            _commonSv = new SearchQueryPredicate(SearchQuery.Parse("rotor"));
            _twoTokensSv = new SearchQueryPredicate(SearchQuery.Parse("crimson rotor"));
        }

        // The original engine, kept as the baseline: per-token
        // string.Contains(token, StringComparison.Ordinal).
        private readonly struct ContainsPredicate : IPredicate<BenchEntityReadOnly>
        {
            private readonly string[] _tokens;
            public ContainsPredicate(params string[] tokens) => _tokens = tokens;

            public bool Invoke(BenchEntityReadOnly item)
            {
                var field = item.SearchField;
                var tokens = _tokens;
                for (int i = 0; i < tokens.Length; i++)
                {
                    if (!field.Contains(tokens[i], StringComparison.Ordinal))
                        return false;
                }
                return true;
            }
        }

        // The shipped engine: SearchQuery.Matches, i.e. one single-string
        // SearchValues per token probed with IndexOfAny.
        private readonly struct SearchQueryPredicate : IPredicate<BenchEntityReadOnly>
        {
            private readonly SearchQuery _query;
            public SearchQueryPredicate(SearchQuery query) => _query = query;
            public bool Invoke(BenchEntityReadOnly item) => _query.Matches(item.SearchField);
        }

        // ---- No match: every field scanned end to end, 0 hits -------------

        [BenchmarkCategory("Search no match"), Benchmark(Baseline = true)]
        public async Task<int> NoMatch_Contains()
            => (await _store.BenchEntities.QueryCount(_noMatchC, 0, 0)).MatchCount;

        [BenchmarkCategory("Search no match"), Benchmark]
        public async Task<int> NoMatch_SearchValues()
            => (await _store.BenchEntities.QueryCount(_noMatchSv, 0, 0)).MatchCount;

        // ---- Common token: ~160k of 4M rows match --------------------------

        [BenchmarkCategory("Search common token"), Benchmark(Baseline = true)]
        public async Task<int> Common_Contains()
            => (await _store.BenchEntities.QueryCount(_commonC, 0, 0)).MatchCount;

        [BenchmarkCategory("Search common token"), Benchmark]
        public async Task<int> Common_SearchValues()
            => (await _store.BenchEntities.QueryCount(_commonSv, 0, 0)).MatchCount;

        // ---- Two tokens AND: ~8k of 4M rows match ---------------------------

        [BenchmarkCategory("Search two tokens"), Benchmark(Baseline = true)]
        public async Task<int> TwoTokens_Contains()
            => (await _store.BenchEntities.QueryCount(_twoTokensC, 0, 0)).MatchCount;

        [BenchmarkCategory("Search two tokens"), Benchmark]
        public async Task<int> TwoTokens_SearchValues()
            => (await _store.BenchEntities.QueryCount(_twoTokensSv, 0, 0)).MatchCount;
    }
}
