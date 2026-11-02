using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ef.Mems.Tests
{
    public class ManyNavTests : StoreTestBase
    {
        private async Task<int> CreateWithChildrenAsync(params string[] subStrings)
        {
            var entity = NewTestClass("parent");
            entity.TestSubClasses = subStrings.Select(s => new TestSubClass { SubString = s }).ToList();
            var ro = await Store.TestClasses.Create(entity);
            return ro.Id;
        }

        [Fact]
        public async Task Create_PersistsChildren()
        {
            var id = await CreateWithChildrenAsync("one", "two");

            var ro = Store.TestClasses.GetById(id);
            Assert.Equal(new[] { "one", "two" }, ro.TestSubClasses.Select(x => x.SubString).OrderBy(x => x));

            using var ctx = Store.GetContext();
            Assert.Equal(2, await ctx.Set<TestSubClass>().CountAsync());
        }

        [Fact]
        public async Task Update_AddsModifiesAndRemovesChildren()
        {
            var id = await CreateWithChildrenAsync("keep", "remove");

            var entity = Store.TestClasses.GetById(id).GetEntity();
            entity.TestSubClasses.Remove(entity.TestSubClasses.Single(x => x.SubString == "remove"));
            entity.TestSubClasses.Single(x => x.SubString == "keep").SubString = "keep++";
            // No FK and no parent scalar change: the generated Update must
            // attach the parent so EF relationship fixup fills TestClassId.
            entity.TestSubClasses.Add(new TestSubClass { SubString = "added" });
            await Store.TestClasses.Update(entity);

            var ro = Store.TestClasses.GetById(id);
            Assert.Equal(new[] { "added", "keep++" }, ro.TestSubClasses.Select(x => x.SubString).OrderBy(x => x));

            using var ctx = Store.GetContext();
            var rows = await ctx.Set<TestSubClass>().AsNoTracking().ToListAsync();
            Assert.Equal(new[] { "added", "keep++" }, rows.Select(x => x.SubString).OrderBy(x => x));
        }

        [Fact]
        public async Task ChildReadOnly_GetEntity_ReturnsClone()
        {
            var id = await CreateWithChildrenAsync("sub");

            var clone = Store.TestClasses.GetById(id).TestSubClasses.Single().GetEntity();
            clone.SubString = "mutated";

            Assert.Equal("sub", Store.TestClasses.GetById(id).TestSubClasses.Single().SubString);
        }
    }

    public class ManyToManyTests : StoreTestBase
    {
        /// <summary>Creates one TestClass and one UntestedClass joined to it; returns their ids.</summary>
        private async Task<(int TestClassId, int UntestedClassId)> CreateJoinedPairAsync(string joinData = "join")
        {
            var tc = await Store.TestClasses.Create(NewTestClass("tc"));
            var uc = await Store.UntestedClasses.Create(new UntestedClass
            {
                TestName = "uc",
                UntestedTests = new List<UntestedTest>
                {
                    new UntestedTest { TestClassId = tc.Id, JoinData = joinData },
                },
            });
            return (tc.Id, uc.Id);
        }

        [Fact]
        public async Task Create_WiresJoinIntoBothSides()
        {
            var (tcId, ucId) = await CreateJoinedPairAsync();

            var tcJoin = Assert.Single(Store.TestClasses.GetById(tcId).UntestedTests);
            var ucJoin = Assert.Single(Store.UntestedClasses.GetById(ucId).UntestedTests);
            Assert.Equal(tcId, tcJoin.TestClassId);
            Assert.Equal(ucId, tcJoin.UntestedClassId);
            Assert.Equal("join", ucJoin.JoinData);
        }

        [Fact]
        public async Task UpdateJoinData_IsVisibleFromBothSides()
        {
            var (tcId, ucId) = await CreateJoinedPairAsync();

            var entity = await Store.UntestedClasses.GetById(ucId).GetEntity();
            entity.UntestedTests.Single().JoinData = "changed";
            await Store.UntestedClasses.Update(entity);

            Assert.Equal("changed", Store.UntestedClasses.GetById(ucId).UntestedTests.Single().JoinData);
            Assert.Equal("changed", Store.TestClasses.GetById(tcId).UntestedTests.Single().JoinData);
        }

        [Fact]
        public async Task AddJoinViaUpdate_IsVisibleFromBothSides()
        {
            var tc = await Store.TestClasses.Create(NewTestClass("tc"));
            var uc = await Store.UntestedClasses.Create(new UntestedClass { TestName = "uc", UntestedTests = new() });

            var entity = await Store.UntestedClasses.GetById(uc.Id).GetEntity();
            entity.UntestedTests.Add(new UntestedTest { TestClassId = tc.Id, UntestedClassId = uc.Id, JoinData = "late" });
            await Store.UntestedClasses.Update(entity);

            Assert.Equal("late", Store.UntestedClasses.GetById(uc.Id).UntestedTests.Single().JoinData);
            Assert.Equal("late", Store.TestClasses.GetById(tc.Id).UntestedTests.Single().JoinData);
        }

        [Fact]
        public async Task ClearJoinsViaUpdate_RemovesFromOtherSide()
        {
            var tc = await Store.TestClasses.Create(NewTestClass("tc"));
            UntestedClass NewJoined(string name) => new UntestedClass
            {
                TestName = name,
                UntestedTests = new List<UntestedTest> { new UntestedTest { TestClassId = tc.Id, JoinData = name } },
            };
            var uc1 = await Store.UntestedClasses.Create(NewJoined("uc1"));
            var uc2 = await Store.UntestedClasses.Create(NewJoined("uc2"));
            Assert.Equal(2, Store.TestClasses.GetById(tc.Id).UntestedTests.Count);

            var entity = await Store.UntestedClasses.GetById(uc1.Id).GetEntity();
            entity.UntestedTests.Clear();
            await Store.UntestedClasses.Update(entity);

            var remaining = Assert.Single(Store.TestClasses.GetById(tc.Id).UntestedTests);
            Assert.Equal(uc2.Id, remaining.UntestedClassId);
            Assert.Empty(Store.UntestedClasses.GetById(uc1.Id).UntestedTests);
        }

        [Fact]
        public async Task DeleteEntity_RemovesJoinsFromOtherSide()
        {
            var (tcId, ucId) = await CreateJoinedPairAsync();

            await Store.UntestedClasses.Delete(await Store.UntestedClasses.GetById(ucId).GetEntity());

            Assert.Null(Store.UntestedClasses.GetById(ucId));
            Assert.Empty(Store.TestClasses.GetById(tcId).UntestedTests);
        }
    }
}
