using Xunit;

namespace Ef.Mems.Tests
{
    /// <summary>
    /// Context-taking hook coverage across every write path: the optional
    /// trailing context argument on Create/Update/Delete (and Range variants)
    /// and the per-commit context on EntityTransaction.Commit reach the
    /// Before* hooks that declare a context parameter. AuditExtension stamps
    /// CreatedBy/CreatedOn/UpdatedBy from it - the motivating scenario.
    /// (Extension queues are static, so assertions use per-test unique notes.)
    /// </summary>
    public class AuditContextTests : StoreTestBase
    {
        private static string Unique(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N");

        private static AuditedClass NewAudited(string note) => new AuditedClass { Note = note };

        [Fact]
        public async Task Create_PassesContextToHooks()
        {
            var before = DateTime.UtcNow;
            var ro = await Store.AuditedClasses.Create(NewAudited(Unique("c")), context: "user-1");

            Assert.Equal("user-1", ro.CreatedBy);
            Assert.InRange(ro.CreatedOn, before, DateTime.UtcNow);
            Assert.Null(ro.UpdatedBy);
        }

        [Fact]
        public async Task Create_OmittedContext_HooksReceiveNull()
        {
            var ro = await Store.AuditedClasses.Create(NewAudited(Unique("cn")));
            Assert.Null(ro.CreatedBy);
        }

        [Fact]
        public async Task CreateRange_PassesContextToEveryItem()
        {
            var notes = new[] { Unique("r"), Unique("r") };
            var ros = await Store.AuditedClasses.CreateRange(notes.Select(NewAudited).ToList(), context: "user-range");
            Assert.All(ros, ro => Assert.Equal("user-range", ro.CreatedBy));
        }

        [Fact]
        public async Task Update_PassesContextToHooks()
        {
            var ro = await Store.AuditedClasses.Create(NewAudited(Unique("u")), context: "creator");

            var entity = Store.AuditedClasses.GetById(ro.Id).GetEntity();
            entity.Note = Unique("u2");
            var ro2 = await Store.AuditedClasses.Update(entity, context: "updater");

            Assert.Equal("creator", ro2.CreatedBy);
            Assert.Equal("updater", ro2.UpdatedBy);
        }

        [Fact]
        public async Task UpdateRange_PassesContextToEveryItem()
        {
            var ros = await Store.AuditedClasses.CreateRange(
                new[] { NewAudited(Unique("ur")), NewAudited(Unique("ur")) }, context: "creator");

            var entities = ros.Select(ro => Store.AuditedClasses.GetById(ro.Id).GetEntity()).ToList();
            var updated = await Store.AuditedClasses.UpdateRange(entities, context: "range-updater");
            Assert.All(updated, ro => Assert.Equal("range-updater", ro.UpdatedBy));
        }

        [Fact]
        public async Task Delete_PassesContextToHooks()
        {
            var note = Unique("d");
            var ro = await Store.AuditedClasses.Create(NewAudited(note), context: "creator");

            await Store.AuditedClasses.Delete(Store.AuditedClasses.GetById(ro.Id).GetEntity(), context: "deleter");
            Assert.Contains((note, (object)"deleter"), AuditExtension.Deleted);
        }

        [Fact]
        public async Task DeleteRange_PassesContextToEveryItem()
        {
            var notes = new[] { Unique("dr"), Unique("dr") };
            var ros = await Store.AuditedClasses.CreateRange(notes.Select(NewAudited).ToList(), context: "creator");

            var entities = ros.Select(ro => Store.AuditedClasses.GetById(ro.Id).GetEntity()).ToList();
            await Store.AuditedClasses.DeleteRange(entities, context: "range-deleter");
            Assert.All(notes, n => Assert.Contains((n, (object)"range-deleter"), AuditExtension.Deleted));
        }

        [Fact]
        public async Task Commit_PassesContextToStagedHooks()
        {
            var note = Unique("sc");
            var trx = Store.StartTransaction();
            Store.AuditedClasses.StageCreate(trx, NewAudited(note));
            await trx.Commit(context: "trx-user");

            var created = await Store.AuditedClasses.FirstOrDefault(x => x.Note == note);
            Assert.Equal("trx-user", created.CreatedBy);

            var note2 = Unique("su");
            var entity = Store.AuditedClasses.GetById(created.Id).GetEntity();
            entity.Note = note2;
            var trx2 = Store.StartTransaction();
            Store.AuditedClasses.StageUpdate(trx2, entity);
            await trx2.Commit(context: "trx-updater");
            Assert.Equal("trx-updater", Store.AuditedClasses.GetById(created.Id).UpdatedBy);

            var trx3 = Store.StartTransaction();
            Store.AuditedClasses.StageDelete(trx3, Store.AuditedClasses.GetById(created.Id).GetEntity());
            await trx3.Commit(context: "trx-deleter");
            Assert.Contains((note2, (object)"trx-deleter"), AuditExtension.Deleted);
        }

        [Fact]
        public async Task Commit_OmittedContext_HooksReceiveNull()
        {
            var note = Unique("scn");
            var trx = Store.StartTransaction();
            Store.AuditedClasses.StageCreate(trx, NewAudited(note));
            await trx.Commit();

            var created = await Store.AuditedClasses.FirstOrDefault(x => x.Note == note);
            Assert.Null(created.CreatedBy);
        }

        [Fact]
        public async Task ValuedContextHook_ReceivesValuesAndContext()
        {
            var note = Unique("vc");
            await Store.AuditedClasses.Create(NewAudited(note), context: "valued-user");

            var entry = ValuedContextExtension.Created.Single(e => (string)e.Values[0] == note);
            Assert.Equal("valued-user", entry.Context);
        }
    }
}
