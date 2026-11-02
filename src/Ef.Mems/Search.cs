using System.Buffers;
using System.Globalization;
using System.Text;

namespace Ef.Mems
{
    /// <summary>
    /// Implemented by entities whose store uses <see cref="SearchExtension"/>.
    /// SearchField is rebuilt from the configured properties on every create
    /// and update. The property must be cached (UseAllProperties, or listed
    /// in UseProperties) so queries can scan it and update diffs persist it.
    /// </summary>
    public interface ISearchable
    {
        string SearchField { get; set; }
    }

    /// <summary>
    /// A parsed, normalized search input: latinized, lower-cased and split
    /// into whitespace-separated tokens. Parsing happens once per query;
    /// <see cref="Matches"/> is allocation-free and scans the field with the
    /// runtime's vectorized ordinal substring search, so it is safe to use
    /// inside predicate scan loops:
    /// <code>.UsePredicate("Search", (TEntity x, SearchQuery query) => query.Matches(x.SearchField))</code>
    /// </summary>
    public readonly struct SearchQuery
    {
        // One single-string SearchValues per token: Create picks statistically
        // rare anchor characters once, which benchmarked ~2x faster than
        // per-call string.Contains on 4M-row scans (see Ef.Mems.Benchmarks).
        private readonly SearchValues<string>[] _tokens;

        private SearchQuery(SearchValues<string>[] tokens) => _tokens = tokens;

        public static SearchQuery Parse(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return new SearchQuery(Array.Empty<SearchValues<string>>());
            var parts = SearchExtension.Latinize(input)
                .Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            var tokens = new SearchValues<string>[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                tokens[i] = SearchValues.Create(new[] { parts[i] }, StringComparison.Ordinal);
            return new SearchQuery(tokens);
        }

        /// <summary>
        /// True when every token occurs in <paramref name="searchField"/>.
        /// An empty (or default) query matches everything.
        /// </summary>
        public bool Matches(string searchField)
        {
            var tokens = _tokens;
            if (tokens == null || tokens.Length == 0) return true;
            if (searchField == null) return false;
            ReadOnlySpan<char> field = searchField;
            for (int i = 0; i < tokens.Length; i++)
            {
                if (field.IndexOfAny(tokens[i]) < 0)
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Canonical search extension: joins the configured property values into
    /// a single latinized, lower-case SearchField. Configure the inputs on
    /// the builder:
    /// <code>.UseExtension(typeof(SearchExtension), x => new { x.Name, x.Body })</code>
    /// and query it with a <see cref="SearchQuery"/> predicate.
    /// </summary>
    public static class SearchExtension
    {
        public static void BeforeCreate(object dataStore, ISearchable entity, object[] values)
            => entity.SearchField = Build(values);

        public static void BeforeUpdate(object dataStore, ISearchable entity, object[] values)
            => entity.SearchField = Build(values);

        private static string Build(object[] values)
        {
            var sb = new StringBuilder();
            foreach (var value in values)
            {
                var text = value?.ToString();
                if (string.IsNullOrEmpty(text)) continue;
                if (sb.Length != 0) sb.Append(' ');
                sb.Append(text);
            }
            return Latinize(sb.ToString());
        }

        /// <summary>
        /// Lower-cases and strips diacritics (š → s, č → c, é → e, ...):
        /// FormD decomposition splits base letters from combining marks and
        /// the marks are dropped. Both SearchField and query input go through
        /// this, so the two sides always compare in the same alphabet.
        /// </summary>
        public static string Latinize(string text)
        {
            var normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (var c in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                    continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }
}
