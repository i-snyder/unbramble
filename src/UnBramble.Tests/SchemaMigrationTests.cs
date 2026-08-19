using Microsoft.Data.Sqlite;
using UnBramble.Core.Config;
using UnBramble.Core.Store;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// Schema v4 adds columns to pre-existing tables (refs.target_type_name,
/// symbols.attrs/entry_reason) and a new table (name_hints) -- a materially different case from
/// v2-&gt;v3's purely additive new-table bump. `CREATE TABLE IF NOT EXISTS` is a no-op against an
/// already-existing table, so it cannot retrofit a new column onto an old-shaped one; the
/// pre-existing version-mismatch path only did `DELETE FROM` on three tables, which would have
/// left a stale old-shaped `refs` table underneath and made the very first
/// `INSERT INTO refs (..., target_type_name)` throw. Fixed by making a version mismatch drop
/// every data object first so the schema is always rebuilt from nothing. This test pins that fix
/// against a genuinely old-shaped table, not just a version-number bump.
/// </summary>
public class SchemaMigrationTests
{
    [Fact]
    public void VersionMismatch_AgainstOldShapedRefsTable_RebuildsSchemaCleanlyAndAcceptsNewColumns()
    {
        using var fixture = FixtureCopy.Create();
        var dbPath = UnBramblePaths.RelativeTo(fixture.Root, UnBramblePaths.DbRelativePath);

        using (var store = UnBrambleStore.OpenOrCreate(dbPath, "2022.3.30f1"))
        {
            Assert.True(store.WasCreated);
        }

        // Simulate a genuinely old (pre-v4) database on disk: a refs table with no
        // target_type_name column at all, and a stale schema_version.
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            Exec(conn, "DROP TABLE refs;");
            Exec(conn, """
                CREATE TABLE refs (
                  id             INTEGER PRIMARY KEY,
                  source_file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                  target_guid    TEXT NOT NULL,
                  line           INTEGER NOT NULL,
                  source_classid INTEGER,
                  source_fileid  TEXT,
                  method_name    TEXT,
                  context        TEXT
                );
                """);
            Exec(conn, "UPDATE meta_kv SET value = '3' WHERE key = 'schema_version';");
        }

        using (var store = UnBrambleStore.OpenOrCreate(dbPath, "2022.3.30f1"))
        {
            Assert.True(store.SchemaWasReset);
            Assert.False(store.WasCreated);

            // The rebuilt refs table must have the new column -- proof the old-shaped table was
            // actually replaced, not left stale underneath a CREATE TABLE IF NOT EXISTS no-op.
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(refs);";
            using var reader = cmd.ExecuteReader();
            var columns = new List<string>();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }

            Assert.Contains("target_type_name", columns);

            using var nameHintsCheck = conn.CreateCommand();
            nameHintsCheck.CommandText = "SELECT COUNT(*) FROM name_hints;"; // throws if the table doesn't exist
            Assert.Equal(0L, (long)nameHintsCheck.ExecuteScalar()!);
        }
    }

    /// <summary>
    /// Schema v6 adds `assemblies.mode_reason` (why a syntactic-mode assembly is syntactic — no
    /// generated csproj, an unusable one, or a parse failure). Same "CREATE TABLE IF NOT EXISTS
    /// can't retrofit a column" hazard as v4/v5: pins a genuine v5-shaped `assemblies` table
    /// (no `mode_reason` column) getting rebuilt cleanly rather than left stale underneath.
    /// </summary>
    [Fact]
    public void VersionMismatch_AgainstV5ShapedAssembliesTable_RebuildsSchemaWithModeReasonColumn()
    {
        using var fixture = FixtureCopy.Create();
        var dbPath = UnBramblePaths.RelativeTo(fixture.Root, UnBramblePaths.DbRelativePath);

        using (var store = UnBrambleStore.OpenOrCreate(dbPath, "2022.3.30f1"))
        {
            Assert.True(store.WasCreated);
        }

        // Simulate a genuine v5 database: an assemblies table with no mode_reason column, and a
        // stale schema_version.
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            Exec(conn, "DROP TABLE assemblies;");
            Exec(conn, """
                CREATE TABLE assemblies (
                  id             INTEGER PRIMARY KEY,
                  name           TEXT NOT NULL UNIQUE,
                  asmdef_file_id INTEGER REFERENCES files(id) ON DELETE SET NULL,
                  mode           TEXT NOT NULL,
                  analyzed_utc   TEXT,
                  csproj_mtime   INTEGER
                );
                """);
            Exec(conn, "UPDATE meta_kv SET value = '5' WHERE key = 'schema_version';");
        }

        using (var store = UnBrambleStore.OpenOrCreate(dbPath, "2022.3.30f1"))
        {
            Assert.True(store.SchemaWasReset);
            Assert.False(store.WasCreated);

            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(assemblies);";
            using var reader = cmd.ExecuteReader();
            var columns = new List<string>();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }

            Assert.Contains("mode_reason", columns);
        }
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
