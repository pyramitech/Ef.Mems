// Test data model + store, mirroring Ef.Mems.Demo but self-contained.
// Schema is created via EnsureCreated in TestStore.Initialize (which runs
// before the DataStore base constructor calls Database.Migrate).
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ef.Mems.Tests
{
    public enum Flavor
    {
        None = 0,
        Sweet = 1,
        Sour = 2,
        Bitter = 3,
    }

    public class TestClass : ISearchable
    {
        public int Id { get; set; }
        public string TestName { get; set; }
        public string IndexedName { get; set; }
        public int Number { get; set; }
        public Flavor Flavor { get; set; }
        public byte[] Payload { get; set; }
        // Rebuilt by SearchExtension from {TestName, Number} on every
        // create/update; cached via UseAllProperties so queries can scan it.
        public string SearchField { get; set; }

        public List<TestSubClass> TestSubClasses { get; set; }
        public List<UntestedTest> UntestedTests { get; set; }
    }

    public class TestSubClass
    {
        public int Id { get; set; }
        public int TestClassId { get; set; }
        public string SubString { get; set; }
    }

    public class UntestedClass
    {
        public int Id { get; set; }
        public string TestName { get; set; }
        // Deliberately NOT cached by the store (UseProperties omits it), so
        // UntestedClassReadOnly.GetEntity is generated async and re-fetches
        // the full entity from the context.
        public string Description { get; set; }
        public List<UntestedTest> UntestedTests { get; set; }
    }

    public class UntestedTest
    {
        public int TestClassId { get; set; }
        public int UntestedClassId { get; set; }
        public string JoinData { get; set; }
    }

    /// <summary>
    /// UseMany with a composite child key (the anonymous-object key overload)
    /// - a distinct nav-diff code shape from TestSubClass's single-key child.
    /// </summary>
    public class CompositeParent
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public List<CompositeChild> Slots { get; set; }
    }

    public class CompositeChild
    {
        public int CompositeParentId { get; set; }
        public int Slot { get; set; }
        public string Data { get; set; }
    }

    /// <summary>
    /// Composite (multi-property) index: a value-tuple-keyed dictionary with
    /// a generated GetByRegionCode(region, code) lookup.
    /// </summary>
    public class RegionalCode
    {
        public int Id { get; set; }
        public string Region { get; set; }
        public string Code { get; set; }
        public string Payload { get; set; }
    }

    /// <summary>
    /// Index-only entity (no sequential index): exercises the dictionary
    /// QuerySource/UpdateKey code shape that TestClass/UntestedClass never
    /// generate.
    /// </summary>
    public class IndexedClass
    {
        public string Code { get; set; }
        public string Label { get; set; }
        public int Value { get; set; }
        // Second (Guid) index: exercises non-string dictionary keys and
        // multi-index maintenance on one entity.
        public Guid ExternalId { get; set; }
    }

    /// <summary>
    /// Primitive collections + [NotMapped]: Favorites (List&lt;int&gt;) is the
    /// mapped pass-through over the unmapped FavoriteSet backing HashSet -
    /// the store must cache/clone/diff the List and fully ignore the set.
    /// Tags/Scores exercise plain List&lt;string&gt; and int[] collections.
    /// </summary>
    public class Profile
    {
        public int Id { get; set; }
        public string Alias { get; set; }

        [MaxLength(200)]
        public List<int> Favorites
        {
            get => FavoriteSet.ToList();
            set => FavoriteSet = value?.ToHashSet() ?? new HashSet<int>();
        }

        [NotMapped] public HashSet<int> FavoriteSet { get; set; } = new();

        public List<string> Tags { get; set; }
        public int[] Scores { get; set; }
    }

    /// <summary>
    /// UseUnion members: distinct entity types sharing Id/Amount/Reference
    /// (the union interface) while each also carries a non-shared property
    /// that must stay off the interface.
    /// </summary>
    public class ActiveTransaction
    {
        public int Id { get; set; }
        public decimal Amount { get; set; }
        public string Reference { get; set; }
        public string ProcessorState { get; set; }
    }

    public class ArchivedTransaction
    {
        public int Id { get; set; }
        public decimal Amount { get; set; }
        public string Reference { get; set; }
        public DateTime ArchivedOn { get; set; }
    }

    /// <summary>
    /// EF owned type (OwnsOne): stored in Company's table, declared to the
    /// store via UseOwned.
    /// </summary>
    public class Address
    {
        public string Street { get; set; }
        public string City { get; set; }
        public int ZipCode { get; set; }
    }

    public class Company
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public Address RegistrationAddress { get; set; }
    }

    /// <summary>
    /// Audit stamping via context-taking hooks: AuditExtension fills
    /// CreatedBy/UpdatedBy from the caller-supplied context argument.
    /// </summary>
    public class AuditedClass
    {
        public int Id { get; set; }
        public string Note { get; set; }
        public string CreatedBy { get; set; }
        public DateTime CreatedOn { get; set; }
        public string UpdatedBy { get; set; }
    }

    public class TestContext : DbContext
    {
        // Required by the DataStore<T> new() constraint; not used directly.
        public TestContext() { }

        public TestContext(DbContextOptions<TestContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<UntestedTest>().HasKey(x => new { x.TestClassId, x.UntestedClassId });
            modelBuilder.Entity<TestClass>().HasMany(x => x.UntestedTests);
            modelBuilder.Entity<UntestedClass>().HasMany(x => x.UntestedTests);
            modelBuilder.Entity<IndexedClass>().HasKey(x => x.Code);
            modelBuilder.Entity<CompositeChild>().HasKey(x => new { x.CompositeParentId, x.Slot });
            modelBuilder.Entity<Company>().OwnsOne(c => c.RegistrationAddress);
            // Computed pass-through property (backed by FavoriteSet): EF's
            // primitive-collection materializer populates the getter's list
            // in place, which is a throwaway copy here. An explicit value
            // converter makes it a plain scalar conversion, which assigns
            // loaded values through the setter instead.
            modelBuilder.Entity<Profile>().Property(p => p.Favorites)
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<List<int>>(v, (System.Text.Json.JsonSerializerOptions)null),
                    new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<List<int>>(
                        (a, b) => a.SequenceEqual(b),
                        v => v.Aggregate(0, (h, x) => HashCode.Combine(h, x)),
                        v => v.ToList()));
            base.OnModelCreating(modelBuilder);
        }

        public DbSet<TestClass> TestClasses { get; set; }
        public DbSet<UntestedClass> UntestedClasses { get; set; }
        public DbSet<IndexedClass> IndexedClasses { get; set; }
        public DbSet<CompositeParent> CompositeParents { get; set; }
        public DbSet<RegionalCode> RegionalCodes { get; set; }
        public DbSet<AuditedClass> AuditedClasses { get; set; }
        public DbSet<Company> Companies { get; set; }
        public DbSet<ActiveTransaction> ActiveTransactions { get; set; }
        public DbSet<ArchivedTransaction> ArchivedTransactions { get; set; }
        public DbSet<Profile> Profiles { get; set; }
    }

    /// <summary>Records the entities passed to the Before* hooks so tests can assert UseExtension wiring.</summary>
    public static class HookExtension
    {
        public static readonly ConcurrentQueue<string> Created = new();
        public static readonly ConcurrentQueue<string> Updated = new();
        public static readonly ConcurrentQueue<string> Deleted = new();

        public static void BeforeCreate(object dataStore, TestClass entity) => Created.Enqueue(entity.TestName);
        public static void BeforeUpdate(object dataStore, TestClass entity) => Updated.Enqueue(entity.TestName);
        public static void BeforeDelete(object dataStore, TestClass entity) => Deleted.Enqueue(entity.TestName);
    }

    /// <summary>
    /// Context-taking hooks (Before*(store, entity, context)): stamps audit
    /// columns from the context passed to Create/Update/Delete or Commit.
    /// </summary>
    public static class AuditExtension
    {
        public static readonly ConcurrentQueue<(string Note, object Context)> Deleted = new();

        public static void BeforeCreate(object dataStore, AuditedClass entity, object context)
        {
            entity.CreatedBy = context as string;
            entity.CreatedOn = DateTime.UtcNow;
        }

        public static void BeforeUpdate(object dataStore, AuditedClass entity, object context)
            => entity.UpdatedBy = context as string;

        public static void BeforeDelete(object dataStore, AuditedClass entity, object context)
            => Deleted.Enqueue((entity.Note, context));
    }

    /// <summary>
    /// Values + context hooks (Before*(store, entity, object[] values,
    /// context)): records both so tests can assert the 4-parameter wiring.
    /// </summary>
    public static class ValuedContextExtension
    {
        public static readonly ConcurrentQueue<(object[] Values, object Context)> Created = new();

        public static void BeforeCreate(object dataStore, AuditedClass entity, object[] values, object context)
            => Created.Enqueue((values, context));
    }

    public class TestStore : DataStore<TestContext>
    {
        private string _dbPath;

        public TestStore(string dbPath) : base(dbPath) { }

        protected override void Initialize(object arg)
        {
            _dbPath = (string)arg;
            using var ctx = GetContext();
            ctx.Database.EnsureCreated();
        }

        public override TestContext GetContext()
        {
            var optionsBuilder = new DbContextOptionsBuilder<TestContext>();
            optionsBuilder.UseSqlite($"Data Source={_dbPath}");
            return new TestContext(optionsBuilder.Options);
        }

        public TestClassEntityStore TestClasses;
        public UntestedClassEntityStore UntestedClasses;
        public IndexedClassEntityStore IndexedClasses;
        public CompositeParentEntityStore CompositeParents;
        public RegionalCodeEntityStore RegionalCodes;
        public AuditedClassEntityStore AuditedClasses;
        public CompanyEntityStore Companies;
        public ActiveTransactionEntityStore ActiveTransactions;
        public ArchivedTransactionEntityStore ArchivedTransactions;
        public AllTransactionsUnion AllTransactions;
        public ProfileEntityStore Profiles;

        protected override void ConstructStore(StoreBuilder<TestContext> builder)
        {
            // Store-wide safety defaults (same as the implicit defaults, but
            // exercises the store-level UseSafety parse path).
            builder.UseSafety(copyOnInsert: true, copyOnStage: true, sequentialIndexGuard: true);

            TestClasses = builder.UseEntity(x => x.TestClasses)
                .UseAllProperties()
                .UseMany(x => x.TestSubClasses, x => x.Id)
                .UseManyToMany(x => x.UntestedTests, x => new { x.TestClassId, x.UntestedClassId })
                .UseSequentialIndex(x => x.Id, generateId: true)
                .UseIndex(x => x.IndexedName, GenIndexedName)
                .UseExtension(typeof(HookExtension))
                .UseExtension(typeof(SearchExtension), x => new { x.TestName, x.Number })
                .UsePredicate("Active", x => x.Number > 0)
                .UsePredicate("MinNumber", (TestClass x, int min) => x.Number >= min)
                .UsePredicate("Sweet", x => x.Flavor == Flavor.Sweet)
                .UsePredicate("Search", (TestClass x, SearchQuery query) => query.Matches(x.SearchField))
                // Same key twice: scan-backed and materialized, so tests can
                // cross-check the two engines against each other.
                .UseSort(x => x.Number)
                .UseSort("NumberMat", x => x.Number, materialized: true)
                // Combined predicate: cheap scalar test first so the search
                // probe only runs on rows that pass it.
                .UsePredicate("SearchActive", (TestClass x, SearchQuery query, int min) => x.Number >= min && query.Matches(x.SearchField))
                .Build();

            UntestedClasses = builder.UseEntity(x => x.UntestedClasses)
                .UseProperties(x => new { x.Id, x.TestName })
                .UseManyToMany(x => x.UntestedTests, x => new { x.TestClassId, x.UntestedClassId })
                .UseSequentialIndex(x => x.Id)
                // Opt-out override: cache stores the caller's reference on
                // Create/Update (SafetyTests cover both behaviors).
                .UseSafety(copyOnInsert: false)
                .Build();

            IndexedClasses = builder.UseEntity(x => x.IndexedClasses)
                .UseAllProperties()
                .UseIndex(x => x.Code)
                .UseIndex(x => x.ExternalId)
                .Build();

            CompositeParents = builder.UseEntity(x => x.CompositeParents)
                .UseAllProperties()
                .UseMany(x => x.Slots, x => new { x.CompositeParentId, x.Slot })
                .UseSequentialIndex(x => x.Id, generateId: true)
                .Build();

            RegionalCodes = builder.UseEntity(x => x.RegionalCodes)
                .UseAllProperties()
                .UseSequentialIndex(x => x.Id, generateId: true)
                .UseIndex(x => new { x.Region, x.Code })
                .Build();

            AuditedClasses = builder.UseEntity(x => x.AuditedClasses)
                .UseAllProperties()
                .UseSequentialIndex(x => x.Id, generateId: true)
                .UseExtension(typeof(AuditExtension))
                .UseExtension(typeof(ValuedContextExtension), x => new { x.Note })
                .Build();

            Companies = builder.UseEntity(x => x.Companies)
                .UseAllProperties()
                .UseOwned(x => x.RegistrationAddress)
                .UseSequentialIndex(x => x.Id, generateId: true)
                .UsePredicate("InCity", (Company x, string city) => x.RegistrationAddress != null && x.RegistrationAddress.City == city)
                .Build();

            ActiveTransactions = builder.UseEntity(x => x.ActiveTransactions)
                .UseAllProperties()
                .UseSequentialIndex(x => x.Id, generateId: true)
                .Build();

            ArchivedTransactions = builder.UseEntity(x => x.ArchivedTransactions)
                .UseAllProperties()
                .UseSequentialIndex(x => x.Id, generateId: true)
                .Build();

            AllTransactions = builder.UseUnion("AllTransactions", ActiveTransactions, ArchivedTransactions).Build();

            Profiles = builder.UseEntity(x => x.Profiles)
                .UseAllProperties()
                .UseSequentialIndex(x => x.Id, generateId: true)
                .UsePredicate("Favors", (Profile x, int fav) => x.Favorites.Contains(fav))
                .Build();
        }

        public static string GenIndexedName() => Guid.NewGuid().ToString("N");
    }

    public partial class TestClassEntityStore : EntityStore<TestClass> { }
    public partial class UntestedClassEntityStore : EntityStore<UntestedClass> { }
    public partial class IndexedClassEntityStore : EntityStore<IndexedClass> { }
    public partial class CompositeParentEntityStore : EntityStore<CompositeParent> { }
    public partial class RegionalCodeEntityStore : EntityStore<RegionalCode> { }
    public partial class AuditedClassEntityStore : EntityStore<AuditedClass> { }
    public partial class CompanyEntityStore : EntityStore<Company> { }
    public partial class ActiveTransactionEntityStore : EntityStore<ActiveTransaction> { }
    public partial class ArchivedTransactionEntityStore : EntityStore<ArchivedTransaction> { }
    public partial class ProfileEntityStore : EntityStore<Profile> { }
}
