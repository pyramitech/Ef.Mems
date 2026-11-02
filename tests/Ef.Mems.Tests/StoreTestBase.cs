using Microsoft.Data.Sqlite;

namespace Ef.Mems.Tests
{
    /// <summary>
    /// Gives every test its own store backed by its own temp SQLite file
    /// (xUnit constructs the test class once per test method, so tests are
    /// fully isolated and classes can run in parallel).
    /// </summary>
    public abstract class StoreTestBase : IDisposable
    {
        protected string DbPath { get; }

        protected TestStore Store { get; }

        protected StoreTestBase()
        {
            DbPath = Path.Combine(Path.GetTempPath(), $"efmems_tests_{Guid.NewGuid():N}.db");
            Store = new TestStore(DbPath);
        }

        public void Dispose()
        {
            // Microsoft.Data.Sqlite pools connections; drop them so the file unlocks.
            SqliteConnection.ClearAllPools();
            try { File.Delete(DbPath); } catch { /* best effort */ }
        }

        protected static TestClass NewTestClass(string name, int number = 0) => new TestClass
        {
            TestName = name,
            Number = number,
            Flavor = Flavor.Sweet,
            Payload = new byte[] { 1, 2, 3 },
            TestSubClasses = new List<TestSubClass>(),
            UntestedTests = new List<UntestedTest>(),
        };

        /// <summary>Creates <paramref name="count"/> TestClasses named name1..nameN with Number = 1..N.</summary>
        protected async Task SeedAsync(int count, string name = "item")
        {
            for (int i = 1; i <= count; i++)
                await Store.TestClasses.Create(NewTestClass(name + i, i));
        }
    }
}
