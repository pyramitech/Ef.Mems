using Xunit;

namespace Ef.Mems.Tests
{
    /// <summary>
    /// UseUnion coverage: the AllTransactions view spans ActiveTransactions
    /// and ArchivedTransactions in declaration order through the generated
    /// IAllTransactionsReadOnly interface. Paging must be exact across the
    /// store boundary, sorted queries globally ordered, and the view must
    /// reflect writes made through the member stores.
    /// </summary>
    public class UnionTests : StoreTestBase
    {
        private Task<ActiveTransactionReadOnly> Active(decimal amount, string reference)
            => Store.ActiveTransactions.Create(new ActiveTransaction { Amount = amount, Reference = reference, ProcessorState = "live" });

        private Task<ArchivedTransactionReadOnly> Archived(decimal amount, string reference)
            => Store.ArchivedTransactions.Create(new ArchivedTransaction { Amount = amount, Reference = reference, ArchivedOn = DateTime.UtcNow });

        /// <summary>5 active (A1..A5, amounts 10..50) and 5 archived (B1..B5, amounts 15..55).</summary>
        private async Task SeedBoth()
        {
            for (int i = 1; i <= 5; i++) await Active(i * 10, "A" + i);
            for (int i = 1; i <= 5; i++) await Archived(i * 10 + 5, "B" + i);
        }

        [Fact]
        public async Task Count_SumsMembers()
        {
            await SeedBoth();
            Assert.Equal(10, Store.AllTransactions.Count);
        }

        [Fact]
        public async Task Query_PagesAcrossStoreBoundary()
        {
            await SeedBoth();

            // Declaration order: A1..A5 then B1..B5. skip 3, take 4 spans the boundary.
            var page = await Store.AllTransactions.Query(x => true, 3, 4);
            Assert.Equal(new[] { "A4", "A5", "B1", "B2" }, page.Select(x => x.Reference));
        }

        [Fact]
        public async Task Query_SkipConsumedEntirelyInFirstStore_SecondStoreUsesPlainScan()
        {
            await SeedBoth();

            var page = await Store.AllTransactions.Query(x => x.Amount > 20, 1, 10);
            // Matches: A3(30), A4(40), A5(50), B2(25), B3(35), B4(45), B5(55); skip 1 drops A3.
            Assert.Equal(new[] { "A4", "A5", "B2", "B3", "B4", "B5" }, page.Select(x => x.Reference));
        }

        [Fact]
        public async Task Query_SkipLargerThanFirstStoreMatches()
        {
            await SeedBoth();

            // Only 5 active rows match; skip 7 must consume 2 archived matches too.
            var page = await Store.AllTransactions.Query(x => true, 7, 10);
            Assert.Equal(new[] { "B3", "B4", "B5" }, page.Select(x => x.Reference));
        }

        [Fact]
        public async Task QueryCount_TotalsAcrossMembers()
        {
            await SeedBoth();

            var (items, matchCount) = await Store.AllTransactions.QueryCount(x => x.Amount >= 30, 0, 3);
            Assert.Equal(6, matchCount); // A3,A4,A5,B3,B4,B5
            Assert.Equal(new[] { "A3", "A4", "A5" }, items.Select(x => x.Reference));
        }

        [Fact]
        public async Task FirstOrDefault_FallsThroughToSecondStore()
        {
            await SeedBoth();

            var hit = await Store.AllTransactions.FirstOrDefault(x => x.Amount == 45);
            Assert.Equal("B4", hit.Reference);

            Assert.Null(await Store.AllTransactions.FirstOrDefault(x => x.Amount == 999));
        }

