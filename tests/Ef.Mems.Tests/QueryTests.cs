using Xunit;

namespace Ef.Mems.Tests
{
    public class QueryTests : StoreTestBase
    {
        [Fact]
        public async Task Query_FiltersByPredicate()
        {
            await SeedAsync(5);

            var result = await Store.TestClasses.Query(x => x.Number > 2);

            Assert.Equal(new[] { 3, 4, 5 }, result.Select(x => x.Number).OrderBy(x => x));
        }

        [Fact]
        public async Task Query_SkipTake_PagesInIndexOrder()
        {
            await SeedAsync(5);

            var page = await Store.TestClasses.Query(x => true, skip: 1, take: 2);

            Assert.Equal(new[] { 2, 3 }, page.Select(x => x.Id));
        }

        [Fact]
        public async Task Query_SkipTakeWithoutPredicate()
        {
            await SeedAsync(4);

            var page = await Store.TestClasses.Query(skip: 2, take: 5);

            Assert.Equal(new[] { 3, 4 }, page.Select(x => x.Id));
        }

        [Fact]
        public async Task QueryCount_ReturnsPageAndTotalMatches()
        {
            await SeedAsync(5);

            var (items, matchCount) = await Store.TestClasses.QueryCount(x => x.Number >= 2, skip: 0, take: 2);

            Assert.Equal(2, items.Count);
            Assert.Equal(4, matchCount);
        }

        [Fact]
        public async Task QuerySortCount_SortsDescendingAndCounts()
        {
            await SeedAsync(5);

            var (items, matchCount) = await Store.TestClasses.QuerySortCount(
                x => true, skip: 0, take: 3, keySelector: x => x.Number, descending: true);

            Assert.Equal(new[] { 5, 4, 3 }, items.Select(x => x.Number));
            Assert.Equal(5, matchCount);
        }

        [Fact]
        public async Task QuerySortCount_SkipShiftsTheWindow()
        {
            await SeedAsync(5);

            var (items, matchCount) = await Store.TestClasses.QuerySortCount(
                x => true, skip: 1, take: 2, keySelector: x => x.Number, descending: true);

            Assert.Equal(new[] { 4, 3 }, items.Select(x => x.Number));
            Assert.Equal(5, matchCount);
        }

        [Fact]
        public async Task QuerySortCount_TakeZero_DoesNotCountDeletedSlots()
        {
            await SeedAsync(4);
            await Store.TestClasses.Delete(Store.TestClasses.GetById(2).GetEntity());

            var (items, matchCount) = await Store.TestClasses.QuerySortCount(
                x => true, skip: 0, take: 0, keySelector: x => x.Number);

            Assert.Empty(items);
            Assert.Equal(3, matchCount); // deleted slots must not inflate the count
        }

        [Fact]
        public async Task GetAll_SkipsDeletedSlots()
        {
            await SeedAsync(3);
            await Store.TestClasses.Delete(Store.TestClasses.GetById(2).GetEntity());

            var all = await Store.TestClasses.GetAll();

            Assert.Equal(new[] { 1, 3 }, all.Select(x => x.Id));
        }

        [Fact]
        public async Task FirstOrDefault_FindsMatchOrReturnsNull()
        {
            await SeedAsync(3);

            var found = await Store.TestClasses.FirstOrDefault(x => x.Number == 2);
            Assert.Equal(2, found?.Id);

            var missing = await Store.TestClasses.FirstOrDefault(x => x.Number == 42);
            Assert.Null(missing);
        }
    }
}
