using Xunit;

namespace Ef.Mems.Tests
{
    /// <summary>
    /// Truncate(): bulk delete-all with identity reset. Cache and database
    /// both empty afterwards, generated ids restart at 1, Before* hooks are
    /// deliberately NOT fired (TRUNCATE semantics), and the store stays fully
    /// usable for direct and staged writes.
    /// </summary>
    public class TruncateTests : StoreTestBase
    {
        private static string Unique(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N");

        [Fact]
        public async Task Truncate_EmptiesCacheAndDatabase()
        {
            await SeedAsync(3);
            Assert.Equal(3, Store.TestClasses.Count);
            var lastId = (await Store.TestClasses.FirstOrDefault(x => x.TestName == "item3")).Id;

            var deleted = await Store.TestClasses.Truncate();

            Assert.Equal(3, deleted);
            Assert.Equal(0, Store.TestClasses.Count);
            Assert.Null(Store.TestClasses.GetById(lastId));
            Assert.Null(await Store.TestClasses.FirstOrDefault(x => true));

            using var ctx = Store.GetContext();
            Assert.Empty(ctx.TestClasses.ToList());
        }

        [Fact]
        public async Task Truncate_ResetsGeneratedIds()
        {
            await SeedAsync(3);
            await Store.TestClasses.Truncate();

            var ro = await Store.TestClasses.Create(NewTestClass(Unique("fresh"), 1));
            Assert.Equal(1, ro.Id);
        }

        [Fact]
        public async Task Truncate_DoesNotFireDeleteHooks()
        {
            var names = new[] { Unique("t"), Unique("t") };
            await Store.TestClasses.CreateRange(names.Select(n => NewTestClass(n, 1)).ToList());

            await Store.TestClasses.Truncate();

            Assert.All(names, n => Assert.DoesNotContain(n, HookExtension.Deleted));
        }

        [Fact]
        public async Task Truncate_OnEmptyStore_ReturnsZero()
        {
            Assert.Equal(0, await Store.TestClasses.Truncate());
            Assert.Equal(0, Store.TestClasses.Count);
        }

        [Fact]
        public async Task Truncate_ClearsDictionaryIndexes()
        {
            var code = Unique("code");
            var ext = Guid.NewGuid();
            await Store.IndexedClasses.Create(new IndexedClass { Code = code, Label = "x", Value = 1, ExternalId = ext });
            Assert.NotNull(Store.IndexedClasses.GetByCode(code));

            var deleted = await Store.IndexedClasses.Truncate();

            Assert.Equal(1, deleted);
            Assert.Equal(0, Store.IndexedClasses.Count);
            Assert.Null(Store.IndexedClasses.GetByCode(code));
            Assert.Null(Store.IndexedClasses.GetByExternalId(ext));
        }

        [Fact]
        public async Task Truncate_ClearsMaterializedSorts()
        {
            await SeedAsync(5);
            Assert.NotEmpty(await Store.TestClasses.QuerySortNumberMat(0, 10));

            await Store.TestClasses.Truncate();

            Assert.Empty(await Store.TestClasses.QuerySortNumberMat(0, 10));
        }

        [Fact]
        public async Task Truncate_StoreStaysUsable_DirectAndStagedWrites()
        {
            await SeedAsync(2);
            await Store.TestClasses.Truncate();

            var direct = await Store.TestClasses.Create(NewTestClass(Unique("d"), 1));
            Assert.Equal(1, direct.Id);

            var stagedName = Unique("s");
            var trx = Store.StartTransaction();
            Store.TestClasses.StageCreate(trx, NewTestClass(stagedName, 2));
            await trx.Commit();

            var staged = await Store.TestClasses.FirstOrDefault(x => x.TestName == stagedName);
            Assert.Equal(2, staged.Id);
            Assert.Equal(2, Store.TestClasses.Count);
        }

        [Fact]
        public async Task Truncate_ThenRefresh_StaysEmpty()
        {
            await SeedAsync(2);
            await Store.TestClasses.Truncate();
            await Store.TestClasses.Refresh();

            Assert.Equal(0, Store.TestClasses.Count);
            var ro = await Store.TestClasses.Create(NewTestClass(Unique("r"), 1));
            Assert.Equal(1, ro.Id);
        }
    }
}
