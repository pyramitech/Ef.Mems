using System;
using System.Collections.Generic;
using System.Threading;

namespace Ef.Mems
{
    /// <summary>
    /// Helpers for the lock-free-read snapshot scheme used by the generated
    /// entity stores.
    ///
    /// Contract: all writers are serialized by the store's writer lock, while
    /// readers take no lock at all. Readers must therefore never be able to
    /// observe torn state:
    ///  - in-place writes are limited to atomic reference stores (an array
    ///    slot, or overwriting the value of an existing dictionary key),
    ///  - every structural change (growing an array, adding or removing a
    ///    dictionary key) is done on a private copy that is then published
    ///    with <see cref="Volatile.Write{T}(ref T, T)"/>.
    /// Readers may observe slightly stale data, but never an exception.
    /// </summary>
    public static class Snapshot
    {
        /// <summary>Sets <paramref name="array"/>[<paramref name="index"/>], growing and republishing the array when needed, and keeps the logical <paramref name="count"/> up to date.</summary>
        public static void AddAt<T>(ref T[] array, ref int count, int index, T value)
        {
            var arr = array;
            if ((uint)index >= (uint)arr.Length)
            {
                var grown = new T[Math.Max(index + 1, arr.Length == 0 ? 4 : arr.Length * 2)];
                Array.Copy(arr, grown, arr.Length);
                grown[index] = value;
                Volatile.Write(ref array, grown);
            }
            else
            {
                arr[index] = value;
            }

            if (index >= count) count = index + 1;
        }

        /// <summary>Grows and republishes the array so it can hold at least <paramref name="capacity"/> items.</summary>
        public static void EnsureCapacity<T>(ref T[] array, int capacity)
        {
            var arr = array;
            if (arr.Length >= capacity) return;

            var grown = new T[Math.Max(capacity, arr.Length == 0 ? 4 : arr.Length * 2)];
            Array.Copy(arr, grown, arr.Length);
            Volatile.Write(ref array, grown);
        }

        /// <summary>
        /// Sets a dictionary entry. Overwriting an existing key is an atomic
        /// in-place store (the hot update path); adding a new key is structural
        /// and clones + republishes the dictionary.
        /// </summary>
        public static void SetItem<TKey, TValue>(ref Dictionary<TKey, TValue> dictionary, TKey key, TValue value)
            where TKey : notnull
        {
            var dict = dictionary;
            if (dict.ContainsKey(key))
            {
                dict[key] = value;
                return;
            }

            var clone = new Dictionary<TKey, TValue>(dict, dict.Comparer);
            clone[key] = value;
            Volatile.Write(ref dictionary, clone);
        }

        /// <summary>Removes a dictionary entry by cloning and republishing (removal is always structural).</summary>
        public static void RemoveItem<TKey, TValue>(ref Dictionary<TKey, TValue> dictionary, TKey key)
            where TKey : notnull
        {
            var dict = dictionary;
            if (!dict.ContainsKey(key)) return;

            var clone = new Dictionary<TKey, TValue>(dict, dict.Comparer);
            clone.Remove(key);
            Volatile.Write(ref dictionary, clone);
        }

        /// <summary>Clones a dictionary (preserving its comparer) for batched structural edits; publish the result with <see cref="Volatile.Write{T}(ref T, T)"/>.</summary>
        public static Dictionary<TKey, TValue> Clone<TKey, TValue>(Dictionary<TKey, TValue> dictionary)
            where TKey : notnull
            => new Dictionary<TKey, TValue>(dictionary, dictionary.Comparer);

        // The two helpers below use a CAS loop instead of relying on the
        // writer lock: ManyToMany navigation snapshots on a ReadOnly DTO can
        // be mutated by the writers of *different* stores concurrently (each
        // store only holds its own lock).

        /// <summary>Appends an item by copying and republishing the array (lock-free, CAS).</summary>
        public static void Append<T>(ref T[] array, T item) where T : class
        {
            while (true)
            {
                var arr = Volatile.Read(ref array);
                var next = new T[arr.Length + 1];
                Array.Copy(arr, next, arr.Length);
                next[arr.Length] = item;
                if (Interlocked.CompareExchange(ref array, next, arr) == arr) return;
            }
        }

        /// <summary>Removes an item (by reference) by copying and republishing the array (lock-free, CAS); no-op when absent.</summary>
        public static void Remove<T>(ref T[] array, T item) where T : class
        {
            while (true)
            {
                var arr = Volatile.Read(ref array);
                int index = Array.IndexOf(arr, item);
                if (index < 0) return;

                var next = new T[arr.Length - 1];
                Array.Copy(arr, 0, next, 0, index);
                Array.Copy(arr, index + 1, next, index, arr.Length - index - 1);
                if (Interlocked.CompareExchange(ref array, next, arr) == arr) return;
            }
        }
    }
}
