using Microsoft.EntityFrameworkCore;
using Proto.Promises;
using Proto.Promises.Threading;
using System;
using System.Buffers;
using System.Linq.Expressions;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

using ArgumentNullException = System.ArgumentNullException;

namespace Ef.Mems
{
    public abstract class DataStore<T> where T : DbContext, new()
    {
        protected DataStore(object initArg = null)
        {
            Initialize(initArg);

            //ensure migrated
            using var ctx = GetContext();
            ctx.Database.Migrate();
            if (ctx.Database.ProviderName == "Microsoft.EntityFrameworkCore.SqlServer")
            {
                ctx.Database.ExecuteSqlRaw("ALTER DATABASE SCOPED CONFIGURATION SET IDENTITY_CACHE = OFF");
            }
            var builder = new StoreBuilder<T>(this);
            ConstructStore(builder);
        }

        public EntityTransaction StartTransaction()
        {
            return new EntityTransaction(this, GetContext(), _transactionSemaphore);
        }
                
        public abstract T GetContext();
        protected abstract void ConstructStore(StoreBuilder<T> builder);
        protected virtual void Initialize(object arg)
        {
            //doesn't have to be implemented
        }

        private SemaphoreSlim _transactionSemaphore = new SemaphoreSlim(1, 1);       
    }

    public sealed class EntityTransaction : IDisposable
    {
        public EntityTransaction(object store, DbContext context, SemaphoreSlim semaphore)
        {
            _store = store;
            _context = context;
            _semaphore = semaphore;
        }

        private object _store;
        private DbContext _context;
        private SemaphoreSlim _semaphore;
        private bool _committed;

        /// <summary>Releases the transaction's context; safe to call after Commit.</summary>
        public void Dispose() => _context.Dispose();

        private List<(object entityStore, object obj)> _newObjects = new();
        private List<(object entityStore, object obj)> _updateObjects = new();
        private List<(object entityStore, object obj)> _deleteObjects = new();

        public void StageCreate<T>(object entityStore, ICollection<T> entities) where T : class
        {
            _newObjects.AddRange(entities.Select(x => (entityStore, x as object)));
        }

        public void StageCreate<T>(object entityStore, T entity) where T : class
        {
            _newObjects.AddRange((entityStore, entity as object));
        }

        public void StageUpdate<T>(object entityStore, ICollection<T> entities) where T : class
        {
            _updateObjects.AddRange(entities.Select(x => (entityStore, x as object)));
        }

        public void StageUpdate<T>(object entityStore, T entity) where T : class
        {
            _updateObjects.AddRange((entityStore, entity as object));
        }

        public void StageDelete<T>(object entityStore, ICollection<T> entities) where T : class
        {
            _deleteObjects.AddRange(entities.Select(x => (entityStore, x as object)));
        }

        public void StageDelete<T>(object entityStore, T entity) where T : class
        {
            _deleteObjects.AddRange((entityStore, entity as object));
        }

