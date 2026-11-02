using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ef.Mems.Tests
{
    /// <summary>
    /// UseMany child mutations beyond "add": the nav diff must persist child
    /// scalar changes, delete removed children (cache AND database), and
    /// handle add+modify+remove in a single update - for single-key children
    /// (TestSubClass) and composite-key children (CompositeChild).
    /// </summary>
    public class NavMutationTests : StoreTestBase
    {
        private async Task<int> CreateWithSubs(params string[] subs)
        {
            var created = NewTestClass("parent");
            foreach (var s in subs)
                created.TestSubClasses.Add(new TestSubClass { SubString = s });
            var ro = await Store.TestClasses.Create(created);
            return ro.Id;
        }

        [Fact]
        public async Task Update_RemovesChild_FromCacheAndDatabase()
        {
            var id = await CreateWithSubs("keep", "drop");

            var entity = Store.TestClasses.GetById(id).GetEntity();
            entity.TestSubClasses.RemoveAll(x => x.SubString == "drop");
            await Store.TestClasses.Update(entity);

            Assert.Equal("keep", Assert.Single(Store.TestClasses.GetById(id).TestSubClasses).SubString);
            using var ctx = Store.GetContext();
            Assert.Equal(1, await ctx.Set<TestSubClass>().CountAsync());
        }

        [Fact]
        public async Task Update_ModifiesChildScalar()
        {
            var id = await CreateWithSubs("before");

            var entity = Store.TestClasses.GetById(id).GetEntity();
            entity.TestSubClasses.Single().SubString = "after";
            await Store.TestClasses.Update(entity);

            Assert.Equal("after", Store.TestClasses.GetById(id).TestSubClasses.Single().SubString);
            using var ctx = Store.GetContext();
            Assert.Equal("after", (await ctx.Set<TestSubClass>().SingleAsync()).SubString);
        }

        [Fact]
        public async Task Update_AddModifyRemove_InOneUpdate()
        {
            var id = await CreateWithSubs("modify-me", "remove-me");

            var entity = Store.TestClasses.GetById(id).GetEntity();
            entity.TestSubClasses.First(x => x.SubString == "modify-me").SubString = "modified";
            entity.TestSubClasses.RemoveAll(x => x.SubString == "remove-me");
            entity.TestSubClasses.Add(new TestSubClass { SubString = "added" });
            await Store.TestClasses.Update(entity);

            var subs = Store.TestClasses.GetById(id).TestSubClasses.Select(x => x.SubString).OrderBy(x => x);
            Assert.Equal(new[] { "added", "modified" }, subs);
            using var ctx = Store.GetContext();
            Assert.Equal(new[] { "added", "modified" },
                (await ctx.Set<TestSubClass>().Select(x => x.SubString).ToListAsync()).OrderBy(x => x));
        }

        [Fact]
        public async Task StagedUpdate_RemovesAndModifiesChildren()
        {
            var id = await CreateWithSubs("modify-me", "remove-me");

            var entity = Store.TestClasses.GetById(id).GetEntity();
            entity.TestSubClasses.First(x => x.SubString == "modify-me").SubString = "modified";
            entity.TestSubClasses.RemoveAll(x => x.SubString == "remove-me");

            var transaction = Store.StartTransaction();
            Store.TestClasses.StageUpdate(transaction, entity);
            await transaction.Commit();

            Assert.Equal("modified", Assert.Single(Store.TestClasses.GetById(id).TestSubClasses).SubString);
            using var ctx = Store.GetContext();
            Assert.Equal(1, await ctx.Set<TestSubClass>().CountAsync());
        }

        // ---- Composite-key children ----------------------------------------

        [Fact]
        public async Task CompositeKey_CreateAndLookup()
        {
            var ro = await Store.CompositeParents.Create(new CompositeParent
            {
                Name = "p",
                Slots = new List<CompositeChild>
                {
                    new CompositeChild { Slot = 1, Data = "one" },
                    new CompositeChild { Slot = 2, Data = "two" },
                },
            });

            var cached = Store.CompositeParents.GetById(ro.Id);
            Assert.Equal(2, cached.Slots.Count);
            Assert.Equal("one", cached.Slots.Single(x => x.Slot == 1).Data);
            // FK fixup filled the composite key half on insert.
            Assert.All(cached.Slots, s => Assert.Equal(ro.Id, s.CompositeParentId));
        }

        [Fact]
        public async Task CompositeKey_AddModifyRemove_InOneUpdate()
        {
            var ro = await Store.CompositeParents.Create(new CompositeParent
            {
                Name = "p",
                Slots = new List<CompositeChild>
                {
                    new CompositeChild { Slot = 1, Data = "one" },
                    new CompositeChild { Slot = 2, Data = "two" },
                },
            });

            var entity = Store.CompositeParents.GetById(ro.Id).GetEntity();
            entity.Slots.Single(x => x.Slot == 1).Data = "uno";        // modify
            entity.Slots.RemoveAll(x => x.Slot == 2);                  // remove
            entity.Slots.Add(new CompositeChild { Slot = 3, Data = "three" }); // add
            await Store.CompositeParents.Update(entity);

            var cached = Store.CompositeParents.GetById(ro.Id);
            Assert.Equal(new[] { 1, 3 }, cached.Slots.Select(x => x.Slot).OrderBy(x => x));
            Assert.Equal("uno", cached.Slots.Single(x => x.Slot == 1).Data);

            using var ctx = Store.GetContext();
            var rows = await ctx.Set<CompositeChild>().ToListAsync();
            Assert.Equal(new[] { 1, 3 }, rows.Select(x => x.Slot).OrderBy(x => x));
            Assert.Equal("three", rows.Single(x => x.Slot == 3).Data);
        }

        [Fact]
        public async Task CompositeKey_SecondStoreReloads()
        {
            var ro = await Store.CompositeParents.Create(new CompositeParent
            {
                Name = "p",
                Slots = new List<CompositeChild> { new CompositeChild { Slot = 1, Data = "one" } },
            });

            var second = new TestStore(DbPath);
            var cached = second.CompositeParents.GetById(ro.Id);
            Assert.Equal("p", cached.Name);
            Assert.Equal("one", Assert.Single(cached.Slots).Data);
        }
    }
}
