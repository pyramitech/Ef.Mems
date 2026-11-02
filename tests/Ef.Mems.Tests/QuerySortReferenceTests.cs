using Xunit;

namespace Ef.Mems.Tests
{
    /// <summary>
    /// QuerySortCount is backed by a hand-written heap select; these tests
    /// pin it against the obvious LINQ reference over a grid of windows,
    /// directions and comparers, with heavy key duplication (the classic
    /// failure mode of top-k selection).
    /// </summary>
    public class QuerySortReferenceTests : StoreTestBase
    {
        private async Task<List<TestClassReadOnly>> SeedRandomAsync(int count, int keySpace)
        {
            var rng = new Random(1234);
            var items = Enumerable.Range(1, count)
                .Select(i => NewTestClass("r" + i, rng.Next(0, keySpace)))
                .ToList();
            return await Store.TestClasses.CreateRange(items);
        }

        [Fact]
        public async Task QuerySortCount_MatchesLinqReference_AcrossWindowGrid()
        {
            await SeedRandomAsync(200, 20); // ~10 duplicates per key
            var all = await Store.TestClasses.GetAll();
            Predicate<TestClassReadOnly> pred = x => x.Number % 2 == 0;
            var matches = all.Where(x => pred(x)).ToList();

            foreach (var descending in new[] { false, true })
                foreach (var skip in new[] { 0, 1, 7, 50, matches.Count - 1, matches.Count + 50 })
                    foreach (var take in new[] { 0, 1, 13, 60, 300 })
                    {
                        var (items, matchCount) = await Store.TestClasses.QuerySortCount(
                            pred, skip, take, x => x.Number, descending: descending);

                        Assert.Equal(matches.Count, matchCount);

                        if (take <= 0)
                        {
                            Assert.Empty(items);
                            continue;
                        }

                        var expected = (descending
                                ? matches.OrderByDescending(x => x.Number)
                                : matches.OrderBy(x => x.Number))
                            .Skip(skip).Take(take)
                            .Select(x => x.Number).ToList();
                        // Duplicate keys make entity order unspecified; the
                        // key sequence is the contract.
                        Assert.Equal(expected, items.Select(x => x.Number).ToList());
                    }
        }

        [Fact]
        public async Task QuerySortCount_UniqueStringKeys_ExactOrder()
        {
            await SeedRandomAsync(50, 1000);
            var all = await Store.TestClasses.GetAll();

            var (items, matchCount) = await Store.TestClasses.QuerySortCount(
                x => true, 5, 20, x => x.TestName, StringComparer.Ordinal);

            Assert.Equal(50, matchCount);
            var expected = all.OrderBy(x => x.TestName, StringComparer.Ordinal)
                .Skip(5).Take(20).Select(x => x.TestName).ToList();
            Assert.Equal(expected, items.Select(x => x.TestName).ToList());
        }

        [Fact]
        public async Task QuerySortCount_CustomComparer_EqualsDescending()
        {
            await SeedRandomAsync(80, 10);
            var reverse = Comparer<int>.Create((a, b) => b.CompareTo(a));

            var (viaComparer, _) = await Store.TestClasses.QuerySortCount(
                x => true, 0, 30, x => x.Number, reverse);
            var (viaDescending, _) = await Store.TestClasses.QuerySortCount(
                x => true, 0, 30, x => x.Number, descending: true);

            Assert.Equal(
                viaDescending.Select(x => x.Number).ToList(),
                viaComparer.Select(x => x.Number).ToList());
        }

        [Fact]
        public async Task QuerySortCount_NamedPredicate_MatchesDelegate()
        {
            await SeedRandomAsync(100, 20);

            var (viaStruct, structCount) = await Store.TestClasses.QuerySortCountMinNumber(
                10, 2, 15, x => x.Number);
            var (viaDelegate, delegateCount) = await Store.TestClasses.QuerySortCount(
                x => x.Number >= 10, 2, 15, x => x.Number);

            Assert.Equal(delegateCount, structCount);
            Assert.Equal(
                viaDelegate.Select(x => x.Number).ToList(),
                viaStruct.Select(x => x.Number).ToList());
        }
    }
}