        [Fact]
        public async Task QuerySortCount_GloballyOrdersAcrossMembers()
        {
            await SeedBoth();

            // Amounts interleave: 10,15,20,25,... Global ascending skip 1 take 4.
            var (items, matchCount) = await Store.AllTransactions.QuerySortCount(
                x => true, 1, 4, x => x.Amount);
            Assert.Equal(10, matchCount);
            Assert.Equal(new decimal[] { 15, 20, 25, 30 }, items.Select(x => x.Amount));

            var (desc, _) = await Store.AllTransactions.QuerySortCount(
                x => true, 0, 3, x => x.Amount, descending: true);
            Assert.Equal(new decimal[] { 55, 50, 45 }, desc.Select(x => x.Amount));
        }

        [Fact]
        public async Task QuerySort_WithPredicate()
        {
            await SeedBoth();

            var items = await Store.AllTransactions.QuerySort(
                x => x.Reference.StartsWith("B") || x.Amount >= 40, 0, 10, x => x.Amount);
            Assert.Equal(new[] { "B1", "B2", "B3", "A4", "B4", "A5", "B5" }, items.Select(x => x.Reference));
        }

        [Fact]
        public async Task Query_Unfiltered_PagesAcrossBoundary()
        {
            await SeedBoth();

            var page = await Store.AllTransactions.Query(4, 3);
            Assert.Equal(new[] { "A5", "B1", "B2" }, page.Select(x => x.Reference));

            // Skip past everything.
            Assert.Empty(await Store.AllTransactions.Query(10, 5));
        }

        [Fact]
        public async Task Union_ReflectsLaterWrites()
        {
            Assert.Equal(0, Store.AllTransactions.Count);
            Assert.Empty(await Store.AllTransactions.Query(x => true, 0, 10));

            await Archived(99, "late");
            Assert.Equal(1, Store.AllTransactions.Count);
            Assert.Equal("late", (await Store.AllTransactions.FirstOrDefault(x => true)).Reference);
        }

        [Fact]
        public async Task InterfaceExposesSharedPropsOnly_ValuesIntact()
        {
            var a = await Active(12.5m, "iface");
            IAllTransactionsReadOnly viaUnion = await Store.AllTransactions.FirstOrDefault(x => x.Reference == "iface");

            Assert.Equal(a.Id, viaUnion.Id);
            Assert.Equal(12.5m, viaUnion.Amount);
            // Non-shared props (ProcessorState/ArchivedOn) are reachable by
            // downcasting to the member ReadOnly, not on the interface.
            var concrete = Assert.IsType<ActiveTransactionReadOnly>(viaUnion);
            Assert.Equal("live", concrete.ProcessorState);
        }
    }

    /// <summary>Direct unit tests for the k-way merge behind union sorted queries.</summary>
    public class UnionMergeSortedTests
    {
        [Fact]
        public void Merges_SlicesAndRespectsDirection()
        {
            var a = new[] { 1, 4, 7 };
            var b = new[] { 2, 5, 8 };
            var c = new[] { 3, 6, 9 };

            var merged = UnionQuery.MergeSorted(new IReadOnlyList<int>[] { a, b, c }, x => x, null, false, 2, 4);
            Assert.Equal(new[] { 3, 4, 5, 6 }, merged);

            var desc = UnionQuery.MergeSorted(
                new IReadOnlyList<int>[] { new[] { 7, 4, 1 }, new[] { 8, 5, 2 } }, x => x, null, true, 0, 3);
            Assert.Equal(new[] { 8, 7, 5 }, desc);
        }

        [Fact]
        public void HandlesEmptyAndExhaustedSources()
        {
            var merged = UnionQuery.MergeSorted(
                new IReadOnlyList<int>[] { Array.Empty<int>(), new[] { 1, 2 } }, x => x, null, false, 0, 10);
            Assert.Equal(new[] { 1, 2 }, merged);

            Assert.Empty(UnionQuery.MergeSorted(
                new IReadOnlyList<int>[] { Array.Empty<int>() }, x => x, null, false, 0, 5));

            Assert.Empty(UnionQuery.MergeSorted(
                new IReadOnlyList<int>[] { new[] { 1 } }, x => x, null, false, 5, 5));
        }
    }
}
