using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ef.Mems.Tests
{
    /// <summary>
    /// Transaction behavior beyond the happy path: mixed staging in one
    /// commit, and failure semantics (a faulting commit must throw - never
    /// hang - and leave the store usable).
    /// </summary>
    public class TransactionRobustnessTests : StoreTestBase
    {
        [Fact]
        public async Task MixedStaging_SameStore_CreateUpdateAndDelete()
        {
            var keep = await Store.TestClasses.Create(NewTestClass("keep", 1));
            var doomed = await Store.TestClasses.Create(NewTestClass("doomed", 2));

            var updated = Store.TestClasses.GetById(keep.Id).GetEntity();
            updated.Number = 100;

            // All three operation kinds on ONE store in ONE transaction: the
            // merged per-store commit applies them under a single lock hold.
            var transaction = Store.StartTransaction();
            Store.TestClasses.StageCreate(transaction, NewTestClass("fresh", 3));
            Store.TestClasses.StageUpdate(transaction, updated);
            Store.TestClasses.StageDelete(transaction, Store.TestClasses.GetById(doomed.Id).GetEntity());
            await transaction.Commit();

            Assert.Equal(2, Store.TestClasses.Count);
            Assert.Equal(100, Store.TestClasses.GetById(keep.Id).Number);
            Assert.Null(Store.TestClasses.GetById(doomed.Id));
            Assert.NotNull(await Store.TestClasses.FirstOrDefault(x => x.TestName == "fresh"));

            using var ctx = Store.GetContext();
            Assert.Equal(2, await ctx.TestClasses.CountAsync());
        }

        [Fact]
        public async Task DuplicateStaging_SameEntityTwice_Throws()
        {
            var tc = await Store.TestClasses.Create(NewTestClass("dup", 1));
            var entity = Store.TestClasses.GetById(tc.Id).GetEntity();
            entity.Number = 2;

            // update + delete of the same entity
            var transaction = Store.StartTransaction();
            Store.TestClasses.StageUpdate(transaction, entity);
            Store.TestClasses.StageDelete(transaction, Store.TestClasses.GetById(tc.Id).GetEntity());
            await Assert.ThrowsAsync<DuplicateStagedEntityException>(() => transaction.Commit());

            // update + update of the same entity
            var transaction2 = Store.StartTransaction();
            Store.TestClasses.StageUpdate(transaction2, entity);
            Store.TestClasses.StageUpdate(transaction2, Store.TestClasses.GetById(tc.Id).GetEntity());
            await Assert.ThrowsAsync<DuplicateStagedEntityException>(() => transaction2.Commit());

            // nothing changed, store still usable
            Assert.Equal(1, Store.TestClasses.GetById(tc.Id).Number);
            var after = await Store.TestClasses.Create(NewTestClass("after", 9));
            Assert.NotNull(Store.TestClasses.GetById(after.Id));
        }

        [Fact]
        public async Task DuplicateStaging_ExplicitKeyCreates_Throws()
        {
            // IndexedClass keys are caller-provided, so create+create with the
            // same key is detectable at commit.
            var transaction = Store.StartTransaction();
            Store.IndexedClasses.StageCreate(transaction, new IndexedClass { Code = "dup", Label = "a", Value = 1 });
            Store.IndexedClasses.StageCreate(transaction, new IndexedClass { Code = "dup", Label = "b", Value = 2 });
            await Assert.ThrowsAsync<DuplicateStagedEntityException>(() => transaction.Commit());

            Assert.Null(Store.IndexedClasses.GetByCode("dup"));
        }

        [Fact]
        public async Task MixedStaging_AcrossStores_CreateUpdateAndDelete()
        {
            var tc = await Store.TestClasses.Create(NewTestClass("keep", 1));
            var doomed = await Store.UntestedClasses.Create(new UntestedClass { TestName = "doomed", UntestedTests = new() });

            var updated = Store.TestClasses.GetById(tc.Id).GetEntity();
            updated.Number = 100;

            var transaction = Store.StartTransaction();
            Store.IndexedClasses.StageCreate(transaction, new IndexedClass { Code = "fresh", Label = "l", Value = 1 });
            Store.TestClasses.StageUpdate(transaction, updated);
            Store.UntestedClasses.StageDelete(transaction, await Store.UntestedClasses.GetById(doomed.Id).GetEntity());
            await transaction.Commit();

            Assert.Equal(100, Store.TestClasses.GetById(tc.Id).Number);
            Assert.Null(Store.UntestedClasses.GetById(doomed.Id));
            Assert.Equal("l", Store.IndexedClasses.GetByCode("fresh").Label);

            // The database agrees with the cache.
            using var ctx = Store.GetContext();
            Assert.Equal(0, await ctx.UntestedClasses.CountAsync());
            Assert.Equal(1, await ctx.IndexedClasses.CountAsync());
        }

        [Fact]
        public async Task MixedStaging_AcrossStores_SingleCommit()
        {
            var transaction = Store.StartTransaction();
            Store.TestClasses.StageCreate(transaction, NewTestClass("t1", 1));
            Store.UntestedClasses.StageCreate(transaction, new UntestedClass { TestName = "u1", UntestedTests = new() });
            Store.IndexedClasses.StageCreate(transaction, new IndexedClass { Code = "x1", Label = "l", Value = 1 });
            await transaction.Commit();

            Assert.Equal(1, Store.TestClasses.Count);
            Assert.Equal(1, Store.UntestedClasses.Count);
            Assert.Equal("l", Store.IndexedClasses.GetByCode("x1").Label);
        }

        [Fact]
        public async Task EmptyTransaction_CommitSucceeds()
        {
            var transaction = Store.StartTransaction();
            await transaction.Commit();

            // Store fully usable afterwards.
            var tc = await Store.TestClasses.Create(NewTestClass("after-empty", 1));
            Assert.NotNull(Store.TestClasses.GetById(tc.Id));
        }

        [Fact]
        public async Task DoubleCommit_Throws()
        {
            var transaction = Store.StartTransaction();
            Store.TestClasses.StageCreate(transaction, NewTestClass("once", 1));
            await transaction.Commit();

            await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.Commit());
            // The first commit's effect stands exactly once.
            Assert.Equal(1, Store.TestClasses.Count);
        }

        [Fact]
        public async Task CommitAfterFailedCommit_Throws()
        {
            var ghost = NewTestClass("ghost", 1);
            ghost.Id = 4242;
            var transaction = Store.StartTransaction();
            Store.TestClasses.StageUpdate(transaction, ghost);
            await Assert.ThrowsAsync<EntityNotCachedException>(() => transaction.Commit());

            // Retrying the same instance is misuse - the context may hold
            // half-attached state; a fresh transaction is required.
            await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.Commit());
        }

        [Fact]
        public async Task FailedCommit_ThrowsInsteadOfHanging()
        {
            var ghost = NewTestClass("ghost", 1);
            ghost.Id = 4242; // never cached

            var transaction = Store.StartTransaction();
            Store.TestClasses.StageUpdate(transaction, ghost);

            // Must surface as the guarded error within the timeout - a hang
            // here means the ready/gate machinery leaked again.
            var commit = transaction.Commit();
            var finished = await Task.WhenAny(commit, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.Same(commit, finished);
            await Assert.ThrowsAsync<EntityNotCachedException>(() => commit);
        }

        [Fact]
        public async Task FailedCommit_LeavesStoreUsable()
        {
            var tc = await Store.TestClasses.Create(NewTestClass("survivor", 1));

            var ghost = NewTestClass("ghost", 1);
            ghost.Id = 4242;
            var transaction = Store.StartTransaction();
            Store.TestClasses.StageUpdate(transaction, ghost);
            await Assert.ThrowsAsync<EntityNotCachedException>(() => transaction.Commit());

            // Nothing was written...
            using (var ctx = Store.GetContext())
            {
                Assert.Equal(1, await ctx.TestClasses.CountAsync());
            }
            Assert.Equal(1, Store.TestClasses.Count);
            Assert.Equal("survivor", Store.TestClasses.GetById(tc.Id).TestName);

            // ...and the transaction semaphore was released: new work proceeds.
            var after = await Store.TestClasses.Create(NewTestClass("after", 2));
            Assert.NotNull(Store.TestClasses.GetById(after.Id));

            var retry = Store.StartTransaction();
            Store.TestClasses.StageCreate(retry, NewTestClass("retried", 3));
            await retry.Commit();
            Assert.Equal(3, Store.TestClasses.Count);
        }
    }
}
