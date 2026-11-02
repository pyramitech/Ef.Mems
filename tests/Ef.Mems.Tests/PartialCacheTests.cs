using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ef.Mems.Tests
{
    /// <summary>
    /// UntestedClass caches only {Id, TestName} (Description is omitted), so
    /// the generator emits an async GetEntity that fetches the full entity
    /// from the context instead of cloning the incomplete cached copy.
    /// TestClass caches all properties, so its GetEntity stays synchronous.
    /// </summary>
    public class PartialCacheTests : StoreTestBase
    {
        [Fact]
        public async Task GetEntity_ReturnsUncachedProperties()
        {
            var uc = await Store.UntestedClasses.Create(new UntestedClass
            {
                TestName = "uc",
                Description = "not cached, but stored",
                UntestedTests = new List<UntestedTest>(),
            });

            var full = await Store.UntestedClasses.GetById(uc.Id).GetEntity();

            Assert.Equal("uc", full.TestName);
            Assert.Equal("not cached, but stored", full.Description);
        }

        [Fact]
        public async Task GetEntity_ReadsCurrentDatabaseState()
        {
            var uc = await Store.UntestedClasses.Create(new UntestedClass
            {
                TestName = "uc",
                Description = "old",
                UntestedTests = new List<UntestedTest>(),
            });

            // Change the row behind the store's back; GetEntity must see it
            // without a Refresh because it queries the context directly.
            using (var ctx = Store.GetContext())
            {
                var row = await ctx.UntestedClasses.SingleAsync(x => x.Id == uc.Id);
                row.Description = "new";
                await ctx.SaveChangesAsync();
            }

            var full = await Store.UntestedClasses.GetById(uc.Id).GetEntity();
            Assert.Equal("new", full.Description);
        }

        [Fact]
        public async Task GetEntity_IncludesDeclaredNavigations()
        {
            var tc = await Store.TestClasses.Create(NewTestClass("tc"));
            var uc = await Store.UntestedClasses.Create(new UntestedClass
            {
                TestName = "uc",
                UntestedTests = new List<UntestedTest>
                {
                    new UntestedTest { TestClassId = tc.Id, JoinData = "join" },
                },
            });

            var full = await Store.UntestedClasses.GetById(uc.Id).GetEntity();

            var join = Assert.Single(full.UntestedTests);
            Assert.Equal(tc.Id, join.TestClassId);
            Assert.Equal("join", join.JoinData);
        }

        [Fact]
        public async Task Refresh_ProjectedLoad_PopulatesCacheAndNavigations()
        {
            // Seed through the first store, then boot a second store on the
            // same file: its Refresh runs the partial-column projection over
            // real rows (cached props + index keys + declared navs only).
            var tc = await Store.TestClasses.Create(NewTestClass("tc"));
            var uc = await Store.UntestedClasses.Create(new UntestedClass
            {
                TestName = "uc",
                Description = "only in db",
                UntestedTests = new List<UntestedTest>
                {
                    new UntestedTest { TestClassId = tc.Id, JoinData = "join" },
                },
            });

            var second = new TestStore(DbPath);
            var ro = second.UntestedClasses.GetById(uc.Id);

            Assert.Equal("uc", ro.TestName);
            Assert.Equal("join", Assert.Single(ro.UntestedTests).JoinData);
            // The uncached column still comes back through the async fetch.
            Assert.Equal("only in db", (await ro.GetEntity()).Description);
        }

        [Fact]
        public async Task Update_PreservesUncachedColumns()
        {
            var uc = await Store.UntestedClasses.Create(new UntestedClass
            {
                TestName = "before",
                Description = "must survive",
                UntestedTests = new List<UntestedTest>(),
            });

            // The scalar diff only marks cached properties IsModified, so an
            // update must never overwrite uncached columns.
            var entity = await Store.UntestedClasses.GetById(uc.Id).GetEntity();
            entity.TestName = "after";
            await Store.UntestedClasses.Update(entity);

            using var ctx = Store.GetContext();
            var row = await ctx.UntestedClasses.SingleAsync(x => x.Id == uc.Id);
            Assert.Equal("after", row.TestName);
            Assert.Equal("must survive", row.Description);
        }

        [Fact]
        public async Task GetEntity_ReturnsNullWhenRowDeletedBehindCache()
        {
            var uc = await Store.UntestedClasses.Create(new UntestedClass
            {
                TestName = "uc",
                UntestedTests = new List<UntestedTest>(),
            });

            using (var ctx = Store.GetContext())
            {
                ctx.Remove(await ctx.UntestedClasses.SingleAsync(x => x.Id == uc.Id));
                await ctx.SaveChangesAsync();
            }

            // Cache is stale (still has the ReadOnly), but the row is gone.
            var full = await Store.UntestedClasses.GetById(uc.Id).GetEntity();
            Assert.Null(full);
        }
    }
}
