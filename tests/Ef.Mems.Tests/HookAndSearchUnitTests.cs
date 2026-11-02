using Xunit;

namespace Ef.Mems.Tests
{
    /// <summary>
    /// Extension hook coverage across every write path (HookExtension's
    /// queues are static, so assertions use per-test unique names), plus pure
    /// SearchQuery/Latinize unit tests.
    /// </summary>
    public class HookAndSearchUnitTests : StoreTestBase
    {
        private static string Unique(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N");

        [Fact]
        public async Task Hooks_FireOnEveryWritePath()
        {
            var createName = Unique("c");
            var tc = await Store.TestClasses.Create(NewTestClass(createName, 1));
            Assert.Contains(createName, HookExtension.Created);

            var updateName = Unique("u");
            var entity = Store.TestClasses.GetById(tc.Id).GetEntity();
            entity.TestName = updateName;
            await Store.TestClasses.Update(entity);
            Assert.Contains(updateName, HookExtension.Updated);

            var deleteName = updateName;
            await Store.TestClasses.Delete(Store.TestClasses.GetById(tc.Id).GetEntity());
            Assert.Contains(deleteName, HookExtension.Deleted);
        }

        [Fact]
        public async Task Hooks_FireOnRangePaths()
        {
            var names = new[] { Unique("r"), Unique("r") };
            var ros = await Store.TestClasses.CreateRange(names.Select(n => NewTestClass(n, 1)).ToList());
            Assert.All(names, n => Assert.Contains(n, HookExtension.Created));

            var updated = ros.Select((ro, i) =>
            {
                var e = Store.TestClasses.GetById(ro.Id).GetEntity();
                e.TestName = Unique("ru");
                return e;
            }).ToList();
            await Store.TestClasses.UpdateRange(updated);
            Assert.All(updated, e => Assert.Contains(e.TestName, HookExtension.Updated));

            var toDelete = ros.Select(ro => Store.TestClasses.GetById(ro.Id).GetEntity()).ToList();
            await Store.TestClasses.DeleteRange(toDelete);
            Assert.All(toDelete, e => Assert.Contains(e.TestName, HookExtension.Deleted));
        }

        [Fact]
        public async Task Hooks_FireOnStagedPaths()
        {
            var createName = Unique("sc");
            var trx = Store.StartTransaction();
            Store.TestClasses.StageCreate(trx, NewTestClass(createName, 1));
            await trx.Commit();
            Assert.Contains(createName, HookExtension.Created);

            var tc = await Store.TestClasses.FirstOrDefault(x => x.TestName == createName);
            var updateName = Unique("su");
            var entity = Store.TestClasses.GetById(tc.Id).GetEntity();
            entity.TestName = updateName;
            var trx2 = Store.StartTransaction();
            Store.TestClasses.StageUpdate(trx2, entity);
            await trx2.Commit();
            Assert.Contains(updateName, HookExtension.Updated);

            var trx3 = Store.StartTransaction();
            Store.TestClasses.StageDelete(trx3, Store.TestClasses.GetById(tc.Id).GetEntity());
            await trx3.Commit();
            Assert.Contains(updateName, HookExtension.Deleted);
        }

        // ---- SearchQuery / Latinize pure unit tests ------------------------

        [Fact]
        public void DefaultQuery_MatchesEverything()
        {
            Assert.True(default(SearchQuery).Matches(null));
            Assert.True(default(SearchQuery).Matches("anything"));
            Assert.True(SearchQuery.Parse("").Matches("anything"));
            Assert.True(SearchQuery.Parse("   ").Matches(null));
        }

        [Fact]
        public void NonEmptyQuery_NullField_NeverMatches()
        {
            Assert.False(SearchQuery.Parse("token").Matches(null));
        }

        [Fact]
        public void Parse_LatinizesAndLowercases()
        {
            Assert.True(SearchQuery.Parse("CRÈME brûlée").Matches("creme brulee dessert"));
            Assert.True(SearchQuery.Parse("škoda").Matches("skoda octavia"));
            Assert.False(SearchQuery.Parse("škoda").Matches("fiat panda"));
        }

        [Fact]
        public void Latinize_StripsDiacriticsAndCase()
        {
            Assert.Equal("skoda caj zlutoucky", SearchExtension.Latinize("ŠKODA Čaj Žluťoučký"));
            Assert.Equal("creme brulee", SearchExtension.Latinize("Crème BRÛLÉE"));
            Assert.Equal("plain 123", SearchExtension.Latinize("Plain 123"));
        }
    }
}
