// Parsed store description and template view models for the Razor templates in Templates/.
//
// The parse-time models (StoreParseResult, StoreInfo, EntityInfo, ...) flow
// through the incremental pipeline, so they must:
//  - be value-equatable (the incremental driver caches node outputs and skips
//    regeneration when the new value equals the cached one), and
//  - hold no ISymbol / Location / Diagnostic references (those root whole
//    compilations and never compare equal across compilations).
// Everything symbol-derived is reduced to strings/spans at parse time.
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Ef.Mems.SourceGenerators
{
    /// <summary>An immutable array with sequence value equality, for use in incremental pipeline models.</summary>
    internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
        where T : IEquatable<T>
    {
        private readonly T[] _items;

        public EquatableArray(IEnumerable<T> items) => _items = items as T[] ?? items.ToArray();

        public int Count => _items?.Length ?? 0;
        public T this[int index] => _items[index];

        public bool Equals(EquatableArray<T> other)
        {
            var a = _items;
            var b = other._items;
            if (ReferenceEquals(a, b)) return true;
            if (a is null || b is null) return a is null && b is null;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (!a[i].Equals(b[i])) return false;
            return true;
        }

        public override bool Equals(object obj) => obj is EquatableArray<T> other && Equals(other);

        public override int GetHashCode()
        {
            var items = _items;
            if (items is null) return 0;
            unchecked
            {
                int hash = 17;
                for (int i = 0; i < items.Length; i++)
                    hash = hash * 31 + items[i].GetHashCode();
                return hash;
            }
        }

        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items ?? Array.Empty<T>())).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Value-equatable stand-in for <see cref="Location"/> (which holds a syntax tree reference and would defeat caching).</summary>
    internal readonly struct LocationData : IEquatable<LocationData>
    {
        public readonly string FilePath;
        public readonly TextSpan Span;
        public readonly LinePositionSpan LineSpan;

        private LocationData(string filePath, TextSpan span, LinePositionSpan lineSpan)
            => (FilePath, Span, LineSpan) = (filePath, span, lineSpan);

        public static LocationData From(Location location)
            => location?.SourceTree == null
                ? default
                : new LocationData(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);

        public Location ToLocation()
            => FilePath == null ? null : Location.Create(FilePath, Span, LineSpan);

        public bool Equals(LocationData other)
            => FilePath == other.FilePath && Span == other.Span && LineSpan == other.LineSpan;

        public override bool Equals(object obj) => obj is LocationData other && Equals(other);

        public override int GetHashCode()
            => unchecked(((FilePath?.GetHashCode() ?? 0) * 31 + Span.GetHashCode()) * 31 + LineSpan.GetHashCode());
    }

    /// <summary>Value-equatable stand-in for a <see cref="Diagnostic"/> to report later.</summary>
    internal sealed class DiagInfo : IEquatable<DiagInfo>
    {
        public readonly string Id;
        public readonly string Title;
        public readonly string Message;
        public readonly LocationData Location;

        public DiagInfo(string id, string title, string message, LocationData location = default)
            => (Id, Title, Message, Location) = (id, title, message, location);

        public bool Equals(DiagInfo other)
            => other != null && Id == other.Id && Title == other.Title && Message == other.Message && Location.Equals(other.Location);

        public override bool Equals(object obj) => Equals(obj as DiagInfo);
        public override int GetHashCode() => unchecked((Id?.GetHashCode() ?? 0) * 31 + (Message?.GetHashCode() ?? 0));
    }

    /// <summary>Result of parsing one DataStore class; one pipeline item per store so stores cache and regenerate independently.</summary>
    internal sealed class StoreParseResult : IEquatable<StoreParseResult>
    {
        public StoreInfo Store;
        public List<DiagInfo> Errors = new();
        // Non-blocking diagnostics (E11 guard): reported, but generation
        // still runs.
        public List<DiagInfo> Warnings = new();
        public DiagInfo Exception;

        public bool Equals(StoreParseResult other)
            => other != null
            && Equals(Store, other.Store)
            && Errors.SequenceEqual(other.Errors)
            && Warnings.SequenceEqual(other.Warnings)
            && Equals(Exception, other.Exception);

        public override bool Equals(object obj) => Equals(obj as StoreParseResult);
        public override int GetHashCode()
            => unchecked((Store?.GetHashCode() ?? 0) * 31 + Errors.Count);
    }

    internal sealed class StoreInfo : IEquatable<StoreInfo>
    {
        public string Namespace { get; }
        public string StoreName { get; }
        public string StoreType { get; }
        public string ContextType { get; }
        public List<EntityInfo> Entities { get; } = new();
        public List<EntityStoreInfo> Stores { get; } = new();
        // UseUnion declarations: read views over member entity stores.
        public List<(string Name, EquatableArray<string> MemberEntityTypes, LocationData loc)> Unions { get; } = new();

        public StoreInfo(string ns, string storeName, string storeType, string contextType)
            => (Namespace, StoreName, StoreType, ContextType) = (ns, storeName, storeType, contextType);

        public bool Equals(StoreInfo other)
            => other != null
            && Namespace == other.Namespace
            && StoreName == other.StoreName
            && StoreType == other.StoreType
            && ContextType == other.ContextType
            && Entities.SequenceEqual(other.Entities)
            && Stores.SequenceEqual(other.Stores)
            && Unions.SequenceEqual(other.Unions);

        public override bool Equals(object obj) => Equals(obj as StoreInfo);
        public override int GetHashCode()
            => unchecked((StoreType?.GetHashCode() ?? 0) * 31 + Entities.Count);
    }

    internal sealed class EntityStoreInfo : IEquatable<EntityStoreInfo>
    {
        public string ClassName { get; set; }
        public string EntityName { get; set; }
        public string PropertyName { get; set; }

        public bool Equals(EntityStoreInfo other)
            => other != null && ClassName == other.ClassName && EntityName == other.EntityName && PropertyName == other.PropertyName;

        public override bool Equals(object obj) => Equals(obj as EntityStoreInfo);
        public override int GetHashCode() => ClassName?.GetHashCode() ?? 0;
    }

    internal sealed class EntityInfo : IEquatable<EntityInfo>
    {
        public LocationData Location { get; set; }

        public string EntityType { get; }
        public string DbSetProperty { get; }

        public bool CanCreate { get; set; } = true;
        public bool CanUpdate { get; set; } = true;
        public bool CanDelete { get; set; } = true;

        // True when the declared properties cover every simple property of
        // the entity type (UseAllProperties, or an exhaustive UseProperties).
        // When false, GetEntity is generated async and re-fetches the full
        // entity from the context.
        public bool CachesAllProperties { get; set; } = true;

        // Safety features (UseSafety): store-level defaults resolved with
        // per-entity overrides at parse time; all default ON.
        public bool CopyOnInsert { get; set; } = true;
        public bool CopyOnStage { get; set; } = true;
        public bool SeqIndexGuard { get; set; } = true;

        // Every simple property of the entity type (not just the cached
        // ones) - the copy-on-insert clone must carry uncached columns too,
        // since the clone is what gets written to the database. Types are
        // kept so the clone can value-copy primitive collections.
        public EquatableArray<(string Name, string Type)> AllProps { get; set; } = new(new List<(string, string)>());

        // UseOwned declarations: class-typed members stored in the entity's
        // table (EF OwnsOne/ComplexProperty). Members are the owned type's
        // scalar properties.
        public readonly List<(string Name, string Type, string TypeName,
                               EquatableArray<(string Name, string Type)> Members, LocationData loc)> Owneds = new();

        // One entry per hook method; HasValues marks the values-array form
        // (Before*(store, entity, object[] values)) fed by the Props selected
        // in UseExtension's property selector, HasContext the form taking the
        // caller-supplied per-operation context as its last parameter.
        public readonly List<(string Type, bool HasBeforeCreate, bool HasBeforeUpdate, bool HasBeforeDelete,
                               bool HasValues, bool HasContext, EquatableArray<string> Props)> Extensions = new();

        public readonly List<(string Name, string Type, LocationData loc)> Properties = new();
        // Props holds the index's member properties: one entry for a plain
        // index, several for a composite (anonymous-object) index whose Name
        // is the concatenated member names and Type a value-tuple type.
        public readonly List<(string Name, string Type, string GenFunc, bool GenArg,
                               EquatableArray<(string Name, string Type)> Props, LocationData loc)> Indexes = new();
        public readonly List<(string Name, string Type, bool HasMinValue, bool GenerateId, LocationData loc)> SeqIndexes = new();
        public readonly List<(string NavProp, string ChildType,
                               EquatableArray<(string Name, string Type)> ChildProps, EquatableArray<string> KeyProp, LocationData loc)> ManyNavs = new();
        // JoinTypeName/AllProps are extracted from the join INamedTypeSymbol at
        // parse time; symbols must not flow through the pipeline.
        public readonly List<(string JoinTypeName, string Name,
                               EquatableArray<(string Name, string Type)> ChildProps, EquatableArray<string> KeyProps,
                               EquatableArray<(string Name, string Type)> AllProps, LocationData loc)> ManyToManys = new();
        // Body is the validated, fully-qualified expression text of the
        // UsePredicate lambda; Args are its extra parameters.
        public readonly List<(string Name, string EntityParam,
                               EquatableArray<(string Name, string Type)> Args, string Body, LocationData loc)> Predicates = new();

        // UseSort declarations: Name is the method suffix ("ByNumber" or the
        // explicit name); Materialized sorts maintain a pre-sorted snapshot.
        public readonly List<(string Name, string PropName, string PropType, bool Materialized, LocationData loc)> Sorts = new();

        public EntityInfo(string entityType, string dbSetProperty)
            => (EntityType, DbSetProperty) = (entityType, dbSetProperty);

        public bool Equals(EntityInfo other)
            => other != null
            && EntityType == other.EntityType
            && DbSetProperty == other.DbSetProperty
            && CanCreate == other.CanCreate
            && CanUpdate == other.CanUpdate
            && CanDelete == other.CanDelete
            && CachesAllProperties == other.CachesAllProperties
            && CopyOnInsert == other.CopyOnInsert
            && CopyOnStage == other.CopyOnStage
            && SeqIndexGuard == other.SeqIndexGuard
            && AllProps.Equals(other.AllProps)
            && Location.Equals(other.Location)
            && Owneds.SequenceEqual(other.Owneds)
            && Extensions.SequenceEqual(other.Extensions)
            && Properties.SequenceEqual(other.Properties)
            && Indexes.SequenceEqual(other.Indexes)
            && SeqIndexes.SequenceEqual(other.SeqIndexes)
            && ManyNavs.SequenceEqual(other.ManyNavs)
            && ManyToManys.SequenceEqual(other.ManyToManys)
            && Predicates.SequenceEqual(other.Predicates)
            && Sorts.SequenceEqual(other.Sorts);

        public override bool Equals(object obj) => Equals(obj as EntityInfo);
        public override int GetHashCode()
            => unchecked((EntityType?.GetHashCode() ?? 0) * 31 + Properties.Count);
    }

    // ---------------------------------------------------------------------
    // Template view models. All code-shape decisions (index names, key join
    // expressions, comparison operators, ...) are precomputed here so the
    // Razor templates only print values. These never enter the incremental
    // pipeline (they are built inside RegisterSourceOutput), so they need no
    // value equality.
    // ---------------------------------------------------------------------

    internal sealed class PropModel
    {
        private const string ListPrefix = "System.Collections.Generic.List<";

        public string Name;
        public string Type;
        // byte[]/char[] props are compared with EqualsFast instead of !=
        public bool IsArrayLike;
        // Primitive collections (List<T>/T[] of EF scalars, byte[]/char[]
        // excepted): sequence-compared, defensively copied by clones, and
        // exposed read-only. The generator only admits vetted types into
        // cached properties, so classification by type text is safe here.
        public bool IsScalarCollection;
        public bool IsCollectionArray; // T[] flavor of IsScalarCollection
        public string ElementType;     // scalar collections only

        public PropModel(string name, string type)
        {
            Name = name;
            Type = type;
            IsArrayLike = type == IncrementalCacheGenerator.ByteArrayType
                       || type == IncrementalCacheGenerator.CharArrayType;
            if (!IsArrayLike)
            {
                if (type.EndsWith("[]", StringComparison.Ordinal))
                {
                    IsScalarCollection = true;
                    IsCollectionArray = true;
                    ElementType = type.Substring(0, type.Length - 2);
                }
                else if (type.StartsWith(ListPrefix, StringComparison.Ordinal) && type.EndsWith(">", StringComparison.Ordinal))
                {
                    IsScalarCollection = true;
                    ElementType = type.Substring(ListPrefix.Length, type.Length - ListPrefix.Length - 1);
                }
            }
        }

        /// <summary>C# condition text: true when <paramref name="left"/>.Name differs from <paramref name="right"/>.Name.</summary>
        public string NotEqual(string left, string right)
            => IsArrayLike || IsScalarCollection
                ? $"!{left}.{Name}.EqualsFast({right}.{Name})"
                : $"{left}.{Name} != {right}.{Name}";

        // Type exposed on the ReadOnly wrapper: array-likes and primitive
        // collections become read-only views so readers cannot mutate the
        // cached instance in place.
        public string RoType
            => Type == IncrementalCacheGenerator.ByteArrayType ? "ReadOnlyMemory<byte>"
             : Type == IncrementalCacheGenerator.CharArrayType ? "ReadOnlyMemory<char>"
             : IsCollectionArray ? $"ReadOnlyMemory<{ElementType}>"
             : IsScalarCollection ? $"IReadOnlyList<{ElementType}>"
             : Type;

        /// <summary>Clone expression: plain read for scalars, null-safe value copy for primitive collections.</summary>
        public string CloneExpr(string src)
            => !IsScalarCollection ? $"{src}.{Name}"
             : IsCollectionArray ? $"{src}.{Name} == null ? null : ({Type}){src}.{Name}.Clone()"
             : $"{src}.{Name} == null ? null : new {Type}({src}.{Name})";
    }

    internal sealed class IndexModel
    {
        public string Name;   // concatenated prop names for composite indexes
        public string Type;   // dictionary key type; "(T1, T2)" tuple for composite
        public string GenFunc; // single-prop indexes only
        public bool GenArg;
        public List<PropModel> Props; // the index's member properties (1..n)

        public bool IsComposite => Props.Count > 1;

        /// <summary>Key expression read off an entity: "src.Code" or "(src.Region, src.Code)".</summary>
        public string KeyFrom(string src) => IsComposite
            ? "(" + string.Join(", ", Props.Select(p => src + "." + p.Name)) + ")"
            : src + "." + Props[0].Name;

        /// <summary>C# condition: any index member differs between the two entities.</summary>
        public string NotEqual(string left, string right)
            => string.Join(" || ", Props.Select(p => $"{left}.{p.Name} != {right}.{p.Name}"));

        /// <summary>GetBy parameter list: "string key" or "string @region, string @code".</summary>
        public string GetByParams => IsComposite
            ? string.Join(", ", Props.Select(p => $"{p.Type} @{Camel(p.Name)}"))
            : $"{Type} key";

        /// <summary>Dictionary key built from the GetBy parameters.</summary>
        public string GetByKey => IsComposite
            ? "(" + string.Join(", ", Props.Select(p => "@" + Camel(p.Name))) + ")"
            : "key";

        private static string Camel(string name)
            => char.ToLowerInvariant(name[0]) + name.Substring(1);
    }

    internal sealed class SeqIndexModel
    {
        public string Name;
        public string Type;
        public bool GenerateId;
    }

    internal sealed class ExtensionModel
    {
        public string Type;
        public bool HasBeforeCreate;
        public bool HasBeforeUpdate;
        public bool HasBeforeDelete;
        // ", new object[] { item.X, item.Y }" for valued hooks, else ""
        public string ValuesArg;
        public bool HasContext;
        // ", context" for context-taking hooks, else ""
        public string ContextArg;
    }

    internal sealed class NavModel // UseMany navigation
    {
        public string Nav;
        public string ChildType;
        public string ChildName;
        public List<PropModel> ChildProps;
        public List<string> KeyProps;

        public string KeysEqual(string other)
            => string.Join(" && ", KeyProps.Select(k => $"x.{k} == {other}.{k}"));

        public string KeysNotEqual(string other)
            => string.Join(" || ", KeyProps.Select(k => $"x.{k} != {other}.{k}"));

        // "{ Id = c.Id, ... }" member initializers for the __Clone body.
        public string CloneInit
            => string.Join(", ", ChildProps.Select(p => $"{p.Name} = c.{p.Name}"));
    }

    /// <summary>Another entity participating in a ManyToMany join, as seen from one side.</summary>
    internal sealed class MtmOtherModel
    {
        public string DbSetProperty;  // DbSet/store property of the other entity
        public string ClassProp;      // join key prop pointing at the other entity (e.g. TestClassId)
        public string IndexName;      // ClassProp minus the entity name (e.g. Id) -> GetBy{IndexName}
        public string OtherNavName;   // the other entity's nav prop holding the join entities
    }

    /// <summary>Everything the Mtm*Core fragment templates need for one target entity.</summary>
    internal sealed class MtmFragmentModel
    {
        public MtmOtherModel Target;
        public string ReadOnlyType;
        public string KeysEqual;      // "x.K == mtm.K && ..."
        public List<PropModel> ChildProps;
        public bool WithAddElse;      // CreateOrUpdate commit function also adds when not found
    }

    internal sealed class MtmModel // UseManyToMany navigation on one entity
    {
        public string NavName;
        public string ChildType;
        public string ChildName;
        public string ReadOnlyType;
        public string NormTypeName;
        public List<PropModel> ChildProps; // non-key props
        public List<string> Keys;
        public List<MtmOtherModel> Others; // all other entities sharing the join type

        public string KeysEqual(string other)
            => string.Join(" && ", Keys.Select(k => $"x.{k} == {other}.{k}"));

        public string OthersKeyEqual(string other)
            => string.Join(" && ", Others.Select(o => $"x.{o.ClassProp} == {other}.{o.ClassProp}"));

        public string OthersKeyNotEqual(string other)
            => string.Join(" || ", Others.Select(o => $"x.{o.ClassProp} != {other}.{o.ClassProp}"));

        // "{ K = c.K, ..., Prop = c.Prop, ... }" initializers (keys + props)
        // for the __Clone body.
        public string CloneInit
            => string.Join(", ", Keys.Select(k => $"{k} = c.{k}")
                .Concat(ChildProps.Select(p => $"{p.Name} = c.{p.Name}")));

        public MtmFragmentModel Fragment(MtmOtherModel target, bool withAddElse = false) => new MtmFragmentModel
        {
            Target = target,
            ReadOnlyType = ReadOnlyType,
            KeysEqual = KeysEqual("mtm"),
            ChildProps = ChildProps,
            WithAddElse = withAddElse,
        };
    }

    /// <summary>One distinct ManyToMany join type; produces the EntityCommitFunctions pair.</summary>
    internal sealed class MtmJoinModel
    {
        public string StoreType;
        public string ChildType;
        public string ReadOnlyType;
        public string NormTypeName;
        public string KeyTupleTypes;  // "(int, int)"
        public string KeyTupleValues; // "(mtm.A, mtm.B)"
        public List<PropModel> ChildProps; // non-key props
        public List<string> Keys;
        public List<MtmOtherModel> Targets; // every entity using the join type

        public string KeysEqual(string other)
            => string.Join(" && ", Keys.Select(k => $"x.{k} == {other}.{k}"));

        public MtmFragmentModel Fragment(MtmOtherModel target, bool withAddElse = false) => new MtmFragmentModel
        {
            Target = target,
            ReadOnlyType = ReadOnlyType,
            KeysEqual = KeysEqual("mtm"),
            ChildProps = ChildProps,
            WithAddElse = withAddElse,
        };
    }

    /// <summary>A UsePredicate declaration: emitted as a nested struct implementing IPredicate plus named query methods.</summary>
    internal sealed class PredicateModel
    {
        public string Name;        // e.g. OlderThan
        public string StructName;  // e.g. OlderThanPredicate
        public string EntityParam; // the lambda's entity parameter name; reused as Invoke's parameter
        public string Body;        // validated, fully-qualified expression text
        public List<PropModel> Args;

        public string ParamList;        // "int age" (empty when no args)
        public string ParamListLeading; // "int age, " (empty when no args)
        public string ArgNames;         // "age"
    }

    /// <summary>A child ReadOnly DTO (UseMany child or ManyToMany join entity).</summary>
    internal sealed class ChildDtoModel
    {
        public string ClassName;  // simple name
        public string EntityType; // full name
        public List<PropModel> Props;
    }

    /// <summary>A UseUnion read view: generated interface + union class over member stores.</summary>
    internal sealed class UnionModel
    {
        public string Name;
        public List<EntityModel> Members;
        public List<PropModel> CommonProps; // intersection of members' cached scalar props

        public string InterfaceName => "I" + Name + "ReadOnly";
        public string ClassName => Name + "Union";

        private static readonly string[] Ordinals = { "First", "Second", "Third", "Fourth" };

        // "ActiveTransactionEntityStore m0, ArchivedTransactionEntityStore m1"
        public string CtorParams => string.Join(", ", Members.Select((m, i) => $"{m.Name}EntityStore m{i}"));
        // "_m0.Count + _m1.Count"
        public string CountSum => string.Join(" + ", Members.Select((m, i) => $"_m{i}.Count"));
        // "Ns.ActiveTransaction, Ns.ArchivedTransaction" for the UnionBuilder type args
        public string MemberTypeArgs => string.Join(", ", Members.Select(m => m.EntityType));
        // "(ActiveTransactionEntityStore)b.First, (ArchivedTransactionEntityStore)b.Second"
        public string BuildArgs => string.Join(", ", Members.Select((m, i) => $"({m.Name}EntityStore)b.{Ordinals[i]}"));
    }

    /// <summary>A UseOwned member: an EF owned/complex object stored in the entity's own table.</summary>
    internal sealed class OwnedModel
    {
        public string Name;             // property name on the entity
        public string Type;             // owned CLR type, full name
        public string TypeName;         // simple name
        public List<PropModel> Members; // the owned type's scalar properties

        public string WrapperName => TypeName + "ReadOnly";

        /// <summary>Null-safe deep copy expression: "src.X == null ? null : new T { M = src.X.M, ... }".</summary>
        public string CloneExpr(string src)
            => $"{src}.{Name} == null ? null : new {Type} {{ "
             + string.Join(", ", Members.Select(m => $"{m.Name} = {src}.{Name}.{m.Name}"))
             + " }";

        /// <summary>C# condition: the owned objects differ (nullity or any member; both prefixes must be non-null-checked by the caller's expression).</summary>
        public string MembersNotEqual(string left, string right)
            => string.Join(" || ", Members.Select(m => m.NotEqual($"{left}.{Name}", $"{right}.{Name}")));
    }

    internal sealed class EntityModel
    {
        public StoreModel Store;
        public string Name;       // simple name
        public string EntityType; // full name
        public bool CanCreate;
        public bool CanUpdate;
        public bool CanDelete;
        public List<PropModel> Properties;
        public List<PropModel> NonIndexProperties; // props diffed on update
        public List<IndexModel> Indexes;
        public List<SeqIndexModel> SeqIndexes;
        public SeqIndexModel SeqIndex; // single sequential index, or null
        public List<NavModel> ManyNavs;
        public List<MtmModel> ManyToManys;
        public List<OwnedModel> Owneds;
        public List<ExtensionModel> Extensions;
        public List<PredicateModel> Predicates;
        public string QuerySource; // snapshot read of "_seq_X" or "_idx_X.Values"
        public string UpdateKey;   // index prop used to find the original on update
        public string UpdateKeyType;
        // Partial-property stores: the ReadOnly carries the store so its
        // async GetEntity can fetch the full entity from a fresh context.
        public bool CachesAllProperties;
        public string RoCtorExtraParam; // ", {StoreType} store" when partial, else ""
        public string RoCtorExtraArg;   // ", _store" when partial, else ""
        // Columns Refresh projects in partial mode: the cached properties
        // plus any index keys not among them (index columns are read off the
        // raw entity by Refresh/GetBy/update lookups).
        public List<PropModel> ProjectionProps;
        // Safety features (UseSafety), resolved per entity, all default ON.
        public bool CopyOnInsert;
        public bool CopyOnStage;
        public bool SeqIndexGuard;
        public bool NeedsClone => CopyOnInsert || CopyOnStage;
        // Every simple property of the entity type, for the __Clone body.
        public List<PropModel> CloneProps;
        // All index member properties across all indexes (composite-aware).
        public IEnumerable<PropModel> IndexMemberProps => Indexes.SelectMany(x => x.Props);
        public List<SortModel> Sorts;
        public IEnumerable<SortModel> MaterializedSorts => Sorts.Where(s => s.Materialized);
        public bool HasMaterializedSorts => Sorts.Any(s => s.Materialized);
        // ", object context = null" appended to the public write methods when
        // any hook takes the per-operation context; entities without such a
        // hook keep their signatures unchanged.
        public bool HasContextHooks => Extensions.Any(e => e.HasContext);
        public string ContextParam => HasContextHooks ? ", object context = null" : "";

        // Union interfaces this entity's ReadOnly implements (UseUnion).
        public List<string> UnionInterfaces = new();
        public string UnionInterfacesSuffix => UnionInterfaces.Count == 0 ? "" : ", " + string.Join(", ", UnionInterfaces);
    }

    internal sealed class SortModel
    {
        public string Name;         // method suffix ("ByNumber" or explicit)
        public PropModel Prop;      // the (cached) sort key property
        public bool Materialized;   // maintained sorted array vs specialized scan
        public string SelectorStruct => Name + "KeySelector";
    }

    internal sealed class ScalarDiffModel
    {
        public EntityModel Entity;
        public bool IsCommit; // commit transactions diff into a fresh updateEntity
    }

    internal sealed class NavDiffModel
    {
        public NavModel Nav;
        // Parent entity to attach when a child is added, so EF relationship
        // fixup can fill in the child's foreign key ("item", or the
        // key-only "updateEntity" stub in commit transactions).
        public string AttachTarget;
    }

    internal sealed class MtmDiffModel
    {
        public MtmModel Mtm;
        public string AddList; // list receiving newly added join entities
    }

    internal sealed class StoreModel
    {
        public string Namespace;
        public string StoreName;
        public string StoreType;
        public string ContextType;
        public List<EntityModel> Entities;
        public List<MtmJoinModel> CommitJoins;
        public List<ChildDtoModel> ChildDtos;
        // Distinct owned types across all entities (one wrapper each).
        public List<OwnedModel> OwnedDtos;
        public List<UnionModel> Unions;
    }

    internal static class StoreModelBuilder
    {
        public static StoreModel Build(StoreInfo store, List<DiagInfo> errors)
        {
            var model = new StoreModel
            {
                Namespace = store.Namespace,
                StoreName = store.StoreName,
                StoreType = store.StoreType,
                ContextType = store.ContextType,
                Entities = new List<EntityModel>(),
                CommitJoins = new List<MtmJoinModel>(),
                ChildDtos = new List<ChildDtoModel>(),
                OwnedDtos = new List<OwnedModel>(),
                Unions = new List<UnionModel>(),
            };

            // Group entities by ManyToMany join type, in encounter order.
            var joinOrder = new List<string>();
            var joinUsers = new Dictionary<string, List<EntityInfo>>();
            var joinInfo = new Dictionary<string, (EquatableArray<(string Name, string Type)> ChildProps,
                                                   EquatableArray<string> Keys,
                                                   EquatableArray<(string Name, string Type)> AllProps)>();
            foreach (var ei in store.Entities)
            {
                foreach (var (ct, navName, childProps, keys, allProps, loc) in ei.ManyToManys)
                {
                    if (joinUsers.TryGetValue(ct, out var users))
                    {
                        users.Add(ei);
                        continue;
                    }
                    joinOrder.Add(ct);
                    joinUsers.Add(ct, new List<EntityInfo> { ei });
                    joinInfo.Add(ct, (childProps, keys, allProps));
                }
            }

            // EntityCommitFunctions: one CreateOrUpdate/Delete pair per join type.
            foreach (var ct in joinOrder)
            {
                var (childProps, keys, allProps) = joinInfo[ct];
                var childName = ct.Split('.').Last();
                model.CommitJoins.Add(new MtmJoinModel
                {
                    StoreType = model.StoreType,
                    ChildType = ct,
                    ReadOnlyType = childName + "ReadOnly",
                    NormTypeName = ct.Replace('.', '_'),
                    KeyTupleTypes = "(" + string.Join(", ",
                        keys.Select(k => allProps.First(p => p.Name == k).Type)) + ")",
                    KeyTupleValues = "(" + string.Join(", ", keys.Select(k => "mtm." + k)) + ")",
                    ChildProps = childProps.Select(p => new PropModel(p.Name, p.Type)).ToList(),
                    Keys = keys.ToList(),
                    Targets = joinUsers[ct]
                        .Select(other => BuildTarget(other, keys, ct, errors))
                        .Where(t => t != null)
                        .ToList(),
                });
            }

            // Child ReadOnly DTOs: per entity, UseMany children first, then
            // ManyToMany join types (deduplicated across entities).
            var dtoDone = new HashSet<string>();
            foreach (var ei in store.Entities)
            {
                foreach (var (nav, ct, props, _, loc) in ei.ManyNavs)
                {
                    model.ChildDtos.Add(new ChildDtoModel
                    {
                        ClassName = ct.Split('.').Last(),
                        EntityType = ct,
                        Props = props.Select(p => new PropModel(p.Name, p.Type)).ToList(),
                    });
                }
                foreach (var (ct, navName, childProps, keys, allProps, loc) in ei.ManyToManys)
                {
                    if (!dtoDone.Add(ct))
                        continue;
                    model.ChildDtos.Add(new ChildDtoModel
                    {
                        ClassName = ct.Split('.').Last(),
                        EntityType = ct,
                        Props = allProps.Select(p => new PropModel(p.Name, p.Type)).ToList(),
                    });
                }
            }

            foreach (var ei in store.Entities)
            {
                var name = ei.EntityType.Split('.').Last();

                if (ei.SeqIndexes.Count > 1)
                    errors.Add(new DiagInfo(ErrorCodes.E5.ToString(), "Missing index",
                        $"Entity type {name} can only have one sequential index", ei.Location));

                // Without a sequential index, a single-property index must
                // exist to serve as the update key (composite indexes cannot
                // - their members are not a single lookup value).
                if (ei.SeqIndexes.Count != 1 && !ei.Indexes.Any(x => x.Props.Count == 1))
                    errors.Add(new DiagInfo(ErrorCodes.E5.ToString(), "Missing index",
                        $"Entity type {name} must have a sequential index or a single-property index (composite indexes cannot serve as the update key)", ei.Location));

                // A join type used by a single entity has no counterpart to
                // wire up; the diff predicates would be empty (invalid code).
                foreach (var m in ei.ManyToManys)
                {
                    if (joinUsers[m.JoinTypeName].Count < 2)
                        errors.Add(new DiagInfo(ErrorCodes.E7.ToString(), "Invalid ManyToMany",
                            $"ManyToMany join type {m.JoinTypeName} on entity {name} must be used by at least one other entity in the store", m.loc));
                }

                foreach (var duplicate in ei.Predicates.GroupBy(p => p.Name).Where(g => g.Count() > 1))
                {
                    errors.Add(new DiagInfo(ErrorCodes.E8.ToString(), "Invalid predicate",
                        $"Predicate name {duplicate.Key} on entity {name} is declared more than once", duplicate.Last().loc));
                }

                foreach (var duplicate in ei.Sorts.GroupBy(s => s.Name).Where(g => g.Count() > 1))
                {
                    errors.Add(new DiagInfo(ErrorCodes.E10.ToString(), "Invalid sort",
                        $"Sort name {duplicate.Key} on entity {name} is declared more than once", duplicate.Last().loc));
                }
                foreach (var s in ei.Sorts)
                {
                    // The selector struct reads the property off the ReadOnly
                    // wrapper, so the property must be cached; array-likes
                    // have no default ordering.
                    if (ei.Properties.All(p => p.Name != s.PropName))
                        errors.Add(new DiagInfo(ErrorCodes.E10.ToString(), "Invalid sort",
                            $"Sort property {s.PropName} on entity {name} must be a cached property", s.loc));
                    else if (s.PropType == IncrementalCacheGenerator.ByteArrayType || s.PropType == IncrementalCacheGenerator.CharArrayType
                        || new PropModel(s.PropName, s.PropType).IsScalarCollection)
                        errors.Add(new DiagInfo(ErrorCodes.E10.ToString(), "Invalid sort",
                            $"Sort property {s.PropName} on entity {name} cannot be an array or collection type", s.loc));
                }

                var properties = ei.Properties.Select(p => new PropModel(p.Name, p.Type)).ToList();
                var entity = new EntityModel
                {
                    Store = model,
                    Name = name,
                    EntityType = ei.EntityType,
                    CanCreate = ei.CanCreate,
                    CanUpdate = ei.CanUpdate,
                    CanDelete = ei.CanDelete,
                    Properties = properties,
                    // Only sequential-index props are excluded from update
                    // diffs (they are the identity and cannot change).
                    // Secondary index props diff like any other property, so
                    // a changed index value persists to the database - the
                    // cache-side key move is handled by the update templates.
                    NonIndexProperties = properties
                        .Where(p => ei.SeqIndexes.All(x => x.Name != p.Name))
                        .ToList(),
                    Indexes = ei.Indexes
                        .Select(i => new IndexModel
                        {
                            Name = i.Name,
                            Type = i.Type,
                            GenFunc = i.GenFunc,
                            GenArg = i.GenArg,
                            Props = i.Props.Select(p => new PropModel(p.Name, p.Type)).ToList(),
                        })
                        .ToList(),
                    SeqIndexes = ei.SeqIndexes
                        .Select(s => new SeqIndexModel { Name = s.Name, Type = s.Type, GenerateId = s.GenerateId })
                        .ToList(),
                    ManyNavs = ei.ManyNavs.Select(n => new NavModel
                    {
                        Nav = n.NavProp,
                        ChildType = n.ChildType,
                        ChildName = n.ChildType.Split('.').Last(),
                        ChildProps = n.ChildProps.Select(p => new PropModel(p.Name, p.Type)).ToList(),
                        KeyProps = n.KeyProp.ToList(),
                    }).ToList(),
                    ManyToManys = ei.ManyToManys.Select(m =>
                    {
                        var childName = m.JoinTypeName.Split('.').Last();
                        return new MtmModel
                        {
                            NavName = m.Name,
                            ChildType = m.JoinTypeName,
                            ChildName = childName,
                            ReadOnlyType = childName + "ReadOnly",
                            NormTypeName = m.JoinTypeName.Replace('.', '_'),
                            ChildProps = m.ChildProps.Select(p => new PropModel(p.Name, p.Type)).ToList(),
                            Keys = m.KeyProps.ToList(),
                            Others = joinUsers[m.JoinTypeName]
                                .Where(x => x != ei)
                                .Select(other => BuildTarget(other, m.KeyProps, m.JoinTypeName, errors))
                                .Where(t => t != null)
                                .ToList(),
                        };
                    }).ToList(),
                    Owneds = ei.Owneds.Select(o => new OwnedModel
                    {
                        Name = o.Name,
                        Type = o.Type,
                        TypeName = o.TypeName,
                        Members = o.Members.Select(m => new PropModel(m.Name, m.Type)).ToList(),
                    }).ToList(),
                    Extensions = ei.Extensions.Select(e => new ExtensionModel
                    {
                        Type = e.Type,
                        HasBeforeCreate = e.HasBeforeCreate,
                        HasBeforeUpdate = e.HasBeforeUpdate,
                        HasBeforeDelete = e.HasBeforeDelete,
                        ValuesArg = e.HasValues
                            ? ", new object[] { " + string.Join(", ", e.Props.Select(p => "item." + p)) + " }"
                            : "",
                        HasContext = e.HasContext,
                        ContextArg = e.HasContext ? ", context" : "",
                    }).ToList(),
                    Sorts = ei.Sorts.Select(s => new SortModel
                    {
                        Name = s.Name,
                        Prop = new PropModel(s.PropName, s.PropType),
                        Materialized = s.Materialized,
                    }).ToList(),
                    Predicates = ei.Predicates.Select(p =>
                    {
                        var args = p.Args.Select(a => new PropModel(a.Name, a.Type)).ToList();
                        var paramList = string.Join(", ", args.Select(a => a.Type + " " + a.Name));
                        return new PredicateModel
                        {
                            Name = p.Name,
                            StructName = p.Name + "Predicate",
                            EntityParam = p.EntityParam,
                            Body = p.Body,
                            Args = args,
                            ParamList = paramList,
                            ParamListLeading = args.Count != 0 ? paramList + ", " : "",
                            ArgNames = string.Join(", ", args.Select(a => a.Name)),
                        };
                    }).ToList(),
                };

                entity.SeqIndex = entity.SeqIndexes.Count == 1 ? entity.SeqIndexes[0] : null;
                // Snapshot reads: capture the published array/dictionary once.
                entity.QuerySource = entity.SeqIndex != null
                    ? "Volatile.Read(ref _seq_" + entity.SeqIndex.Name + ")"
                    : entity.Indexes.Count != 0 ? "Volatile.Read(ref _idx_" + entity.Indexes[0].Name + ").Values" : "";
                var updateKeyIndex = entity.Indexes.FirstOrDefault(x => !x.IsComposite);
                entity.UpdateKey = entity.SeqIndexes.Count != 0
                    ? entity.SeqIndexes[0].Name
                    : updateKeyIndex?.Name ?? "";
                entity.UpdateKeyType = entity.SeqIndexes.Count != 0
                    ? entity.SeqIndexes[0].Type
                    : updateKeyIndex?.Type ?? "";
                entity.CachesAllProperties = ei.CachesAllProperties;
                entity.RoCtorExtraParam = ei.CachesAllProperties ? "" : ", " + model.StoreType + " store";
                entity.RoCtorExtraArg = ei.CachesAllProperties ? "" : ", _store";
                entity.CopyOnInsert = ei.CopyOnInsert;
                entity.CopyOnStage = ei.CopyOnStage;
                entity.SeqIndexGuard = ei.SeqIndexGuard;
                entity.CloneProps = ei.AllProps.Select(p => new PropModel(p.Name, p.Type)).ToList();

                var projection = new List<PropModel>(properties);
                foreach (var s in entity.SeqIndexes)
                    if (projection.All(p => p.Name != s.Name))
                        projection.Add(new PropModel(s.Name, s.Type));
                foreach (var member in entity.Indexes.SelectMany(ix => ix.Props))
                    if (projection.All(p => p.Name != member.Name))
                        projection.Add(new PropModel(member.Name, member.Type));
                entity.ProjectionProps = projection;

                model.Entities.Add(entity);
            }

            // One ReadOnly wrapper per distinct owned type, shared across
            // entities and properties.
            var ownedDone = new HashSet<string>();
            foreach (var entity in model.Entities)
                foreach (var owned in entity.Owneds)
                    if (ownedDone.Add(owned.Type))
                        model.OwnedDtos.Add(owned);

            // Unions: resolve members and compute the common-property
            // intersection (scalars only; matched by name AND type).
            foreach (var (uname, memberTypes, uloc) in store.Unions)
            {
                var members = memberTypes
                    .Select(t => model.Entities.FirstOrDefault(e => e.EntityType == t))
                    .ToList();
                if (members.Any(m => m == null))
                    continue; // membership is validated at parse time

                var common = members[0].Properties
                    .Where(p => members.Skip(1).All(m => m.Properties.Any(q => q.Name == p.Name && q.Type == p.Type)))
                    .Select(p => new PropModel(p.Name, p.Type))
                    .ToList();
                if (common.Count == 0)
                {
                    errors.Add(new DiagInfo(ErrorCodes.E12.ToString(), "Invalid union",
                        $"Union {uname}'s member entities share no cached properties (matched by name and type); the union interface would be empty", uloc));
                    continue;
                }

                var union = new UnionModel { Name = uname, Members = members, CommonProps = common };
                model.Unions.Add(union);
                foreach (var m in members)
                    m.UnionInterfaces.Add(union.InterfaceName);
            }

            return model;
        }

        private static MtmOtherModel BuildTarget(EntityInfo other, EquatableArray<string> keys, string joinType,
            List<DiagInfo> errors)
        {
            // The join key prop referencing the other entity follows the
            // naming convention {EntityName}{IndexName}.
            var className = other.EntityType.Split('.').Last();
            var classProp = keys.FirstOrDefault(x => x.StartsWith(className, StringComparison.Ordinal));
            if (classProp == null)
            {
                errors.Add(new DiagInfo(ErrorCodes.E4.ToString(), "Missing class prop",
                    "Expected prop for other ManyToMany entity with name: " + className));
                return null;
            }

            var otherNav = other.ManyToManys
                .Where(x => x.JoinTypeName == joinType)
                .Select(x => x.Name)
                .FirstOrDefault();
            if (otherNav == null)
            {
                errors.Add(new DiagInfo(ErrorCodes.E4.ToString(), "Missing class prop",
                    "Expected prop nav with type: " + joinType));
                return null;
            }

            return new MtmOtherModel
            {
                DbSetProperty = other.DbSetProperty,
                ClassProp = classProp,
                IndexName = classProp.Substring(className.Length),
                OtherNavName = otherNav,
            };
        }
    }
}
