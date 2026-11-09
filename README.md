# Ef.Mems

[![NuGet](https://img.shields.io/nuget/v/Ef.Mems.svg)](https://www.nuget.org/packages/Ef.Mems)

> Library primarily human written and used in production. AI was used to clean up generator code: moved StringBuilder code into .cshtml templates, built benchmarks, also found and fixed some edge cases. AI was also used for documentation and with preparing the public release. The library acts as a cache for a single-producer architecture, generates readonly classes so someone doesn't accidentally edit a cached entity directly. Has limitations, but gives massive performance boost when used correctly. 

Ef.Mems is an in-memory cache layer for Entity Framework Core, built for applications
where a single process owns all writes to the database. A Roslyn source generator turns a
fluent store declaration into strongly typed, allocation-conscious cache classes: reads
are served from immutable in-memory snapshots without locks, while writes go through EF
Core and update the cache in place.

The design targets read-heavy workloads at scales of a few hundred to several million
rows per table. Representative numbers from the included benchmark suite (4M rows):
full-text search over all rows in ~50 ms, "latest 50 by Id" in ~400 ns, windowed reads
on a materialized sort in ~600 ns.

## Contents

- [Architecture](#architecture)
- [Requirements and setup](#requirements-and-setup)
- [Getting started](#getting-started)
- [Store configuration reference](#store-configuration-reference)
- [Reading from the cache](#reading-from-the-cache)
- [Writing to the cache](#writing-to-the-cache)
- [Transactions](#transactions)
- [Search](#search)
- [Concurrency and consistency model](#concurrency-and-consistency-model)
- [Safety features](#safety-features)
- [Exceptions](#exceptions)
- [Generator diagnostics](#generator-diagnostics)
- [Performance notes](#performance-notes)
- [Limitations and rules of use](#limitations-and-rules-of-use)
- [Building from source](#building-from-source)
- [License](#license)

## Architecture

The `Ef.Mems` NuGet package contains two components; the repository adds the test and
benchmark projects around them:

| Project | Role |
|---|---|
| `src/Ef.Mems` | Runtime: base classes (`DataStore<T>`, `EntityStore<T>`), query engine, transactions, search, safety exceptions. Ships as `lib/net10.0`. |
| `src/Ef.Mems.Generator` | Roslyn incremental source generator. Reads your `ConstructStore` declaration and emits the cache implementation at compile time. Ships inside the same package as `analyzers/dotnet/cs`. |
| `tests/` | xUnit suites: runtime behavior against SQLite (`Ef.Mems.Tests`) and generator diagnostics, compile-shape matrix and incremental-caching guarantees (`Ef.Mems.Generator.Tests`). |
| `benchmarks/Ef.Mems.Benchmarks` | BenchmarkDotNet suite documenting the performance characteristics (see its own README). |

The core assumptions:

- **Single writer.** Exactly one process writes to the database, through the store. If
  anything else modifies the database, the cache is stale until you call `Refresh()`.
  This is an architectural contract, not something the library detects or enforces.
- **Stale-reads-only races.** Readers never take a lock on the hot paths and never see
  a torn or corrupt snapshot; the only race permitted by the design is reading data that
  is a moment out of date.
- **Immutable read models.** Consumers receive generated `...ReadOnly` wrapper objects,
  never the cached entities themselves. The wrappers expose cached properties as
  read-only (with `byte[]` surfaced as `ReadOnlyMemory<byte>`).

Internally, each generated entity store keeps one or more snapshot structures: a dense
array indexed by the sequential key, a dictionary per declared index, and optionally a
pre-sorted array per materialized sort. Structural changes clone-and-swap the affected
snapshot with `Volatile.Write`; scalar updates mutate the underlying entity in place,
which is safe because readers only ever observe stale-vs-fresh values, never invalid
state.

## Requirements and setup

- .NET 10.0 (SDK 10.0.100 or later)
- Entity Framework Core 10.x with a relational provider
- ProtoPromise 3.5 (transaction gating; transitive via `Ef.Mems`)

Install the single package; it carries both the runtime library and the source
generator, so no analyzer wiring is needed:

```xml
<ItemGroup>
  <PackageReference Include="Ef.Mems" Version="1.0.1" />
</ItemGroup>
```

```
dotnet add package Ef.Mems
```

Working from a clone instead of the package: reference the runtime normally and the
generator as an analyzer.

```xml
<ItemGroup>
  <ProjectReference Include="..\src\Ef.Mems\Ef.Mems.csproj" />
  <ProjectReference Include="..\src\Ef.Mems.Generator\Ef.Mems.Generator.csproj"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
</ItemGroup>
```

To inspect the generated code, build with `-p:EmitCompilerGeneratedFiles=true` and look
under `obj/<Configuration>/<TFM>/generated/`.

## Getting started

A store is a class deriving from `DataStore<TContext>`. You declare which entities to
cache, and how, inside `ConstructStore`. For every `builder.UseEntity(...)...Build()`
chain you also declare an empty partial class deriving from `EntityStore<TEntity>`; the
generator fills it in.

```csharp
public class Product
{
    public int Id { get; set; }
    public string Sku { get; set; }
    public string Name { get; set; }
    public decimal Price { get; set; }
}

public class ShopContext : DbContext
{
    public ShopContext() { }
    public ShopContext(DbContextOptions<ShopContext> options) : base(options) { }
    public DbSet<Product> Products { get; set; }
}

public class ShopStore : DataStore<ShopContext>
{
    private string _connectionString;

    public ShopStore(string connectionString) : base(connectionString) { }

    protected override void Initialize(object arg)
    {
        _connectionString = (string)arg;
    }

    // Must return a NEW context on every call; the store creates and
    // disposes contexts as needed.
    public override ShopContext GetContext()
    {
        var options = new DbContextOptionsBuilder<ShopContext>()
            .UseSqlite(_connectionString)
            .Options;
        return new ShopContext(options);
    }

    public ProductEntityStore Products;

    protected override void ConstructStore(StoreBuilder<ShopContext> builder)
    {
        Products = builder.UseEntity(x => x.Products)
            .UseAllProperties()
            .UseSequentialIndex(x => x.Id, generateId: true)
            .UseIndex(x => x.Sku)
            .UsePredicate("MinPrice", (Product x, decimal min) => x.Price >= min)
            .Build();
    }
}

// The generator completes this class.
public partial class ProductEntityStore : EntityStore<Product> { }
```

Usage:

```csharp
var store = new ShopStore("Data Source=shop.db");

// Lookups
ProductReadOnly p = store.Products.GetById(42);
ProductReadOnly q = store.Products.GetBySku("A-1001");

// Queries
List<ProductReadOnly> page = await store.Products.Query(x => x.Price > 10m, skip: 0, take: 50);
List<ProductReadOnly> cheap = await store.Products.QueryMinPrice(5m, 0, 50);

// Writes (also persist through EF Core)
var created = await store.Products.Create(new Product { Sku = "A-1002", Name = "Widget", Price = 3m });
```

Construction is synchronous and eager: the `DataStore` base constructor runs
`Database.Migrate()` (and disables `IDENTITY_CACHE` on SQL Server), then builds every
entity store, each of which loads its full table into memory before the constructor
returns. `Initialize(object arg)` runs before any of that and receives the constructor
argument, which makes it the place to store connection configuration.

## Store configuration reference

All configuration happens in `ConstructStore` through the fluent builder. The chain is
parsed at compile time by the source generator; it is declarative, so the lambdas must
be written inline (they are read syntactically, not executed).

### Property caching

```csharp
.UseAllProperties()                          // cache every scalar property
.UseProperties(x => new { x.Id, x.Name })    // cache a subset
.UseProperty(x => x.Name)                    // cache a single property
```

Only cached properties appear on the generated `...ReadOnly` wrapper and participate in
queries and update diffs. Index properties are always cached implicitly.

When **all** properties are cached, `ReadOnly.GetEntity()` is synchronous and clones the
entity from the cache. When only a subset is cached, `GetEntity()` is generated as
`async Task<TEntity>` and fetches the complete row from the database, because the cache
cannot reconstruct the uncached columns. `Refresh()` for a partially cached entity also
projects only the cached columns, so uncached data is never pulled into memory.

### Sequential index

```csharp
.UseSequentialIndex(x => x.Id)                     // ids assigned by the database
.UseSequentialIndex(x => x.Id, generateId: true)   // ids assigned by the store
```

The primary index for dense, monotonically increasing integer keys. The cache backs it
with a flat array indexed by key, which makes `GetById` an array access and gives every
query a cache-friendly sequential scan source ordered by id.

This index assumes **dense** keys by definition: the array is sized by the largest key,
so sparse keys (for example ids seeded at 1,000,000) would silently allocate enormous
arrays. A guard throws `SparseSequentialIndexException` when a new key lands far beyond
the current sequence high-water mark; see [Safety features](#safety-features).

With `generateId: true` the store assigns the next sequence value itself on create,
which avoids a database round-trip for identity retrieval. Stores with a sequential
index also gain the `descending` flag on their query methods (see
[Sorted reads](#sorted-reads-and-the-descending-flag)).

### Dictionary indexes

```csharp
.UseIndex(x => x.Sku)                              // unique single-property index
.UseIndex(x => x.Code, GenerateCode)               // with a value generator
.UseIndex(x => new { x.Region, x.Code })           // composite index
```

Each index generates a `GetBy{Property}(key)` lookup backed by a dictionary (`null` when
absent). Composite indexes take an anonymous-object selector with at least two members
and generate a multi-parameter lookup, for example
`GetByRegionCode(string region, string code)`, keyed by value tuple.

The optional generator (`Func<TProp>`, single-property indexes only) is invoked on every
create to assign the index value before insert.

Index keys are expected to be unique per entity. Indexes are fully maintained on update:
when an indexed property changes, the old key is removed and the new one inserted, so
stale lookups cannot occur, and index property changes are persisted to the database
like any other cached property.

Every entity store needs an update key: the sequential index if declared, otherwise a
single-property index. A composite index cannot serve as the update key, so an entity
whose only index is composite is a configuration error (diagnostic E5).

### Navigation collections

```csharp
.UseMany(x => x.OrderLines, line => line.Id)                            // one-to-many
.UseMany(x => x.Slots, s => new { s.ParentId, s.Slot })                 // composite child key
.UseManyToMany(x => x.TagLinks, link => new { link.ProductId, link.TagId })
```

`UseMany` caches a child collection with the parent. The second argument selects the
child's primary key (single property or anonymous object for composite keys); update
diffs use it to detect added, modified, and removed children and to translate those into
the corresponding EF Core operations.

`UseManyToMany` caches a join-entity collection that appears on two cached entities.
The cache maintains both sides: creating or deleting join rows through one side is
reflected in the other side's cached collection (using lock-free compare-and-swap
appends and removes on the wrapper's internal array).

Navigation collections are exposed on the wrapper as `IReadOnlyList<TChildReadOnly>`.

### Named predicates

```csharp
.UsePredicate("Active", x => x.Number > 0)
.UsePredicate("MinNumber", (Product x, decimal min) => x.Price >= min)
.UsePredicate("Search", (Product x, SearchQuery q) => q.Matches(x.SearchField))
.UsePredicate("SearchMin", (Product x, SearchQuery q, decimal min) => x.Price >= min && q.Matches(x.SearchField))
```

A named predicate is lifted into a generated struct implementing
`IPredicate<TReadOnly>`; extra lambda parameters (up to three) become struct fields and
method parameters. For each name the generator emits a full query family:

```
Query{Name}(args..., int skip, int take, bool descending = false)
Query{Name}(args...)                                   // unpaged
QueryCount{Name}(args..., int skip, int take, ...)     // window + total match count
QuerySortCount{Name}<TKey>(args..., int skip, int take, Func<...,TKey> keySelector, ...)
FirstOrDefault{Name}(args..., bool descending = false)
```

Because the predicate is a struct constrained generic, the JIT specializes and inlines
the scan loop per predicate type. This is the recommended way to declare hot queries: in
benchmarks with realistic (polymorphic) call sites it is roughly 1.7-2x faster than
passing a lambda, and it does not allocate a delegate per call.

Constraints: the name must be a constant string and a valid C# identifier; the predicate
must be a single expression (no block body); it may reference only its own parameters
and static members - it cannot capture locals or store fields.

To combine predicates (for example search plus a filter), declare one predicate with the
combined condition, as in `SearchMin` above. Put the cheap scalar test first so the more
expensive probe only runs on rows that pass it.

### Named sorts

```csharp
.UseSort(x => x.Price)                                  // scan-backed, named "ByPrice"
.UseSort("Cheapest", x => x.Price)                      // explicit name
.UseSort("PriceMat", x => x.Price, materialized: true)  // maintained sorted array
```

A named sort generates ordered query methods for a fixed key:

```
QuerySort{Name}(predicate, int skip, int take, bool descending = false)        // delegate predicate
QuerySort{Name}<TPred>(TPred predicate, int skip, int take, bool descending)   // struct predicate
QuerySort{Name}(int skip, int take, bool descending = false)                   // no predicate
QuerySortCount{Name}(...)                                                      // same three, plus total match count
```

Two backing strategies:

- **Scan (default).** Each call runs a fully specialized filter-and-heap pass over the
  snapshot (a bounded top-K selection, not a full sort). No write cost, works for any
  table size; a top-50 over 4M rows costs tens of milliseconds.
- **Materialized (`materialized: true`).** The store maintains a pre-sorted snapshot
  array, updated on every create, update, and delete of the entity. The no-count
  `QuerySort{Name}` methods walk this array directly and early-exit once the window is
  filled: when the window direction aligns with where the matching rows sit, a top-50
  over 4M rows costs well under a microsecond. The trade-offs:
  - Every write to the entity copies the sorted array (O(n)). Use only for read-mostly
    tables.
  - `QuerySortCount{Name}` deliberately routes to the scan engine even when
    materialized, because computing a match count requires visiting every row anyway
    and the sorted array is slower to walk than the dense snapshot. Counted sorted
    queries therefore cost the same with or without materialization.
  - A predicate anti-aligned with the walk direction (e.g. matches sit at the end of
    the sort order but the query pages from the start) degrades to a partial scan.

The sort key must be a cached, non-array property, and sort names must be unique per
entity (diagnostic E10).

### Extensions

```csharp
.UseExtension(typeof(AuditExtension))
.UseExtension(typeof(SearchExtension), x => new { x.Name, x.Description })
```

An extension is a static class whose hook methods are invoked inline before each write,
on both direct operations (`Create`/`Update`/`Delete` and their `Range` variants) and
staged transaction commits. Four shapes are recognized:

```csharp
// Simple form - typeof(MyExtension) with no property selector:
public static void BeforeCreate(object dataStore, MyEntity entity) { ... }
public static void BeforeUpdate(object dataStore, MyEntity entity) { ... }
public static void BeforeDelete(object dataStore, MyEntity entity) { ... }

// Configured form - with a property selector; values holds the selected
// property values in declaration order:
public static void BeforeCreate(object dataStore, MyEntity entity, object[] values) { ... }
public static void BeforeUpdate(object dataStore, MyEntity entity, object[] values) { ... }

// Context form - context is the caller-supplied per-operation value (see
// below); may also be combined with the values array:
public static void BeforeCreate(object dataStore, MyEntity entity, object context) { ... }
public static void BeforeCreate(object dataStore, MyEntity entity, object[] values, object context) { ... }
```

All hooks are optional; declare only the ones you need. The entity parameter may be the
concrete entity type or an interface it implements (this is how `SearchExtension`
accepts any `ISearchable`). The `values` parameter must be typed `object[]` and the
`context` parameter `object`; invalid hook signatures are a compile-time error (E9).

#### Write context

When any hook of an entity declares the context parameter, that entity's generated
write methods gain an optional trailing `object context = null`, delivered untouched to
every context-taking hook of the operation:

```csharp
await store.Products.Create(product, context: userId);
await store.Products.UpdateRange(products, context: userId);
await store.Products.Delete(product, context: userId);

// Staged writes receive the context passed to Commit (one per unit of work):
using (var trx = store.StartTransaction())
{
    store.Products.StageCreate(trx, newProduct);
    await trx.Commit(context: userId);
}
```

This is how per-operation data such as the acting user reaches a hook - e.g. an audit
extension stamping `CreatedOn`/`CreatedBy`:

```csharp
public static class AuditExtension
{
    public static void BeforeCreate(object dataStore, Product entity, object context)
    {
        entity.CreatedOn = DateTime.UtcNow;
        entity.CreatedBy = (Guid)context;
    }
}
```

Entities without a context-taking hook keep their write signatures unchanged. When the
caller omits the argument, hooks receive `null`. Note that staged hooks run at commit
time with `Commit`'s context: if several callers stage into one transaction, the
committer's context wins.

### Owned types

```csharp
// OnModelCreating:
modelBuilder.Entity<Company>().OwnsOne(c => c.RegistrationAddress);

// Store configuration:
builder.UseEntity(x => x.Companies)
    .UseAllProperties()
    .UseOwned(x => x.RegistrationAddress)
    ...
    .Build();
```

`UseOwned` declares an EF owned/complex member stored in the entity's own table
(`OwnsOne`, or EF 8+ `ComplexProperty`). The generator cannot see `OnModelCreating`, so
the declaration is explicit. What it does:

- **Safety clones deep-copy the owned instance**, so `Create`/`Update`/staged writes
  persist it and the cache never shares a mutable instance with the caller.
  `GetEntity()` hands back a deep copy too.
- **The ReadOnly exposes a generated `{Type}ReadOnly` wrapper** (e.g.
  `AddressReadOnly`), one per owned type per store, shared across entities. Members use
  the same read-only mapping as entity properties (`byte[]` → `ReadOnlyMemory<byte>`),
  so readers can never mutate cached state. A null owned member is a null wrapper.
- **Updates diff member-wise but write as a unit**: when any member differs - including
  the member going from null to a value or back - all of the owned object's columns are
  written. Null removal requires the owned navigation to be optional in the EF model.
- **Predicates can reach into owned members**:
  `UsePredicate("InCity", (Company x, string c) => x.RegistrationAddress != null && x.RegistrationAddress.City == c)`.

Not supported (loud E11 diagnostics, not silent gaps): collections of owned types
(`OwnsMany` - use `UseMany`), nested owned types (owned members must be EF scalars),
and owned members as index or sort keys.

**The E11 guard**: any public settable class-typed property that is not a declared
navigation, not `[NotMapped]`, and not declared with `UseOwned` produces a **warning**,
because the store would otherwise silently drop it from clones, diffs and the cache
(historically this meant lost data on `Create` with `copyOnInsert` on). Passing such a
property to `UseProperties` is an **error** - the generated code would treat it as a
scalar and break; use `UseOwned` instead.

### Primitive collections and [NotMapped]

Scalar-element collections typed **`List<T>` or `T[]`** (EF Core 8+ "primitive
collections", stored as a JSON column) are cached like scalar properties: included by
`UseAllProperties`, value-copied by the safety clones and `GetEntity()`, diffed by
**sequence equality** on update, and exposed read-only on the ReadOnly
(`IReadOnlyList<T>` for lists, `ReadOnlyMemory<T>` for arrays; `byte[]`/`char[]` keep
their existing blob semantics). Predicates can reach into them:
`UsePredicate("Favors", (Profile x, int fav) => x.Favorites.Contains(fav))`. Collection
properties cannot be index or sort keys (E10).

`[NotMapped]` properties are now excluded everywhere - from `UseAllProperties`, clones,
diffs and all E11 guards. Selecting one explicitly via `UseProperties` keeps it cached
but warns (E11): reads work, but `Update` throws at runtime if its value ever changes,
since EF has no such property to mark modified.

The intended pattern for set-like data on providers that store collections as a single
column - a mapped `List<T>` pass-through over an unmapped backing set:

```csharp
[MaxLength(200)]
public List<int> Favorites
{
    get => FavoriteSet.ToList();
    set => FavoriteSet = value?.ToHashSet() ?? [];
}

[NotMapped] public HashSet<int> FavoriteSet { get; set; } = [];
```

A scalar collection in any other shape (a bare `HashSet<int>`, `Collection<T>`, ...)
gets an E11 warning pointing at this pattern.

**EF configuration required for computed pass-throughs:** EF's primitive-collection
materializer populates the collection returned by the *getter* in place instead of
calling the setter - and a computed getter returns a throwaway copy, so loaded values
silently vanish (`UsePropertyAccessMode` does not change this). Give such a property an
explicit value converter, which makes it a plain scalar conversion that assigns through
the setter:

```csharp
modelBuilder.Entity<Profile>().Property(p => p.Favorites)
    .HasConversion(
        v => JsonSerializer.Serialize(v, (JsonSerializerOptions)null),
        v => JsonSerializer.Deserialize<List<int>>(v, (JsonSerializerOptions)null),
        new ValueComparer<List<int>>(
            (a, b) => a.SequenceEqual(b),
            v => v.Aggregate(0, (h, x) => HashCode.Combine(h, x)),
            v => v.ToList()));
```

Plain auto-property collections (like a `List<string> Tags`) need no configuration.

### Unions (polymorphic reads)

```csharp
public ActiveTransactionEntityStore ActiveTransactions;
public ArchivedTransactionEntityStore ArchivedTransactions;
public AllTransactionsUnion AllTransactions;

// In ConstructStore, after the member stores' Build() calls:
AllTransactions = builder.UseUnion("AllTransactions", ActiveTransactions, ArchivedTransactions).Build();
```

`UseUnion` declares a **read-only view over 2-4 member stores**, letting distinct entity
types with a shared shape be queried as one. The generator emits:

- **`I{Name}ReadOnly`** - the intersection of the members' cached scalar properties
  (matched by name *and* type), implemented by every member's ReadOnly. Non-shared
  properties stay off the interface; downcast to the concrete ReadOnly to reach them.
- **`{Name}Union`** - the view itself:

```csharp
int Count;                                                       // sum over members
await store.AllTransactions.Query(x => x.Amount > 50, skip, take);
await store.AllTransactions.QueryCount(predicate, skip, take);   // items + total match count
await store.AllTransactions.FirstOrDefault(predicate);
await store.AllTransactions.Query(skip, take);                   // unfiltered paging
await store.AllTransactions.QuerySortCount(predicate, skip, take, x => x.Amount, descending: true);
```

Members are walked in declaration order; paging is exact across store boundaries
(the counted variant's match totals adjust the residual skip at each boundary), and
sorted queries merge each member's bounded top-`skip+take` window into the global
order. Union reads compose the member stores' own query methods, so each member keeps
its snapshot/locking discipline; predicates and key selectors pass through by delegate
variance with no adapters.

Semantics and limits:

- **Stale-ok, not a consistent cross-store snapshot**: an entity mid-move between
  members (e.g. active → archived in a transaction) can transiently appear in both
  stores or neither. Same model as all cross-store reads.
- **No unified `GetById`**: members generate sequential ids independently, so ids
  collide across stores by construction. Fetch by key through the member store.
- **No writes through the union** - it is a view. Moving between members is a normal
  transaction (`StageDelete` on one store + `StageCreate` on the other).
- **No union-level named predicates/sorts** (yet); member-level ones work as always.
- `UseUnion(...).Build()` must run after every member's `Build()` (clear exception
  otherwise). Diagnostics (E12): invalid/duplicate name, a member entity not configured
  with `UseEntity`, two unions over the same member set (the `Build()` overload would
  be ambiguous), and an empty property intersection.

### Feature toggles

```csharp
.UseFeatures(create: true, update: true, delete: false)
```

Disables generation of the corresponding write methods entirely (the store simply will
not have `Delete`/`DeleteRange`/`StageDelete`/`Truncate` in the example above). If a transaction
somehow stages a disabled operation kind against the store, commit throws
`NotSupportedException`.

### Safety configuration

```csharp
// Store-wide defaults:
builder.UseSafety(copyOnInsert: true, copyOnStage: true, sequentialIndexGuard: true);

// Per-entity override:
builder.UseEntity(x => x.Products)
    ...
    .UseSafety(copyOnInsert: false)
    .Build();
```

All three features default to **on** even when no `UseSafety` call is present; see
[Safety features](#safety-features) for what each does.

## Reading from the cache

### Read-only wrappers

Every cached entity type gets a generated `sealed class {Entity}ReadOnly` exposing the
cached properties as get-only members. `byte[]` properties are exposed as
`ReadOnlyMemory<byte>`, and navigation collections as `IReadOnlyList<TChildReadOnly>`.
Wrapper instances are shared snapshots - they are safe to hold, compare by reference,
and read from any thread, but they represent the entity as of some recent moment and do
not update in place after structural changes.

`GetEntity()` produces a detached, mutable copy of the entity - this is the intended way
to obtain an instance to modify and pass to `Update`. It is synchronous (a clone) for
fully cached entities and asynchronous (a database fetch) for partially cached ones.

Do not call the `__`-prefixed members; they are internal cache plumbing.

### Lookups

```csharp
store.Products.GetById(42);           // sequential index: array access
store.Products.GetBySku("A-1001");    // dictionary index
store.Regionals.GetByRegionCode("EU", "X17");  // composite index
store.Products.Count;                 // number of cached entities
```

Lookups return `null` when the key is absent. All lookups are lock-free.

### Queries

Every store has the generic query family (returned lists are freshly allocated and owned
by the caller; all query methods return `ValueTask`):

```csharp
// Delegate predicates - convenient, good for cold paths:
await store.Products.Query(x => x.Price > 10m, skip, take);
await store.Products.Query(x => x.Price > 10m);          // unpaged
await store.Products.Query(skip, take);                  // no predicate
await store.Products.GetAll();
await store.Products.FirstOrDefault(x => x.Sku == sku);

// Struct predicates - the fast path; TPred : struct, IPredicate<TReadOnly>.
// Named predicates generate these structs for you, e.g.:
await store.Products.Query(new ProductEntityStore.MinPricePredicate(10m), skip, take);

// Named predicate methods - the same thing with a friendlier surface:
await store.Products.QueryMinPrice(10m, skip, take);
await store.Products.FirstOrDefaultMinPrice(10m);
```

`Query(skip, take)` windows are applied to matching rows in snapshot order (ascending
sequential key for sequential stores).

### Counted queries

`QueryCount...` variants return `(List<TReadOnly> Items, int MatchCount)`: the requested
window plus the total number of matching rows, computed in a single pass - the standard
shape for paged UIs. Note that counting inherently visits every row, so a counted query
over N rows is O(N) regardless of window size.

### Sorted reads and the descending flag

Stores with a sequential index accept `descending: false` (default) on `Query`,
`QueryCount`, and `FirstOrDefault`, including named-predicate variants. Because the
sequential snapshot is ordered by id by construction, `descending: true` walks it back
to front and early-exits when the window fills. "Latest 50 of 4 million" costs
nanoseconds instead of a heap sort over every match; this is the idiomatic way to read
newest-first.

For ordering by an arbitrary key there are three tiers:

```csharp
// 1. Ad-hoc: heap-based top-K with a key selector (delegate or struct predicate):
await store.Products.QuerySortCount(x => x.Price > 10m, skip, take, x => x.Name);

// 2. Fully specialized ad-hoc: struct predicate + struct key selector + struct comparer,
//    for callers that want the JIT-specialized loop without declaring a named sort:
await store.Products.QuerySortCount<string, MyPred, MySelector, KeyComparer<string>>(
    pred, selector, new KeyComparer<string>(), skip, take);

// 3. Named sorts (preferred for recurring orderings) - see UseSort above:
await store.Products.QuerySortByPrice(pred, skip, take, descending: true);
await store.Products.QuerySortPriceMat(skip, take, descending: true);   // materialized
```

`IKeySelector<T, TKey>` and `KeyComparer<TKey>` live in the `Ef.Mems` namespace for
tier 2; named sorts generate the selector structs internally.

## Writing to the cache

All writes go through the store, persist via EF Core, and update the cache before
returning:

```csharp
ProductReadOnly ro  = await store.Products.Create(product);
List<ProductReadOnly> ros = await store.Products.CreateRange(products);

ProductReadOnly ro2 = await store.Products.Update(modifiedCopy);
List<ProductReadOnly> ros2 = await store.Products.UpdateRange(modifiedCopies);

bool deleted = await store.Products.Delete(product);
bool allDeleted = await store.Products.DeleteRange(products);

int rowsDeleted = await store.Products.Truncate();
```

Semantics:

- **Create** runs extension hooks, assigns generated keys (sequential and/or index
  generators), inserts through EF Core, and adds the entity to every snapshot. With
  `copyOnInsert` on (the default) the cache stores a clone, so you may keep using and
  mutating the instance you passed in.
- **Update** locates the cached entity by the update key, diffs the cached scalar
  properties against your instance, and persists only the changed columns. Navigation
  collections declared with `UseMany`/`UseManyToMany` are diffed too (children added,
  modified, removed). Secondary index changes are applied to both the cache and the
  database. Updating an entity that is not in the cache throws
  `EntityNotCachedException`. The instance you pass is a detached copy (typically from
  `GetEntity()`); the cache is never handed your reference on update when `copyOnInsert`
  is on.
- **Delete** removes the entity from the database and all snapshots, and returns whether
  a row was affected. Deleting an already-deleted entity surfaces EF Core's
  `DbUpdateConcurrencyException`, consistent with updating a deleted entity.
- **Truncate** deletes every row of the table set-based (EF Core `ExecuteDelete`),
  resets the identity (the store's sequential-index counter always; the database's
  identity/`AUTOINCREMENT` seed on SQLite and SQL Server), publishes empty snapshots and
  returns the number of rows deleted. It carries SQL `TRUNCATE` semantics deliberately:
  rows are **not** enumerated and `BeforeDelete` extension hooks do **not** run. The
  next `generateId` create produces id 1, exactly like a fresh store. Caveats:
  - Database-side rows referencing the table follow FK/cascade rules; nothing is staged
    or diffed.
  - Other stores' cached many-to-many navigation lists that referenced the truncated
    rows stay stale until those stores `Refresh()` (the usual cross-store staleness
    model).
  - Generated only when the entity supports deletes (`UseFeatures(delete: ...)`).
- **Refresh** (`await store.Products.Refresh()`) reloads the entire table (cached
  columns only) and atomically swaps every snapshot. It is the escape hatch when the
  database was modified outside the store, and it is what runs at construction. Note it
  materializes a full second copy of the table before the swap.

Writes to one entity store are serialized by that store's writer lock. Readers are never
blocked by writers.

## Transactions

A transaction stages writes across any number of entity stores of one `DataStore` and
commits them in a single EF Core `SaveChanges`, so the database change set is atomic:

```csharp
using (var trx = store.StartTransaction())
{
    store.Products.StageCreate(trx, newProduct);
    store.Products.StageUpdate(trx, changedProducts);   // ICollection overloads exist
    store.Orders.StageDelete(trx, oldOrder);
    await trx.Commit();
}
```

Rules and behavior:

- Any number of transactions may be open and staging concurrently; their commits are
  serialized per `DataStore` by an internal semaphore.
- `Commit` is one-shot, even after a failure: a failed commit may leave half-attached
  state in the transaction's context, so a retry must use a fresh transaction. Calling
  `Commit` twice throws `InvalidOperationException`.
- **An entity may be staged at most once per transaction**, in exactly one operation.
  Staging the same entity (by key) twice - in the same or different operation kinds -
  throws `DuplicateStagedEntityException` at commit. Creates whose key is still
  default/unassigned are exempt from the duplicate check.
- Mixing creates, updates, and deletes for the same store in one transaction is fully
  supported; the store applies all of its staged operations under a single writer-lock
  hold.
- With `copyOnStage` on (the default), entities are snapshotted at stage time, so
  mutating your instance after staging does not affect what commits. Join lists staged
  through many-to-many navigations are handed over by reference; do not reuse them after
  commit.
- If the commit fails, the database transaction rolls back and no cache snapshots are
  modified; the exception propagates from `Commit`.
- `Commit(context)` passes a per-commit value to context-taking extension hooks (see
  [Extensions](#extensions)); all staged operations of the transaction share it.
- `Dispose` releases the transaction's context and is safe to call after `Commit`
  (the `using` pattern above is the intended shape).

**Cross-store visibility caveat:** the database commit is atomic, but cache snapshots
are published per store after `SaveChanges` succeeds. A concurrent reader can therefore
briefly observe store A updated while store B still shows the old data. This is
consistent with the stale-reads-only model, but do not build invariants on reading two
stores at the same instant.

## Search

The built-in search stack has three parts: a marker interface, an extension that
maintains a search field, and an allocation-free query type.

```csharp
public class Product : ISearchable
{
    ...
    public string SearchField { get; set; }   // maintained by SearchExtension
}

// Configuration - SearchField is rebuilt from Name and Description on every
// create and update. SearchField itself must be cached (UseAllProperties or
// listed in UseProperties) so queries can scan it and updates persist it.
builder.UseEntity(x => x.Products)
    .UseAllProperties()
    ...
    .UseExtension(typeof(SearchExtension), x => new { x.Name, x.Description })
    .UsePredicate("Search", (Product x, SearchQuery q) => q.Matches(x.SearchField))
    .Build();

// Querying
var query = SearchQuery.Parse(userInput);            // parse once per request
var hits  = await store.Products.QuerySearch(query, 0, 50);
var page  = await store.Products.QueryCountSearch(query, 0, 50);  // with total count
```

Semantics:

- Both the stored `SearchField` and the query input are **latinized**: lower-cased with
  diacritics stripped (s from š, c from č, e from é, and so on), so the two sides always
  compare in the same alphabet regardless of how the user types.
- The input is split on whitespace into tokens; a row matches when **every** token
  occurs somewhere in its `SearchField` (AND semantics, substring match, order
  independent).
- `SearchQuery.Parse` does the normalization and builds one vectorized
  `SearchValues<string>` matcher per token; `Matches` is allocation-free and safe inside
  predicate scan loops. On 4M rows a search costs roughly 50 ms; on a few thousand rows,
  tens of microseconds.
- To combine search with filters, declare a combined predicate (see `SearchMin` under
  [Named predicates](#named-predicates)) with the cheap condition first.

## Concurrency and consistency model

What the cache guarantees:

- Readers never block, never see torn snapshots, and never throw due to a concurrent
  write. Array-backed read paths (sequential index, materialized sorts, lookups) take no
  lock at all; enumerable-backed paths (queries on stores whose only source is a
  dictionary index) take a short reader lock.
- A race between a reader and a writer can only manifest as the reader seeing the
  **previous** state - stale data, nothing else.
- Writes within one entity store are serialized and each write is atomic with respect to
  readers of any single snapshot.

What it deliberately does not guarantee:

- Freshness across snapshots or stores at a single instant (see the transaction
  caveat above; the same applies to an entity appearing in an index a moment before a
  materialized sort, and so on).
- Detection of external database writers. The single-writer rule is on you; `Refresh()`
  is the recovery tool.
- Repeatable reads: two consecutive reads may straddle a write.

## Safety features

All default to on; configure via `UseSafety` at store or entity level.

| Feature | Default | What it does | When to disable |
|---|---|---|---|
| `copyOnInsert` | on | `Create`/`Update` clone the entity before it enters the cache, so callers may keep mutating their instance. | Hot write paths where you can guarantee you never touch an instance after handing it over; saves one clone per write. |
| `copyOnStage` | on | `StageCreate`/`StageUpdate`/`StageDelete` snapshot the entity at stage time. | Same reasoning as `copyOnInsert`, for transactions. |
| `sequentialIndexGuard` | on | Throws `SparseSequentialIndexException` when a new sequential key exceeds `1024 + 4 x` the current sequence high-water mark, catching misconfigured (sparse) key spaces before they allocate huge arrays. `Refresh` is not guarded, since the table's actual maximum defines the high-water mark. | Only if a large one-time id jump is intentional. |

Guarded error paths (always on, not configurable): update-of-uncached-entity throws
`EntityNotCachedException`; duplicate staging throws `DuplicateStagedEntityException`;
double `Commit` throws `InvalidOperationException`.

## Exceptions

| Exception | Thrown when |
|---|---|
| `EntityNotCachedException` | An `Update` targets an entity not present in the cache (deleted, never created, or wrong/default key). Derives from `InvalidOperationException`. |
| `DuplicateStagedEntityException` | The same entity (by index key) is staged more than once in a single transaction. |
| `SparseSequentialIndexException` | A sequential index key lands far beyond the sequence high-water mark; see `sequentialIndexGuard`. |
| `NotSupportedException` | A transaction stages an operation kind disabled via `UseFeatures`. |
| `DbUpdateConcurrencyException` (EF Core) | Update or delete of a row that no longer exists in the database. |

## Generator diagnostics

Configuration errors are reported at compile time with these ids:

| Id | Meaning |
|---|---|
| E1 | Unexpected generator exception (bug report material; includes the stack trace). |
| E2 | Unexpected structure of a builder call - the chain contains something the generator cannot parse (e.g. an index selector that is not a property access or anonymous object with two or more members). |
| E3 | Incorrect argument type - e.g. an index `idGenerator` with parameters, or an `idGenerator` on a composite index. |
| E4 | A referenced class property is missing (e.g. a navigation or key property the configuration points at does not exist). |
| E5 | Missing update key: the entity has no sequential index and no single-property index (composite indexes cannot serve as the update key). |
| E6 | Unexpected store definition - the class does not derive from `DataStore<T>` or lacks a `ConstructStore` body. |
| E7 | Invalid `UseManyToMany` configuration (e.g. the join entity is not declared consistently on both sides). |
| E8 | Invalid predicate: name not a constant identifier, block body, or a captured reference the struct cannot carry. |
| E9 | Invalid extension: the hook methods do not match a recognized `Before*` signature. |
| E10 | Invalid sort: duplicate name, non-identifier name, key not a single cached property, or an array/collection-typed key. |
| E11 | Property guard. Warning: a class-typed or unsupported-collection property the store would silently drop (declare `UseOwned`, use a `List<T>` pass-through, or mark it `[NotMapped]`), or a `[NotMapped]` property selected explicitly. Error: a class-typed property passed to `UseProperties`, an invalid `UseOwned` target, or a nested owned type. |
| E12 | Invalid union: name not a constant identifier or duplicated, a member entity not configured with `UseEntity`, two unions over the same member set, or an empty property intersection. |

## Performance notes

Full methodology and current numbers live in `Ef.Mems.Benchmarks/README.md`. The
headlines that should shape how you use the API (4M-row table, .NET 9):

| Scenario | Cost |
|---|---|
| `GetById` / `GetBy{Index}` | nanoseconds (array or dictionary access) |
| Full-scan named (struct) predicate count | ~5-6 ms per million rows |
| Same scan with a delegate predicate at a polymorphic call site | ~1.7x slower |
| Search (`SearchQuery`), any token count | ~12 ns per row (~50 ms over 4M) |
| "Latest N" via `descending: true` on a sequential store | hundreds of nanoseconds |
| Named sort window, materialized, aligned direction, no count | hundreds of nanoseconds |
| Named sort window, scan-backed or counted | tens of milliseconds |

Practical guidance:

- Declare hot filters as `UsePredicate` and hot orderings as `UseSort` instead of
  passing lambdas.
- Prefer `descending: true` on the sequential index for newest-first reads; it is
  thousands of times cheaper than sorting by id.
- Reserve `materialized: true` for read-mostly tables whose UIs page in the sort
  direction, and avoid `QuerySortCount` on paths where the count is not actually
  displayed - counting is O(n) by nature.
- `Refresh()` on a large table temporarily doubles that table's memory footprint.

## Limitations and rules of use

- One process, one writer. The library neither detects nor tolerates external writers
  between refreshes.
- Entity class names must be unique across the store: generated store and wrapper type
  names derive from the simple class name.
- Sequential indexes require dense integer keys starting near `minValue`.
- Predicate and sort declarations are compile-time constructs; predicates cannot be
  composed at runtime - declare a combined predicate instead.
- Builder lambdas are parsed, not executed: keep them to plain property selections and
  expressions, as shown in this document.
- The whole cached data set lives in memory; size accordingly.
- Read models are snapshots: hold them as long as you like, but re-fetch (or re-query)
  when you need current values after a known write.

## Building from source

```
dotnet test Ef.Mems.sln              # runtime + generator test suites
.\pack.ps1                           # test, then pack to .\artifacts (nupkg + snupkg)
```

The repository layout is `src/` (runtime and generator), `tests/` and `benchmarks/`.
`src/Ef.Mems` is the only packable project; its `dotnet pack` bundles the generator
DLL under `analyzers/dotnet/cs`.

## License

Ef.Mems is licensed under the [Apache License 2.0](LICENSE). The analyzer embeds the
MIT-licensed RazorBlade templating runtime; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

The code the generator emits into your project is yours: it is produced from your store
declaration at compile time and carries no license obligation beyond those of the
library itself.
