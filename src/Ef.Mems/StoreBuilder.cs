using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using System.Collections;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace Ef.Mems
{
    public class StoreBuilder<T> where T : DbContext, new()
    {
        public DataStore<T> Store { get; private set; }
        internal StoreBuilder(DataStore<T> store) { Store = store; }
        public EntityStoreBuilder<T, TEntity> UseEntity<TEntity>(Expression<Func<T, DbSet<TEntity>>> property) where TEntity : class, new()
        {
            return new EntityStoreBuilder<T, TEntity>(Store);
        }

        // Store-wide safety defaults, overridable per entity with the
        // EntityStoreBuilder overload. All features default to ON when no
        // UseSafety call is present. copyOnInsert clones entities entering
        // the cache on Create/Update (callers may keep mutating their
        // instance); copyOnStage snapshots staged transaction entities at
        // stage time; sequentialIndexGuard throws SparseSequentialIndex-
        // Exception when sequential keys stop being dense.
        public StoreBuilder<T> UseSafety(bool copyOnInsert = true, bool copyOnStage = true, bool sequentialIndexGuard = true)
        {
            return this;
        }

        // UseUnion declares a read-only union view over 2-4 already-built
        // member stores: the generator emits an I{Name}ReadOnly interface
        // (the intersection of the members' cached scalar properties, which
        // every member ReadOnly implements) and a {Name}Union class with
        // Query/QueryCount/QuerySort/FirstOrDefault/Count spanning all
        // members in declaration order. Pass the store fields assigned by the
        // members' Build() calls; the name must be a constant valid C#
        // identifier. Reads are stale-ok and not a consistent cross-store
        // snapshot.
        public UnionBuilder<T, TE1, TE2> UseUnion<TE1, TE2>(string name, EntityStore<TE1> first, EntityStore<TE2> second)
            where TE1 : class, new() where TE2 : class, new()
            => new UnionBuilder<T, TE1, TE2>(first, second);

        public UnionBuilder<T, TE1, TE2, TE3> UseUnion<TE1, TE2, TE3>(string name, EntityStore<TE1> first, EntityStore<TE2> second, EntityStore<TE3> third)
            where TE1 : class, new() where TE2 : class, new() where TE3 : class, new()
            => new UnionBuilder<T, TE1, TE2, TE3>(first, second, third);

        public UnionBuilder<T, TE1, TE2, TE3, TE4> UseUnion<TE1, TE2, TE3, TE4>(string name, EntityStore<TE1> first, EntityStore<TE2> second, EntityStore<TE3> third, EntityStore<TE4> fourth)
            where TE1 : class, new() where TE2 : class, new() where TE3 : class, new() where TE4 : class, new()
            => new UnionBuilder<T, TE1, TE2, TE3, TE4>(first, second, third, fourth);
    }

    // UseUnion markers: carry the member store instances to the generated
    // Build() extension, which dispatches on the member entity type tuple.
    public sealed class UnionBuilder<TContext, TE1, TE2>
        where TContext : DbContext, new()
        where TE1 : class, new() where TE2 : class, new()
    {
        public object First { get; }
        public object Second { get; }
        internal UnionBuilder(object first, object second) => (First, Second) = (first, second);
    }

    public sealed class UnionBuilder<TContext, TE1, TE2, TE3>
        where TContext : DbContext, new()
        where TE1 : class, new() where TE2 : class, new() where TE3 : class, new()
    {
        public object First { get; }
        public object Second { get; }
        public object Third { get; }
        internal UnionBuilder(object first, object second, object third) => (First, Second, Third) = (first, second, third);
    }

    public sealed class UnionBuilder<TContext, TE1, TE2, TE3, TE4>
        where TContext : DbContext, new()
        where TE1 : class, new() where TE2 : class, new() where TE3 : class, new() where TE4 : class, new()
    {
        public object First { get; }
        public object Second { get; }
        public object Third { get; }
        public object Fourth { get; }
        internal UnionBuilder(object first, object second, object third, object fourth) => (First, Second, Third, Fourth) = (first, second, third, fourth);
    }



    public class EntityStoreBuilder<TContext, TEntity> 
        where TContext : DbContext, new()
        where TEntity: class, new()
    {
        public DataStore<TContext> Store { get; private set; }

        internal EntityStoreBuilder(DataStore<TContext> store) { Store = store; }

        public EntityStoreBuilder<TContext, TEntity> UseProperty<TProp>(Expression<Func<TEntity, TProp>> property) 
        {
            return this;
        }

        public EntityStoreBuilder<TContext, TEntity> UseProperties(Expression<Func<TEntity, object>> propertySelectors)
        {            
            return this;
        }

        public EntityStoreBuilder<TContext, TEntity> UseAllProperties()
        {
            return this;
        }

        public EntityStoreBuilder<TContext, TEntity> UseIndex<TProp>(Expression<Func<TEntity, TProp>> index)
        {
            return this;
        }

        public EntityStoreBuilder<TContext, TEntity> UseIndex<TProp>(Expression<Func<TEntity, TProp>> index, Func<TProp> idGenerator)
        {
            return this;
        }

        //public EntityStoreBuilder<TContext, TEntity> UseIndex<TProp>(Expression<Func<TEntity, TProp>> index, Func<TEntity, TProp> idGenerator)
        //{
        //    return this;
        //}

        public EntityStoreBuilder<TContext, TEntity> UseSequentialIndex(Expression<Func<TEntity, long>> index, int minValue = 1, bool generateId = false)
        {
            return this;
        }

        public EntityStoreBuilder<TContext, TEntity> UseSequentialIndex(Expression<Func<TEntity, ulong>> index, int minValue = 1, bool generateId = false)
        {
            return this;
        }

        public EntityStoreBuilder<TContext, TEntity> UseMany<TProp>(Expression<Func<TEntity, IList<TProp>>> property, Expression<Func<TProp, object>> primaryKey) where TProp : class
        {
            return this;
        }

        // Declares an EF owned/complex member stored in the entity's own
        // table (OwnsOne/ComplexProperty). The owned instance is deep-copied
        // by the safety clones, exposed on the ReadOnly through a generated
        // {Type}ReadOnly wrapper, and diffed on update as a unit: when any
        // member differs (including null transitions) all of its columns are
        // written. Owned members must be EF scalars (no nesting), and the
        // property cannot be a collection (use UseMany) or an index/sort key.
        public EntityStoreBuilder<TContext, TEntity> UseOwned<TOwned>(Expression<Func<TEntity, TOwned>> property) where TOwned : class
        {
            return this;
        }

        public EntityStoreBuilder<TContext, TEntity> UseManyToMany<TJoin>(Expression<Func<TEntity, IList<TJoin>>> joinListNav, Expression<Func<TJoin, object>> joinKeyProps) where TJoin : class
        {
            return this;
        }

        public EntityStoreBuilder<TContext, TEntity> UseExtension(Type type)
        {
            return this;
        }

        // Configured extension: the selected property values are passed to
        // hooks declared as Before*(object dataStore, TEntity entity,
        // object[] values), e.g. SearchExtension rebuilding a SearchField.
        public EntityStoreBuilder<TContext, TEntity> UseExtension(Type type, Expression<Func<TEntity, object>> properties)
        {
            return this;
        }

        // UsePredicate declares a named query predicate. The generator lifts
        // the expression body into a struct implementing IPredicate<TReadOnly>
        // (extra lambda parameters become struct fields) and emits named
        // Query{Name}/QueryCount{Name}/QuerySortCount{Name}/FirstOrDefault{Name}
        // methods with JIT-specialized, allocation-free scan loops.
        // The predicate body may only reference its own parameters and types;
        // it must not capture locals or members of the enclosing store.

        public EntityStoreBuilder<TContext, TEntity> UsePredicate(string name, Expression<Func<TEntity, bool>> predicate)
        {
            return this;
        }

        public EntityStoreBuilder<TContext, TEntity> UsePredicate<TArg1>(string name, Expression<Func<TEntity, TArg1, bool>> predicate)
        {
            return this;
        }

        public EntityStoreBuilder<TContext, TEntity> UsePredicate<TArg1, TArg2>(string name, Expression<Func<TEntity, TArg1, TArg2, bool>> predicate)
        {
            return this;
        }

        public EntityStoreBuilder<TContext, TEntity> UsePredicate<TArg1, TArg2, TArg3>(string name, Expression<Func<TEntity, TArg1, TArg2, TArg3, bool>> predicate)
        {
            return this;
        }

        public EntityStoreBuilder<TContext, TEntity> UseFeatures(bool create = true, bool update = true, bool delete = true)
        {
            return this;
        }

        // Per-entity safety overrides; see StoreBuilder.UseSafety.
        public EntityStoreBuilder<TContext, TEntity> UseSafety(bool copyOnInsert = true, bool copyOnStage = true, bool sequentialIndexGuard = true)
        {
            return this;
        }

        // Declares a named sort: generates QuerySort{Name}/QuerySortCount{Name}
        // methods (predicate overloads + descending flag). The default name is
        // "By{Property}". materialized: false (default) backs the methods with
        // a JIT-specialized scan; materialized: true maintains a pre-sorted
        // snapshot array - microsecond windows, at the cost of an O(n) copy of
        // the sorted array on every write to this entity (use for read-mostly
        // tables). The sorted property must be cached.
        public EntityStoreBuilder<TContext, TEntity> UseSort<TProp>(Expression<Func<TEntity, TProp>> key, bool materialized = false)
        {
            return this;
        }

        public EntityStoreBuilder<TContext, TEntity> UseSort<TProp>(string name, Expression<Func<TEntity, TProp>> key, bool materialized = false)
        {
            return this;
        }

        /*public EntityStoreBuilder<TContext, TEntity> UseManyToMany<TProp>(Expression<Func<TEntity, TProp>> property, Expression<Func<TProp, object>> otherValues) where TProp : ICollection
        {
            return this;
        }

        public EntityStoreBuilder<TContext, TEntity> UseManyToOne<TProp, TKey>(Expression<Func<TEntity, TProp>> property, Expression<Func<TProp, TKey>> key) where TProp : ICollection
        {
            return this;
        }

        public EntityStoreBuilder<TContext, TEntity> UseOneToMany<TProp, TKey>(Expression<Func<TEntity, TProp>> property, Expression<Func<TProp, TKey>> key) where TKey : ICollection
        {
            return this;
        }

        public EntityStoreBuilder<TContext, TEntity> UseReference<TProp>(Expression<Func<TEntity, TProp>> property, Expression<Func<TProp, object>> otherValues)
        {
            return this;
        }*/


    }

    public static class PlaceholderExtensions
    {
        public static EntityStore<TEntity> Build<TContext, TEntity>(this EntityStoreBuilder<TContext, TEntity> builder)
            where TContext : DbContext, new()
            where TEntity : class, new()
        {
            throw new Exception("Potential code generation failure, ensure project references   'Ef.Mems.Generator'");
        }

        public static object Build<TContext, TE1, TE2>(this UnionBuilder<TContext, TE1, TE2> builder)
            where TContext : DbContext, new()
            where TE1 : class, new() where TE2 : class, new()
            => throw new Exception("Potential code generation failure, ensure project references 'Ef.Mems.Generator'");

        public static object Build<TContext, TE1, TE2, TE3>(this UnionBuilder<TContext, TE1, TE2, TE3> builder)
            where TContext : DbContext, new()
            where TE1 : class, new() where TE2 : class, new() where TE3 : class, new()
            => throw new Exception("Potential code generation failure, ensure project references 'Ef.Mems.Generator'");

        public static object Build<TContext, TE1, TE2, TE3, TE4>(this UnionBuilder<TContext, TE1, TE2, TE3, TE4> builder)
            where TContext : DbContext, new()
            where TE1 : class, new() where TE2 : class, new() where TE3 : class, new() where TE4 : class, new()
            => throw new Exception("Potential code generation failure, ensure project references 'Ef.Mems.Generator'");
    }

    public static class DbContextExtensions
    {
        public static EntityEntry<T> EnsureAttached<T>(this DbContext context, T entity) where T : class
        {
            var entry = context.Entry(entity);
            entry.State = EntityState.Unchanged;
            return entry;
        }
    }
}
