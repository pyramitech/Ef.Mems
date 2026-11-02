using Xunit;

namespace Ef.Mems.Tests
{
    /// <summary>
    /// The Snapshot primitives are the concurrency bedrock of every generated
    /// store; these pin their publication semantics directly (in-place vs
    /// clone-and-swap) and stress the CAS pair under real contention.
    /// </summary>
    public class SnapshotTests
    {
        // ---- AddAt / EnsureCapacity ----------------------------------------

        [Fact]
        public void AddAt_WithinCapacity_WritesInPlace()
        {
            var array = new string[4];
            var count = 0;

            var before = array;
            Snapshot.AddAt(ref array, ref count, 2, "x");

            Assert.Same(before, array); // no structural change
            Assert.Equal("x", array[2]);
            Assert.Equal(3, count);
        }

        [Fact]
        public void AddAt_BeyondCapacity_GrowsAndRepublishes()
        {
            var array = new string[2] { "a", "b" };
            var count = 2;

            var before = array;
            Snapshot.AddAt(ref array, ref count, 5, "f");

            Assert.NotSame(before, array);        // clone-and-swap
            Assert.Equal(new[] { "a", "b" }, before); // old snapshot untouched
            Assert.Equal("f", array[5]);
            Assert.Equal("a", array[0]);
            Assert.Equal(6, count);
            Assert.True(array.Length >= 6);
        }

        [Fact]
        public void AddAt_LowerIndex_KeepsCount()
        {
            var array = new string[8];
            var count = 5;

            Snapshot.AddAt(ref array, ref count, 1, "x");
            Assert.Equal(5, count);
        }

        [Fact]
        public void EnsureCapacity_GrowsOnceAndPreservesContent()
        {
            var array = new[] { "a", "b" };
            Snapshot.EnsureCapacity(ref array, 10);
            Assert.True(array.Length >= 10);
            Assert.Equal("a", array[0]);
            Assert.Equal("b", array[1]);

            var grown = array;
            Snapshot.EnsureCapacity(ref array, 5); // already large enough
            Assert.Same(grown, array);
        }

        // ---- SetItem / RemoveItem / Clone ----------------------------------

        [Fact]
        public void SetItem_ExistingKey_OverwritesInPlace()
        {
            var dict = new Dictionary<string, int>(StringComparer.Ordinal) { ["a"] = 1 };
            var before = dict;

            Snapshot.SetItem(ref dict, "a", 2);

            Assert.Same(before, dict); // atomic in-place value store
            Assert.Equal(2, dict["a"]);
        }

        [Fact]
        public void SetItem_NewKey_CloneAndSwap_OldSnapshotUnchanged()
        {
            var dict = new Dictionary<string, int>(StringComparer.Ordinal) { ["a"] = 1 };
            var before = dict;

            Snapshot.SetItem(ref dict, "b", 2);

            Assert.NotSame(before, dict);
            Assert.False(before.ContainsKey("b")); // readers on the old snapshot are safe
            Assert.Equal(2, dict["b"]);
            Assert.Same(StringComparer.Ordinal, dict.Comparer); // comparer preserved
        }

        [Fact]
        public void RemoveItem_CloneAndSwap_OldSnapshotUnchanged()
        {
            var dict = new Dictionary<string, int>(StringComparer.Ordinal) { ["a"] = 1, ["b"] = 2 };
            var before = dict;

            Snapshot.RemoveItem(ref dict, "a");

            Assert.NotSame(before, dict);
            Assert.True(before.ContainsKey("a"));
            Assert.False(dict.ContainsKey("a"));
            Assert.Same(StringComparer.Ordinal, dict.Comparer);

            var current = dict;
            Snapshot.RemoveItem(ref dict, "missing"); // no-op, no swap
            Assert.Same(current, dict);
        }

        [Fact]
        public void Clone_PreservesComparer()
        {
            var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["A"] = 1 };
            var clone = Snapshot.Clone(dict);

            Assert.NotSame(dict, clone);
            Assert.Same(StringComparer.OrdinalIgnoreCase, clone.Comparer);
            Assert.True(clone.ContainsKey("a"));
        }

        // ---- CAS Append/Remove (cross-store MTM mutation path) -------------

        [Fact]
        public async Task Append_UnderContention_LosesNothing()
        {
            var array = Array.Empty<string>();
            const int Writers = 8, PerWriter = 500;

            await Task.WhenAll(Enumerable.Range(0, Writers).Select(w => Task.Run(() =>
            {
                for (int i = 0; i < PerWriter; i++)
                    Snapshot.Append(ref array, $"{w}:{i}");
            })));

            Assert.Equal(Writers * PerWriter, array.Length);
            Assert.Equal(Writers * PerWriter, array.Distinct().Count());
        }

        [Fact]
        public async Task AppendAndRemove_UnderContention_Converges()
        {
            var array = Array.Empty<string>();
            var keep = Enumerable.Range(0, 200).Select(i => "keep" + i).ToArray();
            var churn = Enumerable.Range(0, 200).Select(i => "churn" + i).ToArray();

            foreach (var item in churn)
                Snapshot.Append(ref array, item);

            var appenders = Task.Run(() =>
            {
                foreach (var item in keep) Snapshot.Append(ref array, item);
            });
            var removers = Task.Run(() =>
            {
                foreach (var item in churn) Snapshot.Remove(ref array, item);
            });
            await Task.WhenAll(appenders, removers);

            Assert.Equal(keep.OrderBy(x => x), array.OrderBy(x => x));

            Snapshot.Remove(ref array, "not-there"); // absent removal is a no-op
            Assert.Equal(keep.Length, array.Length);
        }
    }
}
