using Xunit;

namespace Ef.Mems.Tests
{
    /// <summary>
    /// Primitive-collection caching (List&lt;T&gt;/T[] of EF scalars) and full
    /// [NotMapped] respect, driven by the HashSet-passthrough pattern:
    /// Favorites (mapped List) flows through create/clone/diff/reads while
    /// FavoriteSet ([NotMapped] backing HashSet) is ignored everywhere.
    /// Before this feature, collection properties were silently dropped by
    /// the safety clone - lost on Create, never diffed on Update.
    /// </summary>
    public class PrimitiveCollectionTests : StoreTestBase
    {
        private static string Unique(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N");

        private static Profile NewProfile(string alias, params int[] favorites) => new Profile
        {
            Alias = alias,
            Favorites = favorites.ToList(),
            Tags = new List<string> { "a", "b" },
            Scores = new[] { 1, 2, 3 },
        };

        [Fact]
        public async Task Create_PersistsCollectionsThroughSafetyClone()
        {
            var ro = await Store.Profiles.Create(NewProfile(Unique("c"), 7, 11));

            // Cache carries the collections (the clone must value-copy them).
            Assert.Equal(new[] { 7, 11 }, ro.Favorites.OrderBy(x => x));
            Assert.Equal(new[] { "a", "b" }, ro.Tags);
            Assert.Equal(new[] { 1, 2, 3 }, ro.Scores.ToArray());

            // And so does the database row, materialized back through the
            // List pass-through into the HashSet.
            using var ctx = Store.GetContext();
            var db = ctx.Profiles.Single(p => p.Id == ro.Id);
            Assert.Equal(new[] { 7, 11 }, db.FavoriteSet.OrderBy(x => x));
            Assert.Equal(new[] { "a", "b" }, db.Tags);
        }

        [Fact]
        public async Task Create_CallerMutationAfterCreate_DoesNotLeakIntoCache()
        {
            var profile = NewProfile(Unique("m"), 1);
            var ro = await Store.Profiles.Create(profile);

            profile.FavoriteSet.Add(999);
            profile.Tags.Add("MUTATED");
            profile.Scores[0] = 999;

            var cached = Store.Profiles.GetById(ro.Id);
            Assert.Equal(new[] { 1 }, cached.Favorites);
            Assert.Equal(new[] { "a", "b" }, cached.Tags);
            Assert.Equal(1, cached.Scores.Span[0]);
        }

        [Fact]
        public async Task GetEntity_RoundTripsIntoBackingSet()
        {
            var ro = await Store.Profiles.Create(NewProfile(Unique("g"), 3, 5));

            var entity = Store.Profiles.GetById(ro.Id).GetEntity();
            Assert.Equal(new[] { 3, 5 }, entity.FavoriteSet.OrderBy(x => x));

            // The copy is private: mutating it must not touch the cache.
            entity.FavoriteSet.Add(999);
            entity.Tags.Add("MUTATED");
            Assert.Equal(new[] { 3, 5 }, Store.Profiles.GetById(ro.Id).Favorites.OrderBy(x => x));
            Assert.Equal(new[] { "a", "b" }, Store.Profiles.GetById(ro.Id).Tags);
        }

        [Fact]
        public async Task Update_ChangedCollections_PersistToCacheAndDatabase()
        {
            var ro = await Store.Profiles.Create(NewProfile(Unique("u"), 1));

            var entity = Store.Profiles.GetById(ro.Id).GetEntity();
            entity.FavoriteSet.Add(42);
            entity.Favorites = entity.FavoriteSet.ToList(); // re-sync through the pass-through
            entity.Tags = new List<string> { "x" };
            entity.Scores = new[] { 9 };
            var ro2 = await Store.Profiles.Update(entity);

            Assert.Contains(42, ro2.Favorites);
            Assert.Equal(new[] { "x" }, ro2.Tags);

            using var ctx = Store.GetContext();
            var db = ctx.Profiles.Single(p => p.Id == ro.Id);
            Assert.Contains(42, db.FavoriteSet);
            Assert.Equal(new[] { "x" }, db.Tags);
            Assert.Equal(new[] { 9 }, db.Scores);
        }

        [Fact]
        public async Task Update_EqualContentDifferentInstances_NoFalseDiff()
        {
            var ro = await Store.Profiles.Create(NewProfile(Unique("e"), 1, 2));

            // Fresh instances with identical content plus one real change:
            // the update must succeed (sequence equality, not reference).
            var entity = Store.Profiles.GetById(ro.Id).GetEntity();
            entity.Alias = Unique("e2");
            var ro2 = await Store.Profiles.Update(entity);
            Assert.Equal(entity.Alias, ro2.Alias);
            Assert.Equal(new[] { "a", "b" }, ro2.Tags);
        }

        [Fact]
        public async Task StagedWrites_CarryCollectionsThroughCommit()
        {
            var alias = Unique("s");
            var trx = Store.StartTransaction();
            Store.Profiles.StageCreate(trx, NewProfile(alias, 8));
            await trx.Commit();

            var created = await Store.Profiles.FirstOrDefault(x => x.Alias == alias);
            Assert.Equal(new[] { 8 }, created.Favorites);

            var entity = Store.Profiles.GetById(created.Id).GetEntity();
            entity.Favorites = new List<int> { 8, 21 };
            var trx2 = Store.StartTransaction();
            Store.Profiles.StageUpdate(trx2, entity);
            await trx2.Commit();

            Assert.Equal(new[] { 8, 21 }, Store.Profiles.GetById(created.Id).Favorites.OrderBy(x => x));
            using var ctx = Store.GetContext();
            Assert.Equal(new[] { 8, 21 }, ctx.Profiles.Single(p => p.Id == created.Id).FavoriteSet.OrderBy(x => x));
        }

        [Fact]
        public async Task Predicate_OverCollectionContains_Works()
        {
            await Store.Profiles.Create(NewProfile(Unique("p"), 5, 6));
            await Store.Profiles.Create(NewProfile(Unique("p"), 7));

            var hits = await Store.Profiles.QueryFavors(6, 0, 10);
            Assert.Single(hits);
            Assert.Contains(6, hits[0].Favorites);
        }

        [Fact]
        public async Task Refresh_ReloadsCollections()
        {
            var ro = await Store.Profiles.Create(NewProfile(Unique("r"), 13));
            await Store.Profiles.Refresh();

            var cached = Store.Profiles.GetById(ro.Id);
            Assert.Equal(new[] { 13 }, cached.Favorites);
            Assert.Equal(new[] { 1, 2, 3 }, cached.Scores.ToArray());
        }
    }
}
