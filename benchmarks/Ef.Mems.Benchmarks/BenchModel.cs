// Minimal store for query benchmarks: scalar-only entity (no navigations) so
// setup stays fast and the measurements isolate the predicate/scan cost.
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Ef.Mems.Benchmarks
{
    public class BenchEntity
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int Number { get; set; }
        public long Ticks { get; set; }
        // Pre-normalized (lower-case) word text seeded directly by
        // BenchSeeder; search benchmarks scan it like a SearchExtension field.
        public string SearchField { get; set; }
    }

    public class BenchContext : DbContext
    {
        // Required by the DataStore<T> new() constraint; not used directly.
        public BenchContext() { }

        public BenchContext(DbContextOptions<BenchContext> options) : base(options) { }

        public DbSet<BenchEntity> BenchEntities { get; set; }
    }

    public class BenchStore : DataStore<BenchContext>
    {
        private string _dbPath;

        public BenchStore(string dbPath) : base(dbPath) { }

        protected override void Initialize(object arg)
        {
            _dbPath = (string)arg;
            using var ctx = GetContext();
            ctx.Database.EnsureCreated();
        }

        public override BenchContext GetContext()
        {
            var optionsBuilder = new DbContextOptionsBuilder<BenchContext>();
            optionsBuilder.UseSqlite($"Data Source={_dbPath}");
            return new BenchContext(optionsBuilder.Options);
        }

        public BenchEntityEntityStore BenchEntities;

        protected override void ConstructStore(StoreBuilder<BenchContext> builder)
        {
            BenchEntities = builder.UseEntity(x => x.BenchEntities)
                .UseAllProperties()
                .UseSequentialIndex(x => x.Id)
                .UsePredicate("MinNumber", (BenchEntity x, int min) => x.Number >= min)
                .UseSort(x => x.Number)
                .UseSort("NumberMat", x => x.Number, materialized: true)
                .Build();
        }
    }

    public partial class BenchEntityEntityStore : EntityStore<BenchEntity> { }

    public static class BenchSeeder
    {
        // Word pools for SearchField: "{color} {noun} {extra} unit {n}".
        // Chosen so no pool word is a substring of another; "xylophone"
        // never occurs (the guaranteed-no-match token).
        private static readonly string[] Colors =
        {
            "red", "green", "blue", "amber", "violet", "crimson", "teal", "ochre", "ivory", "slate",
            "coral", "navy", "olive", "maroon", "silver", "gold", "bronze", "copper", "indigo", "pearl",
        };
        private static readonly string[] Nouns =
        {
            "apple", "engine", "cable", "valve", "rotor", "sensor", "bracket", "piston", "flange", "gasket",
            "bearing", "spindle", "clutch", "manifold", "nozzle", "pulley", "damper", "gearbox", "axle", "turbine",
            "filter", "spring", "washer", "socket", "lever",
        };
        private static readonly string[] Extras =
        {
            "spare", "legacy", "imported", "certified", "tested", "sealed", "balanced", "coated",
            "forged", "milled", "anodized", "polished", "tempered", "brushed", "plated",
        };

        public static string SearchFieldFor(int i)
            => $"{Colors[i % Colors.Length]} {Nouns[(i / 3) % Nouns.Length]} {Extras[(i / 7) % Extras.Length]} unit {i % 1000}";

        /// <summary>DB file per row count; versioned so schema changes reseed.</summary>
        public static string DbPath(int count)
            => Path.Combine(Path.GetTempPath(), $"efmems_bench_v2_{count}.db");

        /// <summary>
        /// Creates the benchmark database once (reused across benchmark
        /// processes/runs): <paramref name="count"/> rows with Id = 1..count
        /// and Number = Id % 1000, inserted via raw SQL inside a transaction.
        /// </summary>
        public static void EnsureSeeded(string dbPath, int count)
        {
            if (File.Exists(dbPath))
            {
                using var check = new SqliteConnection($"Data Source={dbPath}");
                check.Open();
                using var countCmd = check.CreateCommand();
                countCmd.CommandText = "SELECT COUNT(*) FROM BenchEntities";
                try
                {
                    if (Convert.ToInt32(countCmd.ExecuteScalar()) == count)
                        return;
                }
                catch (SqliteException)
                {
                    // table missing - reseed
                }
                SqliteConnection.ClearAllPools();
                File.Delete(dbPath);
            }

            using (var ctx = new BenchContext(new DbContextOptionsBuilder<BenchContext>()
                       .UseSqlite($"Data Source={dbPath}").Options))
            {
                ctx.Database.EnsureCreated();
            }

            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO BenchEntities (Id, Name, Number, Ticks, SearchField) VALUES ($id, $name, $number, $ticks, $search)";
            var id = cmd.CreateParameter(); id.ParameterName = "$id"; cmd.Parameters.Add(id);
            var name = cmd.CreateParameter(); name.ParameterName = "$name"; cmd.Parameters.Add(name);
            var number = cmd.CreateParameter(); number.ParameterName = "$number"; cmd.Parameters.Add(number);
            var ticks = cmd.CreateParameter(); ticks.ParameterName = "$ticks"; cmd.Parameters.Add(ticks);
            var search = cmd.CreateParameter(); search.ParameterName = "$search"; cmd.Parameters.Add(search);

            for (int i = 1; i <= count; i++)
            {
                id.Value = i;
                name.Value = "entity_" + i;
                number.Value = i % 1000;
                ticks.Value = (long)i * 397;
                search.Value = SearchFieldFor(i);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
}
