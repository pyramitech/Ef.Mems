using Xunit;

namespace Ef.Mems.Tests
{
    /// <summary>
    /// The three sorted-query engines: descending traversal of the sequential
    /// snapshot (Query/QueryCount/FirstOrDefault descending: true), scan-backed
    /// named sorts (UseSort => QuerySortByNumber), and materialized named
    /// sorts (UseSort materialized: true => QuerySortNumberMat, maintained on
    /// every write). All are pinned against LINQ and against each other.
    /// </summary>
    public class SortFeatureTests : StoreTestBase
    {
        // ---- descending on the sequential snapshot -------------------------

        [Fact]
        public async Task Query_Descending_ReturnsNewestFirst()
        {
            await SeedAsync(10);

            var latest = await Store.TestClasses.Query(x => true, 0, 3, descending: true);
            Assert.Equal(new[] { 10, 9, 8 }, latest.Select(x => x.Id));

            var page2 = await Store.TestClasses.Query(x => true, 3, 3, descending: true);
            Assert.Equal(new[] { 7, 6, 5 }, page2.Select(x => x.Id));

            var noPredicate = await Store.TestClasses.Query(0, 2, descending: true);
            Assert.Equal(new[] { 10, 9 }, noPredicate.Select(x => x.Id));
        }

        [Fact]
        public async Task QueryCount_Descending_CountsAllAndOrdersWindow()
        {
            await SeedAsync(10);

            var (items, count) = await Store.TestClasses.QueryCount(x => x.Number % 2 == 0, 0, 2, descending: true);
            Assert.Equal(5, count);
            Assert.Equal(new[] { 10, 8 }, items.Select(x => x.Id));
        }

        [Fact]
        public async Task FirstOrDefault_Descending_ReturnsNewestMatch()
        {
            await SeedAsync(10);

            Assert.Equal(10, (await Store.TestClasses.FirstOrDefault(x => true, descending: true)).Id);
            Assert.Equal(9, (await Store.TestClasses.FirstOrDefault(x => x.Number % 2 == 1, descending: true)).Id);
            // Named predicate variant.
            Assert.Equal(10, (await Store.TestClasses.FirstOrDefaultMinNumber(1, descending: true)).Id);
        }

        [Fact]
        public async Task Descending_SkipsDeletedSlots()
        {
            await SeedAsync(5);
            await Store.TestClasses.Delete(Store.TestClasses.GetById(5).GetEntity());
            await Store.TestClasses.Delete(Store.TestClasses.GetById(3).GetEntity());

            var latest = await Store.TestClasses.Query(x => true, 0, 3, descending: true);
            Assert.Equal(new[] { 4, 2, 1 }, latest.Select(x => x.Id));
        }

        [Fact]
        public async Task NamedPredicate_Descending()
        {
            await SeedAsync(10);

            var items = await Store.TestClasses.QueryMinNumber(5, 0, 3, descending: true);
            Assert.Equal(new[] { 10, 9, 8 }, items.Select(x => x.Id));
        }

        // ---- named sorts: scan vs materialized vs LINQ ----------------------

        private async Task<List<TestClassReadOnly>> SeedRandomAsync(int count)
        {
            var rng = new Random(77);
            var items = Enumerable.Range(1, count)
                .Select(i => NewTestClass("s" + i, rng.Next(0, 15)))
                .ToList();
            return await Store.TestClasses.CreateRange(items);
        }

        [Fact]
        public async Task ScanSort_MatchesLinq()
        {
            await SeedRandomAsync(60);
            var all = await Store.TestClasses.GetAll();

            foreach (var descending in new[] { false, true })
            {
                var (items, count) = await Store.TestClasses.QuerySortCountByNumber(
                    x => x.Number >= 5, 2, 10, descending);

                var matches = all.Where(x => x.Number >= 5).ToList();
                Assert.Equal(matches.Count, count);
                var expected = (descending ? matches.OrderByDescending(x => x.Number) : matches.OrderBy(x => x.Number))
                    .Skip(2).Take(10).Select(x => x.Number).ToList();
                Assert.Equal(expected, items.Select(x => x.Number).ToList());
            }
        }

        [Fact]
        public async Task MaterializedSort_MatchesScanSort()
        {
            await SeedRandomAsync(60);

            foreach (var descending in new[] { false, true })
            {
                var (scan, scanCount) = await Store.TestClasses.QuerySortCountByNumber(
                    new TestClassEntityStore.MinNumberPredicate(5), 0, 25, descending);
                var (mat, matCount) = await Store.TestClasses.QuerySortCountNumberMat(
                    new TestClassEntityStore.MinNumberPredicate(5), 0, 25, descending);

                Assert.Equal(scanCount, matCount);
                Assert.Equal(scan.Select(x => x.Number), mat.Select(x => x.Number));
            }
        }

