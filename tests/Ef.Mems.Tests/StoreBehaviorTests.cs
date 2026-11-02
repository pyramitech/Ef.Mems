using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ef.Mems.Tests
{
    public class StoreBehaviorTests : StoreTestBase
    {
        [Fact]
        public async Task Refresh_PicksUpExternalChanges()
        {
            await SeedAsync(1);

            using (var ctx = Store.GetContext())
            {
                ctx.TestClasses.Add(new TestClass
                {
                    TestName = "external",
                    IndexedName = "external_idx",
                    Payload = Array.Empty<byte>(),
                });
                await ctx.SaveChangesAsync();
            }

            Assert.Null(Store.TestClasses.GetById(2)); // not visible yet
            await Store.TestClasses.Refresh();

            Assert.Equal(2, Store.TestClasses.Count);
            Assert.Equal("external", Store.TestClasses.GetById(2)?.TestName);
            Assert.Equal(2, Store.TestClasses.GetByIndexedName("external_idx")?.Id);
        }

        [Fact]
        public async Task NewStoreInstance_LoadsExistingData()
        {
            var entity = NewTestClass("survivor", number: 5);
            entity.TestSubClasses.Add(new TestSubClass { SubString = "child" });
            await Store.TestClasses.Create(entity);
            var uc = await Store.UntestedClasses.Create(new UntestedClass
            {
                TestName = "uc",
                UntestedTests = new List<UntestedTest> { new UntestedTest { TestClassId = 1, JoinData = "j" } },
            });

            // A second store over the same database must rebuild the full cache.
            var reloaded = new TestStore(DbPath);
            var ro = reloaded.TestClasses.GetById(1);
            Assert.Equal("survivor", ro?.TestName);
            Assert.Equal("child", Assert.Single(ro.TestSubClasses).SubString);
            Assert.Equal("j", Assert.Single(ro.UntestedTests).JoinData);
            Assert.Equal("j", Assert.Single(reloaded.UntestedClasses.GetById(uc.Id).UntestedTests).JoinData);
        }

        [Fact]
        public async Task ConcurrentReaders_NeverThrowWhileWriting()
        {
            const int writeIterations = 60;
            using var done = new CancellationTokenSource();

            var writer = Task.Run(async () =>
            {
                try
                {
                    for (int i = 1; i <= writeIterations; i++)
                    {
                        var ro = await Store.TestClasses.Create(NewTestClass($"w{i}", i));

                        var entity = Store.TestClasses.GetById(ro.Id).GetEntity();
                        entity.Number += 1000;
                        entity.Payload = new byte[] { (byte)i };
                        await Store.TestClasses.Update(entity);

                        if (i % 2 == 0)
                            await Store.TestClasses.Delete(Store.TestClasses.GetById(ro.Id).GetEntity());
                    }
                }
                finally
                {
                    done.Cancel();
                }
            });

            var readers = Enumerable.Range(0, 4).Select(seed => Task.Run(async () =>
            {
                var rnd = new Random(seed);
                while (!done.IsCancellationRequested)
                {
                    // Any exception here fails the test: readers may see stale
                    // data but must never observe torn state.
                    Store.TestClasses.GetById(rnd.Next(0, writeIterations + 2));
                    Store.TestClasses.GetByIndexedName("never-matches");
                    await Store.TestClasses.Query(x => x.Number >= 0);
                    await Store.TestClasses.QuerySortCount(x => true, 0, 5, x => x.Number, descending: true);
                    foreach (var item in await Store.TestClasses.GetAll())
                        _ = item.TestName;
                    await Task.Yield();
                }
            })).ToArray();

            await writer;
            await Task.WhenAll(readers);

            Assert.Equal(writeIterations / 2, Store.TestClasses.Count);
        }
    }
}
