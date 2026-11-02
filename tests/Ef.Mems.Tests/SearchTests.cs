using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ef.Mems.Tests
{
    /// <summary>
    /// SearchExtension wiring: TestClass is ISearchable and the store declares
    /// UseExtension(typeof(SearchExtension), x => new { x.TestName, x.Number }),
    /// so SearchField is rebuilt (lower-cased, space-joined) on every create
    /// and update, and the "Search" predicate scans it from the cache.
    /// </summary>
    public class SearchTests : StoreTestBase
    {
        [Fact]
        public async Task Create_BuildsSearchField()
        {
            var tc = await Store.TestClasses.Create(NewTestClass("Alpha", 7));

            Assert.Equal("alpha 7", Store.TestClasses.GetById(tc.Id).SearchField);
        }

        [Fact]
        public async Task Create_PersistsSearchFieldToDatabase()
        {
            var tc = await Store.TestClasses.Create(NewTestClass("Alpha", 7));

            using var ctx = Store.GetContext();
            var row = await ctx.TestClasses.AsNoTracking().SingleAsync(x => x.Id == tc.Id);
            Assert.Equal("alpha 7", row.SearchField);
        }

        [Fact]
        public async Task Update_RebuildsSearchField()
        {
            var tc = await Store.TestClasses.Create(NewTestClass("Alpha", 7));

            var entity = Store.TestClasses.GetById(tc.Id).GetEntity();
            entity.TestName = "Bravo";
            await Store.TestClasses.Update(entity);

            Assert.Equal("bravo 7", Store.TestClasses.GetById(tc.Id).SearchField);

            using var ctx = Store.GetContext();
            var row = await ctx.TestClasses.AsNoTracking().SingleAsync(x => x.Id == tc.Id);
            Assert.Equal("bravo 7", row.SearchField);
        }

        [Fact]
        public async Task StagedUpdate_RebuildsSearchField()
        {
            var tc = await Store.TestClasses.Create(NewTestClass("Alpha", 7));

            var entity = Store.TestClasses.GetById(tc.Id).GetEntity();
            entity.TestName = "Charlie";
            var transaction = Store.StartTransaction();
            Store.TestClasses.StageUpdate(transaction, entity);
            await transaction.Commit();

            Assert.Equal("charlie 7", Store.TestClasses.GetById(tc.Id).SearchField);
        }

        [Fact]
        public async Task SearchPredicate_MatchesAnyConfiguredField()
        {
            await Store.TestClasses.Create(NewTestClass("Red Apple", 1));
            await Store.TestClasses.Create(NewTestClass("Green Apple", 2));
            await Store.TestClasses.Create(NewTestClass("Banana", 3));

            var apples = await Store.TestClasses.QuerySearch(SearchQuery.Parse("apple"), 0, 10);
            Assert.Equal(2, apples.Count);

            // Number participates in the search field too.
            var three = await Store.TestClasses.FirstOrDefaultSearch(SearchQuery.Parse("3"));
            Assert.Equal("Banana", three.TestName);

            Assert.Empty(await Store.TestClasses.QuerySearch(SearchQuery.Parse("cherry"), 0, 10));
        }

        [Fact]
        public async Task Search_AllTokensMustMatch()
        {
            await Store.TestClasses.Create(NewTestClass("Red Apple", 1));
            await Store.TestClasses.Create(NewTestClass("Green Apple", 2));
            await Store.TestClasses.Create(NewTestClass("Red Cherry", 3));

            // Token order is irrelevant; every token must occur somewhere.
            var redApples = await Store.TestClasses.QuerySearch(SearchQuery.Parse("apple red"), 0, 10);
            Assert.Equal("Red Apple", Assert.Single(redApples).TestName);

            Assert.Empty(await Store.TestClasses.QuerySearch(SearchQuery.Parse("green cherry"), 0, 10));

            // Empty query matches everything.
            Assert.Equal(3, (await Store.TestClasses.QuerySearch(SearchQuery.Parse("  "), 0, 10)).Count);
        }

        [Fact]
        public async Task Search_LatinizesFieldAndQuery()
        {
            var tc = await Store.TestClasses.Create(NewTestClass("Švestkový Čaj", 9));

            // The stored field is latinized + lower-cased...
            Assert.Equal("svestkovy caj 9", Store.TestClasses.GetById(tc.Id).SearchField);

            // ...and so is the query input, so either alphabet finds it.
            var byLatin = await Store.TestClasses.FirstOrDefaultSearch(SearchQuery.Parse("svestkovy"));
            Assert.Equal(tc.Id, byLatin.Id);
            var byDiacritics = await Store.TestClasses.FirstOrDefaultSearch(SearchQuery.Parse("ŠVESTKOVÝ čaj"));
            Assert.Equal(tc.Id, byDiacritics.Id);
        }

        [Fact]
        public async Task CombinedPredicate_SearchPlusScalarFilter()
        {
            await Store.TestClasses.Create(NewTestClass("Red Apple", 1));
            await Store.TestClasses.Create(NewTestClass("Green Apple", 5));
            await Store.TestClasses.Create(NewTestClass("Banana", 9));

            // "SearchActive" combines Number >= min && search in one struct.
            var hits = await Store.TestClasses.QuerySearchActive(SearchQuery.Parse("apple"), 4, 0, 10);
            Assert.Equal("Green Apple", Assert.Single(hits).TestName);

            Assert.Empty(await Store.TestClasses.QuerySearchActive(SearchQuery.Parse("banana"), 10, 0, 10));
        }

        [Fact]
        public async Task SearchField_IgnoresCallerProvidedValue()
        {
            // Whatever the caller puts in SearchField is overwritten by the
            // extension before the entity is saved or cached.
            var item = NewTestClass("Echo", 5);
            item.SearchField = "handcrafted nonsense";
            var tc = await Store.TestClasses.Create(item);

            Assert.Equal("echo 5", Store.TestClasses.GetById(tc.Id).SearchField);
        }
    }
}
