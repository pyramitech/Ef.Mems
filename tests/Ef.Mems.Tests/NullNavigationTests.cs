using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ef.Mems.Tests
{
    /// <summary>
    /// A null navigation list on an incoming entity means "empty": updates
    /// must diff it as such (deleting existing children) instead of
    /// dereferencing it, and deletes must cope with entities whose join
    /// lists were never populated.
    /// </summary>
    public class NullNavigationTests : StoreTestBase
    {
        private async Task<int> CreateWithChildAndLink()
        {
            var other = await Store.UntestedClasses.Create(new UntestedClass { Id = 1, TestName = "other", UntestedTests = new List<UntestedTest>() });
            var created = NewTestClass("parent");
            created.TestSubClasses.Add(new TestSubClass { SubString = "child" });
            created.UntestedTests.Add(new UntestedTest { UntestedClassId = other.Id, JoinData = "link" });
            var ro = await Store.TestClasses.Create(created);
            return ro.Id;
        }

        [Fact]
        public async Task Update_NullNavigations_TreatedAsEmpty_RemoveChildrenAndLinks()
        {
            var id = await CreateWithChildAndLink();
            Assert.Single(Store.TestClasses.GetById(id).TestSubClasses);
            Assert.Single(Store.TestClasses.GetById(id).UntestedTests);

            var entity = Store.TestClasses.GetById(id).GetEntity();
            entity.TestSubClasses = null;
            entity.UntestedTests = null;
            await Store.TestClasses.Update(entity);

            Assert.Empty(Store.TestClasses.GetById(id).TestSubClasses);
            Assert.Empty(Store.TestClasses.GetById(id).UntestedTests);
            using var ctx = Store.GetContext();
            Assert.Equal(0, await ctx.Set<TestSubClass>().CountAsync());
            Assert.Equal(0, await ctx.Set<UntestedTest>().CountAsync());
        }

        [Fact]
        public async Task Update_NullNavigations_Repeatedly_DoesNotThrow()
        {
            var id = await CreateWithChildAndLink();

            for (int i = 0; i < 3; i++)
            {
                var entity = Store.TestClasses.GetById(id).GetEntity();
                entity.TestName = "pass" + i;
                entity.TestSubClasses = null;
                entity.UntestedTests = null;
                await Store.TestClasses.Update(entity);
            }

            Assert.Equal("pass2", Store.TestClasses.GetById(id).TestName);
            Assert.Empty(Store.TestClasses.GetById(id).TestSubClasses);
        }

        [Fact]
        public async Task Update_NullNavigations_OnEntityWithoutChildren_IsNoop()
        {
            var ro = await Store.TestClasses.Create(NewTestClass("lonely"));

            var entity = Store.TestClasses.GetById(ro.Id).GetEntity();
            entity.TestName = "renamed";
            entity.TestSubClasses = null;
            entity.UntestedTests = null;
            var ro2 = await Store.TestClasses.Update(entity);

            Assert.Equal("renamed", ro2.TestName);
            Assert.Empty(ro2.TestSubClasses);
        }

        [Fact]
        public async Task StagedUpdate_NullNavigations_TreatedAsEmpty()
        {
            var id = await CreateWithChildAndLink();

            var entity = Store.TestClasses.GetById(id).GetEntity();
            entity.TestSubClasses = null;
            entity.UntestedTests = null;
            var trx = Store.StartTransaction();
            Store.TestClasses.StageUpdate(trx, entity);
            await trx.Commit();

            Assert.Empty(Store.TestClasses.GetById(id).TestSubClasses);
            Assert.Empty(Store.TestClasses.GetById(id).UntestedTests);
            using var ctx = Store.GetContext();
            Assert.Equal(0, await ctx.Set<TestSubClass>().CountAsync());
        }

        [Fact]
        public async Task Delete_NullJoinList_Works()
        {
            var ro = await Store.TestClasses.Create(NewTestClass("bare"));
            var before = Store.TestClasses.Count;

            var entity = Store.TestClasses.GetById(ro.Id).GetEntity();
            entity.UntestedTests = null;
            Assert.True(await Store.TestClasses.Delete(entity));

            Assert.Equal(before - 1, Store.TestClasses.Count);
            Assert.Null(Store.TestClasses.GetById(ro.Id));
        }

        [Fact]
        public async Task StagedDelete_NullJoinList_Works()
        {
            var ro = await Store.TestClasses.Create(NewTestClass("bare-staged"));

            var trx = Store.StartTransaction();
            var entity = Store.TestClasses.GetById(ro.Id).GetEntity();
            entity.UntestedTests = null;
            Store.TestClasses.StageDelete(trx, entity);
            await trx.Commit();

            Assert.Null(Store.TestClasses.GetById(ro.Id));
        }
    }
}
