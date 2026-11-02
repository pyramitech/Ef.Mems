// Ef.Mems.SourceGenerators/IncrementalCacheGenerator.cs
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Ef.Mems.SourceGenerators
{
    public enum ErrorCodes
    {
        E1,
        E2,
        E3,
        E4,
        E5,
        E6,
        E7,
        E8,
        E9,
        E10,
        E11,
        E12
    }

    [Generator]
    public class IncrementalCacheGenerator : IIncrementalGenerator
    {
        public const string ByteArrayType = "byte[]";
        public const string CharArrayType = "char[]";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
#if DEBUG
            // Debug-only hook; reading ambient state is banned for analyzers
            // (RS1035) because it breaks determinism, but that is irrelevant
            // for opting into a debugger prompt.
#pragma warning disable RS1035
            if (Environment.GetEnvironmentVariable("EFMEMS_GENERATOR_DEBUG") == "1" && !Debugger.IsAttached)
                Debugger.Launch();
#pragma warning restore RS1035
#endif
            // One pipeline item per store class. The transform reduces
            // everything to value-equatable, symbol-free data, so the
            // incremental driver can skip regeneration whenever an edit does
            // not change the parsed store model, and each store caches
            // independently of the others.
            var stores = context.SyntaxProvider
                .CreateSyntaxProvider<StoreParseResult>(
                    predicate: static (node, _) =>
                        node is ClassDeclarationSyntax cds &&
                        cds.BaseList?.Types.Any(static bt => bt.Type is GenericNameSyntax g && g.Identifier.ValueText == "DataStore") == true,
                    transform: static (ctx, _) => {
                        var result = new StoreParseResult();
                        try
                        {
                            result.Store = GetStoreInfo(ctx, result.Errors, result.Warnings);
                        }
                        catch (Exception ex)
                        {
                            result.Exception = new DiagInfo(ErrorCodes.E1.ToString(), "Unexpected exception", ex.ToString(), LocationData.From(ctx.Node.GetLocation()));
                        }
                        return result;
                    })
                .Where(result => result.Store != null || result.Errors.Count != 0 || result.Warnings.Count != 0 || result.Exception != null)
                .WithTrackingName("EfMems.Stores");

            context.RegisterSourceOutput(stores, static (spc, result) =>
            {
                try
                {
                    if (result.Exception != null)
                    {
                        spc.ReportError(result.Exception.Id, result.Exception.Title, result.Exception.Message, result.Exception.Location.ToLocation());
                        return;
                    }

                    foreach (var error in result.Errors)
                    {
                        spc.ReportError(error.Id, error.Title, error.Message, error.Location.ToLocation());
                    }

                    // Warnings (E11 guard) never block generation.
                    foreach (var warning in result.Warnings)
                    {
                        spc.ReportWarning(warning.Id, warning.Title, warning.Message, warning.Location.ToLocation());
                    }

                    if (result.Store != null && result.Errors.Count == 0)
                    {
                        GenerateForStore(spc, result.Store);
                    }
                }
                catch (Exception ex)
                {
                    spc.ReportError(ErrorCodes.E1.ToString(), "Unexpected exception", ex.ToString());
                }
            });
        }

        private static StoreInfo? GetStoreInfo(GeneratorSyntaxContext ctx, List<DiagInfo> errors, List<DiagInfo> warnings)
        {
            if (ctx.Node is not ClassDeclarationSyntax cds)
            {
                errors.Add(new DiagInfo(ErrorCodes.E6.ToString(), "Unexpected definition", "Expected class symbol, but type was: " + ctx.Node.GetType().FullName, LocationData.From(ctx.Node.GetLocation())));
                return null;
            }
            var model = ctx.SemanticModel;
            var symbol = model.GetDeclaredSymbol(cds) as INamedTypeSymbol;
            if (symbol == null) {
                errors.Add(new DiagInfo(ErrorCodes.E6.ToString(), "Unexpected definition", "Could you parse symbol " + cds.GetType().FullName, LocationData.From(cds.GetLocation())));
                return null;
            }
            var baseType = symbol.BaseType;
            if (baseType == null || !baseType.OriginalDefinition.ToDisplayString().StartsWith("Ef.Mems.DataStore<"))
            {
                errors.Add(new DiagInfo(ErrorCodes.E6.ToString(), "Unexpected definition", "Base type should be Ef.Mems.DataStore, but it was " + baseType?.OriginalDefinition.ToDisplayString(), LocationData.From(symbol.GetLocation())));
                return null;
            }
            var contextType = baseType.TypeArguments[0];

            var construct = cds.Members.OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == "ConstructStore");
            if (construct?.Body == null)
            {
                errors.Add(new DiagInfo(ErrorCodes.E6.ToString(), "Unexpected definition", "Missing function \"ConstructStore\"", LocationData.From(symbol.GetLocation())));
                return null;
            }

            var info = new StoreInfo(
                symbol.ContainingNamespace.IsGlobalNamespace ? "" : symbol.ContainingNamespace.ToDisplayString(),
                symbol.Name,
                symbol.ToDisplayString(),
                contextType.ToDisplayString());

            // Store-wide safety defaults: builder.UseSafety(...) calls bound
            // to StoreBuilder (entity-chain UseSafety binds to
            // EntityStoreBuilder and is handled in ParseEntityChain). Applied
            // store-wide regardless of position in ConstructStore.
            bool defCopyOnInsert = true, defCopyOnStage = true, defSeqIndexGuard = true;
            foreach (var invocation in construct.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is MemberAccessExpressionSyntax sma && sma.Name.Identifier.Text == "UseSafety" &&
                    model.GetSymbolInfo(invocation.Expression).Symbol is IMethodSymbol useSafetySym &&
                    useSafetySym.ContainingType.Name == "StoreBuilder")
                {
                    ApplySafetyArgs(GetArgumentList(invocation, model), model,
                        ref defCopyOnInsert, ref defCopyOnStage, ref defSeqIndexGuard);
                }
            }

            foreach (var invocation in construct.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text == "UseEntity")
                {
                    var ei = ParseEntityChain(model, invocation, errors, warnings,
                        defCopyOnInsert, defCopyOnStage, defSeqIndexGuard);
                    if (ei != null)
                        info.Entities.Add(ei);
                }
            }
            if (!info.Entities.Any()) return null;

            // UseUnion declarations (StoreBuilder-bound, after the entities
            // are parsed so membership can be validated). Member entity types
            // come from the bound method's inferred type arguments.
            var unionNames = new HashSet<string>(StringComparer.Ordinal);
            var unionSets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var invocation in construct.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax uma || uma.Name.Identifier.Text != "UseUnion" ||
                    model.GetSymbolInfo(invocation.Expression).Symbol is not IMethodSymbol unionSym ||
                    unionSym.ContainingType.Name != "StoreBuilder")
                    continue;

                var uloc = LocationData.From(invocation.GetLocation());
                var uargs = GetArgumentList(invocation, model);

                string uname = null;
                if (uargs.TryGetValue("name", out var unameArg))
                {
                    var nameConst = model.GetConstantValue(unameArg.Expression);
                    if (nameConst.HasValue) uname = nameConst.Value as string;
                }
                if (uname == null || !SyntaxFacts.IsValidIdentifier(uname))
                {
                    errors.Add(new DiagInfo(ErrorCodes.E12.ToString(), "Invalid union",
                        "Union name must be a constant string that is a valid C# identifier", uloc));
                    continue;
                }
                if (!unionNames.Add(uname))
                {
                    errors.Add(new DiagInfo(ErrorCodes.E12.ToString(), "Invalid union",
                        $"Union name {uname} is declared more than once", uloc));
                    continue;
                }

                var memberTypes = unionSym.TypeArguments.Select(t => t.ToDisplayString()).ToList();
                if (memberTypes.Distinct().Count() != memberTypes.Count)
                {
                    errors.Add(new DiagInfo(ErrorCodes.E12.ToString(), "Invalid union",
                        $"Union {uname} lists the same member entity type more than once", uloc));
                    continue;
                }
                var missing = memberTypes.FirstOrDefault(t => info.Entities.All(e => e.EntityType != t));
                if (missing != null)
                {
                    errors.Add(new DiagInfo(ErrorCodes.E12.ToString(), "Invalid union",
                        $"Union {uname} member entity {missing} is not configured with UseEntity in this store", uloc));
                    continue;
                }
                // Build() dispatches on the member type tuple, so two unions
                // over the same set would be ambiguous.
                if (!unionSets.Add(string.Join("|", memberTypes.OrderBy(x => x, StringComparer.Ordinal))))
                {
                    errors.Add(new DiagInfo(ErrorCodes.E12.ToString(), "Invalid union",
                        $"Union {uname} spans the same member entities as another union; Build() dispatch would be ambiguous", uloc));
                    continue;
                }

                info.Unions.Add((uname, new EquatableArray<string>(memberTypes), uloc));
            }

            //get property names
            var props = symbol
                .GetMembers()
                .OfType<IPropertySymbol>()
                //only the ones declared in this class, not inherited
                .Where(p => SymbolEqualityComparer.Default.Equals(p.ContainingType, symbol) && p.Type.Name.EndsWith("EntityStore"));

            foreach (var p in props)
            {
                // p.Name is the property name
                // p.Type is an ITypeSymbol, so e.g. p.Type.ToDisplayString() is the source code
                var propTypeName = p.Type.ToDisplayString();
                var propName = p.Name;

                info.Stores.Add(new EntityStoreInfo() { PropertyName = propName, ClassName = propTypeName });
            }

            //get property names
            var fields = symbol
                .GetMembers()
                .OfType<IFieldSymbol>()
                //only the ones declared in this class, not inherited
                .Where(p => SymbolEqualityComparer.Default.Equals(p.ContainingType, symbol) && p.Type.Name.EndsWith("EntityStore"));

            foreach (var p in fields)
            {
                var typeargs = p?.Type?.OriginalDefinition?.BaseType?.TypeArguments;

                if (typeargs == null || typeargs.Value.Length == 0)
                    continue;

                // p.Name is the property name
                // p.Type is an ITypeSymbol, so e.g. p.Type.ToDisplayString() is the source code
                var propTypeName = p.Type.ToDisplayString();
                var baseEntityType = p.Type.OriginalDefinition.BaseType.TypeArguments.First().ToDisplayString();
                var propName = p.Name;

                info.Stores.Add(new EntityStoreInfo() { PropertyName = propName, EntityName = baseEntityType, ClassName = propTypeName });
            }


            if (!info.Stores.Any()) return null;


            return info;
        }

        private static void ApplySafetyArgs(Dictionary<string, ArgumentSyntax> args, SemanticModel model,
            ref bool copyOnInsert, ref bool copyOnStage, ref bool sequentialIndexGuard)
        {
            foreach (var arg in args)
            {
                var valueOpt = model.GetConstantValue(arg.Value.Expression);
                if (!valueOpt.HasValue || valueOpt.Value is not bool b) continue;
                switch (arg.Key)
                {
                    case "copyOnInsert":
                        copyOnInsert = b;
                        break;
                    case "copyOnStage":
                        copyOnStage = b;
                        break;
                    case "sequentialIndexGuard":
                        sequentialIndexGuard = b;
                        break;
                }
            }
        }

        private static EntityInfo? ParseEntityChain(SemanticModel model, InvocationExpressionSyntax invocation,
            List<DiagInfo> errors, List<DiagInfo> warnings,
            bool defCopyOnInsert = true, bool defCopyOnStage = true, bool defSeqIndexGuard = true)
        {
            //build the full chain of invocations starting from the current UseEntity call and walking up the tree.
            var chain = invocation
                .AncestorsAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .ToList();

            //first element in the list has to be the UseEntity<T>() call we started from.
            var root = chain.FirstOrDefault(inv =>
                inv.Expression is MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text == "UseEntity");
            if (root == null)
                return null;

            //extract the entity type generic argument from root
            if (model.GetSymbolInfo(root.Expression).Symbol is not IMethodSymbol methodSym ||
                methodSym.TypeArguments.Length < 1)
                return null;
            var entityType = methodSym.TypeArguments[0].ToDisplayString();

            //extract the DbSet property from the first argument of root
            if (root.ArgumentList.Arguments.FirstOrDefault()?.Expression is not LambdaExpressionSyntax dsLam ||
                dsLam.Body is not MemberAccessExpressionSyntax dsMa)
                return null;
            var dbSetProp = dsMa.Name.Identifier.Text;

            var ei = new EntityInfo(entityType, dbSetProp)
            {
                Location = LocationData.From(root.GetLocation()),
                CopyOnInsert = defCopyOnInsert,
                CopyOnStage = defCopyOnStage,
                SeqIndexGuard = defSeqIndexGuard,
            };

            //traverse each invocation in the chain after root
            var startIndex = chain.IndexOf(root) + 1;
            for (int i = startIndex; i < chain.Count; i++)
            {
                var inv = chain[i];
                if (inv.Expression is not MemberAccessExpressionSyntax ma2)
                    continue;
                var method = ma2.Name.Identifier.Text;

                var args = GetArgumentList(inv, model);
                switch (method)
                {
                    case "UseProperty":
                        if (args.Count == 1 && args["property"].Expression is LambdaExpressionSyntax lamP &&
                            lamP.Body is MemberAccessExpressionSyntax pma &&
                            model.GetSymbolInfo(pma).Symbol is IPropertySymbol ps)
                        {
                            if (HasNotMapped(ps))
                                warnings.Add(new DiagInfo(ErrorCodes.E11.ToString(), "NotMapped property cached",
                                    $"Property {ps.Name} is [NotMapped]: caching it works for reads, but Update throws at runtime when its value changes (EF has no such property to mark modified)", LocationData.From(ma2.GetLocation())));
                            ei.Properties.Add((ps.Name, ps.Type.ToDisplayString(), LocationData.From(ps.GetLocation())));
                        }
                        else
                        {
                            errors.Add(new DiagInfo(ErrorCodes.E2.ToString(), "Incorrect structure", "Unexpected structure of " + method, LocationData.From(ma2.GetLocation())));
                        }
                        break;
                    case "UseProperties":
                        if (args.Count == 1 && args["propertySelectors"].Expression is LambdaExpressionSyntax lamPs &&
                            lamPs.Body is AnonymousObjectCreationExpressionSyntax anon)
                        {
                            foreach (var init in anon.Initializers)
                                if (init.Expression is MemberAccessExpressionSyntax pma2 &&
                                    model.GetSymbolInfo(pma2).Symbol is IPropertySymbol pi)
                                {
                                    if (HasNotMapped(pi))
                                        warnings.Add(new DiagInfo(ErrorCodes.E11.ToString(), "NotMapped property cached",
                                            $"Property {pi.Name} is [NotMapped]: caching it works for reads, but Update throws at runtime when its value changes (EF has no such property to mark modified)", LocationData.From(ma2.GetLocation())));
                                    ei.Properties.Add((pi.Name, pi.Type.ToDisplayString(), LocationData.From(pi.GetLocation())));
                                }
                        }
                        else
                        {
                            errors.Add(new DiagInfo(ErrorCodes.E2.ToString(), "Incorrect structure", "Unexpected structure of " + method, LocationData.From(ma2.GetLocation())));
                        }
                        break;
                    case "UseAllProperties":
                        if (args.Count == 0)
                        {
                            var props = GetSimpleProperties(methodSym.TypeArguments[0], includeScalarCollections: true);

                            foreach (var prop in props)
                            {
                                ei.Properties.Add((prop.Name, prop.Type.ToDisplayString(), LocationData.From(prop.GetLocation())));
                            }
                        }
                        else
                        {
                            errors.Add(new DiagInfo(ErrorCodes.E2.ToString(), "Incorrect structure", "Unexpected structure of " + method, LocationData.From(ma2.GetLocation())));
                        }
                        break;
                    case "UseIndex":
                        if (args.Count != 0 && args["index"].Expression is LambdaExpressionSyntax lamI &&
                            lamI.Body is MemberAccessExpressionSyntax ima &&
                            model.GetSymbolInfo(ima).Symbol is IPropertySymbol isym)
                        {
                            var singleProps = new EquatableArray<(string Name, string Type)>(
                                new List<(string, string)> { (isym.Name, isym.Type.ToDisplayString()) });
                            if (args.Count == 1)
                            {
                                ei.Indexes.Add((isym.Name, isym.Type.ToDisplayString(), null, false, singleProps, LocationData.From(isym.GetLocation())));
                            }
                            else if (args.Count == 2)
                            {
                                var symInfo = model.GetSymbolInfo(args["idGenerator"].Expression);
                                if (symInfo.Symbol is IMethodSymbol metSym)
                                {
                                    var fqName = metSym.ToDisplayString();
                                    int paramCount = metSym.Parameters.Length;

                                    if (paramCount != 0)
                                    {
                                        errors.Add(new DiagInfo(ErrorCodes.E3.ToString(), "Incorrect argument type", "Unexpected argument type of " + method, LocationData.From(metSym.Locations.Length != 0 ? metSym.Locations[0] : null)));
                                    }

                                    ei.Indexes.Add((isym.Name, isym.Type.ToDisplayString(), fqName, true, singleProps, LocationData.From(isym.GetLocation())));
                                }
                                else
                                {
                                    errors.Add(new DiagInfo(ErrorCodes.E3.ToString(), "Incorrect argument type", "Unexpected argument type of " + method, LocationData.From(ma2.GetLocation())));
                                }
                            }
                            else
                            {
                                errors.Add(new DiagInfo(ErrorCodes.E2.ToString(), "Incorrect structure", "Unexpected structure of " + method, LocationData.From(ma2.GetLocation())));
                            }
                        }
                        else if (args.Count != 0 && args["index"].Expression is LambdaExpressionSyntax lamIc &&
                            lamIc.Body is AnonymousObjectCreationExpressionSyntax anonIdx)
                        {
                            // Composite index: UseIndex(x => new { x.A, x.B })
                            // becomes a value-tuple-keyed dictionary. No id
                            // generator overload for composites.
                            if (args.Count != 1)
                            {
                                errors.Add(new DiagInfo(ErrorCodes.E3.ToString(), "Incorrect argument type", "Composite indexes do not support an idGenerator", LocationData.From(ma2.GetLocation())));
                                break;
                            }

                            var members = new List<(string Name, string Type)>();
                            foreach (var init in anonIdx.Initializers)
                                if (init.Expression is MemberAccessExpressionSyntax pmaI &&
                                    model.GetSymbolInfo(pmaI).Symbol is IPropertySymbol piI)
                                {
                                    members.Add((piI.Name, piI.Type.ToDisplayString()));
                                }

                            if (members.Count < 2)
                            {
                                errors.Add(new DiagInfo(ErrorCodes.E2.ToString(), "Incorrect structure", "Composite index must select at least two properties", LocationData.From(ma2.GetLocation())));
                                break;
                            }

                            var idxName = string.Concat(members.Select(m => m.Name));
                            var idxType = "(" + string.Join(", ", members.Select(m => m.Type)) + ")";
                            ei.Indexes.Add((idxName, idxType, null, false,
                                new EquatableArray<(string Name, string Type)>(members), LocationData.From(ma2.GetLocation())));
                        }
                        else
                        {
                            errors.Add(new DiagInfo(ErrorCodes.E2.ToString(), "Incorrect structure", "Unexpected structure of " + method, LocationData.From(ma2.GetLocation())));
                        }
                        break;
                    case "UseSequentialIndex":
                        if (args.Count != 0 && args["index"].Expression is LambdaExpressionSyntax lamS &&
                            lamS.Body is MemberAccessExpressionSyntax sma &&
                            model.GetSymbolInfo(sma).Symbol is IPropertySymbol ssym)
                        {
                            var genId = args.ContainsKey("generateId") ? args["generateId"].Expression.ToString() == "true" : false;
                            ei.SeqIndexes.Add((ssym.Name, ssym.Type.ToDisplayString(), false, genId, LocationData.From(ssym.GetLocation())));
                        }
                        else
                        {
                            errors.Add(new DiagInfo(ErrorCodes.E2.ToString(), "Incorrect structure", "Unexpected structure of " + method, LocationData.From(ma2.GetLocation())));
                        }
                        break;
                    case "UseMany":
                        {
                            if (args.Count == 2 &&
                                TryExtractProperty(model, args["property"].Expression, out var navsym) &&
                                navsym.Type is INamedTypeSymbol col2 && col2.TypeArguments.Length == 1
                                && args["primaryKey"].Expression is LambdaExpressionSyntax lam2)
                            {
                                if (lam2.Body is MemberAccessExpressionSyntax ima2 &&
                                model.GetSymbolInfo(ima2).Symbol is IPropertySymbol isym2)
                                {
                                    var ct = col2.TypeArguments[0].ToDisplayString();
                                    var childProps = new List<(string, string)>();

                                    var underlyingType = col2.TypeArguments[0] as INamedTypeSymbol;
                                    var properties = GetSimpleProperties(underlyingType).ToList();

                                    foreach (var prop in properties)
                                    {
                                        childProps.Add((prop.Name, prop.Type.ToDisplayString()));
                                    }

                                    ei.ManyNavs.Add((navsym.Name, ct,
                                        new EquatableArray<(string Name, string Type)>(childProps),
                                        new EquatableArray<string>(new[] { isym2.Name }),
                                        LocationData.From(navsym.GetLocation())));
                                }
                                else if (lam2.Body is AnonymousObjectCreationExpressionSyntax anonObj)
                                {
                                    var propKeys = new List<string>();
                                    foreach (var init in anonObj.Initializers)
                                        if (init.Expression is MemberAccessExpressionSyntax pma2 &&
                                            model.GetSymbolInfo(pma2).Symbol is IPropertySymbol pi)
                                        {
                                            propKeys.Add(pi.Name);
                                        }

                                    var ct = col2.TypeArguments[0].ToDisplayString();
                                    var childProps = new List<(string, string)>();

                                    var underlyingType = col2.TypeArguments[0] as INamedTypeSymbol;
                                    var properties = GetSimpleProperties(underlyingType).ToList();

                                    foreach (var prop in properties)
                                    {
                                        // prop.Name, prop.Type, etc.
                                        childProps.Add((prop.Name, prop.Type.ToDisplayString()));
                                    }

                                    ei.ManyNavs.Add((navsym.Name, ct,
                                        new EquatableArray<(string Name, string Type)>(childProps),
                                        new EquatableArray<string>(propKeys),
                                        LocationData.From(navsym.GetLocation())));
                                }
                                else
                                {
                                    errors.Add(new DiagInfo(ErrorCodes.E2.ToString(), "Incorrect structure", "Unexpected structure of " + method, LocationData.From(ma2.GetLocation())));
                                }
                            }
                            else
                            {
                                errors.Add(new DiagInfo(ErrorCodes.E2.ToString(), "Incorrect structure", "Unexpected structure of " + method, LocationData.From(ma2.GetLocation())));
                            }
                        }
                        break;

                    case "UseManyToMany":
                        {
                            if (args.Count == 2 && TryExtractProperty(model, args["joinListNav"].Expression, out var navsym2) &&
                                navsym2.Type is INamedTypeSymbol col3 && col3.TypeArguments.Length == 1 &&
                                args["joinKeyProps"].Expression is LambdaExpressionSyntax keyLam &&
                                keyLam.Body is AnonymousObjectCreationExpressionSyntax anonObj)
                            {
                                var underlyingType = col3.TypeArguments[0] as INamedTypeSymbol;
                                var propKeys = new List<string>();
                                foreach (var init in anonObj.Initializers)
                                    if (init.Expression is MemberAccessExpressionSyntax pma2 &&
                                        model.GetSymbolInfo(pma2).Symbol is IPropertySymbol pi)
                                    {
                                        propKeys.Add(pi.Name);
                                    }

                                var childProps = new List<(string, string)>();
                                var properties = GetSimpleProperties(underlyingType).ToList();

                                foreach (var prop in properties)
                                {
                                    if (!propKeys.Contains(prop.Name))
                                    {
                                        childProps.Add((prop.Name, prop.Type.ToDisplayString()));
                                    }
                                }

                                // Reduce the join symbol to data here: symbols
                                // must not flow through the incremental pipeline.
                                var allProps = GetSimpleProperties(underlyingType)
                                    .Select(p => (p.Name, p.Type.ToDisplayString()))
                                    .ToList();

                                ei.ManyToManys.Add((underlyingType.ToDisplayString(), navsym2.Name,
                                    new EquatableArray<(string Name, string Type)>(childProps),
                                    new EquatableArray<string>(propKeys),
                                    new EquatableArray<(string Name, string Type)>(allProps),
                                    LocationData.From(underlyingType.GetLocation())));
                            }
                            else
                            {
                                errors.Add(new DiagInfo(ErrorCodes.E2.ToString(), "Incorrect structure", "Unexpected structure of " + method, LocationData.From(ma2.GetLocation())));
                            }
                        }
                        break;

                    case "UseOwned":
                        if (args.Count == 1 && args["property"].Expression is LambdaExpressionSyntax lamO &&
                            lamO.Body is MemberAccessExpressionSyntax oma &&
                            model.GetSymbolInfo(oma).Symbol is IPropertySymbol osym)
                        {
                            var ownedType = osym.Type as INamedTypeSymbol;
                            var isCollection = ownedType != null &&
                                (ownedType.SpecialType == SpecialType.System_Collections_IEnumerable ||
                                 ownedType.AllInterfaces.Any(i => i.SpecialType == SpecialType.System_Collections_IEnumerable));
                            if (ownedType == null || ownedType.TypeKind != TypeKind.Class || IsEfScalar(ownedType) || isCollection)
                            {
                                errors.Add(new DiagInfo(ErrorCodes.E11.ToString(), "Invalid owned type",
                                    $"UseOwned({osym.Name}) requires a non-collection class-typed property (for collections of owned entities use UseMany)", LocationData.From(ma2.GetLocation())));
                                break;
                            }
                            if (ei.Owneds.Any(o => o.Name == osym.Name))
                            {
                                errors.Add(new DiagInfo(ErrorCodes.E11.ToString(), "Invalid owned type",
                                    $"UseOwned({osym.Name}) is declared more than once", LocationData.From(ma2.GetLocation())));
                                break;
                            }

                            // v1: owned members must all be EF scalars; nested
                            // owned types are rejected loudly.
                            var badMember = ownedType.GetMembers().OfType<IPropertySymbol>()
                                .FirstOrDefault(p => !p.IsStatic && p.Parameters.Length == 0
                                    && p.GetMethod != null && p.SetMethod != null
                                    && p.DeclaredAccessibility == Accessibility.Public
                                    && !IsEfScalar(p.Type)
                                    && !p.GetAttributes().Any(a => a.AttributeClass?.Name == "NotMappedAttribute"));
                            if (badMember != null)
                            {
                                errors.Add(new DiagInfo(ErrorCodes.E11.ToString(), "Invalid owned type",
                                    $"Owned type {ownedType.Name} member {badMember.Name} is not an EF scalar; nested owned types are not supported", LocationData.From(ma2.GetLocation())));
                                break;
                            }

                            var ownedMembers = GetSimpleProperties(ownedType)
                                .Select(p => (p.Name, p.Type.ToDisplayString()))
                                .ToList();
                            if (ownedMembers.Count == 0)
                            {
                                errors.Add(new DiagInfo(ErrorCodes.E11.ToString(), "Invalid owned type",
                                    $"Owned type {ownedType.Name} has no scalar properties", LocationData.From(ma2.GetLocation())));
                                break;
                            }

                            ei.Owneds.Add((osym.Name, ownedType.ToDisplayString(), ownedType.Name,
                                new EquatableArray<(string Name, string Type)>(ownedMembers), LocationData.From(osym.GetLocation())));
                        }
                        else
                        {
                            errors.Add(new DiagInfo(ErrorCodes.E2.ToString(), "Incorrect structure", "Unexpected structure of " + method, LocationData.From(ma2.GetLocation())));
                        }
                        break;

                    case "UseExtension":
                        if (args.Count >= 1 && args.Count <= 2 &&
                            args.TryGetValue("type", out var typeArg) && typeArg.Expression is TypeOfExpressionSyntax tof)
                        {
                            var typeInfo = model.GetTypeInfo(tof.Type);
                            var extType = typeInfo.Type as INamedTypeSymbol;
                            if (extType == null)
                            {
                                errors.Add(new DiagInfo(ErrorCodes.E3.ToString(), "Incorrect argument type", "Unexpected argument type of " + method, LocationData.From(ma2.GetLocation())));
                                break;
                            }

                            // Optional property selector: values passed to
                            // values-array hooks (Before*(store, entity,
                            // object[] values[, context])).
                            var extProps = new List<string>();
                            if (args.Count == 2)
                            {
                                if (args.TryGetValue("properties", out var propsArg) &&
                                    propsArg.Expression is LambdaExpressionSyntax lamE &&
                                    lamE.Body is AnonymousObjectCreationExpressionSyntax anonE)
                                {
                                    foreach (var init in anonE.Initializers)
                                        if (init.Expression is MemberAccessExpressionSyntax pmaE &&
                                            model.GetSymbolInfo(pmaE).Symbol is IPropertySymbol piE)
                                        {
                                            extProps.Add(piE.Name);
                                        }
                                }
                                if (extProps.Count == 0)
                                {
                                    errors.Add(new DiagInfo(ErrorCodes.E2.ToString(), "Incorrect structure", "Unexpected structure of " + method, LocationData.From(ma2.GetLocation())));
                                    break;
                                }
                            }
                            var extPropsArr = new EquatableArray<string>(extProps);

                            var staticMethods = extType
                                .GetMembers()
                                .OfType<IMethodSymbol>()
                                .Where(m =>
                                    m.MethodKind == MethodKind.Ordinary
                                    && m.IsStatic
                                    && m.DeclaredAccessibility == Accessibility.Public).ToList();

                            bool anyValued = false;
                            foreach (var func in staticMethods)
                            {
                                bool hasBeforeCreate = false;
                                bool hasBeforeUpdate = false;
                                bool hasBeforeDelete = false;

                                switch (func.Name)
                                {
                                    case "BeforeCreate":
                                        hasBeforeCreate = true;
                                        break;

                                    case "BeforeUpdate":
                                        hasBeforeUpdate = true;
                                        break;

                                    case "BeforeDelete":
                                        hasBeforeDelete = true;
                                        break;
                                }

                                if (!hasBeforeCreate && !hasBeforeUpdate && !hasBeforeDelete)
                                    continue;

                                var paramCount = func.Parameters.Length;
                                if (paramCount < 2 || paramCount > 4)
                                {
                                    errors.Add(new DiagInfo(ErrorCodes.E9.ToString(), "Invalid extension",
                                        $"Extension hook {extType.Name}.{func.Name} must take 2-4 parameters: (store, entity[, object[] values][, context])", LocationData.From(ma2.GetLocation())));
                                    continue;
                                }

                                // A third parameter typed object[] is the values
                                // array; one typed object is the per-operation
                                // context. Four parameters is always
                                // (store, entity, object[] values, object context).
                                bool hasValues = false;
                                bool hasContext = false;
                                if (paramCount >= 3)
                                {
                                    var thirdIsObjectArray = func.Parameters[2].Type is IArrayTypeSymbol arr
                                        && arr.ElementType.SpecialType == SpecialType.System_Object;
                                    if (paramCount == 4)
                                    {
                                        if (!thirdIsObjectArray || func.Parameters[3].Type.SpecialType != SpecialType.System_Object)
                                        {
                                            errors.Add(new DiagInfo(ErrorCodes.E9.ToString(), "Invalid extension",
                                                $"Extension hook {extType.Name}.{func.Name} with 4 parameters must be (store, entity, object[] values, object context)", LocationData.From(ma2.GetLocation())));
                                            continue;
                                        }
                                        hasValues = true;
                                        hasContext = true;
                                    }
                                    else if (thirdIsObjectArray)
                                    {
                                        hasValues = true;
                                    }
                                    else if (func.Parameters[2].Type.SpecialType == SpecialType.System_Object)
                                    {
                                        hasContext = true;
                                    }
                                    else
                                    {
                                        errors.Add(new DiagInfo(ErrorCodes.E9.ToString(), "Invalid extension",
                                            $"Extension hook {extType.Name}.{func.Name}'s third parameter must be object[] (values) or object (context)", LocationData.From(ma2.GetLocation())));
                                        continue;
                                    }
                                }

                                if (hasValues && extProps.Count == 0)
                                {
                                    errors.Add(new DiagInfo(ErrorCodes.E9.ToString(), "Invalid extension",
                                        $"Extension hook {extType.Name}.{func.Name} takes a values array but UseExtension was given no property selector", LocationData.From(ma2.GetLocation())));
                                    continue;
                                }
                                anyValued |= hasValues;

                                ei.Extensions.Add((extType.ToDisplayString(), hasBeforeCreate, hasBeforeUpdate, hasBeforeDelete, hasValues, hasContext, extPropsArr));
                            }

                            if (extProps.Count != 0 && !anyValued)
                            {
                                errors.Add(new DiagInfo(ErrorCodes.E9.ToString(), "Invalid extension",
                                    $"UseExtension for {extType.Name} was given a property selector but the extension declares no hook accepting a values array", LocationData.From(ma2.GetLocation())));
                            }
                        }
                        else
                        {
                            errors.Add(new DiagInfo(ErrorCodes.E2.ToString(), "Incorrect structure", "Unexpected structure of " + method, LocationData.From(ma2.GetLocation())));
                        }
                        break;

                    case "UsePredicate":
                        if (args.Count == 2 && args["predicate"].Expression is LambdaExpressionSyntax predLam)
                        {
                            var nameConst = model.GetConstantValue(args["name"].Expression);
                            if (!nameConst.HasValue || nameConst.Value is not string predName || !SyntaxFacts.IsValidIdentifier(predName))
                            {
                                errors.Add(new DiagInfo(ErrorCodes.E8.ToString(), "Invalid predicate", "Predicate name must be a constant string that is a valid C# identifier", LocationData.From(ma2.GetLocation())));
                                break;
                            }
                            if (predLam.Body is not ExpressionSyntax predBody)
                            {
                                errors.Add(new DiagInfo(ErrorCodes.E8.ToString(), "Invalid predicate", "Predicate must be a single expression; block bodies are not supported", LocationData.From(predLam.GetLocation())));
                                break;
                            }
                            if (model.GetSymbolInfo(predLam).Symbol is not IMethodSymbol predSym || predSym.Parameters.Length == 0)
                            {
                                errors.Add(new DiagInfo(ErrorCodes.E2.ToString(), "Incorrect structure", "Unexpected structure of " + method, LocationData.From(ma2.GetLocation())));
                                break;
                            }

                            var bodyText = QualifyPredicateBody(model, predBody, predLam, errors);
                            if (bodyText == null)
                                break;

                            var predArgs = predSym.Parameters.Skip(1)
                                .Select(p => (p.Name, p.Type.ToDisplayString()))
                                .ToList();

                            ei.Predicates.Add((predName, predSym.Parameters[0].Name,
                                new EquatableArray<(string Name, string Type)>(predArgs),
                                bodyText,
                                LocationData.From(ma2.GetLocation())));
                        }
                        else
                        {
                            errors.Add(new DiagInfo(ErrorCodes.E2.ToString(), "Incorrect structure", "Unexpected structure of " + method, LocationData.From(ma2.GetLocation())));
                        }
                        break;

                    case "UseSafety":
                        {
                            bool ci = ei.CopyOnInsert, cs = ei.CopyOnStage, sg = ei.SeqIndexGuard;
                            ApplySafetyArgs(args, model, ref ci, ref cs, ref sg);
                            ei.CopyOnInsert = ci;
                            ei.CopyOnStage = cs;
                            ei.SeqIndexGuard = sg;
                        }
                        break;

                    case "UseSort":
                        if (args.Count != 0 && args.TryGetValue("key", out var sortKeyArg) &&
                            sortKeyArg.Expression is LambdaExpressionSyntax lamSrt &&
                            lamSrt.Body is MemberAccessExpressionSyntax srtMa &&
                            model.GetSymbolInfo(srtMa).Symbol is IPropertySymbol sortProp)
                        {
                            var sortName = "By" + sortProp.Name;
                            if (args.TryGetValue("name", out var sortNameArg))
                            {
                                var nameConst = model.GetConstantValue(sortNameArg.Expression);
                                if (!nameConst.HasValue || nameConst.Value is not string explicitName || !SyntaxFacts.IsValidIdentifier(explicitName))
                                {
                                    errors.Add(new DiagInfo(ErrorCodes.E10.ToString(), "Invalid sort", "Sort name must be a constant string that is a valid C# identifier", LocationData.From(ma2.GetLocation())));
                                    break;
                                }
                                sortName = explicitName;
                            }

                            var materialized = false;
                            if (args.TryGetValue("materialized", out var matArg))
                            {
                                var matConst = model.GetConstantValue(matArg.Expression);
                                if (matConst.HasValue && matConst.Value is bool mb) materialized = mb;
                            }

                            ei.Sorts.Add((sortName, sortProp.Name, sortProp.Type.ToDisplayString(), materialized, LocationData.From(ma2.GetLocation())));
                        }
                        else
                        {
                            errors.Add(new DiagInfo(ErrorCodes.E10.ToString(), "Invalid sort", "UseSort key must select a single property", LocationData.From(ma2.GetLocation())));
                        }
                        break;

                    case "UseFeatures":
                        foreach (var arg in args)
                        {
                            var name = arg.Value.NameColon?.Name.Identifier.Text;
                            var valueOpt = model.GetConstantValue(arg.Value.Expression);
                            if (!valueOpt.HasValue || !(valueOpt.Value is bool b)) continue;
                            switch (name)
                            {
                                case "create":
                                    ei.CanCreate = b;
                                    break;
                                case "update":
                                    ei.CanUpdate = b;
                                    break;
                                case "delete":
                                    ei.CanDelete = b;
                                    break;
                            }
                        }
                        break;
                }
            }

            // Coverage check: GetEntity can only be a synchronous clone when
            // every simple property of the entity type is cached (regardless
            // of whether that was declared via UseAllProperties or an
            // exhaustive UseProperties). Otherwise the generated ReadOnly
            // gets an async GetEntity that re-fetches from the context.
            var simpleProps = GetSimpleProperties(methodSym.TypeArguments[0], includeScalarCollections: true);
            ei.CachesAllProperties = simpleProps.All(p => ei.Properties.Any(x => x.Name == p.Name));
            ei.AllProps = new EquatableArray<(string Name, string Type)>(
                simpleProps.Select(p => (p.Name, p.Type.ToDisplayString())).ToList());

            // E11 guard: members the store would otherwise silently drop from
            // clones, diffs and the cache. A class-typed prop declared via
            // UseProperties is an error (the generated code would treat it as
            // a scalar and break); an undeclared class prop or a scalar
            // collection in an unsupported shape is a warning.
            foreach (var p in GetUndeclaredCandidateProps(methodSym.TypeArguments[0]))
            {
                if (ei.Owneds.Any(o => o.Name == p.Name)) continue;
                if (ei.ManyNavs.Any(n => n.NavProp == p.Name)) continue;
                if (ei.ManyToManys.Any(m => m.Name == p.Name)) continue;
                if (IsPrimitiveCollection(p.Type)) continue; // supported, cached

                if (IsUnsupportedScalarCollection(p.Type))
                {
                    warnings.Add(new DiagInfo(ErrorCodes.E11.ToString(), "Property not cached",
                        $"Property {p.Name} of {ei.EntityType} is a scalar collection in a shape the store does not cache; expose it through a mapped List<T> pass-through property and mark this one [NotMapped]", LocationData.From(p.GetLocation())));
                    continue;
                }
                // Other collections are navigation-shaped; UseMany/UseManyToMany cover them.
                if (p.Type is IArrayTypeSymbol ||
                    p.Type.SpecialType == SpecialType.System_Collections_IEnumerable ||
                    p.Type.AllInterfaces.Any(i => i.SpecialType == SpecialType.System_Collections_IEnumerable)) continue;
                if (p.Type.TypeKind != TypeKind.Class) continue;

                if (ei.Properties.Any(x => x.Name == p.Name))
                {
                    errors.Add(new DiagInfo(ErrorCodes.E11.ToString(), "Unsupported property type",
                        $"Property {p.Name} of {ei.EntityType} is not an EF scalar and cannot be cached as a plain property; declare it with UseOwned instead", LocationData.From(p.GetLocation())));
                }
                else
                {
                    warnings.Add(new DiagInfo(ErrorCodes.E11.ToString(), "Property not cached",
                        $"Property {p.Name} of {ei.EntityType} is not an EF scalar and will not be cached, cloned or persisted by the store; declare it with UseOwned or mark it [NotMapped]", LocationData.From(p.GetLocation())));
                }
            }

            return ei;
        }

        /// <summary>
        /// Public settable non-scalar, non-[NotMapped] properties: the E11
        /// guard's candidates (owned classes, unsupported collections, ...).
        /// </summary>
        private static IEnumerable<IPropertySymbol> GetUndeclaredCandidateProps(ITypeSymbol type)
        {
            if (type is not INamedTypeSymbol named) yield break;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (INamedTypeSymbol t = named; t is not null; t = t.BaseType)
            {
                foreach (var m in t.GetMembers())
                {
                    if (m is not IPropertySymbol p) continue;
                    if (p.IsStatic || p.Parameters.Length != 0) continue;
                    if (p.GetMethod == null || p.SetMethod == null) continue;
                    if (p.DeclaredAccessibility != Accessibility.Public) continue;
                    if (IsEfScalar(p.Type)) continue;
                    if (HasNotMapped(p)) continue;
                    if (!seen.Add(p.Name)) continue;

                    yield return p;
                }
            }
        }

        /// <summary>
        /// Validates a UsePredicate body and returns its text, ready to be
        /// transplanted into the generated file: identifiers may only be the
        /// lambda's own parameters/locals or type references, and type
        /// references are rewritten to fully-qualified form (the generated
        /// file does not share the user's using directives).
        /// Returns null (with diagnostics) when the body is not liftable.
        /// </summary>
        private static string QualifyPredicateBody(SemanticModel model, ExpressionSyntax body,
            LambdaExpressionSyntax lambda, List<DiagInfo> errors)
        {
            var rewriter = new PredicateBodyRewriter(model, lambda, errors);
            var rewritten = rewriter.Visit(body);
            return rewriter.IsValid ? rewritten.ToString() : null;
        }

        private sealed class PredicateBodyRewriter : CSharpSyntaxRewriter
        {
            private readonly SemanticModel _model;
            private readonly LambdaExpressionSyntax _lambda;
            private readonly List<DiagInfo> _errors;

            public bool IsValid { get; private set; } = true;

            public PredicateBodyRewriter(SemanticModel model, LambdaExpressionSyntax lambda, List<DiagInfo> errors)
                => (_model, _lambda, _errors) = (model, lambda, errors);

            public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node)
            {
                // Member names to the right of a dot resolve relative to the
                // qualified left side; only leftmost/standalone identifiers
                // need checking. Named-argument labels resolve by signature.
                if (node.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == node)
                    return node;
                if (node.Parent is NameColonSyntax)
                    return node;

                var symbol = _model.GetSymbolInfo(node).Symbol;
                switch (symbol)
                {
                    case IParameterSymbol or ILocalSymbol or IRangeVariableSymbol when IsDeclaredInsideLambda(symbol):
                        return node;

                    case INamespaceSymbol:
                        return node; // root of an already-qualified name

                    case ITypeSymbol type:
                        // Qualify so the reference binds without the user's usings.
                        return SyntaxFactory.ParseExpression(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                            .WithTriviaFrom(node);

                    default:
                        IsValid = false;
                        _errors.Add(new DiagInfo(ErrorCodes.E8.ToString(), "Invalid predicate",
                            $"Predicate bodies may only reference their own parameters and types; '{node.Identifier.ValueText}' captures enclosing state or needs a using directive (fully qualify it)",
                            LocationData.From(node.GetLocation())));
                        return node;
                }
            }

            private bool IsDeclaredInsideLambda(ISymbol symbol)
            {
                if (symbol.DeclaringSyntaxReferences.Length == 0) return false;
                var reference = symbol.DeclaringSyntaxReferences[0];
                return reference.SyntaxTree == _lambda.SyntaxTree
                    && reference.Span.Start >= _lambda.FullSpan.Start
                    && reference.Span.End <= _lambda.FullSpan.End;
            }
        }

        private static bool TryExtractProperty(SemanticModel model, SyntaxNode expr, out IPropertySymbol prop)
        {
            prop = null!;

            if (expr is LambdaExpressionSyntax les)
                return TryExtractProperty(model, les.Body, out prop);

            if (expr is MemberAccessExpressionSyntax ma &&
                model.GetSymbolInfo(ma).Symbol is IPropertySymbol ps)
            {
                prop = ps;
                return true;
            }

            if (expr is IdentifierNameSyntax id &&
                model.GetSymbolInfo(id).Symbol is IPropertySymbol ps2)
            {
                prop = ps2;
                return true;
            }

            return false;
        }

        private static void GenerateForStore(
            SourceProductionContext spc,
            StoreInfo store)
        {
            var errors = new List<DiagInfo>();
            var model = StoreModelBuilder.Build(store, errors);

            if (errors.Count != 0)
            {
                foreach (var error in errors)
                {
                    spc.ReportError(error.Id, error.Title, error.Message, error.Location.ToLocation());
                }
                return;
            }

            var source = new Ef.Mems.Generator.Templates.StoreCacheTemplate(model).Render();
            spc.AddSource($"{store.StoreName}_Cache.g.cs", SourceText.From(source, Encoding.UTF8));
        }

        public static List<IPropertySymbol> GetSimpleProperties(ITypeSymbol type, bool includeScalarCollections = false)
        {
            var result = new List<IPropertySymbol>();
            if (type is not INamedTypeSymbol named) return result;

            var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var seenByName = new HashSet<string>(StringComparer.Ordinal);

            void TryAdd(IPropertySymbol p)
            {
                if (p.IsStatic) return;
                if (p.Parameters.Length != 0) return; //exclude indexers
                if (p.GetMethod is null) return;
                if (HasNotMapped(p)) return;
                if (!IsEfScalar(p.Type) && !(includeScalarCollections && IsPrimitiveCollection(p.Type))) return;

                if (seenByName.Add(p.Name))
                    result.Add(p);
            }

            //walk derivatives
            for (INamedTypeSymbol t = named; t is not null; t = t.BaseType)
            {
                if (!visited.Add(t)) break;
                foreach (var m in t.GetMembers())
                    if (m is IPropertySymbol p) TryAdd(p);
            }

            //include interfaces
            foreach (var i in named.AllInterfaces)
            {
                if (!visited.Add(i)) continue;
                foreach (var m in i.GetMembers())
                    if (m is IPropertySymbol p) TryAdd(p);
            }

            return result;
        }

        internal static bool HasNotMapped(IPropertySymbol p)
            => p.GetAttributes().Any(a => a.AttributeClass?.Name == "NotMappedAttribute");

        // List<T> or T[] with an EF-scalar element (byte[]/char[] excluded -
        // those are scalar blobs). EF Core 8+ maps these by convention as
        // primitive collections.
        internal static bool IsPrimitiveCollection(ITypeSymbol t)
        {
            if (t is IArrayTypeSymbol arr)
                return arr.ElementType.SpecialType != SpecialType.System_Byte
                    && arr.ElementType.SpecialType != SpecialType.System_Char
                    && IsEfScalar(arr.ElementType);

            return t is INamedTypeSymbol named
                && named.IsGenericType
                && named.ConstructedFrom.ToDisplayString() == "System.Collections.Generic.List<T>"
                && IsEfScalar(named.TypeArguments[0]);
        }

        // A scalar-element collection in an unsupported shape (HashSet<int>,
        // Collection<string>, ...): candidate for the List-passthrough +
        // [NotMapped] pattern warning.
        internal static bool IsUnsupportedScalarCollection(ITypeSymbol t)
        {
            if (t is IArrayTypeSymbol || IsPrimitiveCollection(t)) return false;
            if (t is not INamedTypeSymbol named) return false;

            foreach (var i in named.AllInterfaces)
                if (i.IsGenericType
                    && i.ConstructedFrom.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T
                    && IsEfScalar(i.TypeArguments[0]))
                    return true;
            return false;
        }

        internal static bool IsEfScalar(ITypeSymbol t)
        {
            //unwrap nullable
            if (t is INamedTypeSymbol nt &&
                nt.IsGenericType &&
                nt.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T)
            {
                t = nt.TypeArguments[0];
            }

            if (t.TypeKind == TypeKind.Enum)
                return true;

            if (t is IArrayTypeSymbol arr)
            {
                var elem = arr.ElementType;
                return elem.SpecialType == SpecialType.System_Byte
                    || elem.SpecialType == SpecialType.System_Char;
            }

            switch (t.SpecialType)
            {
                case SpecialType.System_Boolean:
                case SpecialType.System_Char:
                case SpecialType.System_SByte:
                case SpecialType.System_Byte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                case SpecialType.System_Decimal:
                case SpecialType.System_String:
                    return true;
            }

            if (t is INamedTypeSymbol namedT)
            {
                var fq = namedT.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (fq is "global::System.Guid"
                    or "global::System.DateTime"
                    or "global::System.DateTimeOffset"
                    or "global::System.TimeSpan"
                    or "global::System.DateOnly"
                    or "global::System.TimeOnly")
                {
                    return true;
                }
            }

            return false;
        }

        public static Dictionary<string, ArgumentSyntax> GetArgumentList(InvocationExpressionSyntax invocation,
        SemanticModel model)
        {
            var op = (IInvocationOperation?)model.GetOperation(invocation);
            if (op is null) return new Dictionary<string, ArgumentSyntax>(0);

            var result = new Dictionary<string, ArgumentSyntax>(op.Arguments.Length, System.StringComparer.Ordinal);

            foreach (var a in op.Arguments)
            {
                if (a.IsImplicit) continue;
                var paramName = a.Parameter?.Name;
                if (paramName is null) continue;
                if (a.Syntax is ArgumentSyntax argSyntax)
                    result[paramName] = argSyntax;
            }

            return result;
        }
    }
}

