using Xunit;

namespace Ef.Mems.Tests
{
    /// <summary>
    /// Covers the struct predicate query surface: named methods generated
    /// from UsePredicate declarations, hand-written IPredicate structs via
    /// the generic overloads, and the delegate overloads staying available.
    /// </summary>
    public class PredicateQueryTests : StoreTestBase
    {
        [Fact]
        public async Task NamedPredicate_WithoutArgs_Filters()
        {
            await SeedAsync(3); // Number = 1..3
            await Store.TestClasses.Create(NewTestClass("zero", number: 0));

            var active = await Store.TestClasses.QueryActive();

            Assert.Equal(new[] { 1, 2, 3 }, active.Select(x => x.Number).OrderBy(x => x));
        }

        [Fact]
        public async Task NamedPredicate_WithArg_Filters()
        {
            await SeedAsync(5);

            var result = await Store.TestClasses.QueryMinNumber(3);

            Assert.Equal(new[] { 3, 4, 5 }, result.Select(x => x.Number).OrderBy(x => x));
        }

        [Fact]
        public async Task NamedPredicate_SkipTake_Pages()
        {
            await SeedAsync(5);

            var page = await Store.TestClasses.QueryMinNumber(2, skip: 1, take: 2);

            Assert.Equal(new[] { 3, 4 }, page.Select(x => x.Number));
        }

        [Fact]
        public async Task NamedPredicate_QueryCount_ReturnsTotalMatches()
        {
            await SeedAsync(5);

            var (items, matchCount) = await Store.TestClasses.QueryCountMinNumber(2, skip: 0, take: 2);

            Assert.Equal(2, items.Count);
            Assert.Equal(4, matchCount);
        }

        [Fact]
        public async Task NamedPredicate_QuerySortCount_SortsAndCounts()
        {
            await SeedAsync(5);

            var (items, matchCount) = await Store.TestClasses.QuerySortCountMinNumber(
                2, skip: 0, take: 2, keySelector: x => x.Number, descending: true);

            Assert.Equal(new[] { 5, 4 }, items.Select(x => x.Number));
            Assert.Equal(4, matchCount);
        }

        [Fact]
        public async Task NamedPredicate_WithEnumLiteralBody_Works()
        {
            var sour = NewTestClass("sour", number: 1);
            sour.Flavor = Flavor.Sour;
            await Store.TestClasses.Create(sour);
            await Store.TestClasses.Create(NewTestClass("sweet", number: 2)); // NewTestClass defaults to Sweet

            var found = await Store.TestClasses.FirstOrDefaultSweet();

            Assert.Equal("sweet", found?.TestName);
        }

        private readonly struct NumberAbovePredicate : IPredicate<TestClassReadOnly>
        {
            private readonly int _min;
            public NumberAbovePredicate(int min) => _min = min;
            public bool Invoke(TestClassReadOnly item) => item.Number > _min;
        }

        [Fact]
        public async Task HandWrittenStructPredicate_WorksViaGenericOverloads()
        {
            await SeedAsync(5);

            var result = await Store.TestClasses.Query(new NumberAbovePredicate(3));
            Assert.Equal(new[] { 4, 5 }, result.Select(x => x.Number).OrderBy(x => x));

            var (items, matchCount) = await Store.TestClasses.QueryCount(new NumberAbovePredicate(1), skip: 0, take: 2);
            Assert.Equal(2, items.Count);
            Assert.Equal(4, matchCount);

            var first = await Store.TestClasses.FirstOrDefault(new NumberAbovePredicate(4));
            Assert.Equal(5, first?.Number);
        }

        [Fact]
        public async Task DelegatePredicates_RemainAvailable()
        {
            await SeedAsync(3);

            // The dynamic overloads must keep working next to the struct ones.
            int min = 2;
            var viaDelegate = await Store.TestClasses.Query(x => x.Number >= min);
            var viaStruct = await Store.TestClasses.QueryMinNumber(min);

            Assert.Equal(viaStruct.Select(x => x.Id).OrderBy(x => x), viaDelegate.Select(x => x.Id).OrderBy(x => x));
        }
    }
}