        [Fact]
        public async Task MaterializedSort_NoPredicateOverload()
        {
            await SeedRandomAsync(30);
            var all = await Store.TestClasses.GetAll();

            var (items, count) = await Store.TestClasses.QuerySortCountNumberMat(0, 30, descending: false);
            Assert.Equal(30, count);
            Assert.Equal(all.OrderBy(x => x.Number).Select(x => x.Number), items.Select(x => x.Number));

            var window = await Store.TestClasses.QuerySortNumberMat(5, 10, descending: true);
            Assert.Equal(all.OrderByDescending(x => x.Number).Skip(5).Take(10).Select(x => x.Number),
                window.Select(x => x.Number));
        }

        [Fact]
        public async Task MaterializedSort_TracksCreateUpdateDelete()
        {
            var a = await Store.TestClasses.Create(NewTestClass("a", 5));
            var b = await Store.TestClasses.Create(NewTestClass("b", 1));
            var c = await Store.TestClasses.Create(NewTestClass("c", 9));

            Assert.Equal(new[] { 1, 5, 9 }, (await Store.TestClasses.QuerySortNumberMat(0, 10)).Select(x => x.Number));

            // key change moves the entity within the sorted snapshot
            var entity = Store.TestClasses.GetById(b.Id).GetEntity();
            entity.Number = 7;
            await Store.TestClasses.Update(entity);
            Assert.Equal(new[] { 5, 7, 9 }, (await Store.TestClasses.QuerySortNumberMat(0, 10)).Select(x => x.Number));

            // non-key update replaces in place (same position, fresh snapshot)
            entity = Store.TestClasses.GetById(b.Id).GetEntity();
            entity.TestName = "b2";
            await Store.TestClasses.Update(entity);
            Assert.Equal("b2", (await Store.TestClasses.QuerySortNumberMat(0, 10))[1].TestName);

            await Store.TestClasses.Delete(Store.TestClasses.GetById(c.Id).GetEntity());
            Assert.Equal(new[] { 5, 7 }, (await Store.TestClasses.QuerySortNumberMat(0, 10)).Select(x => x.Number));
        }

        [Fact]
        public async Task MaterializedSort_TracksStagedCommits()
        {
            var a = await Store.TestClasses.Create(NewTestClass("a", 5));

            var updated = Store.TestClasses.GetById(a.Id).GetEntity();
            updated.Number = 2;

            var transaction = Store.StartTransaction();
            Store.TestClasses.StageCreate(transaction, NewTestClass("staged", 4));
            Store.TestClasses.StageUpdate(transaction, updated);
            await transaction.Commit();

            Assert.Equal(new[] { 2, 4 }, (await Store.TestClasses.QuerySortNumberMat(0, 10)).Select(x => x.Number));
        }

        [Fact]
        public async Task MaterializedSort_SecondStoreRebuilds()
        {
            await Store.TestClasses.Create(NewTestClass("x", 9));
            await Store.TestClasses.Create(NewTestClass("y", 2));

            var second = new TestStore(DbPath);
            Assert.Equal(new[] { 2, 9 }, (await second.TestClasses.QuerySortNumberMat(0, 10)).Select(x => x.Number));
        }

        [Fact]
        public async Task MaterializedSort_RangeOperations()
        {
            var ros = await Store.TestClasses.CreateRange(new[]
            {
                NewTestClass("r1", 8), NewTestClass("r2", 3), NewTestClass("r3", 6),
            });
            Assert.Equal(new[] { 3, 6, 8 }, (await Store.TestClasses.QuerySortNumberMat(0, 10)).Select(x => x.Number));

            var e1 = Store.TestClasses.GetById(ros[0].Id).GetEntity();
            var e2 = Store.TestClasses.GetById(ros[1].Id).GetEntity();
            e1.Number = 1;
            e2.Number = 9;
            await Store.TestClasses.UpdateRange(new[] { e1, e2 });
            Assert.Equal(new[] { 1, 6, 9 }, (await Store.TestClasses.QuerySortNumberMat(0, 10)).Select(x => x.Number));

            await Store.TestClasses.DeleteRange(new[]
            {
                Store.TestClasses.GetById(ros[0].Id).GetEntity(),
                Store.TestClasses.GetById(ros[2].Id).GetEntity(),
            });
            Assert.Equal(new[] { 9 }, (await Store.TestClasses.QuerySortNumberMat(0, 10)).Select(x => x.Number));
        }
    }
}
