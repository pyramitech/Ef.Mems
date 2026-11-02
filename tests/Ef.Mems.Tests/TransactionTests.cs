using Xunit;

namespace Ef.Mems.Tests
{
    public class TransactionTests : StoreTestBase
    {
        [Fact]
        public async Task StageCreate_CommitsAcrossEntityStores()
        {
            var transaction = Store.StartTransaction();
            Store.TestClasses.StageCreate(transaction, new[] { NewTestClass("t1"), NewTestClass("t2") });
            Store.UntestedClasses.StageCreate(transaction, new UntestedClass { TestName = "u1", UntestedTests = new() });

            await transaction.Commit();

            Assert.Equal(2, Store.TestClasses.Count);
            Assert.Equal("t1", Store.TestClasses.GetById(1)?.TestName);
            Assert.Equal("t2", Store.TestClasses.GetById(2)?.TestName);
            Assert.Equal("u1", Store.UntestedClasses.GetById(1)?.TestName);
        }

        [Fact]
        public async Task StageUpdate_CommitsScalarsAndJoins()
        {
            var tc = await Store.TestClasses.Create(NewTestClass("tc"));
            var uc = await Store.UntestedClasses.Create(new UntestedClass
            {
                TestName = "uc",
                UntestedTests = new List<UntestedTest>
                {
                    new UntestedTest { TestClassId = tc.Id, JoinData = "before" },
                },
            });

            var tcEntity = Store.TestClasses.GetById(tc.Id).GetEntity();
            var ucEntity = await Store.UntestedClasses.GetById(uc.Id).GetEntity();
            tcEntity.TestName += "_tx";
            tcEntity.Payload = new byte[] { 4, 5, 6 };
            ucEntity.UntestedTests.Single().JoinData = "after";

            var transaction = Store.StartTransaction();
            Store.TestClasses.StageUpdate(transaction, tcEntity);
            Store.UntestedClasses.StageUpdate(transaction, ucEntity);
            await transaction.Commit();

            var tcRo = Store.TestClasses.GetById(tc.Id);
            Assert.EndsWith("_tx", tcRo.TestName);
            Assert.Equal(new byte[] { 4, 5, 6 }, tcRo.Payload.ToArray());
            // The join update must be visible from both sides.
            Assert.Equal("after", Store.UntestedClasses.GetById(uc.Id).UntestedTests.Single().JoinData);
            Assert.Equal("after", tcRo.UntestedTests.Single().JoinData);
        }

        [Fact]
        public async Task StageUpdate_AddsNavChildWithoutExplicitForeignKey()
        {
            var created = NewTestClass("tc");
            created.TestSubClasses.Add(new TestSubClass { SubString = "existing" });
            var tc = await Store.TestClasses.Create(created);

            // No FK, no scalar change: the commit path must attach the
            // key-only update stub (with the nav list copied onto it) so EF
            // relationship fixup fills TestClassId.
            var entity = Store.TestClasses.GetById(tc.Id).GetEntity();
            entity.TestSubClasses.Add(new TestSubClass { SubString = "staged" });

            var transaction = Store.StartTransaction();
            Store.TestClasses.StageUpdate(transaction, entity);
            await transaction.Commit();

            var subs = Store.TestClasses.GetById(tc.Id).TestSubClasses;
            Assert.Equal(new[] { "existing", "staged" }, subs.Select(x => x.SubString).OrderBy(x => x));
        }

        [Fact]
        public async Task StageDelete_CommitsAndCleansJoins()
        {
            var tc = await Store.TestClasses.Create(NewTestClass("tc"));
            var uc = await Store.UntestedClasses.Create(new UntestedClass
            {
                TestName = "uc",
                UntestedTests = new List<UntestedTest>
                {
                    new UntestedTest { TestClassId = tc.Id, JoinData = "doomed" },
                },
            });

            var transaction = Store.StartTransaction();
            Store.UntestedClasses.StageDelete(transaction, await Store.UntestedClasses.GetById(uc.Id).GetEntity());
            await transaction.Commit();

            Assert.Null(Store.UntestedClasses.GetById(uc.Id));
            Assert.Equal(0, Store.UntestedClasses.Count);
            Assert.Empty(Store.TestClasses.GetById(tc.Id).UntestedTests);
        }
    }
}