        public async Task Commit(object? context = null)
        {
            // One shot per transaction, even after a failed attempt: the
            // context may hold half-attached state from the failure, so a
            // retry must use a fresh transaction.
            if (_committed) throw new System.InvalidOperationException(
                "This transaction has already been committed (or a commit was attempted); start a new transaction");
            _committed = true;

            var gates = new List<(Promise.Deferred, Task, bool)>();
            var internals = new TransactionUpdateInterals();
            await _semaphore.WaitAsync();
            try
            {
                // One commit call per distinct store (first-encounter order):
                // the store applies its creates, updates and deletes under a
                // single writer-lock hold, so mixing operation kinds on one
                // store cannot deadlock on its own lock.
                var stores = new List<object>();
                void Collect(List<(object entityStore, object obj)> staged)
                {
                    foreach (var (entityStore, _) in staged)
                        if (!stores.Contains(entityStore))
                            stores.Add(entityStore);
                }
                Collect(_newObjects);
                Collect(_updateObjects);
                Collect(_deleteObjects);

                foreach (var storeObj in stores)
                {
                    var store = storeObj as IStageable;
                    var creates = _newObjects.Where(x => ReferenceEquals(x.entityStore, storeObj)).Select(x => x.obj).ToList();
                    var updates = _updateObjects.Where(x => ReferenceEquals(x.entityStore, storeObj)).Select(x => x.obj).ToList();
                    var deletes = _deleteObjects.Where(x => ReferenceEquals(x.entityStore, storeObj)).Select(x => x.obj).ToList();
                    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var gate = Promise.NewDeferred();

                    Task task;
                    try
                    {
                        task = store!.CommitTransaction(gate.Promise, _context, creates, updates, deletes, ready, internals, context);
                    }
                    catch (Exception ex)
                    {
                        gate.Reject(ex); //need to reject because this here, failed before adding to list
                        throw;
                    }

                    gates.Add((gate, task, false));

                    // A commit function faulting before it signals ready
                    // must surface as an exception, never a hang.
                    _ = task.ContinueWith(t =>
                    {
                        if (ready.Task.IsCompleted) return;

                        if (t.IsFaulted) ready.TrySetException(t.Exception!.GetBaseException());
                        else ready.TrySetException(new System.InvalidOperationException("Operation completed before signaling ready."));
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

                    await ready.Task;
                }
                await _context.SaveChangesAsync();

                // PushUpdates runs the ContinueWith actions (do not invoke
                // them here as well - they mutate Count and must run exactly
                // once) and then the staged ManyToMany commit functions.
                internals.PushUpdates(_store);

                for (int i = 0; i < gates.Count; i++)
                {
                    var gate = gates[i];
                    gate.Item1.Resolve();
                    await gate.Item2;
                    gates[i] = (gate.Item1, gate.Item2, true);
                }
            }
            catch (Exception ex)
            {
                foreach (var gate in gates)
                {
                    if (!gate.Item3)
                    {
                        gate.Item1.Reject(ex);
                    }
                }
                // Drain the commit tasks so none is still touching the
                // context when the caller disposes the transaction.
                foreach (var gate in gates)
                {
                    try { await gate.Item2; } catch { /* surfaced via ready/gate */ }
                }
                throw;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public readonly struct TransactionUpdateInterals
        {
            //used for tracking many/many inserts
            //(Action<object, object>, object) = (func(store, list), list)
            public TransactionUpdateInterals()
            {
            }

            public Dictionary<Type, (Action<object, object>, object)> CreateUpdateList { get; } = new();
            public Dictionary<Type, (Action<object, object>, object)> DeleteList { get; } = new();
            public List<Action> ContinueWith { get; } = new(16);

            public void InsertCreateOrUpdate<T>(Type type, ICollection<T> values, Action<object, object> action)
            {
                if (CreateUpdateList.TryGetValue(type, out var listObj))
                {
                    var list = listObj.Item2 as List<T>;
                    list.AddRange(values);
                }
                else
                {
                    CreateUpdateList.Add(type, (action, new List<T>(values)));
                }
            }

            // NOTE: the first insert for a type stores the caller's list
            // reference and later inserts AddRange into it. The contract is
            // that staged entities (and their join lists) are no longer used
            // by the caller once the transaction commits. A copy-on-insert
            // option may be added later.
            public void InsertCreateOrUpdate<T>(Type type, List<T> values, Action<object, object> action)
            {
                if (CreateUpdateList.TryGetValue(type, out var listObj))
                {
                    var list = listObj.Item2 as List<T>;
                    list.AddRange(values);
                }
                else
                {
                    CreateUpdateList.Add(type, (action, values));
                }
            }

            public void InsertDelete<T>(Type type, List<T> values, Action<object, object> action)
            {
                if (DeleteList.TryGetValue(type, out var listObj))
                {
                    var list = listObj.Item2 as List<T>;
                    list.AddRange(values);
                }
                else
                {
                    DeleteList.Add(type, (action, values));
                }
            }

            public void InsertDelete<T>(Type type, ICollection<T> values, Action<object, object> action)
            {
                if (DeleteList.TryGetValue(type, out var listObj))
                {
                    var list = listObj.Item2 as List<T>;
                    list.AddRange(values);
                }
                else
                {
                    DeleteList.Add(type, (action, new List<T>(values)));
                }
            }

            internal void PushUpdates(object store)
            {
                foreach (var cont in ContinueWith)
                {
                    cont();
                }

                foreach (var kvp in CreateUpdateList)
                {
                    var val = kvp.Value;
                    val.Item1.Invoke(store, val.Item2);
                }

                foreach (var kvp in DeleteList)
                {
                    var val = kvp.Value;
                    val.Item1.Invoke(store, val.Item2);
                }
            }
        }
    }

    public interface IStageable
    {
        // One commit per store and transaction: the store takes its writer
        // lock once, applies creates then updates then deletes under it,
        // signals ready, and parks at the gate until the transaction resolves
        // it after SaveChanges + PushUpdates. (One method instead of one per
        // operation kind, so mixing kinds on the same store cannot deadlock
        // on the store's own lock.)
        // context is the caller-supplied per-commit value handed to
        // context-taking extension hooks (Commit(context)); null when omitted.
        Task CommitTransaction(Promise gate, DbContext ctx,
            ICollection<object> creates, ICollection<object> updates, ICollection<object> deletes,
            TaskCompletionSource ready, EntityTransaction.TransactionUpdateInterals internals, object? context);
    }

    public abstract class EntityStore<TEntity> : IStageable where TEntity: class, new()
    {
        protected readonly AsyncReaderWriterLock _rwl = new AsyncReaderWriterLock();

        public virtual Task CommitTransaction(Promise gate, DbContext ctx,
            ICollection<object> creates, ICollection<object> updates, ICollection<object> deletes,
            TaskCompletionSource ready, EntityTransaction.TransactionUpdateInterals internals, object? context)
        {
            throw new NotImplementedException();
        }

        private struct Entry<T, TKey>
        {
            public T Item;
            public TKey Key;
        }

        // Sorts the final heap window without allocating a comparer.
        private readonly struct EntryComparer<T, TKey> : IComparer<Entry<T, TKey>>
        {
            private readonly IComparer<TKey> _cmp;
            private readonly int _dir;
            public EntryComparer(IComparer<TKey> cmp, int dir) { _cmp = cmp; _dir = dir; }
            public int Compare(Entry<T, TKey> x, Entry<T, TKey> y) => _cmp.Compare(x.Key, y.Key) * _dir;
        }

        private readonly struct EntryComparer<T, TKey, TCmp> : IComparer<Entry<T, TKey>>
            where TCmp : IComparer<TKey>
        {
            private readonly TCmp _cmp;
            public EntryComparer(TCmp cmp) { _cmp = cmp; }
            public int Compare(Entry<T, TKey> x, Entry<T, TKey> y) => _cmp.Compare(x.Key, y.Key);
        }

        private static void SiftUp<T, TKey>(Span<Entry<T, TKey>> h, ref int n, IComparer<TKey> cmp, int dir)
        {
            int i = n++;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (cmp.Compare(h[i].Key, h[p].Key) * dir > 0) { (h[i], h[p]) = (h[p], h[i]); i = p; }
                else break;
            }
        }

        private static void SiftDown<T, TKey>(Span<Entry<T, TKey>> h, int n, IComparer<TKey> cmp, int dir)
        {
            int i = 0;
            for (; ; )
            {
                int l = (i << 1) + 1; if (l >= n) break;
                int r = l + 1;
                int m = (r < n && cmp.Compare(h[r].Key, h[l].Key) * dir > 0) ? r : l;
                if (cmp.Compare(h[m].Key, h[i].Key) * dir > 0) { (h[i], h[m]) = (h[m], h[i]); i = m; }
                else break;
            }
        }

        // Generic-comparer sifts: direction is pre-folded into TCmp, and a
        // struct TCmp is inlined by generic specialization.
        private static void SiftUp<T, TKey, TCmp>(Span<Entry<T, TKey>> h, ref int n, TCmp cmp)
            where TCmp : IComparer<TKey>
        {
            int i = n++;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (cmp.Compare(h[i].Key, h[p].Key) > 0) { (h[i], h[p]) = (h[p], h[i]); i = p; }
                else break;
            }
        }

        private static void SiftDown<T, TKey, TCmp>(Span<Entry<T, TKey>> h, int n, TCmp cmp)
            where TCmp : IComparer<TKey>
        {
            int i = 0;
            for (; ; )
            {
                int l = (i << 1) + 1; if (l >= n) break;
                int r = l + 1;
                int m = (r < n && cmp.Compare(h[r].Key, h[l].Key) > 0) ? r : l;
                if (cmp.Compare(h[m].Key, h[i].Key) > 0) { (h[i], h[m]) = (h[m], h[i]); i = m; }
                else break;
            }
        }

        protected async ValueTask<(List<T> Items, int MatchCount)> QuerySortCount<T, TKey>(
        List<T> source,
        Predicate<T> predicate,
        Func<T, TKey> keySelector,
        int skip,
        int take,
        IComparer<TKey>? keyComparer = null,
        bool descending = false)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (predicate is null) throw new ArgumentNullException(nameof(predicate));
            if (keySelector is null) throw new ArgumentNullException(nameof(keySelector));
            if (skip < 0) skip = 0;

            if (take <= 0)
            {
                int matches = 0;
                var span0 = CollectionsMarshal.AsSpan(source);
                for (int i = 0; i < span0.Length; i++)
                {
                    var item = span0[i];
                    if (item != null && predicate(item)) matches++;
                }
                return new(new List<T>(0), matches);
            }

            var cmp = keyComparer ?? Comparer<TKey>.Default;
            int dir = descending ? -1 : 1;

            int window = skip == 0 ? take : Math.Min(source.Count, checked(skip + take));
            if (window == 0)
                return new(new List<T>(0), 0);

            var pool = ArrayPool<Entry<T, TKey>>.Shared;
            Entry<T, TKey>[] heap = pool.Rent(window);
            int count = 0, matchesAll = 0;

            try
            {
                using (await _rwl.ReaderLockAsync())
                {
                    var span = CollectionsMarshal.AsSpan(source);
                    for (int i = 0; i < span.Length; i++)
                    {
                        var item = span[i];
                        if (item == null || !predicate(item)) continue;

                        matchesAll++;

                        var e = new Entry<T, TKey> { Item = item, Key = keySelector(item) };

                        if (count < window)
                        {
                            heap[count] = e;
                            SiftUp<T, TKey>(heap, ref count, cmp, dir); //root holds the worst for final order
                            continue;
                        }

                        if (cmp.Compare(e.Key, heap[0].Key) * dir < 0)
                        {
                            heap[0] = e;
                            SiftDown<T, TKey>(heap, count, cmp, dir);
                        }
                    }

                    if (count == 0) return new(new List<T>(0), matchesAll);

                    heap.AsSpan(0, count).Sort(new EntryComparer<T, TKey>(cmp, dir));

                    int start = Math.Min(skip, count);
                    int outCount = Math.Min(take, count - start);

                    var result = new List<T>(outCount);
                    for (int i = 0; i < outCount; i++)
                        result.Add(heap[start + i].Item);

                    return new(result, matchesAll);
                }
            }
            finally
            {
                pool.Return(heap, clearArray: true);
            }
        }

        protected async ValueTask<(List<T> Items, int MatchCount)> QuerySortCount<T, TKey>(
        IEnumerable<T> source,
        Predicate<T> predicate,
        Func<T, TKey> keySelector,
        int skip,
        int take,
        IComparer<TKey>? keyComparer = null,
        bool descending = false)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (predicate is null) throw new ArgumentNullException(nameof(predicate));
            if (keySelector is null) throw new ArgumentNullException(nameof(keySelector));
            if (skip < 0) skip = 0;

            if (take <= 0)
            {
                int matches0 = 0;
                foreach (var x in source)
                {
                    if (x != null && predicate(x)) matches0++;
                }
                return new(new List<T>(0), matches0);
            }

            var cmp = keyComparer ?? Comparer<TKey>.Default;
            int dir = descending ? -1 : 1;

            int window = checked(skip + take);
            var pool = ArrayPool<Entry<T, TKey>>.Shared;
            Entry<T, TKey>[] heap = pool.Rent(window);
            int count = 0, matches = 0;

            try
            {
                using (await _rwl.ReaderLockAsync())
                {
                    foreach (var item in source)
                    {
                        if (item == null || !predicate(item)) continue;

                        matches++;

                        var e = new Entry<T, TKey> { Item = item, Key = keySelector(item) };

                        if (count < window)
                        {
                            heap[count] = e;
                            SiftUp<T, TKey>(heap, ref count, cmp, dir);
                            continue;
                        }

                        if (cmp.Compare(e.Key, heap[0].Key) * dir < 0)
                        {
                            heap[0] = e;
                            SiftDown<T, TKey>(heap, count, cmp, dir);
                        }
                    }

                    if (count == 0) return new(new List<T>(0), matches);

                    heap.AsSpan(0, count).Sort(new EntryComparer<T, TKey>(cmp, dir));

                    int start = Math.Min(skip, count);
                    int outCount = Math.Min(take, count - start);

                    var result = new List<T>(outCount);
                    for (int i = 0; i < outCount; i++)
                        result.Add(heap[start + i].Item);

                    return new(result, matches);
                }
            }
            finally
            {
                pool.Return(heap, clearArray: true);
            }
        }


