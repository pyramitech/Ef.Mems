using Xunit;

namespace Ef.Mems.Tests
{
    /// <summary>
    /// UseOwned coverage: the owned Address survives the safety clones on
    /// every write path (the pre-UseOwned failure mode was silent data loss),
    /// is exposed read-only through the generated AddressReadOnly wrapper,
    /// diffs as a unit on update including null transitions, and works with
    /// predicates and Refresh.
    /// </summary>
    public class OwnedTypeTests : StoreTestBase
    {
        private static string Unique(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N");

        private static Company NewCompany(string name, string city = "Vienna", string street = "Main St 1", int zip = 1010)
            => new Company
            {
                Name = name,
                RegistrationAddress = new Address { Street = street, City = city, ZipCode = zip },
            };

        [Fact]
        public async Task Create_PersistsOwnedThroughSafetyClone()
        {
            var ro = await Store.Companies.Create(NewCompany(Unique("c"), city: "Graz"));

            // Cache carries the address (copyOnInsert clone must deep-copy it).
            Assert.NotNull(ro.RegistrationAddress);
            Assert.Equal("Graz", ro.RegistrationAddress.City);

            // And so does the database row.
            using var ctx = Store.GetContext();
            var db = ctx.Companies.Single(c => c.Id == ro.Id);
            Assert.Equal("Graz", db.RegistrationAddress.City);
            Assert.Equal(1010, db.RegistrationAddress.ZipCode);
        }

        [Fact]
        public async Task Create_CallerMutationAfterCreate_DoesNotLeakIntoCache()
        {
            var company = NewCompany(Unique("m"), city: "Linz");
            var ro = await Store.Companies.Create(company);

            company.RegistrationAddress.City = "MUTATED";
            Assert.Equal("Linz", Store.Companies.GetById(ro.Id).RegistrationAddress.City);
        }

        [Fact]
        public async Task Create_NullOwned_Works()
        {
            var ro = await Store.Companies.Create(new Company { Name = Unique("n") });
            Assert.Null(ro.RegistrationAddress);

            using var ctx = Store.GetContext();
            Assert.Null(ctx.Companies.Single(c => c.Id == ro.Id).RegistrationAddress);
        }

        [Fact]
        public async Task GetEntity_DeepCopiesOwned()
        {
            var ro = await Store.Companies.Create(NewCompany(Unique("g"), city: "Wels"));

            var entity = Store.Companies.GetById(ro.Id).GetEntity();
            Assert.Equal("Wels", entity.RegistrationAddress.City);

            // The copy is private: mutating it must not touch the cache.
            entity.RegistrationAddress.City = "MUTATED";
            Assert.Equal("Wels", Store.Companies.GetById(ro.Id).RegistrationAddress.City);
        }

        [Fact]
        public async Task Update_ChangedOwnedMember_PersistsToCacheAndDatabase()
        {
            var ro = await Store.Companies.Create(NewCompany(Unique("u"), city: "Steyr"));

            var entity = Store.Companies.GetById(ro.Id).GetEntity();
            entity.RegistrationAddress.City = "Salzburg";
            entity.RegistrationAddress.ZipCode = 5020;
            var ro2 = await Store.Companies.Update(entity);

            Assert.Equal("Salzburg", ro2.RegistrationAddress.City);
            Assert.Equal(5020, ro2.RegistrationAddress.ZipCode);
            Assert.Equal("Salzburg", Store.Companies.GetById(ro.Id).RegistrationAddress.City);

            using var ctx = Store.GetContext();
            var db = ctx.Companies.Single(c => c.Id == ro.Id);
            Assert.Equal("Salzburg", db.RegistrationAddress.City);
            Assert.Equal(5020, db.RegistrationAddress.ZipCode);
        }

        [Fact]
        public async Task Update_NullToValue_PersistsOwned()
        {
            var ro = await Store.Companies.Create(new Company { Name = Unique("nv") });

            var entity = Store.Companies.GetById(ro.Id).GetEntity();
            entity.RegistrationAddress = new Address { Street = "New St 2", City = "Krems", ZipCode = 3500 };
            var ro2 = await Store.Companies.Update(entity);

            Assert.Equal("Krems", ro2.RegistrationAddress.City);

            using var ctx = Store.GetContext();
            Assert.Equal("Krems", ctx.Companies.Single(c => c.Id == ro.Id).RegistrationAddress.City);
        }

        [Fact]
        public async Task Update_ValueToNull_RemovesOwned()
        {
            var ro = await Store.Companies.Create(NewCompany(Unique("vn"), city: "Baden"));

            var entity = Store.Companies.GetById(ro.Id).GetEntity();
            entity.RegistrationAddress = null;
            var ro2 = await Store.Companies.Update(entity);

            Assert.Null(ro2.RegistrationAddress);
            Assert.Null(Store.Companies.GetById(ro.Id).RegistrationAddress);

            using var ctx = Store.GetContext();
            Assert.Null(ctx.Companies.Single(c => c.Id == ro.Id).RegistrationAddress);
        }

        [Fact]
        public async Task Update_UnchangedOwned_PreservesIt()
        {
            var ro = await Store.Companies.Create(NewCompany(Unique("k"), city: "Tulln"));

            var entity = Store.Companies.GetById(ro.Id).GetEntity();
            entity.Name = Unique("k2");
            await Store.Companies.Update(entity);

            Assert.Equal("Tulln", Store.Companies.GetById(ro.Id).RegistrationAddress.City);
            using var ctx = Store.GetContext();
            Assert.Equal("Tulln", ctx.Companies.Single(c => c.Id == ro.Id).RegistrationAddress.City);
        }

        [Fact]
        public async Task StagedWrites_CarryOwnedThroughCommit()
        {
            var name = Unique("s");
            var trx = Store.StartTransaction();
            Store.Companies.StageCreate(trx, NewCompany(name, city: "Villach"));
            await trx.Commit();

            var created = await Store.Companies.FirstOrDefault(x => x.Name == name);
            Assert.Equal("Villach", created.RegistrationAddress.City);

            var entity = Store.Companies.GetById(created.Id).GetEntity();
            entity.RegistrationAddress.City = "Klagenfurt";
            var trx2 = Store.StartTransaction();
            Store.Companies.StageUpdate(trx2, entity);
            await trx2.Commit();

            Assert.Equal("Klagenfurt", Store.Companies.GetById(created.Id).RegistrationAddress.City);
            using var ctx = Store.GetContext();
            Assert.Equal("Klagenfurt", ctx.Companies.Single(c => c.Id == created.Id).RegistrationAddress.City);
        }

        [Fact]
        public async Task Predicate_OnOwnedMember_Works()
        {
            await Store.Companies.Create(NewCompany(Unique("p"), city: "Innsbruck"));
            await Store.Companies.Create(NewCompany(Unique("p"), city: "Bregenz"));
            await Store.Companies.Create(new Company { Name = Unique("p") });

            var hits = await Store.Companies.QueryInCity("Innsbruck", 0, 10);
            Assert.Single(hits);
            Assert.Equal("Innsbruck", hits[0].RegistrationAddress.City);
        }

        [Fact]
        public async Task Refresh_ReloadsOwned()
        {
            var ro = await Store.Companies.Create(NewCompany(Unique("r"), city: "Dornbirn"));
            await Store.Companies.Refresh();

            Assert.Equal("Dornbirn", Store.Companies.GetById(ro.Id).RegistrationAddress.City);
        }
    }
}
