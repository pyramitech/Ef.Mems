using Xunit;

namespace Ef.Mems.Tests
{
    /// <summary>
    /// Secondary-index maintenance on TestClass (seq index + IndexedName) and
    /// the index-only code shape on IndexedClass (dictionary QuerySource, no
    /// sequential array).
    /// </summary>
    public class IndexTests : StoreTestBase
    {
        [Fact]
        public async Task Update_ChangedIndexValue_RemovesOldKey()
        {
            var tc = await Store.TestClasses.Create(NewTestClass("a", 1));
            var oldIndexed = tc.IndexedName;

            var entity = Store.TestClasses.GetById(tc.Id).GetEntity();
            entity.IndexedName = "custom-index";
            await Store.TestClasses.Update(entity);

            Assert.Null(Store.TestClasses.GetByIndexedName(oldIndexed));
            Assert.Equal(tc.Id, Store.TestClasses.GetByIndexedName("custom-index").Id);
        }

        [Fact]
        public async Task UpdateRange_ChangedIndexValues_RemovesOldKeys()
        {
            var tc1 = await Store.TestClasses.Create(NewTestClass("a", 1));
            var tc2 = await Store.TestClasses.Create(NewTestClass("b", 2));
            var old1 = tc1.IndexedName;
            var old2 = tc2.IndexedName;

            var e1 = Store.TestClasses.GetById(tc1.Id).GetEntity();
            var e2 = Store.TestClasses.GetById(tc2.Id).GetEntity();
            e1.IndexedName = "idx-1";
            e2.IndexedName = "idx-2";
            await Store.TestClasses.UpdateRange(new[] { e1, e2 });

            Assert.Null(Store.TestClasses.GetByIndexedName(old1));
            Assert.Null(Store.TestClasses.GetByIndexedName(old2));
            Assert.Equal(tc1.Id, Store.TestClasses.GetByIndexedName("idx-1").Id);
            Assert.Equal(tc2.Id, Store.TestClasses.GetByIndexedName("idx-2").Id);
        }

        [Fact]
        public async Task StagedUpdate_ChangedIndexValue_RemovesOldKey()
        {
            var tc = await Store.TestClasses.Create(NewTestClass("a", 1));
            var oldIndexed = tc.IndexedName;

            var entity = Store.TestClasses.GetById(tc.Id).GetEntity();
            entity.IndexedName = "staged-index";
            var transaction = Store.StartTransaction();
            Store.TestClasses.StageUpdate(transaction, entity);
            await transaction.Commit();

            Assert.Null(Store.TestClasses.GetByIndexedName(oldIndexed));
            Assert.Equal(tc.Id, Store.TestClasses.GetByIndexedName("staged-index").Id);
        }

        [Fact]
        public async Task Update_UnchangedIndexValue_KeepsKey()
        {
            var tc = await Store.TestClasses.Create(NewTestClass("a", 1));

            var entity = Store.TestClasses.GetById(tc.Id).GetEntity();
            entity.Number = 42;
            await Store.TestClasses.Update(entity);

            Assert.Equal(42, Store.TestClasses.GetByIndexedName(tc.IndexedName).Number);
        }

        // ---- Index-only entity ---------------------------------------------

        [Fact]
        public async Task IndexOnly_CreateGetQueryUpdateDelete()
        {
            await Store.IndexedClasses.Create(new IndexedClass { Code = "alpha", Label = "first", Value = 1 });
            await Store.IndexedClasses.Create(new IndexedClass { Code = "beta", Label = "second", Value = 2 });
            await Store.IndexedClasses.Create(new IndexedClass { Code = "gamma", Label = "third", Value = 3 });

            Assert.Equal(3, Store.IndexedClasses.Count);
            Assert.Equal("second", Store.IndexedClasses.GetByCode("beta").Label);
            Assert.Null(Store.IndexedClasses.GetByCode("missing"));

            // Query source is the index dictionary's Values.
            var all = await Store.IndexedClasses.GetAll();
            Assert.Equal(3, all.Count);
            var big = await Store.IndexedClasses.Query(x => x.Value >= 2, 0, 10);
            Assert.Equal(2, big.Count);

            var entity = Store.IndexedClasses.GetByCode("beta").GetEntity();
            entity.Label = "renamed";
            await Store.IndexedClasses.Update(entity);
            Assert.Equal("renamed", Store.IndexedClasses.GetByCode("beta").Label);

            Assert.True(await Store.IndexedClasses.Delete(Store.IndexedClasses.GetByCode("alpha").GetEntity()));
            Assert.Null(Store.IndexedClasses.GetByCode("alpha"));
            Assert.Equal(2, Store.IndexedClasses.Count);
        }