        protected async ValueTask<(List<T> Items, int MatchCount)> QueryCount<T>(
            List<T> source,
            Predicate<T> predicate,
            int skip,
            int take)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (predicate is null) throw new ArgumentNullException(nameof(predicate));
            if (skip < 0) skip = 0;

            int matches = 0;
            List<T> result;

            using (await _rwl.ReaderLockAsync())
            {
                var span = CollectionsMarshal.AsSpan(source);
                int len = span.Length;
                result = take > 0 ? new List<T>(Math.Min(take, len)) : new List<T>(0);

                //skip
                int i = 0, skipped = 0;
                for (; i < len && skipped < skip; i++)
                {
                    var x = span[i];
                    if (x != null && predicate(x)) { skipped++; matches++; }
                }

                //fill
                for (; i < len && result.Count < take; i++)
                {
                    var x = span[i];
                    if (x != null && predicate(x)) { result.Add(x); matches++; }
                }

                //tail
                for (; i < len; i++)
                {
                    var x = span[i];
                    if (x != null && predicate(x)) matches++;
                }
            }

            return (result, matches);
        }

        protected async ValueTask<(List<T> Items, int MatchCount)> QueryCount<T>(
            IEnumerable<T> source,
            Predicate<T> predicate,
            int skip,
            int take)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (predicate is null) throw new ArgumentNullException(nameof(predicate));
            if (skip < 0) skip = 0;

            int matches = 0;
            List<T> result;

            using (await _rwl.ReaderLockAsync())
            {
                int capacity = 0;
                if (take > 0)
                {
                    if (source is ICollection<T> coll)
                        capacity = Math.Min(take, Math.Max(0, coll.Count - Math.Min(skip, coll.Count)));
                    else if (source is IReadOnlyCollection<T> ro)
                        capacity = Math.Min(take, Math.Max(0, ro.Count - Math.Min(skip, ro.Count)));
                    else
                        capacity = take; // best guess
                }
                result = new List<T>(capacity);

                using var e = source.GetEnumerator();

                // Phase 1: skip matches
                int skipped = 0;
                while (skipped < skip && e.MoveNext())
                {
                    var x = e.Current;
                    if (x is not null && predicate(x)) { skipped++; matches++; }
                }

                // Phase 2: take matches
                while (result.Count < take && e.MoveNext())
                {
                    var x = e.Current;
                    if (x is not null && predicate(x)) { result.Add(x); matches++; }
                }

                // Phase 3: tail — count only
                while (e.MoveNext())
                {
                    var x = e.Current;
                    if (x is not null && predicate(x)) matches++;
                }
            }

