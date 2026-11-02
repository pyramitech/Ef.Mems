using Ef.Mems.SourceGenerators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Ef.Mems.Generator.Tests
{
    /// <summary>
    /// Guards the generator's incremental caching: the pipeline models must
    /// stay value-equatable and symbol-free, otherwise every keystroke in the
    /// IDE re-renders and re-compiles the generated stores. If
    /// <see cref="CosmeticEdit_UsesCachedOutput"/> starts failing, someone has
    /// most likely added a field to StoreInfo/EntityInfo that does not
    /// compare by value (or reintroduced an ISymbol/Location into them).
    /// </summary>
    public class IncrementalCachingTests
    {
        private const string StorePath = "TestStore.cs";

        private const string StoreSource = """
            using Microsoft.EntityFrameworkCore;
            using System;
            using System.Collections.Generic;
            using Ef.Mems;

            namespace CacheTest
            {
                public class TestClass
                {
                    public int Id { get; set; }
                    public string TestName { get; set; }
                    public string IndexedName { get; set; }
                    public int Number { get; set; }
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
                    public List<UntestedTest> UntestedTests { get; set; }
                }

                public class UntestedTest
                {
                    public int TestClassId { get; set; }
                    public int UntestedClassId { get; set; }
                    public string UntestedTestString { get; set; }
                }

                public class TestContext : DbContext
                {
                    public TestContext() { }
                    public TestContext(DbContextOptions<TestContext> options) : base(options) { }
                    public DbSet<TestClass> TestClasses { get; set; }
                    public DbSet<TestSubClass> TestSubClasses { get; set; }
                    public DbSet<UntestedClass> UntestedClasses { get; set; }
                    public DbSet<UntestedTest> UntestedTests { get; set; }
                }

                public static class SearchExtension
                {
                    public static void BeforeCreate(object dataStore, TestClass test) { }
                }

                public class TestStore : DataStore<TestContext>
                {
                    public override TestContext GetContext() => throw new NotImplementedException();

                    public TestClassEntityStore TestClasses;
                    public UntestedClassEntityStore UntestedClasses;

                    protected override void ConstructStore(StoreBuilder<TestContext> builder)
                    {
                        TestClasses = builder.UseEntity(x => x.TestClasses)
                            .UseAllProperties()
                            .UseMany(x => x.TestSubClasses, x => x.Id)
                            .UseManyToMany(x => x.UntestedTests, x => new { x.TestClassId, x.UntestedClassId })
                            .UseSequentialIndex(x => x.Id, generateId: true)
                            .UseIndex(x => x.IndexedName, GenString)
                            .UseExtension(typeof(SearchExtension))
                            .UseFeatures(delete: true)
                            .UsePredicate("MinNumber", (TestClass x, int min) => x.Number >= min)
                            .Build();

                        UntestedClasses = builder.UseEntity(x => x.UntestedClasses)
                            .UseProperties(x => new { x.Id, x.TestName })
                            .UseManyToMany(x => x.UntestedTests, x => new { x.TestClassId, x.UntestedClassId })
                            .UseSequentialIndex(x => x.Id)
                            .Build();
                    }

                    public static string GenString() => string.Empty;
                }

                public partial class TestClassEntityStore : EntityStore<TestClass> { }
                public partial class UntestedClassEntityStore : EntityStore<UntestedClass> { }
            }
            """;

        // Metadata references from the test host's own dependency closure:
        // includes the runtime, Ef.Mems.dll, EF Core and their dependencies.
        private static readonly Lazy<List<MetadataReference>> References = new(() =>
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                .Split(Path.PathSeparator)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToList());

        private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

        private static SyntaxTree Parse(string source, string path)
            => CSharpSyntaxTree.ParseText(source, ParseOptions, path);

        private static CSharpCompilation CreateCompilation(string storeSource)
            => CSharpCompilation.Create("CacheTest",
                new[] { Parse(storeSource, StorePath) },
                References.Value,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        private static GeneratorDriver CreateDriver()
            => CSharpGeneratorDriver.Create(
                new[] { new IncrementalCacheGenerator().AsSourceGenerator() },
                driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        private static IncrementalStepRunReason[] OutputReasons(GeneratorDriver driver)
            => driver.GetRunResult().Results[0].TrackedOutputSteps
                .SelectMany(step => step.Value)
                .SelectMany(step => step.Outputs)
                .Select(output => output.Reason)
                .ToArray();

        private static void AssertAllCached(IncrementalStepRunReason[] reasons)
        {
            Assert.NotEmpty(reasons);
            Assert.All(reasons, reason =>
                Assert.True(reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged,
                    $"expected Cached/Unchanged but got {reason}"));
        }

        [Fact]
        public void ColdRun_GeneratesStoreWithoutDiagnostics()
        {
            var driver = CreateDriver().RunGenerators(CreateCompilation(StoreSource));
            var result = driver.GetRunResult();

            Assert.Empty(result.Diagnostics);
            Assert.Null(result.Results[0].Exception);
            var source = Assert.Single(result.Results[0].GeneratedSources);
            Assert.Equal("TestStore_Cache.g.cs", source.HintName);
            var text = source.SourceText.ToString();
            Assert.Contains("TestClassEntityStore", text);
            Assert.Contains("UntestedClassEntityStore", text);
            // UsePredicate emission: nested struct + named query methods.
            Assert.Contains("public readonly struct MinNumberPredicate : IPredicate<TestClassReadOnly>", text);
            Assert.Contains("public bool Invoke(TestClassReadOnly x) => x.Number >= min;", text);
            Assert.Contains("QueryMinNumber(int min, int skip, int take, bool descending = false)", text);
            Assert.Contains("QueryCountMinNumber(int min, int skip, int take, bool descending = false)", text);
            Assert.Contains("public async Task<int> Truncate()", text);
        }

        [Fact]
        public void AddingUnrelatedFile_UsesCachedOutput()
        {
            var compilation = CreateCompilation(StoreSource);
            var driver = CreateDriver().RunGenerators(compilation);

            var changed = compilation.AddSyntaxTrees(
                Parse("namespace Unrelated { class Nothing { } }", "Unrelated.cs"));
            driver = driver.RunGenerators(changed);

            AssertAllCached(OutputReasons(driver));
        }

        [Fact]
        public void CosmeticEdit_UsesCachedOutput()
        {
            var compilation = CreateCompilation(StoreSource);
            var driver = CreateDriver().RunGenerators(compilation);

            // The transform re-runs (its file changed) and produces a fresh
            // StoreParseResult instance; output must still be cached because
            // the parsed model compares equal by value.
            var changed = compilation.ReplaceSyntaxTree(
                compilation.SyntaxTrees.Single(),
                Parse(StoreSource + "\n// cosmetic comment\n", StorePath));
            driver = driver.RunGenerators(changed);

            AssertAllCached(OutputReasons(driver));
        }

        [Fact]
        public void HookSignatureChange_Regenerates()
        {
            var compilation = CreateCompilation(StoreSource);
            var driver = CreateDriver().RunGenerators(compilation);

            // Giving a hook the context parameter changes the generated write
            // signatures, so the parsed model must not compare equal.
            var changed = compilation.ReplaceSyntaxTree(
                compilation.SyntaxTrees.Single(),
                Parse(StoreSource.Replace(
                    "BeforeCreate(object dataStore, TestClass test) { }",
                    "BeforeCreate(object dataStore, TestClass test, object context) { }"), StorePath));
            driver = driver.RunGenerators(changed);

            var reasons = OutputReasons(driver);
            Assert.Contains(reasons, reason =>
                reason is IncrementalStepRunReason.Modified or IncrementalStepRunReason.New);

            var source = driver.GetRunResult().Results[0].GeneratedSources.Single().SourceText.ToString();
            Assert.Contains("Create(CacheTest.TestClass item, object context = null)", source);
            Assert.Contains("SearchExtension.BeforeCreate(_store, item, context);", source);
            // No context hook on UntestedClass: its signatures stay unchanged.
            Assert.Contains("Create(CacheTest.UntestedClass item)", source);
        }

        private const string OwnedStoreSource = """
            using Microsoft.EntityFrameworkCore;
            using System;
            using System.Collections.Generic;
            using Ef.Mems;

            namespace OwnedCacheTest
            {
                public class Address
                {
                    public string Street { get; set; }
                    public string City { get; set; }
                }

                public class Company
                {
                    public int Id { get; set; }
                    public string Name { get; set; }
                    public Address RegistrationAddress { get; set; }
                }

                public class Ctx : DbContext
                {
                    public Ctx() { }
                    public DbSet<Company> Companies { get; set; }
                }

                public class OwnedStore : DataStore<Ctx>
                {
                    public override Ctx GetContext() => throw new NotImplementedException();
                    public CompanyEntityStore Companies;
                    protected override void ConstructStore(StoreBuilder<Ctx> builder)
                    {
                        Companies = builder.UseEntity(x => x.Companies)
                            .UseAllProperties()
                            .UseOwned(x => x.RegistrationAddress)
                            .UseSequentialIndex(x => x.Id, generateId: true)
                            .Build();
                    }
                }

                public partial class CompanyEntityStore : EntityStore<Company> { }
            }
            """;

        [Fact]
        public void OwnedMemberChange_Regenerates()
        {
            var compilation = CreateCompilation(OwnedStoreSource);
            var driver = CreateDriver().RunGenerators(compilation);

            var cold = driver.GetRunResult().Results[0].GeneratedSources.Single().SourceText.ToString();
            Assert.Contains("public sealed class AddressReadOnly", cold);
            Assert.Contains("public string City => _o.City;", cold);

            // Renaming a member of the owned type changes the wrapper, clones
            // and diffs, so the parsed model must not compare equal.
            var changed = compilation.ReplaceSyntaxTree(
                compilation.SyntaxTrees.Single(),
                Parse(OwnedStoreSource.Replace("public string City", "public string Town"), StorePath));
            driver = driver.RunGenerators(changed);

            var reasons = OutputReasons(driver);
            Assert.Contains(reasons, reason =>
                reason is IncrementalStepRunReason.Modified or IncrementalStepRunReason.New);

            var source = driver.GetRunResult().Results[0].GeneratedSources.Single().SourceText.ToString();
            Assert.Contains("public string Town => _o.Town;", source);
        }

        private const string UnionStoreSource = """
            using Microsoft.EntityFrameworkCore;
            using System;
            using System.Collections.Generic;
            using Ef.Mems;

            namespace UnionCacheTest
            {
                public class ActiveTx { public int Id { get; set; } public decimal Amount { get; set; } public string Reference { get; set; } }
                public class ArchivedTx { public int Id { get; set; } public decimal Amount { get; set; } public string Reference { get; set; } }

                public class Ctx : DbContext
                {
                    public Ctx() { }
                    public DbSet<ActiveTx> ActiveTxs { get; set; }
                    public DbSet<ArchivedTx> ArchivedTxs { get; set; }
                }

                public class UnionStore : DataStore<Ctx>
                {
                    public override Ctx GetContext() => throw new NotImplementedException();
                    public ActiveTxEntityStore ActiveTxs;
                    public ArchivedTxEntityStore ArchivedTxs;
                    protected override void ConstructStore(StoreBuilder<Ctx> builder)
                    {
                        ActiveTxs = builder.UseEntity(x => x.ActiveTxs).UseAllProperties().UseSequentialIndex(x => x.Id, generateId: true).Build();
                        ArchivedTxs = builder.UseEntity(x => x.ArchivedTxs).UseAllProperties().UseSequentialIndex(x => x.Id, generateId: true).Build();
                        var all = builder.UseUnion("AllTx", ActiveTxs, ArchivedTxs).Build();
                    }
                }

                public partial class ActiveTxEntityStore : EntityStore<ActiveTx> { }
                public partial class ArchivedTxEntityStore : EntityStore<ArchivedTx> { }
            }
            """;

        [Fact]
        public void UnionMemberPropRename_RegeneratesAndShrinksInterface()
        {
            var compilation = CreateCompilation(UnionStoreSource);
            var driver = CreateDriver().RunGenerators(compilation);

            var cold = driver.GetRunResult().Results[0].GeneratedSources.Single().SourceText.ToString();
            Assert.Contains("public interface IAllTxReadOnly", cold);
            Assert.Contains("decimal Amount { get; }", cold);
            Assert.Contains("public sealed class AllTxUnion", cold);

            // Renaming Amount in ONE member drops it from the intersection,
            // so the union interface must shrink - the model cannot compare
            // equal to the cached one.
            var changed = compilation.ReplaceSyntaxTree(
                compilation.SyntaxTrees.Single(),
                Parse(UnionStoreSource.Replace(
                    "public class ActiveTx { public int Id { get; set; } public decimal Amount { get; set; }",
                    "public class ActiveTx { public int Id { get; set; } public decimal Total { get; set; }"), StorePath));
            driver = driver.RunGenerators(changed);

            var reasons = OutputReasons(driver);
            Assert.Contains(reasons, reason =>
                reason is IncrementalStepRunReason.Modified or IncrementalStepRunReason.New);

            var source = driver.GetRunResult().Results[0].GeneratedSources.Single().SourceText.ToString();
            Assert.Contains("public interface IAllTxReadOnly", source);
            Assert.DoesNotContain("decimal Amount { get; }", source);
        }

        [Fact]
        public void NotMappedAdded_RegeneratesWithoutProperty()
        {
            var compilation = CreateCompilation(StoreSource);
            var driver = CreateDriver().RunGenerators(compilation);

            var cold = driver.GetRunResult().Results[0].GeneratedSources.Single().SourceText.ToString();
            Assert.Contains("public int Number => _entity.Number;", cold);

            // Marking a scalar [NotMapped] removes it from the cached model
            // (the predicate over it is dropped in the same edit so the
            // remaining configuration stays coherent).
            var changed = compilation.ReplaceSyntaxTree(
                compilation.SyntaxTrees.Single(),
                Parse(StoreSource
                    .Replace("public int Number { get; set; }",
                        "[System.ComponentModel.DataAnnotations.Schema.NotMapped] public int Number { get; set; }")
                    .Replace(""".UsePredicate("MinNumber", (TestClass x, int min) => x.Number >= min)""", ""), StorePath));
            driver = driver.RunGenerators(changed);

            var reasons = OutputReasons(driver);
            Assert.Contains(reasons, reason =>
                reason is IncrementalStepRunReason.Modified or IncrementalStepRunReason.New);

            var source = driver.GetRunResult().Results[0].GeneratedSources.Single().SourceText.ToString();
            Assert.DoesNotContain("public int Number => _entity.Number;", source);
        }

        [Fact]
        public void ModelChange_Regenerates()
        {
            var compilation = CreateCompilation(StoreSource);
            var driver = CreateDriver().RunGenerators(compilation);

            var changed = compilation.ReplaceSyntaxTree(
                compilation.SyntaxTrees.Single(),
                Parse(StoreSource.Replace(".UseFeatures(delete: true)", ".UseFeatures(delete: false)"), StorePath));
            driver = driver.RunGenerators(changed);

            var reasons = OutputReasons(driver);
            Assert.Contains(reasons, reason =>
                reason is IncrementalStepRunReason.Modified or IncrementalStepRunReason.New);

            var source = driver.GetRunResult().Results[0].GeneratedSources.Single().SourceText.ToString();
            Assert.DoesNotContain("Delete(CacheTest.TestClass item)", source);
            Assert.Contains("Delete(CacheTest.UntestedClass item)", source);
            // Truncate follows the delete feature flag.
            Assert.DoesNotContain("DeleteAllAndReseed<CacheTest.TestClass>", source);
            Assert.Contains("DeleteAllAndReseed<CacheTest.UntestedClass>", source);
        }
    }
}
