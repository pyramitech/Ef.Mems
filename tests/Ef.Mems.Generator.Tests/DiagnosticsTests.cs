using Ef.Mems.SourceGenerators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Ef.Mems.Generator.Tests
{
    /// <summary>
    /// The generator's validation diagnostics (E-codes): each misconfiguration
    /// must surface as its documented diagnostic instead of silently emitting
    /// broken code (or none at all).
    /// </summary>
    public class DiagnosticsTests
    {
        private static readonly Lazy<List<MetadataReference>> References = new(() =>
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                .Split(Path.PathSeparator)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToList());

        /// <summary>Builds a one-entity store whose UseEntity chain is <paramref name="chain"/>.</summary>
        private static string StoreSource(string chain, string extraTypes = "") => $$"""
            using Microsoft.EntityFrameworkCore;
            using System;
            using System.Collections.Generic;
            using Ef.Mems;

            namespace DiagTest
            {
                public class Thing
                {
                    public int Id { get; set; }
                    public string Name { get; set; }
                    public int Number { get; set; }
                    public List<Join> Joins { get; set; }
                }

                public class Join
                {
                    public int ThingId { get; set; }
                    public int OtherId { get; set; }
                }

                public class Ctx : DbContext
                {
                    public Ctx() { }
                    public DbSet<Thing> Things { get; set; }
                }
            {{extraTypes}}
                public class DiagStore : DataStore<Ctx>
                {
                    public override Ctx GetContext() => throw new NotImplementedException();
                    public ThingEntityStore Things;
                    protected override void ConstructStore(StoreBuilder<Ctx> builder)
                    {
                        Things = builder.UseEntity(x => x.Things)
                            {{chain}}
                            .Build();
                    }
                }

                public partial class ThingEntityStore : EntityStore<Thing> { }
            }
            """;

        private static IReadOnlyList<Diagnostic> RunGenerator(string source)
        {
            var compilation = CSharpCompilation.Create("DiagTest",
                new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), "DiagStore.cs") },
                References.Value,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var driver = CSharpGeneratorDriver.Create(new IncrementalCacheGenerator())
                .RunGenerators(compilation);
            return driver.GetRunResult().Diagnostics;
        }

        private static void AssertDiagnostic(string expectedId, string chain, string extraTypes = "")
        {
            var diagnostics = RunGenerator(StoreSource(chain, extraTypes));
            Assert.Contains(diagnostics, d => d.Id == expectedId);
        }

        [Fact]
        public void MissingIndex_ReportsE5()
            => AssertDiagnostic("E5", ".UseAllProperties()");

        [Fact]
        public void MultipleSequentialIndexes_ReportsE5()
            => AssertDiagnostic("E5",
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UseSequentialIndex(x => x.Number)");

        [Fact]
        public void SingleEntityManyToMany_ReportsE7()
            => AssertDiagnostic("E7",
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UseManyToMany(x => x.Joins, x => new { x.ThingId, x.OtherId })");

        [Fact]
        public void DuplicatePredicateName_ReportsE8()
            => AssertDiagnostic("E8",
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UsePredicate(\"Same\", x => x.Number > 0).UsePredicate(\"Same\", x => x.Number > 1)");

        [Fact]
        public void InvalidPredicateName_ReportsE8()
            => AssertDiagnostic("E8",
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UsePredicate(\"not an identifier\", x => x.Number > 0)");

        [Fact]
        public void PredicateBlockBody_ReportsE8()
            => AssertDiagnostic("E8",
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UsePredicate(\"Block\", x => { return x.Number > 0; })");

        [Fact]
        public void ValuesHookWithoutSelector_ReportsE9()
            => AssertDiagnostic("E9",
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UseExtension(typeof(ValuesExt))",
                """
                    public static class ValuesExt
                    {
                        public static void BeforeCreate(object store, Thing entity, object[] values) { }
                    }
                """);

        [Fact]
        public void FourParamHookWithoutSelector_ReportsE9()
            // (store, entity, values, context) still needs the property
            // selector that feeds the values array.
            => AssertDiagnostic("E9",
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UseExtension(typeof(ValuedCtxExt))",
                """
                    public static class ValuedCtxExt
                    {
                        public static void BeforeCreate(object store, Thing entity, object[] values, object context) { }
                    }
                """);

        [Fact]
        public void FourParamHookWithoutValuesArray_ReportsE9()
            // With 4 parameters the third must be object[]; anything else is
            // a malformed hook, not two contexts.
            => AssertDiagnostic("E9",
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UseExtension(typeof(TwoCtxExt), x => new { x.Name })",
                """
                    public static class TwoCtxExt
                    {
                        public static void BeforeCreate(object store, Thing entity, object first, object second) { }
                    }
                """);

        [Fact]
        public void HookWithTooManyParameters_ReportsE9()
            => AssertDiagnostic("E9",
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UseExtension(typeof(FiveParamExt), x => new { x.Name })",
                """
                    public static class FiveParamExt
                    {
                        public static void BeforeCreate(object store, Thing entity, object[] values, object context, object extra) { }
                    }
                """);

        [Fact]
        public void ContextHookConfiguration_ReportsNothing()
        {
            var diagnostics = RunGenerator(StoreSource(
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UseExtension(typeof(CtxExt)).UseExtension(typeof(ValuedCtxOkExt), x => new { x.Name })",
                """
                    public static class CtxExt
                    {
                        public static void BeforeCreate(object store, Thing entity, object context) { }
                        public static void BeforeDelete(object store, Thing entity, object context) { }
                    }

                    public static class ValuedCtxOkExt
                    {
                        public static void BeforeUpdate(object store, Thing entity, object[] values, object context) { }
                    }
                """));
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void SelectorWithoutValuesHook_ReportsE9()
            => AssertDiagnostic("E9",
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UseExtension(typeof(PlainExt), x => new { x.Name })",
                """
                    public static class PlainExt
                    {
                        public static void BeforeCreate(object store, Thing entity) { }
                    }
                """);

        /// <summary>One-entity store whose entity has a class-typed (owned-candidate) property.</summary>
        private static string OwnedStoreSource(string chain,
            string ownedMembers = "public string Street { get; set; } public string City { get; set; }",
            string extraProps = "") => $$"""
            using Microsoft.EntityFrameworkCore;
            using System;
            using System.Collections.Generic;
            using System.ComponentModel.DataAnnotations.Schema;
            using Ef.Mems;

            namespace OwnedDiagTest
            {
                public class Addr
                {
                    {{ownedMembers}}
                }

                public class Site
                {
                    public int Id { get; set; }
                    public string Name { get; set; }
                    public Addr Location { get; set; }
                    {{extraProps}}
                }

                public class Ctx : DbContext
                {
                    public Ctx() { }
                    public DbSet<Site> Sites { get; set; }
                }

                public class OwnedDiagStore : DataStore<Ctx>
                {
                    public override Ctx GetContext() => throw new NotImplementedException();
                    public SiteEntityStore Sites;
                    protected override void ConstructStore(StoreBuilder<Ctx> builder)
                    {
                        Sites = builder.UseEntity(x => x.Sites)
                            {{chain}}
                            .Build();
                    }
                }

                public partial class SiteEntityStore : EntityStore<Site> { }
            }
            """;

        [Fact]
        public void UndeclaredClassProp_ReportsE11Warning()
        {
            var diagnostics = RunGenerator(OwnedStoreSource(
                ".UseAllProperties().UseSequentialIndex(x => x.Id)"));
            var d = Assert.Single(diagnostics, x => x.Id == "E11");
            Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        }

        [Fact]
        public void DeclaredUseOwned_ReportsNothing()
        {
            var diagnostics = RunGenerator(OwnedStoreSource(
                ".UseAllProperties().UseOwned(x => x.Location).UseSequentialIndex(x => x.Id)"));
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void NotMappedClassProp_ReportsNothing()
        {
            var diagnostics = RunGenerator(OwnedStoreSource(
                ".UseAllProperties().UseOwned(x => x.Location).UseSequentialIndex(x => x.Id)",
                extraProps: "[NotMapped] public Addr Legacy { get; set; }"));
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void ScalarCollectionInUnsupportedShape_ReportsE11Warning()
        {
            // HashSet<int> without [NotMapped]: the store cannot cache it and
            // suggests the List pass-through pattern.
            var diagnostics = RunGenerator(OwnedStoreSource(
                ".UseAllProperties().UseOwned(x => x.Location).UseSequentialIndex(x => x.Id)",
                extraProps: "public HashSet<int> FavoriteSet { get; set; }"));
            var d = Assert.Single(diagnostics, x => x.Id == "E11");
            Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        }

        [Fact]
        public void ScalarCollectionWithNotMapped_ReportsNothing()
        {
            var diagnostics = RunGenerator(OwnedStoreSource(
                ".UseAllProperties().UseOwned(x => x.Location).UseSequentialIndex(x => x.Id)",
                extraProps: "[NotMapped] public HashSet<int> FavoriteSet { get; set; }"));
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void PrimitiveCollections_ReportNothing()
        {
            // List<T>/T[] of scalars are supported cached properties now.
            var diagnostics = RunGenerator(OwnedStoreSource(
                ".UseAllProperties().UseOwned(x => x.Location).UseSequentialIndex(x => x.Id)",
                extraProps: "public List<int> Nums { get; set; } public string[] Labels { get; set; }"));
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void NotMappedInUseProperties_ReportsE11Warning()
        {
            var diagnostics = RunGenerator(OwnedStoreSource(
                ".UseProperties(x => new { x.Id, x.Name, x.Computed }).UseOwned(x => x.Location).UseSequentialIndex(x => x.Id)",
                extraProps: "[NotMapped] public string Computed { get; set; }"));
            var d = Assert.Single(diagnostics, x => x.Id == "E11");
            Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        }

        [Fact]
        public void SortOnScalarCollection_ReportsE10()
            => Assert.Contains(RunGenerator(OwnedStoreSource(
                ".UseAllProperties().UseOwned(x => x.Location).UseSequentialIndex(x => x.Id).UseSort(x => x.Nums)",
                extraProps: "public List<int> Nums { get; set; }")),
                d => d.Id == "E10");

        [Fact]
        public void ClassPropInUseProperties_ReportsE11Error()
        {
            var diagnostics = RunGenerator(OwnedStoreSource(
                ".UseProperties(x => new { x.Id, x.Name, x.Location }).UseSequentialIndex(x => x.Id)"));
            Assert.Contains(diagnostics, d => d.Id == "E11" && d.Severity == DiagnosticSeverity.Error);
        }

        [Fact]
        public void NestedOwnedType_ReportsE11Error()
        {
            var diagnostics = RunGenerator(OwnedStoreSource(
                ".UseAllProperties().UseOwned(x => x.Location).UseSequentialIndex(x => x.Id)",
                ownedMembers: "public string Street { get; set; } public Addr Inner { get; set; }"));
            Assert.Contains(diagnostics, d => d.Id == "E11" && d.Severity == DiagnosticSeverity.Error);
        }

        [Fact]
        public void UseOwnedOnCollection_ReportsE11Error()
        {
            var diagnostics = RunGenerator(OwnedStoreSource(
                ".UseAllProperties().UseOwned(x => x.Location).UseOwned(x => x.Many).UseSequentialIndex(x => x.Id)",
                extraProps: "public List<Addr> Many { get; set; }"));
            Assert.Contains(diagnostics, d => d.Id == "E11" && d.Severity == DiagnosticSeverity.Error);
        }

        /// <summary>Two-entity store with a UseUnion call under test.</summary>
        private static string UnionStoreSource(string unionCall, string chainA = null, string chainB = null, string extraTypes = "", string storeFields = "") => $$"""
            using Microsoft.EntityFrameworkCore;
            using System;
            using System.Collections.Generic;
            using Ef.Mems;

            namespace UnionDiagTest
            {
                public class ActiveTx { public int Id { get; set; } public decimal Amount { get; set; } public string Reference { get; set; } }
                public class ArchivedTx { public int Id { get; set; } public decimal Amount { get; set; } public string Reference { get; set; } }

                public class Ctx : DbContext
                {
                    public Ctx() { }
                    public DbSet<ActiveTx> ActiveTxs { get; set; }
                    public DbSet<ArchivedTx> ArchivedTxs { get; set; }
                }
            {{extraTypes}}
                public class UnionDiagStore : DataStore<Ctx>
                {
                    public override Ctx GetContext() => throw new NotImplementedException();
                    public ActiveTxEntityStore ActiveTxs;
                    public ArchivedTxEntityStore ArchivedTxs;
                    {{storeFields}}
                    protected override void ConstructStore(StoreBuilder<Ctx> builder)
                    {
                        ActiveTxs = builder.UseEntity(x => x.ActiveTxs){{chainA ?? ".UseAllProperties().UseSequentialIndex(x => x.Id, generateId: true)"}}.Build();
                        ArchivedTxs = builder.UseEntity(x => x.ArchivedTxs){{chainB ?? ".UseAllProperties().UseSequentialIndex(x => x.Id, generateId: true)"}}.Build();
                        {{unionCall}}
                    }
                }

                public partial class ActiveTxEntityStore : EntityStore<ActiveTx> { }
                public partial class ArchivedTxEntityStore : EntityStore<ArchivedTx> { }
            }
            """;

        [Fact]
        public void ValidUnion_ReportsNothing()
        {
            var diagnostics = RunGenerator(UnionStoreSource(
                """var all = builder.UseUnion("AllTx", ActiveTxs, ArchivedTxs).Build();"""));
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void InvalidUnionName_ReportsE12()
            => Assert.Contains(RunGenerator(UnionStoreSource(
                """var all = builder.UseUnion("not an identifier", ActiveTxs, ArchivedTxs).Build();""")),
                d => d.Id == "E12");

        [Fact]
        public void DuplicateUnionName_ReportsE12()
            => Assert.Contains(RunGenerator(UnionStoreSource(
                """
                var a = builder.UseUnion("AllTx", ActiveTxs, ArchivedTxs).Build();
                                var b = builder.UseUnion("AllTx", ActiveTxs, ArchivedTxs).Build();
                """)),
                d => d.Id == "E12");

        [Fact]
        public void DuplicateMemberSet_ReportsE12()
            => Assert.Contains(RunGenerator(UnionStoreSource(
                """
                var a = builder.UseUnion("AllTx", ActiveTxs, ArchivedTxs).Build();
                                var b = builder.UseUnion("OtherTx", ArchivedTxs, ActiveTxs).Build();
                """)),
                d => d.Id == "E12");

        [Fact]
        public void SameMemberTwice_ReportsE12()
            => Assert.Contains(RunGenerator(UnionStoreSource(
                """var all = builder.UseUnion("AllTx", ActiveTxs, ActiveTxs).Build();""")),
                d => d.Id == "E12");

        [Fact]
        public void UnconfiguredUnionMember_ReportsE12()
            => Assert.Contains(RunGenerator(UnionStoreSource(
                """var all = builder.UseUnion("AllTx", ActiveTxs, Orphans).Build();""",
                extraTypes: """
                    public class OrphanTx { public int Id { get; set; } }
                    public partial class OrphanTxEntityStore : EntityStore<OrphanTx> { }
                """,
                storeFields: "public OrphanTxEntityStore Orphans;")),
                d => d.Id == "E12");

        [Fact]
        public void EmptyUnionIntersection_ReportsE12()
            => Assert.Contains(RunGenerator(UnionStoreSource(
                """var all = builder.UseUnion("AllTx", ActiveTxs, ArchivedTxs).Build();""",
                chainA: ".UseProperties(x => new { x.Id }).UseSequentialIndex(x => x.Id)",
                chainB: ".UseProperties(x => new { x.Reference }).UseIndex(x => x.Reference)")),
                d => d.Id == "E12");

        [Fact]
        public void TupleIndex_ReportsE2()
            // Composite indexes use the anonymous-object syntax
            // (UseIndex(x => new { x.A, x.B })); ValueTuple syntax stays a
            // loud rejection rather than a silent miscompile.
            => AssertDiagnostic("E2",
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UseIndex(x => (x.Id, x.Number))");

        [Fact]
        public void CompositeIndexAsOnlyIndex_ReportsE5()
            // A composite index cannot serve as the update key; entities
            // without a sequential index need a single-property index too.
            => AssertDiagnostic("E5",
                ".UseAllProperties().UseIndex(x => new { x.Id, x.Number })");


        [Fact]
        public void DuplicateSortName_ReportsE10()
            => AssertDiagnostic("E10",
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UseSort(x => x.Number).UseSort(x => x.Number)");

        [Fact]
        public void SortOnUncachedProperty_ReportsE10()
            => AssertDiagnostic("E10",
                ".UseProperties(x => new { x.Id }).UseSequentialIndex(x => x.Id).UseSort(x => x.Number)");

        [Fact]
        public void InvalidSortName_ReportsE10()
            => AssertDiagnostic("E10",
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UseSort(\"not valid\", x => x.Number)");

        [Fact]
        public void ValidConfiguration_ReportsNothing()
        {
            var diagnostics = RunGenerator(StoreSource(
                ".UseAllProperties().UseSequentialIndex(x => x.Id).UsePredicate(\"Min\", (Thing x, int min) => x.Number >= min)"));
            Assert.Empty(diagnostics);
        }
    }
}