public static class ContextExtensions
{
    public static void ReportError(this SourceProductionContext ctx,
    string id, string title, string message, Location? location = null)
    => ctx.ReportDiagnostic(Diagnostic.Create(
           new DiagnosticDescriptor(id, title, "{0}", "Ef.Mems.Generator",
               DiagnosticSeverity.Error, isEnabledByDefault: true),
           location, message));

    public static void ReportError(this SourceProductionContext ctx,
        string id, string title, string message, SyntaxNode node)
        => ctx.ReportError(id, title, message, node.GetLocation());

    public static void ReportError(this SourceProductionContext ctx,
        string id, string title, string message, ISymbol symbol)
        => ctx.ReportError(id, title, message, symbol.GetLocation());

    public static void ReportWarning(this SourceProductionContext ctx,
        string id, string title, string message, Location? location = null)
        => ctx.ReportDiagnostic(Diagnostic.Create(
               new DiagnosticDescriptor(id, title, "{0}", "Ef.Mems.Generator",
                   DiagnosticSeverity.Warning, isEnabledByDefault: true),
               location, message));

    public static Location GetLocation<T>(this T symbol) where T : ISymbol
    {
        if (symbol == null || symbol.Locations == null)
            return null;

        return symbol.Locations.FirstOrDefault();
    }
}
