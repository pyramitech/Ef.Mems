using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ef.Mems.Extensions
{
    public static class Extensions
    {
        public static void AddAt<T>(this List<T> list, int index, T value)
        {
            list.EnsureCapacity(index + 1);

            for (int i = list.Count; i <= index; i++)
                list.Add(default);

            list[index] = value;
        }

        public static bool EqualsFast(this byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            return left.AsSpan().SequenceEqual(right);
        }

        public static bool EqualsFast(this char[] left, char[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            return left.AsSpan().SequenceEqual(right);
        }

        // Sequence equality for cached primitive collections (List<T>/T[] of
        // EF scalars); the byte[]/char[] overloads above stay the specific
        // (span-based) picks for those types.
        public static bool EqualsFast<T>(this List<T> left, List<T> right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            if (left.Count != right.Count) return false;
            var cmp = EqualityComparer<T>.Default;
            for (int i = 0; i < left.Count; i++)
                if (!cmp.Equals(left[i], right[i])) return false;
            return true;
        }

        public static bool EqualsFast<T>(this T[] left, T[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            if (left.Length != right.Length) return false;
            var cmp = EqualityComparer<T>.Default;
            for (int i = 0; i < left.Length; i++)
                if (!cmp.Equals(left[i], right[i])) return false;
            return true;
        }
    }
}
