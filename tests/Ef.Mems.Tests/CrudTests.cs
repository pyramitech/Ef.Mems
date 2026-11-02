using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ef.Mems.Tests
{
    public class CrudTests : StoreTestBase
    {
        [Fact]
        public async Task Create_AssignsSequentialIdAndIndexes()
        {
            var ro = await Store.TestClasses.Create(NewTestClass("first"));

            Assert.Equal(1, ro.Id);
            Assert.False(string.IsNullOrEmpty(ro.IndexedName)); // filled by the id generator
            Assert.Equal(1, Store.TestClasses.Count);

            Assert.Equal("first", Store.TestClasses.GetById(1)?.TestName);
            Assert.Equal(1, Store.TestClasses.GetByIndexedName(ro.IndexedName)?.Id);
            Assert.Null(Store.TestClasses.GetById(42));
            Assert.Null(Store.TestClasses.GetByIndexedName("no-such-index"));
        }

        [Fact]
        public async Task Create_PersistsToDatabase()
        {
            var ro = await Store.TestClasses.Create(NewTestClass("persisted", number: 7));

            using var ctx = Store.GetContext();
            var row = await ctx.TestClasses.AsNoTracking().SingleAsync();
            Assert.Equal(ro.Id, row.Id);
            Assert.Equal("persisted", row.TestName);
            Assert.Equal(7, row.Number);
            Assert.Equal(Flavor.Sweet, row.Flavor);
            Assert.Equal(new byte[] { 1, 2, 3 }, row.Payload);
        }

        [Fact]
        public async Task CreateRange_AssignsSequentialIds()
        {
            var ros = await Store.TestClasses.CreateRange(new[]
            {
                NewTestClass("a"), NewTestClass("b"), NewTestClass("c"),
            });

            Assert.Equal(new[] { 1, 2, 3 }, ros.Select(x => x.Id));
            Assert.Equal(3, Store.TestClasses.Count);
            Assert.Equal(3, (await Store.TestClasses.GetAll()).Count);
        }

        [Fact]
        public async Task Update_ChangesScalarsAndPersists()
        {
            await Store.TestClasses.Create(NewTestClass("before", number: 1));

            var entity = Store.TestClasses.GetById(1).GetEntity();
            entity.TestName = "after";
            entity.Number = 99;
            entity.Flavor = Flavor.Bitter;
            entity.Payload = new byte[] { 9, 9 }; // exercises the EqualsFast diff path
            await Store.TestClasses.Update(entity);

            var ro = Store.TestClasses.GetById(1);
            Assert.Equal("after", ro.TestName);
            Assert.Equal(99, ro.Number);
            Assert.Equal(Flavor.Bitter, ro.Flavor);
            Assert.Equal(new byte[] { 9, 9 }, ro.Payload.ToArray());

            using var ctx = Store.GetContext();
            var row = await ctx.TestClasses.AsNoTracking().SingleAsync();
            Assert.Equal("after", row.TestName);
            Assert.Equal(99, row.Number);
            Assert.Equal(new byte[] { 9, 9 }, row.Payload);
        }

        [Fact]
        public async Task GetEntity_ReturnsDetachedClone()
        {
            await Store.TestClasses.Create(NewTestClass("original"));

            var clone = Store.TestClasses.GetById(1).GetEntity();
            clone.TestName = "mutated";

            // Mutating the clone must not leak into the cache until Update is called.
            Assert.Equal("original", Store.TestClasses.GetById(1).TestName);
        }

        [Fact]
        public async Task UpdateRange_ChangesAllItems()
        {
            await SeedAsync(3);

            var entities = new List<TestClass>();
            for (int id = 1; id <= 3; id++)
            {
                var entity = Store.TestClasses.GetById(id).GetEntity();
                entity.TestName += "_updated";
                entities.Add(entity);
            }
            await Store.TestClasses.UpdateRange(entities);

            for (int id = 1; id <= 3; id++)
                Assert.EndsWith("_updated", Store.TestClasses.GetById(id).TestName);
        }

        [Fact]
        public async Task Delete_RemovesFromCacheAndDatabase()
        {
            await SeedAsync(2);

            Assert.True(await Store.TestClasses.Delete(Store.TestClasses.GetById(1).GetEntity()));

            Assert.Null(Store.TestClasses.GetById(1));
            Assert.NotNull(Store.TestClasses.GetById(2));
            Assert.Equal(1, Store.TestClasses.Count);

            using var ctx = Store.GetContext();
            Assert.Equal(1, await ctx.TestClasses.CountAsync());
        }

        [Fact]
        public async Task Delete_DoesNotReuseIds()
        {
            await SeedAsync(2);
            await Store.TestClasses.Delete(Store.TestClasses.GetById(1).GetEntity());

            var next = await Store.TestClasses.Create(NewTestClass("next"));

            Assert.Equal(3, next.Id); // the freed slot is a hole, ids keep growing
        }

        [Fact]
        public async Task DeleteRange_RemovesAllItems()
        {
            await SeedAsync(3);

            var doomed = new[]
            {
                Store.TestClasses.GetById(1).GetEntity(),
                Store.TestClasses.GetById(3).GetEntity(),
            };
            Assert.True(await Store.TestClasses.DeleteRange(doomed));

            Assert.Equal(1, Store.TestClasses.Count);
            Assert.Null(Store.TestClasses.GetById(1));
            Assert.NotNull(Store.TestClasses.GetById(2));
            Assert.Null(Store.TestClasses.GetById(3));
        }

        [Fact]
        public async Task ExtensionHooks_AreInvoked()
        {
            var name = $"hooked_{Guid.NewGuid():N}";
            await Store.TestClasses.Create(NewTestClass(name));
            Assert.Contains(name, HookExtension.Created);

            var entity = Store.TestClasses.GetById(1).GetEntity();
            entity.Number = 5;
            await Store.TestClasses.Update(entity);
            Assert.Contains(name, HookExtension.Updated);

            await Store.TestClasses.Delete(Store.TestClasses.GetById(1).GetEntity());
            Assert.Contains(name, HookExtension.Deleted);
        }
    }
}
