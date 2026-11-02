using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ef.Mems.Tests
{
    /// <summary>
    /// Composite (multi-property) indexes: RegionalCode declares
    /// UseIndex(x => new { x.Region, x.Code }), generating a value-tuple-
    /// keyed dictionary and GetByRegionCode(region, code).
    /// </summary>
    public class CompositeIndexTests : StoreTestBase
    {
        private Task<RegionalCodeReadOnly> CreateAsync(string region, string code, string payload = "p")
            => Store.RegionalCodes.Create(new RegionalCode { Region = region, Code = code, Payload = payload });

        [Fact]
        public async Task Create_LookupByCompositeKey()
        {
            await CreateAsync("eu", "a1", "one");
            await CreateAsync("eu", "a2", "two");
            await CreateAsync("us", "a1", "three");

            Assert.Equal("one", Store.RegionalCodes.GetByRegionCode("eu", "a1").Payload);
            Assert.Equal("three", Store.RegionalCodes.GetByRegionCode("us", "a1").Payload);
            Assert.Null(Store.RegionalCodes.GetByRegionCode("us", "a2"));
            Assert.Null(Store.RegionalCodes.GetByRegionCode("apac", "a1"));
        }

        [Fact]
        public async Task Update_ChangedKeyMember_MovesCompositeKey_AndPersists()
        {
            var ro = await CreateAsync("eu", "a1");

            var entity = Store.RegionalCodes.GetById(ro.Id).GetEntity();
            entity.Code = "b7";
            await Store.RegionalCodes.Update(entity);

            Assert.Null(Store.RegionalCodes.GetByRegionCode("eu", "a1"));
            Assert.Equal(ro.Id, Store.RegionalCodes.GetByRegionCode("eu", "b7").Id);

            // The changed index member persists to the database.
            using var ctx = Store.GetContext();
            var row = await ctx.RegionalCodes.SingleAsync(x => x.Id == ro.Id);
            Assert.Equal("b7", row.Code);
            Assert.Equal("eu", row.Region);
        }

        [Fact]
        public async Task Update_UnchangedKey_UpdatesInPlace()
        {
            var ro = await CreateAsync("eu", "a1", "before");

            var entity = Store.RegionalCodes.GetById(ro.Id).GetEntity();
            entity.Payload = "after";
            await Store.RegionalCodes.Update(entity);

            Assert.Equal("after", Store.RegionalCodes.GetByRegionCode("eu", "a1").Payload);
        }

        [Fact]
        public async Task StagedUpdate_ChangedKeyMember_MovesCompositeKey()
        {
            var ro = await CreateAsync("eu", "a1");

            var entity = Store.RegionalCodes.GetById(ro.Id).GetEntity();
            entity.Region = "us";
            var transaction = Store.StartTransaction();
            Store.RegionalCodes.StageUpdate(transaction, entity);
            await transaction.Commit();

            Assert.Null(Store.RegionalCodes.GetByRegionCode("eu", "a1"));
            Assert.Equal(ro.Id, Store.RegionalCodes.GetByRegionCode("us", "a1").Id);
        }

        [Fact]
        public async Task Delete_RemovesCompositeKey()
        {
            var ro = await CreateAsync("eu", "a1");

            await Store.RegionalCodes.Delete(Store.RegionalCodes.GetById(ro.Id).GetEntity());

            Assert.Null(Store.RegionalCodes.GetByRegionCode("eu", "a1"));
            Assert.Null(Store.RegionalCodes.GetById(ro.Id));
            Assert.Equal(0, Store.RegionalCodes.Count);
        }

        [Fact]
        public async Task SecondStore_ReloadsCompositeIndex()
        {
            await CreateAsync("eu", "a1", "persisted");

            var second = new TestStore(DbPath);
            Assert.Equal("persisted", second.RegionalCodes.GetByRegionCode("eu", "a1").Payload);
        }

        [Fact]
        public async Task SecondaryIndexChange_NowPersistsToDatabase()
        {
            // Regression for the NonIndexProperties fix: changing a
            // single-property secondary index value must reach the DB too
            // (previously it only moved the cache key).
            var tc = await Store.TestClasses.Create(NewTestClass("a", 1));

            var entity = Store.TestClasses.GetById(tc.Id).GetEntity();
            entity.IndexedName = "persisted-index";
            await Store.TestClasses.Update(entity);

            using var ctx = Store.GetContext();
            var row = await ctx.TestClasses.SingleAsync(x => x.Id == tc.Id);
            Assert.Equal("persisted-index", row.IndexedName);
        }
    }
}
