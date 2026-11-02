using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System;
using System.Globalization;

namespace Ef.Mems
{
    /// <summary>
    /// Provider-aware bulk delete + identity reseed backing the generated
    /// Truncate() methods. TRUNCATE semantics: rows are deleted set-based
    /// without being loaded, so no Before* extension hooks run.
    /// </summary>
    public static class Truncation
    {
        /// <summary>
        /// Deletes every row of the entity's table and reseeds its identity
        /// column (SQLite/SQL Server; other providers delete only), inside one
        /// database transaction. Returns the number of rows deleted.
        /// </summary>
        public static async Task<int> DeleteAllAndReseed<TEntity>(DbContext ctx) where TEntity : class
        {
            var entityType = ctx.Model.FindEntityType(typeof(TEntity));
            var table = entityType?.GetTableName();
            var schema = entityType?.GetSchema();

            await using var trx = await ctx.Database.BeginTransactionAsync();
            var deleted = await ctx.Set<TEntity>().ExecuteDeleteAsync();

            if (table != null)
            {
                switch (ctx.Database.ProviderName)
                {
                    case "Microsoft.EntityFrameworkCore.Sqlite":
                        // sqlite_sequence exists only when some table in the
                        // database uses AUTOINCREMENT; a plain INTEGER PRIMARY
                        // KEY reseeds itself once its rows are gone.
                        if (await HasSqliteSequenceTable(ctx))
                            await ctx.Database.ExecuteSqlAsync($"DELETE FROM sqlite_sequence WHERE name = {table}");
                        break;

                    case "Microsoft.EntityFrameworkCore.SqlServer":
                        // Server-side guard: DBCC CHECKIDENT errors on tables
                        // without an identity column, so only reseed when one
                        // exists. RESEED 0 makes the next identity value 1,
                        // matching a freshly created table.
                        var qualified = Quote(schema, table);
                        await ctx.Database.ExecuteSqlRawAsync(
                            $"IF EXISTS (SELECT 1 FROM sys.identity_columns WHERE object_id = OBJECT_ID(N'{EscapeLiteral(qualified)}')) DBCC CHECKIDENT(N'{EscapeLiteral(qualified)}', RESEED, 0);");
                        break;
                }
            }

            await trx.CommitAsync();
            return deleted;
        }

        private static async Task<bool> HasSqliteSequenceTable(DbContext ctx)
        {
            var conn = ctx.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = ctx.Database.CurrentTransaction?.GetDbTransaction();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'sqlite_sequence'";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture) > 0;
        }

        // The identifiers come from EF model metadata, not user input; quoting
        // and escaping still keep exotic table names (spaces, brackets,
        // quotes) from breaking the statement.
        private static string Quote(string? schema, string table)
        {
            static string Bracket(string name) => "[" + name.Replace("]", "]]") + "]";
            return schema == null ? Bracket(table) : Bracket(schema) + "." + Bracket(table);
        }

        private static string EscapeLiteral(string value) => value.Replace("'", "''");
    }
}
