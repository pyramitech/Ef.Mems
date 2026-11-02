using Xunit;

namespace Ef.Mems.Tests
{
    /// <summary>
    /// Range operations (with safety clones) and diff edge cases the basic
    /// CRUD tests don't reach.
    /// </summary>
    public class RangeAndEdgeTests : StoreTestBase
    {
        [Fact]
        public async Task CreateRange_AssignsIdsAndDetachesCallers()
        {
            var sources = new[] { NewTestClass("r1", 1), NewTestClass("r2", 2), NewTestClass("r3", 3) };
            var ros = await Store.TestClasses.CreateRange(sources);

            Assert.Equal(3, ros.Count);
            Assert.Equal(new[] { 1, 2, 3 }, ros.Select(x => x.Id));
            Assert.Equal(3, Store.TestClasses.Count);

            // copy-on-insert: mutating the sources cannot corrupt the cache
            sources[0].TestName = "corrupted";
            Assert.Equal("r1", Store.TestClasses.GetById(1).TestName);
        }

        [Fact]
        public async Task UpdateRange_AppliesDiffsAndDetachesCallers()
        {
            await SeedAsync(3);
            var e1 = Store.TestClasses.GetById(1).GetEntity();
            var e2 = Store.TestClasses.GetById(2).GetEntity();
            e1.Number = 100;
            e2.TestName = "renamed";

            var ros = await Store.TestClasses.UpdateRange(new[] { e1, e2 });
            Assert.Equal(2, ros.Count);
            Assert.Equal(100, Store.TestClasses.GetById(1).Number);
            Assert.Equal("renamed", Store.TestClasses.GetById(2).TestName);
            Assert.Equal("item3", Store.TestClasses.GetById(3).TestName);

            e1.Number = 999; // post-update mutation is invisible
            Assert.Equal(100, Store.TestClasses.GetById(1).Number);
        }

        [Fact]
        public async Task DeleteRange_RemovesEntitiesAndCleansJoins()
        {
            var tc1 = await Store.TestClasses.Create(NewTestClass("d1", 1));
            var tc2 = await Store.TestClasses.Create(NewTestClass("d2", 2));
            var uc = await Store.UntestedClasses.Create(new UntestedClass
            {
                TestName = "uc",
                UntestedTests = new List<UntestedTest>
                {
                    new UntestedTest { TestClassId = tc1.Id, JoinData = "j1" },
                    new UntestedTest { TestClassId = tc2.Id, JoinData = "j2" },
                },
            });
            Assert.Equal(2, Store.UntestedClasses.GetById(uc.Id).UntestedTests.Count);

            var removed = await Store.TestClasses.DeleteRange(new[]
            {
                Store.TestClasses.GetById(tc1.Id).GetEntity(),
                Store.TestClasses.GetById(tc2.Id).GetEntity(),
            });

            Assert.True(removed);
            Assert.Equal(0, Store.TestClasses.Count);
            Assert.Null(Store.TestClasses.GetById(tc1.Id));
            // Both joins vanished from the other side too.
            Assert.Empty(Store.UntestedClasses.GetById(uc.Id).UntestedTests);
        }

        [Fact]
        public async Task Update_NullPayloadTransitions()
        {
            // null -> value, value -> null, null -> null: all EqualsFast
            // branches in the scalar diff, none may throw.
            var created = NewTestClass("p", 1);
            created.Payload = null;
            var tc = await Store.TestClasses.Create(created);
            Assert.True(Store.TestClasses.GetById(tc.Id).Payload.IsEmpty);

            var entity = Store.TestClasses.GetById(tc.Id).GetEntity();
            entity.Payload = null; // null -> null: no change detected
            await Store.TestClasses.Update(entity);

            entity = Store.TestClasses.GetById(tc.Id).GetEntity();
            entity.Payload = new byte[] { 7 }; // null -> value
            await Store.TestClasses.Update(entity);
            Assert.Equal(new byte[] { 7 }, Store.TestClasses.GetById(tc.Id).Payload.ToArray());

            entity = Store.TestClasses.GetById(tc.Id).GetEntity();
            entity.Payload = null; // value -> null
            await Store.TestClasses.Update(entity);
            Assert.True(Store.TestClasses.GetById(tc.Id).Payload.IsEmpty);
        }

        [Fact]
        public async Task Query_SkipTakeBeyondCount()
        {
            await SeedAsync(3);

            Assert.Empty(await Store.TestClasses.Query(x => true, skip: 10, take: 5));
            Assert.Equal(2, (await Store.TestClasses.Query(x => true, skip: 1, take: 10)).Count);
            Assert.Empty(await Store.TestClasses.Query(x => true, 0, 0));
        }

        [Fact]
        public async Task EmptyStore_QueriesAndLookups()
        {
            Assert.Empty(await Store.TestClasses.GetAll());
            Assert.Null(Store.TestClasses.GetById(1));
            Assert.Null(await Store.TestClasses.FirstOrDefault(x => true));
            var (items, count) = await Store.TestClasses.QueryCount(x => true, 0, 10);
            Assert.Empty(items);
            Assert.Equal(0, count);
        }

        [Fact]
        public async Task Delete_MissingRow_Throws()
        {
            var tc = await Store.TestClasses.Create(NewTestClass("once", 1));
            var entity = Store.TestClasses.GetById(tc.Id).GetEntity();

            Assert.True(await Store.TestClasses.Delete(entity));
            // Deleting an already-deleted row is misuse, consistent with the
            // update-on-deleted contract: EF throws (0 rows affected) and the
            // cache stays consistent.
            await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException>(
                () => Store.TestClasses.Delete(entity));
            Assert.Equal(0, Store.TestClasses.Count);
            Assert.Null(Store.TestClasses.GetById(tc.Id));
        }
    }
}
