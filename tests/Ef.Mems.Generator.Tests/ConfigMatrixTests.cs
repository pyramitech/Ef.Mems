using Ef.Mems.SourceGenerators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Ef.Mems.Generator.Tests
{
    /// <summary>
    /// Every builder configuration is a different generated program. These
    /// tests sweep the configuration matrix and assert that the emitted code
    /// COMPILES (not just that generation succeeds) - historically most
    /// generator bugs were compile-shape bugs one configuration away from the
    /// shapes the runtime test stores happen to use.
    /// </summary>
    public class ConfigMatrixTests
    {
        private static readonly Lazy<List<MetadataReference>> References = new(() =>
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                .Split(Path.PathSeparator)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToList());

        /// <summary>
        /// Model with two MTM partners (Thing/Other via Link), a UseMany child
        /// with single and composite keys, array-like props and extensions -
        /// enough surface for every chain variant below.
        /// </summary>
        private static string StoreSource(string thingChain, string otherChain = null, string storeExtras = "") => $$"""
            using Microsoft.EntityFrameworkCore;
            using System;
            using System.Collections.Generic;
            using Ef.Mems;

            namespace MatrixTest
            {
                public class Thing : ISearchable
                {
                    public int Id { get; set; }
                    public string Name { get; set; }
                    public string Code { get; set; }
                    public string AltCode { get; set; }
                    public Guid Ext { get; set; }
                    public int Number { get; set; }
                    public byte[] Blob { get; set; }
                    public char[] Chars { get; set; }
                    public string SearchField { get; set; }
                    // Primitive collections: cached, cloned and diffed like
                    // scalars in every matrix shape below.
                    public List<int> Nums { get; set; }
                    public string[] Labels { get; set; }
                    public List<Child> Children { get; set; }
                    public List<Link> Links { get; set; }
                }

                public class Child
                {
                    public int Id { get; set; }
                    public int ThingId { get; set; }
                    public int Slot { get; set; }
                    public string Data { get; set; }
                }

                public class Other
                {
                    public int Id { get; set; }
                    public string Label { get; set; }
                    public List<Link> Links { get; set; }
                }

                public class Link
                {
                    public int ThingId { get; set; }
                    public int OtherId { get; set; }
                    public string Payload { get; set; }
                }

                public class Ctx : DbContext
                {
                    public Ctx() { }
                    public DbSet<Thing> Things { get; set; }
                    public DbSet<Other> Others { get; set; }
                }

                public static class PlainHooks
                {
                    public static void BeforeCreate(object store, Thing entity) { }
                    public static void BeforeUpdate(object store, Thing entity) { }
                    public static void BeforeDelete(object store, Thing entity) { }
                }

                public static class CtxHooks
                {
                    public static void BeforeCreate(object store, Thing entity, object context) { }
                    public static void BeforeUpdate(object store, Thing entity, object context) { }
                    public static void BeforeDelete(object store, Thing entity, object context) { }
                }

                public static class ValuedCtxHooks
                {
                    public static void BeforeCreate(object store, Thing entity, object[] values, object context) { }
                    public static void BeforeUpdate(object store, Thing entity, object[] values, object context) { }
                }

                public class MatrixStore : DataStore<Ctx>
                {
                    public override Ctx GetContext() => throw new NotImplementedException();
                    public ThingEntityStore Things;
                    public OtherEntityStore Others;
                    protected override void ConstructStore(StoreBuilder<Ctx> builder)
                    {
            {{storeExtras}}
                        Things = builder.UseEntity(x => x.Things)
                            {{thingChain}}
                            .Build();

                        Others = builder.UseEntity(x => x.Others)
                            {{otherChain ?? ".UseAllProperties().UseSequentialIndex(x => x.Id)"}}
                            .Build();
                    }

                    public static string GenCode() => Guid.NewGuid().ToString("N");
                }

                public partial class ThingEntityStore : EntityStore<Thing> { }
                public partial class OtherEntityStore : EntityStore<Other> { }
            }
            """;

        private static void AssertGeneratesAndCompiles(string source)
        {
            var compilation = CSharpCompilation.Create("MatrixTest",
                new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), "MatrixStore.cs") },
                References.Value,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var driver = CSharpGeneratorDriver.Create(new IncrementalCacheGenerator())
                .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics);

            Assert.Empty(generatorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            var result = driver.GetRunResult();
            Assert.Null(result.Results[0].Exception);
            Assert.NotEmpty(result.Results[0].GeneratedSources);

            var errors = outputCompilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();
            Assert.True(errors.Count == 0,
                "generated code does not compile:\n" + string.Join("\n", errors.Take(10)));
        }

        [Fact]
        public void FullFeatures_AllOptions()
            => AssertGeneratesAndCompiles(StoreSource("""
                .UseAllProperties()
                            .UseMany(x => x.Children, x => x.Id)
                            .UseManyToMany(x => x.Links, x => new { x.ThingId, x.OtherId })
                            .UseSequentialIndex(x => x.Id, generateId: true)
                            .UseIndex(x => x.Code, GenCode)
                            .UseExtension(typeof(PlainHooks))
                            .UseExtension(typeof(SearchExtension), x => new { x.Name, x.Number })
                            .UsePredicate("Active", x => x.Number > 0)
                            .UsePredicate("Min", (Thing x, int min) => x.Number >= min)
                """,
                otherChain: ".UseAllProperties().UseSequentialIndex(x => x.Id).UseManyToMany(x => x.Links, x => new { x.ThingId, x.OtherId })"));

        [Fact]
        public void ContextHooks_FullFeatures()
            // Context, plain and valued+context hooks together on an entity
            // with navs/MTM: the trailing context parameter and every hook
            // call site (direct, range, staged commit) must compile.
            => AssertGeneratesAndCompiles(StoreSource("""
                .UseAllProperties()
                            .UseMany(x => x.Children, x => x.Id)
                            .UseManyToMany(x => x.Links, x => new { x.ThingId, x.OtherId })
                            .UseSequentialIndex(x => x.Id, generateId: true)
                            .UseExtension(typeof(PlainHooks))
                            .UseExtension(typeof(CtxHooks))
                            .UseExtension(typeof(ValuedCtxHooks), x => new { x.Name, x.Number })
                """,
                otherChain: ".UseAllProperties().UseSequentialIndex(x => x.Id).UseManyToMany(x => x.Links, x => new { x.ThingId, x.OtherId })"));

        [Fact]
        public void ContextHooks_WithDisabledFeatures()
            // Context hooks on a store missing some write methods: the
            // context parameter only exists on the generated ones.
            => AssertGeneratesAndCompiles(StoreSource(
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UseExtension(typeof(CtxHooks)).UseFeatures(update: false, delete: false)"));

        [Fact]
        public void PartialProperties()
            => AssertGeneratesAndCompiles(StoreSource(
                ".UseProperties(x => new { x.Id, x.Name }).UseSequentialIndex(x => x.Id)"));

        [Fact]
        public void PartialProperties_WithNavsAndIndex()
            => AssertGeneratesAndCompiles(StoreSource("""
                .UseProperties(x => new { x.Id, x.Name })
                            .UseMany(x => x.Children, x => x.Id)
                            .UseManyToMany(x => x.Links, x => new { x.ThingId, x.OtherId })
                            .UseSequentialIndex(x => x.Id)
                            .UseIndex(x => x.Code, GenCode)
                """,
                otherChain: ".UseAllProperties().UseSequentialIndex(x => x.Id).UseManyToMany(x => x.Links, x => new { x.ThingId, x.OtherId })"));

        [Fact]
        public void IndexOnly_NoSequentialIndex()
            => AssertGeneratesAndCompiles(StoreSource(
                ".UseAllProperties().UseIndex(x => x.Code)"));

        [Fact]
        public void MultipleIndexes()
            => AssertGeneratesAndCompiles(StoreSource(
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UseIndex(x => x.Code).UseIndex(x => x.AltCode)"));

        [Fact]
        public void GuidAndIntIndexes()
            => AssertGeneratesAndCompiles(StoreSource(
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UseIndex(x => x.Ext).UseIndex(x => x.Number)"));

        [Fact]
        public void GuidIndexOnly_NoSequentialIndex()
            => AssertGeneratesAndCompiles(StoreSource(
                ".UseAllProperties().UseIndex(x => x.Ext)"));

        [Fact]
        public void CompositeIndex_WithSequentialIndex()
            => AssertGeneratesAndCompiles(StoreSource(
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UseIndex(x => new { x.Code, x.AltCode })"));

        [Fact]
        public void CompositeIndex_MixedTypes_AlongsideSingleIndexes()
            => AssertGeneratesAndCompiles(StoreSource(
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UseIndex(x => x.Code).UseIndex(x => new { x.Name, x.Number }).UseIndex(x => new { x.Ext, x.AltCode })"));

        [Fact]
        public void CompositeIndex_OnPartialStore()
            => AssertGeneratesAndCompiles(StoreSource(
                ".UseProperties(x => new { x.Id, x.Name }).UseSequentialIndex(x => x.Id).UseIndex(x => new { x.Code, x.AltCode })"));

        [Fact]
        public void Sorts_ScanAndMaterializedAndNamed()
            => AssertGeneratesAndCompiles(StoreSource(
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UseSort(x => x.Number).UseSort(\"Newest\", x => x.Ext, materialized: true).UsePredicate(\"Min\", (Thing x, int min) => x.Number >= min)"));

        [Fact]
        public void Sorts_OnIndexOnlyStore()
            => AssertGeneratesAndCompiles(StoreSource(
                ".UseAllProperties().UseIndex(x => x.Code).UseSort(x => x.Number).UseSort(\"Mat\", x => x.Name, materialized: true)"));

        [Fact]
        public void CompositeKeyUseMany()
            => AssertGeneratesAndCompiles(StoreSource(
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UseMany(x => x.Children, x => new { x.ThingId, x.Slot })"));

        [Fact]
        public void FeatureFlags_NoCreate()
            => AssertGeneratesAndCompiles(StoreSource(
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UseFeatures(create: false)"));

        [Fact]
        public void FeatureFlags_NoUpdate()
            => AssertGeneratesAndCompiles(StoreSource(
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UseFeatures(update: false)"));

        [Fact]
        public void FeatureFlags_NoDelete()
            => AssertGeneratesAndCompiles(StoreSource(
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UseFeatures(delete: false)"));

        [Fact]
        public void FeatureFlags_ReadOnlyStore()
            => AssertGeneratesAndCompiles(StoreSource(
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UseFeatures(create: false, update: false, delete: false)"));

        [Fact]
        public void Safety_AllOptOut()
            => AssertGeneratesAndCompiles(StoreSource(
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UseSafety(copyOnInsert: false, copyOnStage: false, sequentialIndexGuard: false)"));

        [Fact]
        public void Safety_StoreWideDefaultsWithOverride()
            => AssertGeneratesAndCompiles(StoreSource(
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UseSafety(copyOnInsert: false)",
                storeExtras: "            builder.UseSafety(copyOnStage: false, sequentialIndexGuard: false);"));

        [Fact]
        public void SequentialWithoutGenerateId()
            => AssertGeneratesAndCompiles(StoreSource(
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UsePredicate(\"Named\", (Thing x, string name) => x.Name == name)"));

        [Fact]
        public void Minimal_NoNavsNoExtensionsNoPredicates()
            => AssertGeneratesAndCompiles(StoreSource(
                ".UseProperties(x => new { x.Id }).UseSequentialIndex(x => x.Id)"));

        /// <summary>Two entities sharing one owned type (wrapper must be emitted exactly once).</summary>
        private static string OwnedSource(string siteChain, string branchChain) => $$"""
            using Microsoft.EntityFrameworkCore;
            using System;
            using System.Collections.Generic;
            using Ef.Mems;

            namespace OwnedMatrixTest
            {
                public class Addr
                {
                    public string Street { get; set; }
                    public string City { get; set; }
                    public int Zip { get; set; }
                    public byte[] Geo { get; set; }
                }

                public class Site
                {
                    public int Id { get; set; }
                    public string Name { get; set; }
                    public int Number { get; set; }
                    public Addr Location { get; set; }
                }

                public class Branch
                {
                    public int Id { get; set; }
                    public string Label { get; set; }
                    public Addr HomeBase { get; set; }
                }

                public class Ctx : DbContext
                {
                    public Ctx() { }
                    public DbSet<Site> Sites { get; set; }
                    public DbSet<Branch> Branches { get; set; }
                }

                public class OwnedMatrixStore : DataStore<Ctx>
                {
                    public override Ctx GetContext() => throw new NotImplementedException();
                    public SiteEntityStore Sites;
                    public BranchEntityStore Branches;
                    protected override void ConstructStore(StoreBuilder<Ctx> builder)
                    {
                        Sites = builder.UseEntity(x => x.Sites)
                            {{siteChain}}
                            .Build();

                        Branches = builder.UseEntity(x => x.Branches)
                            {{branchChain}}
                            .Build();
                    }
                }

                public partial class SiteEntityStore : EntityStore<Site> { }
                public partial class BranchEntityStore : EntityStore<Branch> { }
            }
            """;

        [Fact]
        public void Owned_FullFeatures_SharedOwnedType()
            // Owned + indexes + sorts + predicate over the owned member on one
            // entity, plus a second entity sharing the owned type: exercises
            // the wrapper dedupe, __Clone, GetEntity, the diff sections and
            // the byte[] member inside the owned type.
            => AssertGeneratesAndCompiles(OwnedSource("""
                .UseAllProperties()
                            .UseOwned(x => x.Location)
                            .UseSequentialIndex(x => x.Id, generateId: true)
                            .UseIndex(x => x.Name)
                            .UseSort(x => x.Number, materialized: true)
                            .UsePredicate("InCity", (Site x, string city) => x.Location != null && x.Location.City == city)
                """,
                ".UseAllProperties().UseOwned(x => x.HomeBase).UseSequentialIndex(x => x.Id, generateId: true)"));

        [Fact]
        public void Owned_OnPartialPropertyStore()
            // Partial cache with an owned member: the Refresh projection must
            // include the owned property and GetEntity goes async.
            => AssertGeneratesAndCompiles(OwnedSource(
                ".UseProperties(x => new { x.Id, x.Name }).UseOwned(x => x.Location).UseSequentialIndex(x => x.Id)",
                ".UseAllProperties().UseOwned(x => x.HomeBase).UseSequentialIndex(x => x.Id)"));

        [Fact]
        public void Owned_WithDisabledFeatures()
            => AssertGeneratesAndCompiles(OwnedSource(
                ".UseAllProperties().UseOwned(x => x.Location).UseSequentialIndex(x => x.Id).UseFeatures(update: false, delete: false)",
                ".UseAllProperties().UseOwned(x => x.HomeBase).UseSequentialIndex(x => x.Id)"));

        /// <summary>Three transaction-like entities sharing Id/Amount/Reference for union shapes.</summary>
        private static string UnionSource(string chainA, string chainB, string chainC, string unionCalls) => $$"""
            using Microsoft.EntityFrameworkCore;
            using System;
            using System.Collections.Generic;
            using Ef.Mems;

            namespace UnionMatrixTest
            {
                public class ActiveTx { public int Id { get; set; } public decimal Amount { get; set; } public string Reference { get; set; } public string State { get; set; } }
                public class ArchivedTx { public int Id { get; set; } public decimal Amount { get; set; } public string Reference { get; set; } public DateTime ArchivedOn { get; set; } }
                public class FailedTx { public int Id { get; set; } public decimal Amount { get; set; } public string Reference { get; set; } public string Error { get; set; } }

                public class Ctx : DbContext
                {
                    public Ctx() { }
                    public DbSet<ActiveTx> ActiveTxs { get; set; }
                    public DbSet<ArchivedTx> ArchivedTxs { get; set; }
                    public DbSet<FailedTx> FailedTxs { get; set; }
                }

                public class UnionMatrixStore : DataStore<Ctx>
                {
                    public override Ctx GetContext() => throw new NotImplementedException();
                    public ActiveTxEntityStore ActiveTxs;
                    public ArchivedTxEntityStore ArchivedTxs;
                    public FailedTxEntityStore FailedTxs;
                    protected override void ConstructStore(StoreBuilder<Ctx> builder)
                    {
                        ActiveTxs = builder.UseEntity(x => x.ActiveTxs){{chainA}}.Build();
                        ArchivedTxs = builder.UseEntity(x => x.ArchivedTxs){{chainB}}.Build();
                        FailedTxs = builder.UseEntity(x => x.FailedTxs){{chainC}}.Build();
                        {{unionCalls}}
                    }
                }

                public partial class ActiveTxEntityStore : EntityStore<ActiveTx> { }
                public partial class ArchivedTxEntityStore : EntityStore<ArchivedTx> { }
                public partial class FailedTxEntityStore : EntityStore<FailedTx> { }
            }
            """;

        [Fact]
        public void GlobalNamespaceStore_Compiles()
            // A store declared without a namespace (minimal console apps):
            // the generated file must not wrap itself in "<global namespace>".
            => AssertGeneratesAndCompiles("""
                using Microsoft.EntityFrameworkCore;
                using System;
                using System.Collections.Generic;
                using Ef.Mems;

                public class Item { public int Id { get; set; } public string Name { get; set; } }

                public class Ctx : DbContext
                {
                    public Ctx() { }
                    public DbSet<Item> Items { get; set; }
                }

                public class GlobalStore : DataStore<Ctx>
                {
                    public override Ctx GetContext() => throw new NotImplementedException();
                    public ItemEntityStore Items;
                    protected override void ConstructStore(StoreBuilder<Ctx> builder)
                    {
                        Items = builder.UseEntity(x => x.Items).UseAllProperties().UseSequentialIndex(x => x.Id, generateId: true).Build();
                    }
                }

                public partial class ItemEntityStore : EntityStore<Item> { }
                """);

        [Fact]
        public void Union_MixedBackings_AndThreeMembers()
            // One seq-backed member with sorts/predicates, one dictionary-
            // backed member (index only), plus a three-member union: the
            // composed query calls must compile against every member shape.
            => AssertGeneratesAndCompiles(UnionSource(
                ".UseAllProperties().UseSequentialIndex(x => x.Id, generateId: true).UseSort(x => x.Amount).UsePredicate(\"Big\", x => x.Amount > 100)",
                ".UseAllProperties().UseIndex(x => x.Reference)",
                ".UseAllProperties().UseSequentialIndex(x => x.Id, generateId: true)",
                """
                var pair = builder.UseUnion("RecentTx", ActiveTxs, ArchivedTxs).Build();
                                var all = builder.UseUnion("EveryTx", ActiveTxs, ArchivedTxs, FailedTxs).Build();
                """));
    }
}
