using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ef.Mems.Tests
{
    /// <summary>
    /// UseSafety features (all on by default; TestClasses uses the defaults,
    /// UntestedClasses opts out of copyOnInsert only): copy-on-insert clones
    /// entities entering the cache on Create/Update, copy-on-stage snapshots
    /// staged entities at stage time, guarded update errors, and the sparse
    /// sequential-index guard.
    /// </summary>
    public class SafetyTests : StoreTestBase
    {
        [Fact]
        public async Task Create_CopyOnInsert_DetachesCallerInstance()
        {
            var source = NewTestClass("safe", 1);
            var ro = await Store.TestClasses.Create(source);

            source.TestName = "mutated after create";
            source.Number = 666;

            var cached = Store.TestClasses.GetById(ro.Id);
            Assert.Equal("safe", cached.TestName);
            Assert.Equal(1, cached.Number);
            // The generated id landed on the private clone, not the caller's instance.
            Assert.Equal(0, source.Id);
        }

        [Fact]
        public async Task Update_CopyOnWrite_DetachesCallerInstance()
        {
            var tc = await Store.TestClasses.Create(NewTestClass("a", 1));

            var entity = Store.TestClasses.GetById(tc.Id).GetEntity();
            entity.Number = 2;
            await Store.TestClasses.Update(entity);

            entity.TestName = "poison";
            entity.Number = 999;

            var cached = Store.TestClasses.GetById(tc.Id);
            Assert.Equal("a", cached.TestName);
            Assert.Equal(2, cached.Number);
        }

        [Fact]
        public async Task StageUpdate_CopyOnStage_SnapshotsAtStageTime()
        {
            var tc = await Store.TestClasses.Create(NewTestClass("a", 1));
            var entity = Store.TestClasses.GetById(tc.Id).GetEntity();
            entity.Number = 5;

            var transaction = Store.StartTransaction();
            Store.TestClasses.StageUpdate(transaction, entity);
            entity.Number = 999; // mutation between stage and commit is ignored
            await transaction.Commit();

            Assert.Equal(5, Store.TestClasses.GetById(tc.Id).Number);
        }

        [Fact]
        public async Task Update_NotCached_ThrowsEntityNotCached()
        {
            var ghost = NewTestClass("ghost", 1);
            ghost.Id = 12345;

            await Assert.ThrowsAsync<EntityNotCachedException>(() => Store.TestClasses.Update(ghost));
        }

        [Fact]
        public async Task Update_AfterDelete_ThrowsEntityNotCached()
        {
            var tc = await Store.TestClasses.Create(NewTestClass("doomed", 1));
            var entity = Store.TestClasses.GetById(tc.Id).GetEntity();
            await Store.TestClasses.Delete(Store.TestClasses.GetById(tc.Id).GetEntity());

            entity.Number = 2;
            await Assert.ThrowsAsync<EntityNotCachedException>(() => Store.TestClasses.Update(entity));
        }

        [Fact]
        public async Task Create_SparseSequentialId_Throws()
        {
            await Assert.ThrowsAsync<SparseSequentialIndexException>(() =>
                Store.UntestedClasses.Create(new UntestedClass
                {
                    Id = 50_000,
                    TestName = "sparse",
                    UntestedTests = new List<UntestedTest>(),
                }));
        }

        [Fact]
        public async Task Guard_AllowsChurnedSequence()
        {
            // Ids grow monotonically under create/delete churn while Count
            // stays flat - that is the documented sequential contract, so the
            // guard compares against the sequence high-water mark, not Count.
            for (int i = 0; i < 30; i++)
            {
                var ro = await Store.TestClasses.Create(NewTestClass("churn" + i, i));
                await Store.TestClasses.Delete(Store.TestClasses.GetById(ro.Id).GetEntity());
            }

            var survivor = await Store.TestClasses.Create(NewTestClass("survivor", 1));
            Assert.Equal("survivor", Store.TestClasses.GetById(survivor.Id).TestName);
            Assert.Equal(1, Store.TestClasses.Count);
        }

        [Fact]
        public async Task Create_OptOut_KeepsCallerReference()
        {
            // UntestedClasses declares UseSafety(copyOnInsert: false): the
            // cache intentionally holds the caller's instance.
            var source = new UntestedClass { TestName = "raw", UntestedTests = new List<UntestedTest>() };
            var ro = await Store.UntestedClasses.Create(source);

            source.TestName = "mutated";
            Assert.Equal("mutated", Store.UntestedClasses.GetById(ro.Id).TestName);
        }

        [Fact]
        public async Task ArrayProperty_IsExposedAsReadOnlyMemory()
        {
            var tc = await Store.TestClasses.Create(NewTestClass("bytes", 1));

            ReadOnlyMemory<byte> payload = Store.TestClasses.GetById(tc.Id).Payload;
            Assert.Equal(new byte[] { 1, 2, 3 }, payload.ToArray());
        }
    }
}