            return (result, matches);
        }

        protected async ValueTask<List<T>> Query<T>(
            List<T> source,
            Predicate<T> predicate,
            int skip,
            int take)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (predicate is null) throw new ArgumentNullException(nameof(predicate));
            if (take <= 0) return new List<T>(0);
            if (skip < 0) skip = 0;

            using (await _rwl.ReaderLockAsync())
            {
                var result = new List<T>(Math.Min(take, source.Count));
                var span = CollectionsMarshal.AsSpan(source);
                int len = span.Length;

                //skip
                int i = 0, skipped = 0;
                for (; i < len && skipped < skip; i++)
                {
                    var x = span[i];
                    if (x != null && predicate(x)) { skipped++; }
                }

                //fill
                for (; i < len; i++)
                {
                    var item = span[i];
                    if (item == null || !predicate(item)) continue;
                    result.Add(item);
                    if (result.Count == take) break;
                }

                return result;
            }
        }

        protected async ValueTask<List<T>> Query<T>(
            List<T> source,
            Predicate<T> predicate)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (predicate is null) throw new ArgumentNullException(nameof(predicate));

            using (await _rwl.ReaderLockAsync())
            {
                var result = new List<T>(32); //32 tmp placeholder, idk maybe relate to total source count?
                var span = CollectionsMarshal.AsSpan(source);
                int len = span.Length;

                //fill
                for (int i = 0; i < len; i++)
                {
                    var item = span[i];
                    if (item == null || !predicate(item)) continue;
                    result.Add(item);
                }

                return result;
            }
        }

        protected async ValueTask<List<T>> Query<T>(
           List<T> source,
           int skip,
           int take)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (take <= 0) return new List<T>(0);
            if (skip < 0) skip = 0;

            using (await _rwl.ReaderLockAsync())
            {
                var result = new List<T>(Math.Min(take, source.Count));

                int skipped = 0;
                var span = CollectionsMarshal.AsSpan(source);

                for (int i = 0; i < span.Length; i++)
                {
                    var item = span[i];
                    if (item == null) continue;

                    if (skipped < skip)
                    {
                        skipped++;
                        continue;
                    }

                    result.Add(item);
                    if (result.Count == take) break;
                }

                return result;
            }
        }

        protected async ValueTask<List<T>> Query<T>(
           List<T> source)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));

            using (await _rwl.ReaderLockAsync())
            {
                var result = new List<T>(source.Count);

                int skipped = 0;
                var span = CollectionsMarshal.AsSpan(source);

                for (int i = 0; i < span.Length; i++)
                {
                    var item = span[i];
                    if (item == null) continue;

                    result.Add(item);
                }

                return result;
            }
        }

        protected async ValueTask<T> FirstOrDefault<T>(
           List<T> source,
           Predicate<T> predicate) where T : class
        {
            if (source is null) throw new ArgumentNullException(nameof(source));

            using (await _rwl.ReaderLockAsync())
            {
                var span = CollectionsMarshal.AsSpan(source);

                for (int i = 0; i < span.Length; i++)
                {
                    var item = span[i];
                    if (item == null || !predicate(item)) continue;
                    return item;
                }
            }
            return null;
        }

        // -----------------------------------------------------------------
        // Array snapshot overloads.
        //
        // The generated stores publish their sequential indexes as arrays via
        // Volatile.Write and only ever mutate published arrays with atomic
        // reference stores into slots (see Ef.Mems.Snapshot). Readers can
        // therefore run without the reader lock: they may observe slightly
        // stale slots, but never torn state.
        // -----------------------------------------------------------------

        protected ValueTask<(List<T> Items, int MatchCount)> QuerySortCount<T, TKey>(
        T[] source,
        Predicate<T> predicate,
        Func<T, TKey> keySelector,
        int skip,
        int take,
        IComparer<TKey>? keyComparer = null,
        bool descending = false)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (predicate is null) throw new ArgumentNullException(nameof(predicate));
            if (keySelector is null) throw new ArgumentNullException(nameof(keySelector));
            if (skip < 0) skip = 0;

            if (take <= 0)
            {
                int matches = 0;
                for (int i = 0; i < source.Length; i++)
                {
                    var item = source[i];
                    if (item != null && predicate(item)) matches++;
                }
                return new((new List<T>(0), matches));
            }

            var cmp = keyComparer ?? Comparer<TKey>.Default;
            int dir = descending ? -1 : 1;

            int window = skip == 0 ? take : Math.Min(source.Length, checked(skip + take));
            if (window == 0)
                return new((new List<T>(0), 0));

            var pool = ArrayPool<Entry<T, TKey>>.Shared;
            Entry<T, TKey>[] heap = pool.Rent(window);
            int count = 0, matchesAll = 0;

            try
            {
                for (int i = 0; i < source.Length; i++)
                {
                    var item = source[i];
                    if (item == null || !predicate(item)) continue;

                    matchesAll++;

                    var e = new Entry<T, TKey> { Item = item, Key = keySelector(item) };

                    if (count < window)
                    {
                        heap[count] = e;
                        SiftUp<T, TKey>(heap, ref count, cmp, dir); //root holds the worst for final order
                        continue;
                    }

                    if (cmp.Compare(e.Key, heap[0].Key) * dir < 0)
                    {
                        heap[0] = e;
                        SiftDown<T, TKey>(heap, count, cmp, dir);
                    }
                }

                if (count == 0) return new((new List<T>(0), matchesAll));

                heap.AsSpan(0, count).Sort(new EntryComparer<T, TKey>(cmp, dir));

                int start = Math.Min(skip, count);
                int outCount = Math.Min(take, count - start);

                var result = new List<T>(outCount);
                for (int i = 0; i < outCount; i++)
                    result.Add(heap[start + i].Item);

                return new((result, matchesAll));
            }
            finally
            {
                pool.Return(heap, clearArray: true);
            }
        }

        // descending walks the array back to front: for sequential-index
        // sources (slot == key) that is exactly "newest first", making the
        // common "latest N" query an early-exit scan instead of a sort.
        protected ValueTask<(List<T> Items, int MatchCount)> QueryCount<T>(
            T[] source,
            Predicate<T> predicate,
            int skip,
            int take,
            bool descending = false)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (predicate is null) throw new ArgumentNullException(nameof(predicate));
            if (skip < 0) skip = 0;

            int matches = 0;
            int len = source.Length;
            int last = len - 1;
            var result = take > 0 ? new List<T>(Math.Min(take, len)) : new List<T>(0);

            //skip
            int i = 0, skipped = 0;
            for (; i < len && skipped < skip; i++)
            {
                var x = source[descending ? last - i : i];
                if (x != null && predicate(x)) { skipped++; matches++; }
            }

            //fill
            for (; i < len && result.Count < take; i++)
            {
                var x = source[descending ? last - i : i];
                if (x != null && predicate(x)) { result.Add(x); matches++; }
            }

            //tail
            for (; i < len; i++)
            {
                var x = source[descending ? last - i : i];
                if (x != null && predicate(x)) matches++;
            }

            return new((result, matches));
        }

        protected ValueTask<List<T>> Query<T>(
            T[] source,
            Predicate<T> predicate,
            int skip,
            int take,
            bool descending = false)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (predicate is null) throw new ArgumentNullException(nameof(predicate));
            if (take <= 0) return new(new List<T>(0));
            if (skip < 0) skip = 0;

            var result = new List<T>(Math.Min(take, source.Length));
            int len = source.Length;
            int last = len - 1;

            //skip
            int i = 0, skipped = 0;
            for (; i < len && skipped < skip; i++)
            {
                var x = source[descending ? last - i : i];
                if (x != null && predicate(x)) { skipped++; }
            }

            //fill
            for (; i < len; i++)
            {
                var item = source[descending ? last - i : i];
                if (item == null || !predicate(item)) continue;
                result.Add(item);
                if (result.Count == take) break;
            }

            return new(result);
        }

        protected ValueTask<List<T>> Query<T>(
            T[] source,
            Predicate<T> predicate)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (predicate is null) throw new ArgumentNullException(nameof(predicate));

            var result = new List<T>(32);
            int len = source.Length;

            //fill
            for (int i = 0; i < len; i++)
            {
                var item = source[i];
                if (item == null || !predicate(item)) continue;
                result.Add(item);
            }

            return new(result);
        }

        protected ValueTask<List<T>> Query<T>(
           T[] source,
           int skip,
           int take,
           bool descending = false)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (take <= 0) return new(new List<T>(0));
            if (skip < 0) skip = 0;

            var result = new List<T>(Math.Min(take, source.Length));
            int len = source.Length;
            int last = len - 1;

            int skipped = 0;
            for (int i = 0; i < len; i++)
            {
                var item = source[descending ? last - i : i];
                if (item == null) continue;

                if (skipped < skip)
                {
                    skipped++;
                    continue;
                }

                result.Add(item);
                if (result.Count == take) break;
            }

            return new(result);
        }

        protected ValueTask<List<T>> Query<T>(
           T[] source)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));

            var result = new List<T>(source.Length);

            for (int i = 0; i < source.Length; i++)
            {
                var item = source[i];
                if (item == null) continue;

                result.Add(item);
            }

            return new(result);
        }

        protected ValueTask<T?> FirstOrDefault<T>(
           T[] source,
           Predicate<T> predicate,
           bool descending = false) where T : class
        {
            if (source is null) throw new ArgumentNullException(nameof(source));

            int len = source.Length;
            int last = len - 1;
            for (int i = 0; i < len; i++)
            {
                var item = source[descending ? last - i : i];
                if (item == null || !predicate(item)) continue;
                return new(item);
            }

            return new((T?)null);
        }

        // -----------------------------------------------------------------
        // Struct predicate overloads (see Ef.Mems.IPredicate<T>).
        //
        // TPred is a value type, so each predicate type gets its own
        // JIT-specialized scan loop with Invoke inlined - no delegate
        // indirection, no closure allocations. The Predicate<T> overloads
        // above remain available for ad-hoc/dynamic queries.
        // -----------------------------------------------------------------

        protected ValueTask<(List<T> Items, int MatchCount)> QuerySortCount<T, TKey, TPred>(
        T[] source,
        TPred predicate,
        Func<T, TKey> keySelector,
        int skip,
        int take,
        IComparer<TKey>? keyComparer = null,
        bool descending = false)
            where TPred : struct, IPredicate<T>
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (keySelector is null) throw new ArgumentNullException(nameof(keySelector));
            if (skip < 0) skip = 0;

            if (take <= 0)
            {
                int matches = 0;
                for (int i = 0; i < source.Length; i++)
                {
                    var item = source[i];
                    if (item != null && predicate.Invoke(item)) matches++;
                }
                return new((new List<T>(0), matches));
            }

            var cmp = keyComparer ?? Comparer<TKey>.Default;
            int dir = descending ? -1 : 1;

            int window = skip == 0 ? take : Math.Min(source.Length, checked(skip + take));
            if (window == 0)
                return new((new List<T>(0), 0));

            var pool = ArrayPool<Entry<T, TKey>>.Shared;
            Entry<T, TKey>[] heap = pool.Rent(window);
            int count = 0, matchesAll = 0;

            try
            {
                for (int i = 0; i < source.Length; i++)
                {
                    var item = source[i];
                    if (item == null || !predicate.Invoke(item)) continue;

                    matchesAll++;

                    var e = new Entry<T, TKey> { Item = item, Key = keySelector(item) };

                    if (count < window)
                    {
                        heap[count] = e;
                        SiftUp<T, TKey>(heap, ref count, cmp, dir); //root holds the worst for final order
                        continue;
                    }

                    if (cmp.Compare(e.Key, heap[0].Key) * dir < 0)
                    {
                        heap[0] = e;
                        SiftDown<T, TKey>(heap, count, cmp, dir);
                    }
                }

                if (count == 0) return new((new List<T>(0), matchesAll));

                heap.AsSpan(0, count).Sort(new EntryComparer<T, TKey>(cmp, dir));

                int start = Math.Min(skip, count);
                int outCount = Math.Min(take, count - start);

                var result = new List<T>(outCount);
                for (int i = 0; i < outCount; i++)
                    result.Add(heap[start + i].Item);

                return new((result, matchesAll));
            }
            finally
            {
                pool.Return(heap, clearArray: true);
            }
        }

        // Fully JIT-specialized sorted query: struct predicate, struct key
        // selector and struct comparer are all inlined by generic
        // specialization; sort direction is folded into the comparer once.
        protected ValueTask<(List<T> Items, int MatchCount)> QuerySortCount<T, TKey, TPred, TSel, TCmp>(
        T[] source,
        TPred predicate,
        TSel keySelector,
        TCmp keyComparer,
        int skip,
        int take,
        bool descending = false)
            where TPred : struct, IPredicate<T>
            where TSel : struct, IKeySelector<T, TKey>
            where TCmp : struct, IComparer<TKey>
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (skip < 0) skip = 0;

            if (take <= 0)
            {
                int matches = 0;
                for (int i = 0; i < source.Length; i++)
                {
                    var item = source[i];
                    if (item != null && predicate.Invoke(item)) matches++;
                }
                return new((new List<T>(0), matches));
            }

            var cmp = new DirectionalComparer<TKey, TCmp>(keyComparer, descending);

            int window = skip == 0 ? take : Math.Min(source.Length, checked(skip + take));
            if (window == 0)
                return new((new List<T>(0), 0));

            var pool = ArrayPool<Entry<T, TKey>>.Shared;
            Entry<T, TKey>[] heap = pool.Rent(window);
            int count = 0, matchesAll = 0;

            try
            {
                for (int i = 0; i < source.Length; i++)
                {
                    var item = source[i];
                    if (item == null || !predicate.Invoke(item)) continue;

                    matchesAll++;

                    var e = new Entry<T, TKey> { Item = item, Key = keySelector.Key(item) };

                    if (count < window)
                    {
                        heap[count] = e;
                        SiftUp<T, TKey, DirectionalComparer<TKey, TCmp>>(heap, ref count, cmp); //root holds the worst for final order
                        continue;
                    }

                    if (cmp.Compare(e.Key, heap[0].Key) < 0)
                    {
                        heap[0] = e;
                        SiftDown<T, TKey, DirectionalComparer<TKey, TCmp>>(heap, count, cmp);
                    }
                }

                if (count == 0) return new((new List<T>(0), matchesAll));

                heap.AsSpan(0, count).Sort(new EntryComparer<T, TKey, DirectionalComparer<TKey, TCmp>>(cmp));

                int start = Math.Min(skip, count);
                int outCount = Math.Min(take, count - start);

                var result = new List<T>(outCount);
                for (int i = 0; i < outCount; i++)
                    result.Add(heap[start + i].Item);

                return new((result, matchesAll));
            }
            finally
            {
                pool.Return(heap, clearArray: true);
            }
        }

        protected async ValueTask<(List<T> Items, int MatchCount)> QuerySortCount<T, TKey, TPred, TSel, TCmp>(
        IEnumerable<T> source,
        TPred predicate,
        TSel keySelector,
        TCmp keyComparer,
        int skip,
        int take,
        bool descending = false)
            where TPred : struct, IPredicate<T>
            where TSel : struct, IKeySelector<T, TKey>
            where TCmp : struct, IComparer<TKey>
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (skip < 0) skip = 0;

            if (take <= 0)
            {
                int matches0 = 0;
                foreach (var x in source)
                {
                    if (x != null && predicate.Invoke(x)) matches0++;
                }
                return new(new List<T>(0), matches0);
            }

            var cmp = new DirectionalComparer<TKey, TCmp>(keyComparer, descending);

            int window = checked(skip + take);
            var pool = ArrayPool<Entry<T, TKey>>.Shared;
            Entry<T, TKey>[] heap = pool.Rent(window);
            int count = 0, matches = 0;

            try
            {
                using (await _rwl.ReaderLockAsync())
                {
                    foreach (var item in source)
                    {
                        if (item == null || !predicate.Invoke(item)) continue;

                        matches++;

                        var e = new Entry<T, TKey> { Item = item, Key = keySelector.Key(item) };

                        if (count < window)
                        {
                            heap[count] = e;
                            SiftUp<T, TKey, DirectionalComparer<TKey, TCmp>>(heap, ref count, cmp);
                            continue;
                        }

                        if (cmp.Compare(e.Key, heap[0].Key) < 0)
                        {
                            heap[0] = e;
                            SiftDown<T, TKey, DirectionalComparer<TKey, TCmp>>(heap, count, cmp);
                        }
                    }

                    if (count == 0) return new(new List<T>(0), matches);

                    heap.AsSpan(0, count).Sort(new EntryComparer<T, TKey, DirectionalComparer<TKey, TCmp>>(cmp));

                    int start = Math.Min(skip, count);
                    int outCount = Math.Min(take, count - start);

                    var result = new List<T>(outCount);
                    for (int i = 0; i < outCount; i++)
                        result.Add(heap[start + i].Item);

                    return new(result, matches);
                }
            }
            finally
            {
                pool.Return(heap, clearArray: true);
            }
        }

        protected ValueTask<(List<T> Items, int MatchCount)> QueryCount<T, TPred>(
            T[] source,
            TPred predicate,
            int skip,
            int take,
            bool descending = false)
            where TPred : struct, IPredicate<T>
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (skip < 0) skip = 0;

            int matches = 0;
            int len = source.Length;
            int last = len - 1;
            var result = take > 0 ? new List<T>(Math.Min(take, len)) : new List<T>(0);

            //skip
            int i = 0, skipped = 0;
            for (; i < len && skipped < skip; i++)
            {
                var x = source[descending ? last - i : i];
                if (x != null && predicate.Invoke(x)) { skipped++; matches++; }
            }

            //fill
            for (; i < len && result.Count < take; i++)
            {
                var x = source[descending ? last - i : i];
                if (x != null && predicate.Invoke(x)) { result.Add(x); matches++; }
            }

            //tail
            for (; i < len; i++)
            {
                var x = source[descending ? last - i : i];
                if (x != null && predicate.Invoke(x)) matches++;
            }

            return new((result, matches));
        }

        protected ValueTask<List<T>> Query<T, TPred>(
            T[] source,
            TPred predicate,
            int skip,
            int take,
            bool descending = false)
            where TPred : struct, IPredicate<T>
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (take <= 0) return new(new List<T>(0));
            if (skip < 0) skip = 0;

            var result = new List<T>(Math.Min(take, source.Length));
            int len = source.Length;
            int last = len - 1;

            //skip
            int i = 0, skipped = 0;
            for (; i < len && skipped < skip; i++)
            {
                var x = source[descending ? last - i : i];
                if (x != null && predicate.Invoke(x)) { skipped++; }
            }

            //fill
            for (; i < len; i++)
            {
                var item = source[descending ? last - i : i];
                if (item == null || !predicate.Invoke(item)) continue;
                result.Add(item);
                if (result.Count == take) break;
            }

            return new(result);
        }

        protected ValueTask<List<T>> Query<T, TPred>(
            T[] source,
            TPred predicate)
            where TPred : struct, IPredicate<T>
        {
            if (source is null) throw new ArgumentNullException(nameof(source));

            var result = new List<T>(32);
            int len = source.Length;

            //fill
            for (int i = 0; i < len; i++)
            {
                var item = source[i];
                if (item == null || !predicate.Invoke(item)) continue;
                result.Add(item);
            }

            return new(result);
        }

        protected ValueTask<T?> FirstOrDefault<T, TPred>(
           T[] source,
           TPred predicate,
           bool descending = false)
            where T : class
            where TPred : struct, IPredicate<T>
        {
            if (source is null) throw new ArgumentNullException(nameof(source));

            int len = source.Length;
            int last = len - 1;
            for (int i = 0; i < len; i++)
            {
                var item = source[descending ? last - i : i];
                if (item == null || !predicate.Invoke(item)) continue;
                return new(item);
            }

            return new((T?)null);
        }

        // Scan-backed named sorts with a delegate predicate: struct selector
        // and comparer stay specialized, only the predicate is a delegate.
        protected ValueTask<(List<T> Items, int MatchCount)> QuerySortCount<T, TKey, TSel, TCmp>(
        T[] source,
        Predicate<T> predicate,
        TSel keySelector,
        TCmp keyComparer,
        int skip,
        int take,
        bool descending = false)
            where TSel : struct, IKeySelector<T, TKey>
            where TCmp : struct, IComparer<TKey>
        {
            if (predicate is null) throw new ArgumentNullException(nameof(predicate));
            return QuerySortCount<T, TKey, DelegatePredicate<T>, TSel, TCmp>(
                source, new DelegatePredicate<T>(predicate), keySelector, keyComparer, skip, take, descending);
        }

        protected ValueTask<(List<T> Items, int MatchCount)> QuerySortCount<T, TKey, TSel, TCmp>(
        IEnumerable<T> source,
        Predicate<T> predicate,
        TSel keySelector,
        TCmp keyComparer,
        int skip,
        int take,
        bool descending = false)
            where TSel : struct, IKeySelector<T, TKey>
            where TCmp : struct, IComparer<TKey>
        {
            if (predicate is null) throw new ArgumentNullException(nameof(predicate));
            return QuerySortCount<T, TKey, DelegatePredicate<T>, TSel, TCmp>(
                source, new DelegatePredicate<T>(predicate), keySelector, keyComparer, skip, take, descending);
        }

        // -----------------------------------------------------------------
        // Materialized sort walks: the source array is maintained pre-sorted
        // by the generated store (published clone-and-swap like every other
        // snapshot), so a window is a filtered walk - early-exit when no
        // MatchCount is requested.

        protected ValueTask<List<T>> SortedQuery<T, TPred>(
            T[] sorted,
            TPred predicate,
            int skip,
            int take,
            bool descending = false)
            where TPred : struct, IPredicate<T>
            => Query(sorted, predicate, skip, take, descending);

        protected ValueTask<List<T>> SortedQuery<T>(
            T[] sorted,
            Predicate<T> predicate,
            int skip,
            int take,
            bool descending = false)
            => Query(sorted, predicate, skip, take, descending);

        protected ValueTask<(List<T> Items, int MatchCount)> SortedQueryCount<T, TPred>(
            T[] sorted,
            TPred predicate,
            int skip,
            int take,
            bool descending = false)
            where TPred : struct, IPredicate<T>
            => QueryCount(sorted, predicate, skip, take, descending);

        protected ValueTask<(List<T> Items, int MatchCount)> SortedQueryCount<T>(
            T[] sorted,
            Predicate<T> predicate,
            int skip,
            int take,
            bool descending = false)
            => QueryCount(sorted, predicate, skip, take, descending);

        // Struct predicate overloads for dictionary-backed (IEnumerable)
        // sources; these still take the reader lock like their Predicate<T>
        // counterparts.

        protected async ValueTask<(List<T> Items, int MatchCount)> QuerySortCount<T, TKey, TPred>(
        IEnumerable<T> source,
        TPred predicate,
        Func<T, TKey> keySelector,
        int skip,
        int take,
        IComparer<TKey>? keyComparer = null,
        bool descending = false)
            where TPred : struct, IPredicate<T>
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (keySelector is null) throw new ArgumentNullException(nameof(keySelector));
            if (skip < 0) skip = 0;

            if (take <= 0)
            {
                int matches0 = 0;
                foreach (var x in source)
                {
                    if (x != null && predicate.Invoke(x)) matches0++;
                }
                return new(new List<T>(0), matches0);
            }

            var cmp = keyComparer ?? Comparer<TKey>.Default;
            int dir = descending ? -1 : 1;

            int window = checked(skip + take);
            var pool = ArrayPool<Entry<T, TKey>>.Shared;
            Entry<T, TKey>[] heap = pool.Rent(window);
            int count = 0, matches = 0;

            try
            {
                using (await _rwl.ReaderLockAsync())
                {
                    foreach (var item in source)
                    {
                        if (item == null || !predicate.Invoke(item)) continue;

                        matches++;

                        var e = new Entry<T, TKey> { Item = item, Key = keySelector(item) };

                        if (count < window)
                        {
                            heap[count] = e;
                            SiftUp<T, TKey>(heap, ref count, cmp, dir);
                            continue;
                        }

                        if (cmp.Compare(e.Key, heap[0].Key) * dir < 0)
                        {
                            heap[0] = e;
                            SiftDown<T, TKey>(heap, count, cmp, dir);
                        }
                    }

                    if (count == 0) return new(new List<T>(0), matches);

                    heap.AsSpan(0, count).Sort(new EntryComparer<T, TKey>(cmp, dir));

                    int start = Math.Min(skip, count);
                    int outCount = Math.Min(take, count - start);

                    var result = new List<T>(outCount);
                    for (int i = 0; i < outCount; i++)
                        result.Add(heap[start + i].Item);

                    return new(result, matches);
                }
            }
            finally
            {
                pool.Return(heap, clearArray: true);
            }
        }

        protected async ValueTask<(List<T> Items, int MatchCount)> QueryCount<T, TPred>(
            IEnumerable<T> source,
            TPred predicate,
            int skip,
            int take)
            where TPred : struct, IPredicate<T>
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (skip < 0) skip = 0;

            int matches = 0;
            List<T> result;

            using (await _rwl.ReaderLockAsync())
            {
                int capacity = 0;
                if (take > 0)
                {
                    if (source is ICollection<T> coll)
                        capacity = Math.Min(take, Math.Max(0, coll.Count - Math.Min(skip, coll.Count)));
                    else if (source is IReadOnlyCollection<T> ro)
                        capacity = Math.Min(take, Math.Max(0, ro.Count - Math.Min(skip, ro.Count)));
                    else
                        capacity = take; // best guess
                }
                result = new List<T>(capacity);

                using var e = source.GetEnumerator();

                // Phase 1: skip matches
                int skipped = 0;
                while (skipped < skip && e.MoveNext())
                {
                    var x = e.Current;
                    if (x is not null && predicate.Invoke(x)) { skipped++; matches++; }
                }

                // Phase 2: take matches
                while (result.Count < take && e.MoveNext())
                {
                    var x = e.Current;
                    if (x is not null && predicate.Invoke(x)) { result.Add(x); matches++; }
                }

                // Phase 3: tail — count only
                while (e.MoveNext())
                {
                    var x = e.Current;
                    if (x is not null && predicate.Invoke(x)) matches++;
                }
            }

            return (result, matches);
        }

        protected async ValueTask<List<T>> Query<T, TPred>(
            IEnumerable<T> source,
            TPred predicate,
            int skip,
            int take)
            where TPred : struct, IPredicate<T>
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (take <= 0) return new List<T>(0);
            if (skip < 0) skip = 0;

            using (await _rwl.ReaderLockAsync())
            {
                int capacity = take;
                if (source is ICollection<T> coll)
                    capacity = Math.Min(take, Math.Max(0, coll.Count - Math.Min(skip, coll.Count)));
                else if (source is IReadOnlyCollection<T> ro)
                    capacity = Math.Min(take, Math.Max(0, ro.Count - Math.Min(skip, ro.Count)));

                var res = new List<T>(capacity);

                using var e = source.GetEnumerator();

                //skip
                int s = 0;
                while (s < skip && e.MoveNext())
                {
                    var x = e.Current;
                    if (x is not null && predicate.Invoke(x)) s++;
                }

                //fill
                while (res.Count < take && e.MoveNext())
                {
                    var x = e.Current;
                    if (x is null || !predicate.Invoke(x)) continue;
                    res.Add(x);
                }

                return res;
            }
        }

        protected async ValueTask<List<T>> Query<T, TPred>(
            IEnumerable<T> source,
            TPred predicate)
            where TPred : struct, IPredicate<T>
        {
            if (source is null) throw new ArgumentNullException(nameof(source));

            using (await _rwl.ReaderLockAsync())
            {
                var res = new List<T>(32);
                foreach (var x in source)
                {
                    if (x is null || !predicate.Invoke(x)) continue;
                    res.Add(x);
                }
                return res;
            }
        }

        protected async ValueTask<T?> FirstOrDefault<T, TPred>(
            IEnumerable<T> source,
            TPred predicate)
            where T : class
            where TPred : struct, IPredicate<T>
        {
            if (source is null) throw new ArgumentNullException(nameof(source));

            using (await _rwl.ReaderLockAsync())
            {
                foreach (var x in source)
                {
                    if (x is null || !predicate.Invoke(x)) continue;
                    return x;
                }
            }
            return null;
        }


        protected async ValueTask<List<T>> Query<T>(
           IEnumerable<T> source)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));

            using (await _rwl.ReaderLockAsync())
            {
                int capacity = 32;
                if (source is ICollection<T> coll)
                    capacity = coll.Count;
                else if (source is IReadOnlyCollection<T> ro)
                    capacity = ro.Count;

                var res = new List<T>(capacity);

                foreach (var x in source)
                {
                    if (x is null) continue;
                    res.Add(x);
                }

                return res;
            }
        }

        protected async ValueTask<List<T>> Query<T>(
    IEnumerable<T> source,
    Predicate<T> predicate,
    int skip,
    int take)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (predicate is null) throw new ArgumentNullException(nameof(predicate));
            if (take <= 0) return new List<T>(0);
            if (skip < 0) skip = 0;

            using (await _rwl.ReaderLockAsync())
            {
                int capacity = take;
                if (source is ICollection<T> coll)
                    capacity = Math.Min(take, Math.Max(0, coll.Count - Math.Min(skip, coll.Count)));
                else if (source is IReadOnlyCollection<T> ro)
                    capacity = Math.Min(take, Math.Max(0, ro.Count - Math.Min(skip, ro.Count)));

                var res = new List<T>(capacity);

                using var e = source.GetEnumerator();

                //skip
                int s = 0;
                while (s < skip && e.MoveNext())
                {
                    var x = e.Current;
                    if (x is not null && predicate(x)) s++;
                }

                //fill
                while (res.Count < take && e.MoveNext())
                {
                    var x = e.Current;
                    if (x is null || !predicate(x)) continue;
                    res.Add(x);
                }

                return res;
            }
        }

        protected async ValueTask<List<T>> Query<T>(
    IEnumerable<T> source,
    Predicate<T> predicate)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (predicate is null) throw new ArgumentNullException(nameof(predicate));

            using (await _rwl.ReaderLockAsync())
            {
                var res = new List<T>(32);
                foreach (var x in source)
                {
                    if (x is null || !predicate(x)) continue;
                    res.Add(x);
                }
                return res;
            }
        }

        protected async ValueTask<T> FirstOrDefault<T>(
    IEnumerable<T> source,
    Predicate<T> predicate) where T : class
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (predicate is null) throw new ArgumentNullException(nameof(predicate));

            using (await _rwl.ReaderLockAsync())
            {
                foreach (var x in source)
                {
                    if (x is null || !predicate(x)) continue;
                    return x;
                }
            }
            return null;
        }

        protected async ValueTask<List<T>> Query<T>(
    IEnumerable<T> source,
    int skip,
    int take)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (take <= 0) return new List<T>(0);
            if (skip < 0) skip = 0;

            using (await _rwl.ReaderLockAsync())
            {
                int capacity = take;
                if (source is ICollection<T> coll)
                    capacity = Math.Min(take, Math.Max(0, coll.Count - Math.Min(skip, coll.Count)));
                else if (source is IReadOnlyCollection<T> ro)
                    capacity = Math.Min(take, Math.Max(0, ro.Count - Math.Min(skip, ro.Count)));

                var res = new List<T>(capacity);

                int skipped2 = 0;
                foreach (var x in source)
                {
                    if (x is null) continue;

                    if (skipped2 < skip) { skipped2++; continue; }

                    res.Add(x);
                    if (res.Count == take) break;
                }

                return res;
            }
        }
    }
}