        [Fact]
        public async Task IndexOnly_UpdateChangedKey_ThrowsEntityNotCached()
        {
            await Store.IndexedClasses.Create(new IndexedClass { Code = "alpha", Label = "first", Value = 1 });

            // The index IS the identity; changing it means the original can't
            // be found - guarded error instead of silent corruption.
            var entity = Store.IndexedClasses.GetByCode("alpha").GetEntity();
            entity.Code = "omega";
            await Assert.ThrowsAsync<EntityNotCachedException>(() => Store.IndexedClasses.Update(entity));
        }

        // ---- Guid index (multi-index entity) --------------------------------

        [Fact]
        public async Task GuidIndex_CreateLookupUpdateDelete()
        {
            var ext = Guid.NewGuid();
            await Store.IndexedClasses.Create(new IndexedClass { Code = "g1", Label = "first", Value = 1, ExternalId = ext });

            Assert.Equal("g1", Store.IndexedClasses.GetByExternalId(ext).Code);
            Assert.Null(Store.IndexedClasses.GetByExternalId(Guid.NewGuid()));

            // Update through the OTHER index key; both dictionaries must
            // point at the fresh snapshot.
            var entity = Store.IndexedClasses.GetByCode("g1").GetEntity();
            entity.Label = "renamed";
            await Store.IndexedClasses.Update(entity);
            Assert.Equal("renamed", Store.IndexedClasses.GetByExternalId(ext).Label);

            await Store.IndexedClasses.Delete(Store.IndexedClasses.GetByExternalId(ext).GetEntity());
            Assert.Null(Store.IndexedClasses.GetByExternalId(ext));
            Assert.Null(Store.IndexedClasses.GetByCode("g1"));
        }

        [Fact]
        public async Task GuidIndex_UpdateChangedValue_RemovesOldKey()
        {
            var oldExt = Guid.NewGuid();
            var newExt = Guid.NewGuid();
            await Store.IndexedClasses.Create(new IndexedClass { Code = "g1", Label = "l", Value = 1, ExternalId = oldExt });

            var entity = Store.IndexedClasses.GetByCode("g1").GetEntity();
            entity.ExternalId = newExt;
            await Store.IndexedClasses.Update(entity);

            Assert.Null(Store.IndexedClasses.GetByExternalId(oldExt));
            Assert.Equal("g1", Store.IndexedClasses.GetByExternalId(newExt).Code);
        }

        [Fact]
        public async Task GuidIndex_SecondStoreReloads()
        {
            var ext = Guid.NewGuid();
            await Store.IndexedClasses.Create(new IndexedClass { Code = "g1", Label = "l", Value = 1, ExternalId = ext });

            var second = new TestStore(DbPath);
            Assert.Equal("g1", second.IndexedClasses.GetByExternalId(ext).Code);
        }

        [Fact]
        public async Task IndexOnly_SecondStoreReloads()
        {
            await Store.IndexedClasses.Create(new IndexedClass { Code = "alpha", Label = "first", Value = 1 });

            var second = new TestStore(DbPath);
            Assert.Equal("first", second.IndexedClasses.GetByCode("alpha").Label);
            Assert.Equal(1, second.IndexedClasses.Count);
        }
    }
}
