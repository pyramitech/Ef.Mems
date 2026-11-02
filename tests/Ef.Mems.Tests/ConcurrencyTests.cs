using Xunit;

namespace Ef.Mems.Tests
{
    /// <summary>
    /// The stale-ok contract under load: readers may see stale data during
    /// writes but must NEVER observe an exception. These hammer the paths
    /// with the trickiest publication semantics (MTM CAS arrays, index
    /// dictionary swaps, seq array growth).
    /// </summary>
    public class ConcurrencyTests : StoreTestBase
    {
        [Fact]
        public async Task ParallelCreates_AllSerializedAndCached()
        {
            var tasks = Enumerable.Range(1, 40)
                .Select(i => Store.TestClasses.Create(NewTestClass("p" + i, i)))
                .ToList();
            var ros = await Task.WhenAll(tasks);

            Assert.Equal(40, Store.TestClasses.Count);
            Assert.Equal(40, ros.Select(r => r.Id).Distinct().Count());
            foreach (var ro in ros)
                Assert.NotNull(Store.TestClasses.GetById(ro.Id));
        }

        [Fact]
        public async Task MtmReaders_NeverThrowWhileJoinsChurn()
        {
            var tc = await Store.TestClasses.Create(NewTestClass("hub", 1));
            var uc = await Store.UntestedClasses.Create(new UntestedClass
            {
                TestName = "uc",
                UntestedTests = new List<UntestedTest>
                {
                    new UntestedTest { TestClassId = tc.Id, JoinData = "seed" },
                },
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            Exception readerFailure = null;

            var readers = Enumerable.Range(0, 4).Select(reader => Task.Run(() =>
            {
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        // MTM arrays are CAS-swapped; iteration must never throw.
                        foreach (var join in Store.TestClasses.GetById(tc.Id).UntestedTests)
                            _ = join.JoinData;
                        var ucRo = Store.UntestedClasses.GetById(uc.Id);
                        foreach (var join in ucRo.UntestedTests)
                            _ = join.TestClassId;
                        _ = Store.TestClasses.GetById(tc.Id).GetEntity();
                    }
                }
                catch (Exception ex)
                {
                    readerFailure = ex;
                }
            })).ToArray();

            var writer = Task.Run(async () =>
            {
                var i = 0;
                while (!cts.IsCancellationRequested)
                {
                    var entity = await Store.UntestedClasses.GetById(uc.Id).GetEntity();
                    entity.UntestedTests.Single().JoinData = "gen" + i++;
                    await Store.UntestedClasses.Update(entity);
                }
            });

            await Task.WhenAll(readers.Append(writer));
            Assert.Null(readerFailure);
            Assert.StartsWith("gen", Store.UntestedClasses.GetById(uc.Id).UntestedTests.Single().JoinData);
        }

        [Fact]
        public async Task QueryReaders_NeverThrowWhileCreatingAndDeleting()
        {
            await SeedAsync(20);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            Exception readerFailure = null;

            var readers = Enumerable.Range(0, 3).Select(reader => Task.Run(async () =>
            {
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        _ = await Store.TestClasses.Query(x => x.Number >= 0, 0, 50);
                        _ = await Store.TestClasses.QueryCountMinNumber(5, 0, 0);
                        _ = await Store.TestClasses.QuerySearch(SearchQuery.Parse("item"), 0, 10);
                    }
                }
                catch (Exception ex)
                {
                    readerFailure = ex;
                }
            })).ToArray();

            var writer = Task.Run(async () =>
            {
                var n = 100;
                while (!cts.IsCancellationRequested)
                {
                    var ro = await Store.TestClasses.Create(NewTestClass("churn" + n, n));
                    n++;
                    await Store.TestClasses.Delete(Store.TestClasses.GetById(ro.Id).GetEntity());
                }
            });

            await Task.WhenAll(readers.Append(writer));
            Assert.Null(readerFailure);
            Assert.Equal(20, Store.TestClasses.Count);
        }
    }
}
