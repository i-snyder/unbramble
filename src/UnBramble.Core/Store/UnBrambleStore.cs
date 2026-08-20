using System.Globalization;
using System.Linq;
using Microsoft.Data.Sqlite;
using UnBramble.Core.CSharp;
using UnBramble.Core.Freshness;
using UnBramble.Core.Model;
using UnBramble.Core.Parsing;
using UnBramble.Core.Query;
using UnBramble.Core.Scanning;

namespace UnBramble.Core.Store;

/// <summary>
/// SQLite-backed (WAL mode) identity store. First-pass DDL, designed to grow rather than be
/// replaced.
/// </summary>
public sealed class UnBrambleStore : IDisposable
{
    // v2: adds refs/path_refs/gameobjects/component_gameobject and the all_refs/unresolved_refs
    // views.
    // v3: adds assemblies/symbols/symbol_refs (the C# semantic graph). Purely additive, but bump
    // the version so an older DB opened by a v3 binary gets a clean rebuild rather than silently
    // missing C# data.
    // v4: adds refs.target_type_name, symbols.attrs, symbols.entry_reason, and the name_hints
    // table — the extraction-coverage prerequisites for liveness. Purely additive; same
    // drop-and-clean-rebuild pattern as v2/v3.
    // Also adds the cs_file_refs/unified_walk_edges views on top of v4 WITHOUT a version bump:
    // views carry no stored state and CREATE VIEW IF NOT EXISTS runs unconditionally on every
    // open (not gated on a version mismatch), so an existing v4 DB simply gains the new views in
    // place — no reindex needed for a query-time projection over data that's already correctly
    // there.
    // v5: adds assemblies.csproj_mtime, the per-assembly mode fingerprint RunCsAnalysis's skip
    // gate compares against a plain File.Exists/GetLastWriteTimeUtc stat (never a parse) to
    // detect a generated csproj appearing, disappearing, or changing without any script file
    // itself being dirty. Purely additive but still a version bump, same rule as v4: CREATE
    // TABLE IF NOT EXISTS can't retrofit a column onto an already-existing table.
    // v6: adds assemblies.mode_reason (CsModeReasons: no-csproj/csproj-unusable/
    // csproj-parse-failed, NULL for a semantic-mode assembly) so query/stats output can name WHY
    // an assembly is syntactic instead of just THAT it is. Purely additive, same version-bump rule.
    // v7: adds refs.property_path (best-effort dotted serialized-field path of the referencing
    // line, e.g. "m_Settings.m_VolumeProfile", "m_Materials[2]" — see YamlPropertyPathTracker;
    // NULL for meta/JSON/UI-Toolkit refs). Proving WHICH file
    // references an asset without naming the owning FIELD still forced a round-trip through
    // Unity's own API. Purely additive, same version-bump rule.
    // v8: adds dll_refs — `.asmdef` `precompiledReferences` entries, which name a managed plugin
    // assembly by FILE NAME with no guid and no path anywhere in the serialized form. Neither
    // existing edge table can hold one (`refs` is guid-keyed, `path_refs` resolves against
    // `files.path`), so before this they were not indexed at all: `who-uses SomePlugin.dll`
    // under-reported to 0, and — worse — a plugin DLL is an ordinary `dead-candidates` candidate,
    // so one referenced only this way had no inbound edge and could be emitted as `provenDead`.
    // Purely additive, same version-bump rule as v4-v7 (a new table would sit empty against an
    // existing DB until something forced a reindex; the bump IS that force).
    // v9: adds a persisted build-reachability cache. Query-time reachability over a 100k-file
    // graph took seconds even though the requested reverse GUID lookup itself was indexed. The
    // cache is invalidated whenever inventory/serialized/C# graph data changes; persisting it is
    // what lets separate one-shot agent invocations share the same proven result safely.
    // v10: adds a graph generation to the reachability-cache state. A concurrent writer can now
    // invalidate a computation before it is published instead of allowing stale derived state
    // to become valid again after the writer commits.
    public const int CurrentSchemaVersion = 10;

    private const string BuiltinGuidE = "0000000000000000e000000000000000";
    private const string BuiltinGuidF = "0000000000000000f000000000000000";

    private readonly SqliteConnection _connection;

    public string DbPath { get; }

    public bool WasCreated { get; private set; }

    public bool SchemaWasReset { get; private set; }

    private UnBrambleStore(SqliteConnection connection, string dbPath)
    {
        _connection = connection;
        DbPath = dbPath;
    }

    public static UnBrambleStore OpenOrCreate(string dbPath, string unityVersion, string projectRoot)
    {
        // Required under NativeAOT — Microsoft.Data.Sqlite needs an explicit provider init.
        SQLitePCL.Batteries_V2.Init();

        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();

        // Install the busy handler before anything that can need a lock. journal_mode used to
        // run first, so a healthy concurrent writer could produce an immediate SQLITE_BUSY
        // before the configured timeout even existed.
        ExecuteNonQuery(connection, "PRAGMA busy_timeout=5000;");
        ExecuteNonQuery(connection, "PRAGMA foreign_keys=ON;");

        var store = new UnBrambleStore(connection, dbPath);
        var currentVersionText = CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture);
        var journalMode = QueryScalar(connection, "PRAGMA journal_mode;");

        // The steady-state open path must be genuinely read-only. CREATE ... IF NOT EXISTS and
        // the unity_version refresh used to take SQLite's writer lock on every query command,
        // defeating WAL and making even `stats` fail while another process indexed. A matching
        // schema stamp is the contract that all current objects already exist.
        if (string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase)
            && string.Equals(store.TryQuerySchemaVersion(), currentVersionText, StringComparison.Ordinal))
        {
            ExecuteNonQuery(connection, "PRAGMA synchronous=NORMAL;");
            return store;
        }

        // New databases, journal conversion, and schema rebuilds are real writes. Serialize them
        // with every other index mutation, then re-check after waiting because the previous owner
        // may have completed the exact setup we needed.
        using var schemaLease = IndexWriterLock.Acquire(projectRoot);
        journalMode = QueryScalar(connection, "PRAGMA journal_mode;");
        if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            ExecuteNonQuery(connection, "PRAGMA journal_mode=WAL;");
        }
        // NORMAL is corruption-safe with WAL (fsync is skipped on every commit, only at
        // checkpoints) -- the DB is a rebuildable cache under Library/, and `init`/`index --full`
        // already exist as the recovery path on any corruption, so FULL's extra durability buys
        // nothing here.
        ExecuteNonQuery(connection, "PRAGMA synchronous=NORMAL;");
        if (!string.Equals(store.TryQuerySchemaVersion(), currentVersionText, StringComparison.Ordinal))
        {
            store.EnsureSchema(unityVersion);
        }
        return store;
    }

    /// <summary>Compatibility overload for focused store tests/tools whose database uses the
    /// default <c>&lt;project&gt;/.unbramble/unbramble.db</c> placement.</summary>
    public static UnBrambleStore OpenOrCreate(string dbPath, string unityVersion)
    {
        var stateDirectory = Path.GetDirectoryName(Path.GetFullPath(dbPath))!;
        var projectRoot = string.Equals(Path.GetFileName(stateDirectory), Config.UnBramblePaths.StateDirName, StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(stateDirectory)!.FullName
            : stateDirectory;
        return OpenOrCreate(dbPath, unityVersion, projectRoot);
    }

    private string? TryQuerySchemaVersion()
    {
        using var exists = _connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'meta_kv';";
        if (exists.ExecuteScalar() is null)
        {
            return null;
        }

        return QueryMetaValue("schema_version");
    }

    private static string? QueryScalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString();
    }

    private void EnsureSchema(string unityVersion)
    {
        // meta_kv's own shape never changes across schema versions, so it's safe to create
        // before the version check below — that check needs somewhere to read the stored
        // version from.
        ExecuteNonQuery(_connection, """
            CREATE TABLE IF NOT EXISTS meta_kv (
              key   TEXT PRIMARY KEY,
              value TEXT NOT NULL
            ) WITHOUT ROWID;
            """);

        var existingVersion = QueryMetaValue("schema_version");
        var currentVersionText = CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture);

        if (existingVersion is not null && existingVersion != currentVersionText)
        {
            // A version bump can mean more than new tables (v4 adds columns to the
            // pre-existing refs/symbols tables) — CREATE TABLE IF NOT EXISTS below is a no-op
            // against an already-existing table, so it cannot retrofit a new column onto an
            // old-shaped one. Drop every data object first so the CREATE block below always
            // builds the CURRENT shape from a clean slate. No migration machinery: a version
            // mismatch means a full reindex, always.
            SchemaWasReset = true;
            DropDataObjects();
        }

        ExecuteNonQuery(_connection, """
            CREATE TABLE IF NOT EXISTS roots (
              real_path      TEXT PRIMARY KEY COLLATE NOCASE,
              project_prefix TEXT NOT NULL
            );
            """);

        ExecuteNonQuery(_connection, """
            CREATE TABLE IF NOT EXISTS files (
              id            INTEGER PRIMARY KEY,
              path          TEXT NOT NULL UNIQUE COLLATE NOCASE,
              guid          TEXT,
              kind          TEXT NOT NULL,
              mtime         INTEGER NOT NULL,
              size          INTEGER NOT NULL,
              meta_mtime    INTEGER,
              identity_only INTEGER NOT NULL DEFAULT 0
            );
            """);

        ExecuteNonQuery(_connection, "CREATE INDEX IF NOT EXISTS idx_files_guid ON files(guid) WHERE guid IS NOT NULL;");

        ExecuteNonQuery(_connection, """
            CREATE TABLE IF NOT EXISTS build_reachable_cache (
              file_id INTEGER PRIMARY KEY REFERENCES files(id) ON DELETE CASCADE
            );
            """);
        ExecuteNonQuery(_connection, """
            CREATE TABLE IF NOT EXISTS build_reachable_state (
              id               INTEGER PRIMARY KEY CHECK (id = 1),
              valid            INTEGER NOT NULL,
              config_key       TEXT,
              graph_generation INTEGER NOT NULL
            );
            """);
        ExecuteNonQuery(_connection, "INSERT OR IGNORE INTO build_reachable_state (id, valid, config_key, graph_generation) VALUES (1, 0, NULL, 0);");

        ExecuteNonQuery(_connection, """
            CREATE TABLE IF NOT EXISTS refs (
              id              INTEGER PRIMARY KEY,
              source_file_id  INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
              target_guid     TEXT NOT NULL,          -- 32 lowercase hex; NEVER resolved at parse time
              line            INTEGER NOT NULL,
              source_classid  INTEGER,                -- NULL for JSON/meta sources
              source_fileid   TEXT,                   -- document anchor; TEXT (can be negative/64-bit)
              method_name     TEXT,
              context         TEXT,
              target_type_name TEXT,                  -- v4: raw m_TargetAssemblyTypeName (e.g. "Foo, Game"), guid-carrying UnityEvent calls only
              property_path   TEXT                    -- v7: dotted serialized-field path ("m_Settings.m_VolumeProfile"); YAML sources only, best-effort display metadata
            );
            """);
        ExecuteNonQuery(_connection, "CREATE INDEX IF NOT EXISTS idx_refs_target ON refs(target_guid);");
        ExecuteNonQuery(_connection, "CREATE INDEX IF NOT EXISTS idx_refs_source ON refs(source_file_id);");

        ExecuteNonQuery(_connection, """
            CREATE TABLE IF NOT EXISTS path_refs (
              id              INTEGER PRIMARY KEY,
              source_file_id  INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
              target_path_raw  TEXT NOT NULL,
              target_path_norm TEXT NOT NULL COLLATE NOCASE,
              line            INTEGER NOT NULL,
              context         TEXT
            );
            """);
        ExecuteNonQuery(_connection, "CREATE INDEX IF NOT EXISTS idx_path_refs_norm ON path_refs(target_path_norm);");
        ExecuteNonQuery(_connection, "CREATE INDEX IF NOT EXISTS idx_path_refs_source ON path_refs(source_file_id);");

        // `.asmdef` precompiledReferences: a plugin assembly referenced by FILE NAME (see the v8
        // schema note). Deliberately NOT a branch of `all_refs`: matching a bare file name against
        // `files.path` needs a leading-wildcard LIKE, which no index can serve, and `all_refs` is
        // read by every query — one unindexable branch there would tax the whole verb set to serve
        // a handful of rows. Merged at query time in C# instead, exactly like UnityEvent links
        // (see UnBrambleEngine.WhoUses / Uses), which are also real edges that live outside the
        // view. The common direction is cheap and indexed: "who references THIS dll" is a lookup
        // on target_name_norm.
        ExecuteNonQuery(_connection, """
            CREATE TABLE IF NOT EXISTS dll_refs (
              id               INTEGER PRIMARY KEY,
              source_file_id   INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
              target_name_raw  TEXT NOT NULL,
              target_name_norm TEXT NOT NULL COLLATE NOCASE,   -- lowercased file name; NEVER resolved at parse time
              line             INTEGER NOT NULL,
              context          TEXT
            );
            """);
        ExecuteNonQuery(_connection, "CREATE INDEX IF NOT EXISTS idx_dll_refs_norm ON dll_refs(target_name_norm);");
        ExecuteNonQuery(_connection, "CREATE INDEX IF NOT EXISTS idx_dll_refs_source ON dll_refs(source_file_id);");

        ExecuteNonQuery(_connection, """
            CREATE TABLE IF NOT EXISTS gameobjects (
              file_id   INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
              go_fileid TEXT NOT NULL,
              name      TEXT NOT NULL,
              PRIMARY KEY (file_id, go_fileid)
            );
            """);

        ExecuteNonQuery(_connection, """
            CREATE TABLE IF NOT EXISTS component_gameobject (
              file_id          INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
              component_fileid TEXT NOT NULL,
              go_fileid        TEXT NOT NULL,
              PRIMARY KEY (file_id, component_fileid)
            );
            """);

        // THE unified edge view. Two load-bearing details:
        //  (1) LEFT JOIN: unresolved/external guid refs must still surface in output.
        //  (2) target_file_id resolved AT QUERY TIME for both kinds.
        ExecuteNonQuery(_connection, """
            CREATE VIEW IF NOT EXISTS all_refs AS
            SELECT r.source_file_id,
                   'guid' AS kind,
                   r.target_guid   AS target_key,
                   tf.id           AS target_file_id,
                   r.line, r.context, r.method_name, r.source_classid, r.source_fileid,
                   r.property_path
            FROM refs r
            LEFT JOIN files tf ON tf.guid = r.target_guid
            UNION ALL
            SELECT p.source_file_id,
                   'path',
                   p.target_path_norm,
                   tf.id,
                   p.line, p.context, NULL, NULL, NULL,
                   NULL
            FROM path_refs p
            LEFT JOIN files tf ON tf.path = p.target_path_norm;
            """);

        // ONE canonical unresolved query. uses --missing-only, stats --unresolved, and
        // index's summary line all consume THIS — parallel ad-hoc counts drifted apart
        // repeatedly during design review.
        ExecuteNonQuery(_connection, $"""
            CREATE VIEW IF NOT EXISTS unresolved_refs AS
            SELECT * FROM all_refs
            WHERE target_file_id IS NULL
              AND NOT (kind = 'guid' AND target_key IN
                       ('{BuiltinGuidE}', '{BuiltinGuidF}'));
            """);

        ExecuteNonQuery(_connection, """
            CREATE TABLE IF NOT EXISTS assemblies (
              id             INTEGER PRIMARY KEY,
              name           TEXT NOT NULL UNIQUE,
              asmdef_file_id INTEGER REFERENCES files(id) ON DELETE SET NULL,
              mode           TEXT NOT NULL,
              analyzed_utc   TEXT,
              csproj_mtime   INTEGER,         -- v5: mode fingerprint, ticks or NULL (no csproj on disk)
              mode_reason    TEXT             -- v6: CsModeReasons value, NULL when mode='semantic'
            );
            """);

        ExecuteNonQuery(_connection, """
            CREATE TABLE IF NOT EXISTS symbols (
              id           INTEGER PRIMARY KEY,
              assembly_id  INTEGER NOT NULL REFERENCES assemblies(id) ON DELETE CASCADE,
              file_id      INTEGER REFERENCES files(id) ON DELETE CASCADE,
              kind         TEXT NOT NULL,
              doc_id       TEXT NOT NULL,
              name         TEXT NOT NULL,
              line         INTEGER,
              is_entry_point INTEGER NOT NULL DEFAULT 0,
              attrs        TEXT,                     -- v4: space-joined attribute simple names, types/methods only
              entry_reason TEXT                       -- v4: 'lifecycle'|'attribute'|'main', NULL when is_entry_point = 0
            );
            """);
        ExecuteNonQuery(_connection, "CREATE INDEX IF NOT EXISTS idx_symbols_docid ON symbols(doc_id);");
        ExecuteNonQuery(_connection, "CREATE INDEX IF NOT EXISTS idx_symbols_file  ON symbols(file_id);");

        ExecuteNonQuery(_connection, """
            CREATE TABLE IF NOT EXISTS symbol_refs (
              id               INTEGER PRIMARY KEY,
              source_file_id   INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
              source_symbol_id INTEGER REFERENCES symbols(id) ON DELETE CASCADE,
              target_doc_id    TEXT NOT NULL,
              ref_kind         TEXT NOT NULL,
              line             INTEGER NOT NULL,
              confidence       TEXT NOT NULL
            );
            """);
        ExecuteNonQuery(_connection, "CREATE INDEX IF NOT EXISTS idx_symbol_refs_target ON symbol_refs(target_doc_id);");
        ExecuteNonQuery(_connection, "CREATE INDEX IF NOT EXISTS idx_symbol_refs_source ON symbol_refs(source_file_id);");

        // NOT a third edge store: no target column exists, and this table is never joined into
        // any closure/who-uses result as an edge. Purely negative evidence (by-name dispatch
        // literals, disabled-region identifiers, animation events, guid-less UnityEvent bindings)
        // for the liveness screen. Follows the same delete-then-insert-per-source-file discipline
        // as every other derived table.
        ExecuteNonQuery(_connection, """
            CREATE TABLE IF NOT EXISTS name_hints (
              id             INTEGER PRIMARY KEY,
              source_file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
              name           TEXT NOT NULL,
              kind           TEXT NOT NULL,          -- 'cs-name-literal'|'cs-disabled'|'anim-event'|'unityevent-local'
              line           INTEGER NOT NULL,
              type_name      TEXT                    -- 'unityevent-local' rows only (m_TargetAssemblyTypeName's type part); NULL otherwise
            );
            """);
        ExecuteNonQuery(_connection, "CREATE INDEX IF NOT EXISTS idx_name_hints_name ON name_hints(name);");
        ExecuteNonQuery(_connection, "CREATE INDEX IF NOT EXISTS idx_name_hints_source ON name_hints(source_file_id);");

        // THE seam, as views, not a third edge store. cs_file_refs projects symbol_refs to
        // file->file edges at QUERY time (the doc_id -> declaring-file resolution happens here,
        // against symbols — the same lesson as path_refs: never bake a target row id at parse
        // time). Self-edges excluded, same as all_refs. unified_walk_edges is the lean union the
        // recursive CTEs actually walk — DISTINCT collapses the many-symbol-refs-per-file-pair
        // fan-out so closure cost stays bounded by file-pair count, not symbol-ref count. This is
        // ALSO the liveness propagation relation dead-candidates depends on for its core safety
        // argument, so it must stay exact, not approximated.
        ExecuteNonQuery(_connection, """
            CREATE VIEW IF NOT EXISTS cs_file_refs AS
            SELECT sr.source_file_id,
                   'cs'            AS kind,
                   sr.target_doc_id AS target_key,
                   tgt.file_id     AS target_file_id,
                   sr.line,
                   sr.ref_kind,
                   sr.confidence
            FROM symbol_refs sr
            JOIN symbols tgt ON tgt.doc_id = sr.target_doc_id
            WHERE tgt.file_id IS NOT NULL
              AND tgt.file_id != sr.source_file_id;
            """);

        ExecuteNonQuery(_connection, """
            CREATE VIEW IF NOT EXISTS unified_walk_edges AS
            SELECT source_file_id, target_file_id FROM all_refs WHERE target_file_id IS NOT NULL
            UNION
            SELECT DISTINCT source_file_id, target_file_id FROM cs_file_refs;
            """);

        if (existingVersion is null)
        {
            WasCreated = true;
            SetMetaValue("schema_version", currentVersionText);
            SetMetaValue("unity_version", unityVersion);
            SetMetaValue("created_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
        else if (existingVersion != currentVersionText)
        {
            SetMetaValue("schema_version", currentVersionText);
            SetMetaValue("unity_version", unityVersion);
            SetMetaValue("created_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
        else
        {
            // Keep unity_version fresh; the project may have been upgraded since last index.
            SetMetaValue("unity_version", unityVersion);
        }
    }

    /// <summary>
    /// Drops every data table and the views over them (used for a version-mismatch rebuild,
    /// see <see cref="EnsureSchema"/>). Safe to call unconditionally before the CREATE TABLE IF
    /// NOT EXISTS block runs — that block always rebuilds the CURRENT shape from nothing.
    /// </summary>
    private void DropDataObjects()
    {
        ExecuteNonQuery(_connection, "DROP VIEW IF EXISTS unified_walk_edges;");
        ExecuteNonQuery(_connection, "DROP VIEW IF EXISTS cs_file_refs;");
        ExecuteNonQuery(_connection, "DROP VIEW IF EXISTS unresolved_refs;");
        ExecuteNonQuery(_connection, "DROP VIEW IF EXISTS all_refs;");
        ExecuteNonQuery(_connection, "DROP TABLE IF EXISTS name_hints;");
        ExecuteNonQuery(_connection, "DROP TABLE IF EXISTS build_reachable_state;");
        ExecuteNonQuery(_connection, "DROP TABLE IF EXISTS build_reachable_cache;");
        ExecuteNonQuery(_connection, "DROP TABLE IF EXISTS symbol_refs;");
        ExecuteNonQuery(_connection, "DROP TABLE IF EXISTS symbols;");
        ExecuteNonQuery(_connection, "DROP TABLE IF EXISTS assemblies;");
        ExecuteNonQuery(_connection, "DROP TABLE IF EXISTS component_gameobject;");
        ExecuteNonQuery(_connection, "DROP TABLE IF EXISTS gameobjects;");
        ExecuteNonQuery(_connection, "DROP TABLE IF EXISTS dll_refs;");
        ExecuteNonQuery(_connection, "DROP TABLE IF EXISTS path_refs;");
        ExecuteNonQuery(_connection, "DROP TABLE IF EXISTS refs;");
        ExecuteNonQuery(_connection, "DROP TABLE IF EXISTS roots;");
        ExecuteNonQuery(_connection, "DROP TABLE IF EXISTS files;");
    }

    /// <summary>Drops all data tables' contents (used by `index --full`).</summary>
    public void ResetData()
    {
        InvalidateBuildReachabilityCache();
        ExecuteNonQuery(_connection, "DELETE FROM files;");
        ExecuteNonQuery(_connection, "DELETE FROM roots;");
        ExecuteNonQuery(_connection, "DELETE FROM assemblies;");
    }

    /// <summary>
    /// Diffs a fresh scan against the current files table and applies the minimal set of
    /// writes. Deletions are applied before insertions within the batch (renames otherwise
    /// look like guid collisions). A path whose guid changed is deleted-then-reinserted
    /// (routes it through guid-collision detection) rather than updated in place; a path
    /// whose only mtime/size/meta_mtime changed is updated in place.
    /// </summary>
    public SweepDiff ApplySweep(ScanResult scan) => ApplySweep(scan, LoadAllFiles());

    /// <summary>
    /// Overload accepting an already-loaded path-&gt;row snapshot so a caller that hoisted
    /// <see cref="LoadAllFiles"/> earlier in the same sweep (to feed the scanner's meta
    /// mtime-gate) doesn't pay for a second identical full-table load here. Internal — the
    /// public single-argument overload above is the one
    /// external callers (and tests) use; it still loads for itself, unchanged.
    /// </summary>
    internal SweepDiff ApplySweep(ScanResult scan, Dictionary<string, FileRow> existing)
    {
        var scannedByPath = new Dictionary<string, ScannedFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in scan.Entries)
        {
            scannedByPath[entry.Path] = entry;
        }

        return ApplyDiff(existing, scannedByPath);
    }

    /// <summary>
    /// Targeted variant of <see cref="ApplySweep"/> for the watcher's per-batch push updates:
    /// diffs only <paramref name="consideredPaths"/> against
    /// their current rows, rather than a full-inventory comparison against every file in the
    /// project. A considered path with an existing row but no corresponding entry in
    /// <paramref name="entries"/> is treated as removed — this covers both real on-disk
    /// deletions and paths the watcher's own single-file scan rejected (e.g. now hidden). Same
    /// insert/update/rebuild/collision discipline as <see cref="ApplySweep"/> — they share
    /// <see cref="ApplyDiff"/>.
    /// </summary>
    public SweepDiff ApplyTargetedDiff(IReadOnlyCollection<string> consideredPaths, IReadOnlyList<ScannedFileEntry> entries) =>
        ApplyTargetedDiff(consideredPaths, entries, LoadAllFiles());

    /// <summary>Counterpart of the <see cref="ApplySweep"/> overload above: reuses an
    /// already-loaded full snapshot instead of loading it again here.</summary>
    internal SweepDiff ApplyTargetedDiff(IReadOnlyCollection<string> consideredPaths, IReadOnlyList<ScannedFileEntry> entries, Dictionary<string, FileRow> allFiles)
    {
        var existing = new Dictionary<string, FileRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in LoadFileRows(consideredPaths, allFiles))
        {
            existing[row.Path] = row;
        }

        var scannedByPath = new Dictionary<string, ScannedFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            scannedByPath[entry.Path] = entry;
        }

        return ApplyDiff(existing, scannedByPath);
    }

    private SweepDiff ApplyDiff(Dictionary<string, FileRow> existing, Dictionary<string, ScannedFileEntry> scannedByPath)
    {
        var removedIds = new List<long>();
        // A removal never shows up in DirtyPaths (only added/changed paths do), so this is the
        // only signal RunCsAnalysis's skip gate has for
        // "a script/asmdef/asmref vanished from disk this sweep" -- without it, a deleted script
        // could leave stale symbol/assembly rows behind forever whenever nothing else C#-related
        // happened to be dirty in the same sweep.
        var removedCsRelevant = false;
        foreach (var (path, row) in existing)
        {
            if (!scannedByPath.ContainsKey(path))
            {
                removedIds.Add(row.Id);
                if (!removedCsRelevant && CsRelevantPaths.IsCsRelevant(path))
                {
                    removedCsRelevant = true;
                }
            }
        }

        var rebuildOldIds = new List<long>();
        var rebuildInserts = new List<ScannedFileEntry>();
        var updates = new List<(long Id, ScannedFileEntry Entry)>();
        var newInserts = new List<ScannedFileEntry>();

        foreach (var entry in scannedByPath.Values)
        {
            if (existing.TryGetValue(entry.Path, out var row))
            {
                var guidChanged = !string.Equals(row.Guid, entry.Guid, StringComparison.OrdinalIgnoreCase);
                var otherChanged = row.Mtime != entry.Mtime
                    || row.Size != entry.Size
                    || row.MetaMtime != entry.MetaMtime
                    || row.Kind != entry.Kind
                    || row.IdentityOnly != entry.IdentityOnly;

                if (guidChanged)
                {
                    rebuildOldIds.Add(row.Id);
                    rebuildInserts.Add(entry);
                }
                else if (otherChanged)
                {
                    updates.Add((row.Id, entry));
                }
            }
            else
            {
                newInserts.Add(entry);
            }
        }

        // Nothing to write -- skip opening a transaction at all rather than committing an empty
        // one. Equivalent output either way (all counts
        // zero, no warnings possible since warnings only ever come from a guid collision on an
        // insert, and there are none here).
        if (removedIds.Count == 0 && rebuildOldIds.Count == 0 && updates.Count == 0 && newInserts.Count == 0)
        {
            return new SweepDiff(0, 0, 0, [], [], RemovedCsRelevant: false);
        }

        var warnings = new List<string>();
        var guidCollisions = new List<string>();

        using var transaction = _connection.BeginTransaction();
        ExecuteNonQuery(_connection, "UPDATE build_reachable_state SET valid = 0, graph_generation = graph_generation + 1 WHERE id = 1;", transaction);

        DeleteFiles(removedIds, transaction);
        DeleteFiles(rebuildOldIds, transaction);

        foreach (var (id, entry) in updates)
        {
            UpdateFile(id, entry, transaction);
        }

        foreach (var entry in newInserts.Concat(rebuildInserts))
        {
            if (entry.Guid is not null)
            {
                var conflicting = FindPathsByGuid(entry.Guid, transaction);
                if (conflicting.Count > 0)
                {
                    guidCollisions.Add(
                        $"guid '{entry.Guid}' collision between '{conflicting[0]}' and '{entry.Path}' — both exist on disk");
                }
            }

            InsertFile(entry, transaction);
        }

        transaction.Commit();

        AppendGuidCollisionWarnings(warnings, guidCollisions);

        var dirtyPaths = new List<string>(newInserts.Count + updates.Count + rebuildInserts.Count);
        dirtyPaths.AddRange(newInserts.Select(e => e.Path));
        dirtyPaths.AddRange(updates.Select(u => u.Entry.Path));
        dirtyPaths.AddRange(rebuildInserts.Select(e => e.Path));

        return new SweepDiff(newInserts.Count, updates.Count + rebuildOldIds.Count, removedIds.Count, warnings, dirtyPaths, removedCsRelevant, AnyGuidRebuild: rebuildOldIds.Count > 0);
    }

    /// <summary>Inline cap for per-collision sweep warnings. Past it, they compact to one
    /// counted line pointing at `stats --collisions`: a first index
    /// of a duplicate-heavy project printed one warning per colliding guid, flooding the setup
    /// output with lines nobody can act on mid-index. The detail isn't persisted anywhere
    /// separate: `stats --collisions` derives the CURRENT collision state straight from `files`
    /// (see <see cref="GetGuidCollisionGroups"/>), so there is no side file to go stale.</summary>
    private const int InlineGuidCollisionCap = 3;

    private static void AppendGuidCollisionWarnings(List<string> warnings, List<string> collisions)
    {
        if (collisions.Count == 0)
        {
            return;
        }

        if (collisions.Count <= InlineGuidCollisionCap)
        {
            warnings.AddRange(collisions.Select(c => "warning: " + c));
            return;
        }

        warnings.Add(
            $"warning: {collisions.Count} guid collisions found this sweep — the same guid on multiple on-disk files, " +
            "so references to it resolve to one of them arbitrarily (typically duplicated folders or copied packages). " +
            "List them: `unbramble stats --collisions`.");
    }

    /// <summary>One guid claimed by more than one indexed file, with every claimant path.</summary>
    public sealed record GuidCollisionGroup(string Guid, IReadOnlyList<string> Paths);

    /// <summary>Every current guid collision, derived live from `files` on each call — the data
    /// source for `stats --collisions` and the compacted sweep warning's pointer target. Never
    /// cached or persisted: collisions appear/disappear as files do, and a stale side artifact
    /// would just be one more thing to distrust.</summary>
    public IReadOnlyList<GuidCollisionGroup> GetGuidCollisionGroups()
    {
        const string sql = """
            SELECT guid, path FROM files
            WHERE guid IN (SELECT guid FROM files WHERE guid IS NOT NULL GROUP BY guid HAVING COUNT(*) > 1)
            ORDER BY guid, path;
            """;
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var groups = new List<GuidCollisionGroup>();
        string? currentGuid = null;
        List<string>? currentPaths = null;
        while (reader.Read())
        {
            var guid = reader.GetString(0);
            if (guid != currentGuid)
            {
                currentGuid = guid;
                currentPaths = [];
                groups.Add(new GuidCollisionGroup(guid, currentPaths));
            }

            currentPaths!.Add(reader.GetString(1));
        }

        return groups;
    }

    /// <summary>
    /// SQLite's <c>PRAGMA data_version</c> for this store's connection: the value moves if and
    /// only if some OTHER connection committed a change to the database file since the last read
    /// — this connection's own writes never move it for itself. Used by
    /// <see cref="UnBramble.Core.CSharp.CsSessionModelCache"/> as a cheap cross-process
    /// invalidation guard: a long-lived engine (the `watch` command) caching file-id-bearing
    /// state across passes must detect a concurrent `unbramble index` from another process, which
    /// rewrites file rows without any diff this engine ever sees.
    /// </summary>
    public long GetDataVersion()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA data_version;";
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>Fully replaces the persisted real-root -> project-prefix mapping.</summary>
    public void ReplaceRoots(IReadOnlyList<RootMapping> mappings)
    {
        using var transaction = _connection.BeginTransaction();

        using (var delete = _connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM roots;";
            delete.ExecuteNonQuery();
        }

        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR REPLACE INTO roots (real_path, project_prefix) VALUES (@real, @prefix);";
            var realParam = insert.Parameters.Add("@real", SqliteType.Text);
            var prefixParam = insert.Parameters.Add("@prefix", SqliteType.Text);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mapping in mappings)
            {
                if (!seen.Add(mapping.RealPath))
                {
                    continue;
                }

                realParam.Value = mapping.RealPath;
                prefixParam.Value = mapping.ProjectPrefix;
                insert.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    /// <summary>Loads the current file rows for a set of paths (used to reparse dirty files after a sweep).</summary>
    internal IReadOnlyList<FileRow> LoadFileRows(IReadOnlyCollection<string> paths) => LoadFileRows(paths, LoadAllFiles());

    /// <summary>Counterpart: filters an already-loaded snapshot instead of loading again.</summary>
    internal static IReadOnlyList<FileRow> LoadFileRows(IReadOnlyCollection<string> paths, Dictionary<string, FileRow> allFiles)
    {
        if (paths.Count == 0)
        {
            return [];
        }

        var wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        return [.. allFiles.Values.Where(r => wanted.Contains(r.Path))];
    }

    /// <summary>
    /// Replaces the derived rows (refs/path_refs/gameobjects/component_gameobject) for a set
    /// of files in one transaction. Deletes-before-inserts within the batch, same discipline
    /// as <see cref="ApplySweep"/>. Safe to call for freshly-inserted files (nothing to
    /// delete) and for in-place-updated files (old derived rows are NOT auto-cleared by FK
    /// cascade since the files row itself was not deleted).
    /// </summary>
    public void ReplaceFileReferences(IReadOnlyList<(long FileId, ParsedFileRefs Refs)> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        using var transaction = _connection.BeginTransaction();
        ExecuteNonQuery(_connection, "UPDATE build_reachable_state SET valid = 0, graph_generation = graph_generation + 1 WHERE id = 1;", transaction);

        ExecuteDeleteByFileId(transaction, "DELETE FROM refs WHERE source_file_id = @id;", items.Select(i => i.FileId));
        ExecuteDeleteByFileId(transaction, "DELETE FROM path_refs WHERE source_file_id = @id;", items.Select(i => i.FileId));
        ExecuteDeleteByFileId(transaction, "DELETE FROM dll_refs WHERE source_file_id = @id;", items.Select(i => i.FileId));
        ExecuteDeleteByFileId(transaction, "DELETE FROM gameobjects WHERE file_id = @id;", items.Select(i => i.FileId));
        ExecuteDeleteByFileId(transaction, "DELETE FROM component_gameobject WHERE file_id = @id;", items.Select(i => i.FileId));
        ExecuteDeleteByFileId(transaction, "DELETE FROM name_hints WHERE source_file_id = @id;", items.Select(i => i.FileId));

        using (var insertRef = _connection.CreateCommand())
        {
            insertRef.Transaction = transaction;
            insertRef.CommandText = """
                INSERT INTO refs (source_file_id, target_guid, line, source_classid, source_fileid, method_name, context, target_type_name, property_path)
                VALUES (@fileId, @guid, @line, @classId, @docFileId, @method, @context, @targetTypeName, @propertyPath);
                """;
            var fileIdP = insertRef.Parameters.Add("@fileId", SqliteType.Integer);
            var guidP = insertRef.Parameters.Add("@guid", SqliteType.Text);
            var lineP = insertRef.Parameters.Add("@line", SqliteType.Integer);
            var classIdP = insertRef.Parameters.Add("@classId", SqliteType.Integer);
            var docFileIdP = insertRef.Parameters.Add("@docFileId", SqliteType.Text);
            var methodP = insertRef.Parameters.Add("@method", SqliteType.Text);
            var contextP = insertRef.Parameters.Add("@context", SqliteType.Text);
            var targetTypeNameP = insertRef.Parameters.Add("@targetTypeName", SqliteType.Text);
            var propertyPathP = insertRef.Parameters.Add("@propertyPath", SqliteType.Text);

            foreach (var (fileId, refs) in items)
            {
                foreach (var r in refs.GuidRefs)
                {
                    fileIdP.Value = fileId;
                    guidP.Value = r.TargetGuid;
                    lineP.Value = r.Line;
                    classIdP.Value = (object?)r.SourceClassId ?? DBNull.Value;
                    docFileIdP.Value = (object?)r.SourceFileId ?? DBNull.Value;
                    methodP.Value = (object?)r.MethodName ?? DBNull.Value;
                    contextP.Value = (object?)r.Context ?? DBNull.Value;
                    targetTypeNameP.Value = (object?)r.TargetTypeName ?? DBNull.Value;
                    propertyPathP.Value = (object?)r.PropertyPath ?? DBNull.Value;
                    insertRef.ExecuteNonQuery();
                }
            }
        }

        using (var insertPathRef = _connection.CreateCommand())
        {
            insertPathRef.Transaction = transaction;
            insertPathRef.CommandText = """
                INSERT INTO path_refs (source_file_id, target_path_raw, target_path_norm, line, context)
                VALUES (@fileId, @raw, @norm, @line, @context);
                """;
            var fileIdP = insertPathRef.Parameters.Add("@fileId", SqliteType.Integer);
            var rawP = insertPathRef.Parameters.Add("@raw", SqliteType.Text);
            var normP = insertPathRef.Parameters.Add("@norm", SqliteType.Text);
            var lineP = insertPathRef.Parameters.Add("@line", SqliteType.Integer);
            var contextP = insertPathRef.Parameters.Add("@context", SqliteType.Text);

            foreach (var (fileId, refs) in items)
            {
                foreach (var p in refs.PathRefs)
                {
                    fileIdP.Value = fileId;
                    rawP.Value = p.TargetPathRaw;
                    normP.Value = p.TargetPathNorm;
                    lineP.Value = p.Line;
                    contextP.Value = (object?)p.Context ?? DBNull.Value;
                    insertPathRef.ExecuteNonQuery();
                }
            }
        }

        using (var insertDllRef = _connection.CreateCommand())
        {
            insertDllRef.Transaction = transaction;
            insertDllRef.CommandText = """
                INSERT INTO dll_refs (source_file_id, target_name_raw, target_name_norm, line, context)
                VALUES (@fileId, @raw, @norm, @line, @context);
                """;
            var fileIdP = insertDllRef.Parameters.Add("@fileId", SqliteType.Integer);
            var rawP = insertDllRef.Parameters.Add("@raw", SqliteType.Text);
            var normP = insertDllRef.Parameters.Add("@norm", SqliteType.Text);
            var lineP = insertDllRef.Parameters.Add("@line", SqliteType.Integer);
            var contextP = insertDllRef.Parameters.Add("@context", SqliteType.Text);

            foreach (var (fileId, refs) in items)
            {
                foreach (var d in refs.DllRefs)
                {
                    fileIdP.Value = fileId;
                    rawP.Value = d.TargetNameRaw;
                    normP.Value = d.TargetNameNorm;
                    lineP.Value = d.Line;
                    contextP.Value = (object?)d.Context ?? DBNull.Value;
                    insertDllRef.ExecuteNonQuery();
                }
            }
        }

        using (var insertGo = _connection.CreateCommand())
        {
            insertGo.Transaction = transaction;
            insertGo.CommandText = "INSERT OR REPLACE INTO gameobjects (file_id, go_fileid, name) VALUES (@fileId, @goFileId, @name);";
            var fileIdP = insertGo.Parameters.Add("@fileId", SqliteType.Integer);
            var goFileIdP = insertGo.Parameters.Add("@goFileId", SqliteType.Text);
            var nameP = insertGo.Parameters.Add("@name", SqliteType.Text);

            foreach (var (fileId, refs) in items)
            {
                foreach (var g in refs.GameObjects)
                {
                    fileIdP.Value = fileId;
                    goFileIdP.Value = g.GoFileId;
                    nameP.Value = g.Name;
                    insertGo.ExecuteNonQuery();
                }
            }
        }

        using (var insertLink = _connection.CreateCommand())
        {
            insertLink.Transaction = transaction;
            insertLink.CommandText = """
                INSERT OR REPLACE INTO component_gameobject (file_id, component_fileid, go_fileid)
                VALUES (@fileId, @componentFileId, @goFileId);
                """;
            var fileIdP = insertLink.Parameters.Add("@fileId", SqliteType.Integer);
            var componentFileIdP = insertLink.Parameters.Add("@componentFileId", SqliteType.Text);
            var goFileIdP = insertLink.Parameters.Add("@goFileId", SqliteType.Text);

            foreach (var (fileId, refs) in items)
            {
                foreach (var link in refs.ComponentLinks)
                {
                    fileIdP.Value = fileId;
                    componentFileIdP.Value = link.ComponentFileId;
                    goFileIdP.Value = link.GoFileId;
                    insertLink.ExecuteNonQuery();
                }
            }
        }

        using (var insertNameHint = _connection.CreateCommand())
        {
            insertNameHint.Transaction = transaction;
            insertNameHint.CommandText = """
                INSERT INTO name_hints (source_file_id, name, kind, line, type_name)
                VALUES (@fileId, @name, @kind, @line, @typeName);
                """;
            var fileIdP = insertNameHint.Parameters.Add("@fileId", SqliteType.Integer);
            var nameP = insertNameHint.Parameters.Add("@name", SqliteType.Text);
            var kindP = insertNameHint.Parameters.Add("@kind", SqliteType.Text);
            var lineP = insertNameHint.Parameters.Add("@line", SqliteType.Integer);
            var typeNameP = insertNameHint.Parameters.Add("@typeName", SqliteType.Text);

            foreach (var (fileId, refs) in items)
            {
                foreach (var h in refs.NameHints)
                {
                    fileIdP.Value = fileId;
                    nameP.Value = h.Name;
                    kindP.Value = h.Kind;
                    lineP.Value = h.Line;
                    typeNameP.Value = (object?)h.TypeName ?? DBNull.Value;
                    insertNameHint.ExecuteNonQuery();
                }
            }
        }

        transaction.Commit();
    }

    private static void ExecuteDeleteByFileId(SqliteTransaction transaction, string sql, IEnumerable<long> fileIds)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var idParam = command.Parameters.Add("@id", SqliteType.Integer);
        foreach (var id in fileIds.Distinct())
        {
            idParam.Value = id;
            command.ExecuteNonQuery();
        }
    }

    // ---- Query-target resolution (who-uses / uses) ----------------------------------

    /// <summary>
    /// Resolves a who-uses/uses target argument (project-relative path or 32-hex guid).
    /// A bare guid that matches no file row is still a valid, answerable target (direct refs
    /// by literal guid) — Target is non-null with FileId null in that case, distinct from
    /// "not found" (Target null). Ambiguous fuzzy-path fragments return Target null with
    /// Candidates populated so the caller can list them (queries never run on a fuzzy guess).
    /// </summary>
    public TargetResolution ResolveQueryTarget(string input)
    {
        if (RegexPatterns.BareGuid().IsMatch(input))
        {
            var guid = input.ToLowerInvariant();
            var byGuid = FindFileByExactGuid(guid);
            return byGuid is { } g
                ? new TargetResolution(new QueryTarget(g.Id, g.Path, guid), [])
                : new TargetResolution(new QueryTarget(null, null, guid), []);
        }

        var exact = FindFileByExactPathWithId(input);
        if (exact is { } e)
        {
            return new TargetResolution(new QueryTarget(e.Id, e.Path, e.Guid), []);
        }

        var fuzzy = FindByPathSubstring(input, limit: 20);
        if (fuzzy.Count == 1)
        {
            var single = FindFileByExactPathWithId(fuzzy[0].Path);
            return single is { } s
                ? new TargetResolution(new QueryTarget(s.Id, s.Path, s.Guid), [])
                : new TargetResolution(null, fuzzy);
        }

        return new TargetResolution(null, fuzzy);
    }

    private (long Id, string Path, string? Guid)? FindFileByExactPathWithId(string path)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, path, guid FROM files WHERE path = @path LIMIT 1;";
        command.Parameters.AddWithValue("@path", path);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)) : null;
    }

    private (long Id, string Path)? FindFileByExactGuid(string guid)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, path FROM files WHERE guid = @guid ORDER BY path LIMIT 1;";
        command.Parameters.AddWithValue("@guid", guid);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetInt64(0), reader.GetString(1)) : null;
    }

    // ---- Edge queries (who-uses / uses) ----------------------------------------------

    private const string EdgeSelectSql = """
        SELECT sf.path, tf.path, ar.kind, ar.target_key, ar.target_file_id, ar.line, ar.source_classid, ar.method_name, go.name, ar.property_path
        FROM all_refs ar
        JOIN files sf ON sf.id = ar.source_file_id
        LEFT JOIN files tf ON tf.id = ar.target_file_id
        LEFT JOIN component_gameobject cg ON cg.file_id = ar.source_file_id AND cg.component_fileid = ar.source_fileid
        LEFT JOIN gameobjects go ON go.file_id = ar.source_file_id AND go.go_fileid = cg.go_fileid
        WHERE
        """;

    /// <summary>Direct (depth-1) referencers of a resolved target: WHERE ar.target_file_id = target.</summary>
    public IReadOnlyList<EdgeResult> GetDirectReferencers(long targetFileId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = EdgeSelectSql + " ar.target_file_id = @id ORDER BY sf.path, ar.line;";
        command.Parameters.AddWithValue("@id", targetFileId);
        return ReadEdges(command, depth: 1);
    }

    /// <summary>
    /// Direct referencers of an external/unresolved guid target (no file row exists for it).
    /// Queried straight off `refs` (guid-kind only — path_refs have no guid text to match).
    /// </summary>
    public IReadOnlyList<EdgeResult> GetDirectReferencersByGuidLiteral(string guid)
    {
        const string sql = """
            SELECT sf.path, NULL, 'guid', r.target_guid, NULL, r.line, r.source_classid, r.method_name, go.name, r.property_path
            FROM refs r
            JOIN files sf ON sf.id = r.source_file_id
            LEFT JOIN component_gameobject cg ON cg.file_id = r.source_file_id AND cg.component_fileid = r.source_fileid
            LEFT JOIN gameobjects go ON go.file_id = r.source_file_id AND go.go_fileid = cg.go_fileid
            WHERE r.target_guid = @guid
            ORDER BY sf.path, r.line;
            """;
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@guid", guid);
        return ReadEdges(command, depth: 1);
    }

    /// <summary>Direct (depth-1) forward dependencies of a resolved source: WHERE ar.source_file_id = source. Includes unresolved edges (rendered UNRESOLVED by the caller).</summary>
    public IReadOnlyList<EdgeResult> GetDirectDependencies(long sourceFileId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = EdgeSelectSql + " ar.source_file_id = @id ORDER BY ar.line;";
        command.Parameters.AddWithValue("@id", sourceFileId);
        return ReadEdges(command, depth: 1);
    }

    private static List<EdgeResult> ReadEdges(SqliteCommand command, int depth)
    {
        using var reader = command.ExecuteReader();
        var results = new List<EdgeResult>();
        while (reader.Read())
        {
            results.Add(ReadEdgeResult(reader, depth, via: null));
        }

        return results;
    }

    private static EdgeResult ReadEdgeResult(SqliteDataReader reader, int depth, string? via)
    {
        var sourcePath = reader.GetString(0);
        var targetPath = reader.IsDBNull(1) ? null : reader.GetString(1);
        var kind = reader.GetString(2);
        var targetKey = reader.GetString(3);
        var resolved = !reader.IsDBNull(4);
        var line = checked((int)reader.GetInt64(5));
        var classId = reader.IsDBNull(6) ? (int?)null : checked((int)reader.GetInt64(6));
        var methodName = reader.IsDBNull(7) ? null : reader.GetString(7);
        var gameObject = reader.IsDBNull(8) ? null : reader.GetString(8);
        var propertyPath = reader.IsDBNull(9) ? null : reader.GetString(9);
        var builtin = kind == "guid" && IsBuiltinGuid(targetKey);
        return new EdgeResult(sourcePath, targetPath, targetKey, line, kind, depth, resolved, builtin, classId, gameObject, methodName, via, PropertyPath: propertyPath);
    }

    private static bool IsBuiltinGuid(string guid) =>
        guid == BuiltinGuidE || guid == BuiltinGuidF;

    // ---- Transitive walks ----

    /// <summary>
    /// Reverse (who-uses) transitive closure: files that reference <paramref name="targetFileId"/>
    /// directly or through a chain, each at its minimum depth. Walks `unified_walk_edges` —
    /// guid-, path-, AND cs-kind edges together, at file granularity — so the closure crosses
    /// the asset/C# seam: a symbol's declaring file is reachable from whatever references its
    /// symbols, exactly like an asset-graph referencer. One hop = one file-level edge regardless
    /// of kind; this is the SAME view `dead-candidates` walks forward for liveness propagation,
    /// so who-uses and liveness can never disagree about what "referenced" means.
    /// The depth cap is the ONLY cycle terminator (plain UNION does not terminate cycles).
    /// HAVING is applied AFTER MIN(depth), not WHERE before it: in a cycle the seed reappears
    /// at depth 2+, and WHERE depth&gt;0 would wrongly admit that reappearance while
    /// HAVING MIN(depth)&gt;0 correctly excludes the seed itself.
    /// </summary>
    public Dictionary<long, int> GetReverseClosure(long targetFileId, int depthCap)
    {
        const string sql = """
            WITH RECURSIVE walk(file_id, depth) AS (
              SELECT @target, 0
              UNION ALL
              SELECT uw.source_file_id, w.depth + 1
              FROM unified_walk_edges uw
              JOIN walk w ON uw.target_file_id = w.file_id
              WHERE w.depth < @cap
            )
            SELECT file_id, MIN(depth) AS depth
            FROM walk
            GROUP BY file_id
            HAVING MIN(depth) > 0;
            """;
        return RunClosure(sql, targetFileId, depthCap);
    }

    /// <summary>
    /// Forward (uses) transitive closure: the same walk with the join reversed, over
    /// `unified_walk_edges` (see <see cref="GetReverseClosure"/> for the cross-seam rationale).
    /// No extra "target_file_id IS NOT NULL" guard is needed here — `unified_walk_edges` is
    /// defined to already exclude unresolved targets; unresolved edges are still surfaced
    /// separately by the caller at every depth, they just can't extend the walk.
    /// </summary>
    public Dictionary<long, int> GetForwardClosure(long sourceFileId, int depthCap)
    {
        const string sql = """
            WITH RECURSIVE walk(file_id, depth) AS (
              SELECT @target, 0
              UNION ALL
              SELECT uw.target_file_id, w.depth + 1
              FROM unified_walk_edges uw
              JOIN walk w ON uw.source_file_id = w.file_id
              WHERE w.depth < @cap
            )
            SELECT file_id, MIN(depth) AS depth
            FROM walk
            GROUP BY file_id
            HAVING MIN(depth) > 0;
            """;
        return RunClosure(sql, sourceFileId, depthCap);
    }

    private Dictionary<long, int> RunClosure(string sql, long seedFileId, int depthCap)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@target", seedFileId);
        command.Parameters.AddWithValue("@cap", depthCap);
        using var reader = command.ExecuteReader();
        var result = new Dictionary<long, int>();
        while (reader.Read())
        {
            result[reader.GetInt64(0)] = checked((int)reader.GetInt64(1));
        }

        return result;
    }

    /// <summary>
    /// True if the raw (non-deduped) walk reached exactly the depth cap for the given seed —
    /// a signal that the cap may have cut off real depth, surfaced to callers as `truncated`
    /// so the tool never silently claims a transitive answer is complete when it might not be.
    /// </summary>
    public bool WalkHitDepthCap(bool forward, long seedFileId, int depthCap)
    {
        var joinCol = forward ? "uw.source_file_id" : "uw.target_file_id";
        var emitCol = forward ? "uw.target_file_id" : "uw.source_file_id";
        var sql = $"""
            WITH RECURSIVE walk(file_id, depth) AS (
              SELECT @target, 0
              UNION ALL
              SELECT {emitCol}, w.depth + 1
              FROM unified_walk_edges uw
              JOIN walk w ON {joinCol} = w.file_id
              WHERE w.depth < @cap
            )
            SELECT EXISTS(SELECT 1 FROM walk WHERE depth = @cap);
            """;
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@target", seedFileId);
        command.Parameters.AddWithValue("@cap", depthCap);
        var result = command.ExecuteScalar();
        return result is long l && l != 0;
    }

    /// <summary>
    /// Builds the display edges for a who-uses transitive closure: one representative edge
    /// (earliest line) per closure file, annotated with the "via" file it was reached
    /// through, at its own minimum depth.
    /// </summary>
    public IReadOnlyList<EdgeResult> GetTransitiveWhoUsesEdges(long targetFileId, Dictionary<long, int> closure)
    {
        return GetTransitiveEdges(forward: false, targetFileId, closure);
    }

    /// <summary>
    /// Builds the display edges for a uses transitive closure: one representative resolved
    /// edge per closure file (the chain that reached it), PLUS every unresolved forward edge
    /// sourced from each depth-reached file (surfaced at every depth, even though they cannot
    /// extend the walk).
    /// </summary>
    public IReadOnlyList<EdgeResult> GetTransitiveUsesEdges(long sourceFileId, Dictionary<long, int> closure)
    {
        var results = new List<EdgeResult>(GetTransitiveEdges(forward: true, sourceFileId, closure));

        // depth-0 (the origin) plus every reached file are valid "sources" of further
        // forward edges; unresolved ones are dead ends but still worth reporting.
        var depthByFile = new Dictionary<long, int>(closure) { [sourceFileId] = 0 };
        foreach (var (fileId, depth) in depthByFile)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = EdgeSelectSql + " ar.source_file_id = @id AND ar.target_file_id IS NULL ORDER BY ar.line;";
            command.Parameters.AddWithValue("@id", fileId);
            results.AddRange(ReadEdges(command, depth: depth + 1));
        }

        return results;
    }

    private List<EdgeResult> GetTransitiveEdges(bool forward, long seedFileId, Dictionary<long, int> closure)
    {
        var results = new List<EdgeResult>();
        if (closure.Count == 0)
        {
            return results;
        }

        var maxDepth = closure.Values.Max();
        for (var depth = 1; depth <= maxDepth; depth++)
        {
            var currentIds = closure.Where(kv => kv.Value == depth).Select(kv => kv.Key).ToList();
            var predecessorIds = depth == 1
                ? [seedFileId]
                : closure.Where(kv => kv.Value == depth - 1).Select(kv => kv.Key).ToList();

            if (currentIds.Count == 0 || predecessorIds.Count == 0)
            {
                continue;
            }

            // forward=false (who-uses): edge FROM a current-depth file TO a predecessor file.
            // forward=true (uses): edge FROM a predecessor file TO a current-depth file.
            var sourceIds = forward ? predecessorIds : currentIds;
            var targetIds = forward ? currentIds : predecessorIds;

            var sourceInClause = BuildInClause("ar.source_file_id", sourceIds, "s");
            var targetInClause = BuildInClause("ar.target_file_id", targetIds, "t");

            var sql = $"""
                WITH ranked AS (
                  SELECT ar.source_file_id, ar.target_file_id, ar.line,
                         ROW_NUMBER() OVER (PARTITION BY {(forward ? "ar.target_file_id" : "ar.source_file_id")} ORDER BY ar.line) AS rn
                  FROM all_refs ar
                  WHERE {sourceInClause.Sql} AND {targetInClause.Sql}
                )
                SELECT sf.path, tf.path, ar.kind, ar.target_key, ar.target_file_id, ar.line, ar.source_classid, ar.method_name, go.name, ar.property_path
                FROM ranked
                JOIN all_refs ar ON ar.source_file_id = ranked.source_file_id AND ar.target_file_id = ranked.target_file_id AND ar.line = ranked.line
                JOIN files sf ON sf.id = ar.source_file_id
                LEFT JOIN files tf ON tf.id = ar.target_file_id
                LEFT JOIN component_gameobject cg ON cg.file_id = ar.source_file_id AND cg.component_fileid = ar.source_fileid
                LEFT JOIN gameobjects go ON go.file_id = ar.source_file_id AND go.go_fileid = cg.go_fileid
                WHERE ranked.rn = 1;
                """;

            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in sourceInClause.Parameters.Concat(targetInClause.Parameters))
            {
                command.Parameters.AddWithValue(name, value);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var edge = ReadEdgeResult(reader, depth, via: null);

                // The WHERE clause pinned source_file_id/target_file_id to the current-depth
                // vs. predecessor sets per direction, so whichever side of (SourcePath,
                // TargetPath) holds the predecessor IS the "via" annotation:
                //  - who-uses (forward=false): SourcePath=current-depth file, TargetPath=predecessor.
                //  - uses      (forward=true):  SourcePath=predecessor, TargetPath=current-depth file.
                var via = forward ? edge.SourcePath : edge.TargetPath;
                results.Add(edge with { Via = via });
            }

            // The SAME depth transition can also be crossed by a cs-kind edge (a file's symbol_refs targeting
            // a symbol declared in another file) -- cs edges are always .cs->.cs, so this is a
            // no-op whenever the id sets involve no C# files. Queried as its own ranked pass
            // (not folded into the ranked CTE above, which is asset-shape only) and merged in:
            // a current-depth file can legitimately be reached from the predecessor set via
            // BOTH an asset edge and a cs edge (two real, distinct routes) -- both are worth
            // showing, never silently pick one.
            results.AddRange(QueryCsTransitiveEdges(forward, sourceIds, targetIds, depth));
        }

        return results;
    }

    /// <summary>
    /// Cs-kind display edges for one depth transition of a transitive who-uses/uses walk:
    /// display queries for cs hops pull symbol detail from cs_file_refs, with symbol_refs/symbols
    /// joins for SourceSymbol/TargetSymbol — same shape as <see cref="GetCsReferencersOfFile"/>,
    /// generalized to a predecessor/current id-set pair instead of one fixed target file. One
    /// representative edge (earliest line) per current-depth file, same ROW_NUMBER/rn=1
    /// discipline as the asset-shape query above, partitioned on the same column per direction.
    /// </summary>
    private List<EdgeResult> QueryCsTransitiveEdges(bool forward, IReadOnlyList<long> sourceIds, IReadOnlyList<long> targetIds, int depth)
    {
        var sourceInClause = BuildInClause("sr.source_file_id", sourceIds, "cs");
        var targetInClause = BuildInClause("tgtSym.file_id", targetIds, "ct");

        var sql = $"""
            WITH ranked AS (
              SELECT sr.source_file_id, tgtSym.file_id AS target_file_id, sr.line,
                     sr.target_doc_id, sr.ref_kind, sr.confidence, sr.source_symbol_id,
                     ROW_NUMBER() OVER (PARTITION BY {(forward ? "tgtSym.file_id" : "sr.source_file_id")} ORDER BY sr.line) AS rn
              FROM symbol_refs sr
              JOIN symbols tgtSym ON tgtSym.doc_id = sr.target_doc_id
              WHERE {sourceInClause.Sql} AND {targetInClause.Sql} AND tgtSym.file_id != sr.source_file_id
            )
            SELECT sf.path, tf.path, r.line, r.target_doc_id, r.ref_kind, r.confidence, srcSym.doc_id
            FROM ranked r
            JOIN files sf ON sf.id = r.source_file_id
            JOIN files tf ON tf.id = r.target_file_id
            LEFT JOIN symbols srcSym ON srcSym.id = r.source_symbol_id
            WHERE r.rn = 1;
            """;

        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in sourceInClause.Parameters.Concat(targetInClause.Parameters))
        {
            command.Parameters.AddWithValue(name, value);
        }

        var results = new List<EdgeResult>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var sourcePath = reader.GetString(0);
            var targetPath = reader.GetString(1);
            var line = checked((int)reader.GetInt64(2));
            var targetDocId = reader.GetString(3);
            var refKind = reader.GetString(4);
            var confidence = reader.GetString(5);
            var sourceSymbol = reader.IsDBNull(6) ? null : StripDocIdPrefix(reader.GetString(6));
            var targetSymbol = StripDocIdPrefix(targetDocId);

            var via = forward ? sourcePath : targetPath;
            results.Add(new EdgeResult(
                sourcePath, targetPath, targetDocId, line, "cs", depth, Resolved: true, Builtin: false,
                ClassId: null, GameObject: null, MethodName: null, Via: via,
                Confidence: confidence, TargetSymbol: targetSymbol, SourceSymbol: sourceSymbol, RefKind: refKind));
        }

        return results;
    }

    private static (string Sql, IReadOnlyList<(string Name, object Value)> Parameters) BuildInClause(
        string columnExpr, IReadOnlyList<long> ids, string paramPrefix)
    {
        var parameters = new List<(string, object)>(ids.Count);
        var names = new List<string>(ids.Count);
        for (var i = 0; i < ids.Count; i++)
        {
            var name = $"@{paramPrefix}{i}";
            names.Add(name);
            parameters.Add((name, ids[i]));
        }

        var sql = ids.Count == 0 ? "0 = 1" : $"{columnExpr} IN ({string.Join(",", names)})";
        return (sql, parameters);
    }

    // ---- Unresolved accounting (uses --missing-only / stats --unresolved / index summary) --

    /// <summary>
    /// THE canonical unresolved-refs query — every other surface (uses --missing-only,
    /// stats --unresolved, index's summary line) consumes this same view so their counts can
    /// never drift apart. <paramref name="sourceFileId"/> filters to one file's outgoing
    /// edges (uses --missing-only); null means project-wide (stats/index).
    /// </summary>
    public IReadOnlyList<UnresolvedRefEntry> GetUnresolvedRefs(long? sourceFileId)
    {
        const string sql = """
            SELECT sf.path, u.kind, u.target_key, u.line, u.context,
                   u.source_classid, go.name, u.property_path,
                   (SELECT script_file.path
                      FROM refs script_ref
                      LEFT JOIN files script_file ON script_file.guid = script_ref.target_guid
                     WHERE script_ref.source_file_id = u.source_file_id
                       AND script_ref.source_fileid = u.source_fileid
                       AND (script_ref.property_path = 'm_Script' OR script_ref.property_path LIKE '%.m_Script')
                     LIMIT 1) AS component,
                   (SELECT script_ref.target_guid
                      FROM refs script_ref
                     WHERE script_ref.source_file_id = u.source_file_id
                       AND script_ref.source_fileid = u.source_fileid
                       AND (script_ref.property_path = 'm_Script' OR script_ref.property_path LIKE '%.m_Script')
                     LIMIT 1) AS component_script_guid,
                   CASE WHEN u.kind = 'guid' AND
                                  (u.property_path = 'm_Script' OR u.property_path LIKE '%.m_Script')
                        THEN 1 ELSE 0 END AS is_script_reference,
                   CASE WHEN u.source_classid = 1001 AND u.property_path LIKE 'm_Modification.m_Modifications[%'
                        THEN 1 ELSE 0 END AS is_prefab_override,
                   (SELECT prefab_file.path
                      FROM refs prefab_ref
                      LEFT JOIN files prefab_file ON prefab_file.guid = prefab_ref.target_guid
                     WHERE prefab_ref.source_file_id = u.source_file_id
                       AND prefab_ref.source_fileid = u.source_fileid
                       AND (prefab_ref.property_path = 'm_SourcePrefab' OR prefab_ref.property_path LIKE '%.m_SourcePrefab')
                     LIMIT 1) AS prefab_source
            FROM unresolved_refs u
            JOIN files sf ON sf.id = u.source_file_id
            LEFT JOIN component_gameobject cg ON cg.file_id = u.source_file_id AND cg.component_fileid = u.source_fileid
            LEFT JOIN gameobjects go ON go.file_id = u.source_file_id AND go.go_fileid = cg.go_fileid
            """;
        using var command = _connection.CreateCommand();
        command.CommandText = sourceFileId is null
            ? sql + " ORDER BY sf.path, u.line;"
            : sql + " WHERE u.source_file_id = @id ORDER BY sf.path, u.line;";
        if (sourceFileId is { } id)
        {
            command.Parameters.AddWithValue("@id", id);
        }

        using var reader = command.ExecuteReader();
        var result = new List<UnresolvedRefEntry>();
        while (reader.Read())
        {
            result.Add(new UnresolvedRefEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                checked((int)reader.GetInt64(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : checked((int)reader.GetInt64(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetInt64(10) != 0,
                reader.GetInt64(11) != 0,
                reader.IsDBNull(12) ? null : reader.GetString(12)));
        }

        result.AddRange(GetUnresolvedDllRefs(sourceFileId));
        return result;
    }

    /// <summary>
    /// `precompiledReferences` entries naming a DLL that isn't in the project — real breakage
    /// (the asmdef won't compile) that has to reach `uses --missing-only`, `stats --unresolved`
    /// and the exit-3 CI check like any other broken ref, per "unknown/unresolved targets are
    /// stored and surfaced, never dropped". Computed here rather than in the `unresolved_refs`
    /// view because the name→file match can't be indexed (see <see cref="QueryFilesByFileName"/>);
    /// that's affordable on these two report verbs and would not be inside `all_refs`.
    /// </summary>
    private List<UnresolvedRefEntry> GetUnresolvedDllRefs(long? sourceFileId)
    {
        var sql = """
            SELECT sf.path, d.target_name_raw, d.target_name_norm, d.line, d.context
            FROM dll_refs d
            JOIN files sf ON sf.id = d.source_file_id
            """;
        sql += sourceFileId is null ? " ORDER BY sf.path, d.line;" : " WHERE d.source_file_id = @id ORDER BY sf.path, d.line;";

        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        if (sourceFileId is { } id)
        {
            command.Parameters.AddWithValue("@id", id);
        }

        var rows = new List<(string SourcePath, string Raw, string Norm, int Line, string? Context)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    checked((int)reader.GetInt64(3)), reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        return [.. rows
            .Where(r => FindFileIdsByFileName(r.Norm).Count == 0)
            .Select(r => new UnresolvedRefEntry(r.SourcePath, "dll", r.Raw, r.Line, r.Context))];
    }

    public EdgeStats GetEdgeStats()
    {
        var guidTotal = ExecuteScalarInt("SELECT COUNT(*) FROM refs;");
        var pathTotal = ExecuteScalarInt("SELECT COUNT(*) FROM path_refs;");
        var guidBuiltin = ExecuteScalarInt(
            $"SELECT COUNT(*) FROM refs WHERE target_guid IN ('{BuiltinGuidE}', '{BuiltinGuidF}');");
        var guidUnresolved = ExecuteScalarInt("SELECT COUNT(*) FROM unresolved_refs WHERE kind = 'guid';");
        var pathUnresolved = ExecuteScalarInt("SELECT COUNT(*) FROM unresolved_refs WHERE kind = 'path';");

        return new EdgeStats(guidTotal, guidUnresolved, guidBuiltin, pathTotal, pathUnresolved);
    }

    public StatsResult GetStats()
    {
        int assets = 0, scripts = 0, folders = 0, settings = 0;
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT kind, COUNT(*) FROM files WHERE identity_only = 0 GROUP BY kind;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var kind = FileKindExtensions.FromDbString(reader.GetString(0));
                var count = checked((int)reader.GetInt64(1));
                switch (kind)
                {
                    case FileKind.Asset: assets = count; break;
                    case FileKind.Script: scripts = count; break;
                    case FileKind.Folder: folders = count; break;
                    case FileKind.Settings: settings = count; break;
                }
            }
        }

        var identityOnly = ExecuteScalarInt("SELECT COUNT(*) FROM files WHERE identity_only = 1;");
        var guidLess = ExecuteScalarInt("SELECT COUNT(*) FROM files WHERE identity_only = 0 AND guid IS NULL;");

        var schemaVersionText = QueryMetaValue("schema_version") ?? "0";
        _ = int.TryParse(schemaVersionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var schemaVersion);

        long dbSize;
        try
        {
            dbSize = new FileInfo(DbPath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            dbSize = 0;
        }

        var refSourceExtensions = Scanner.ReferenceSourceExtensions
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new StatsResult(
            new KindCounts(assets, scripts, folders, settings),
            identityOnly,
            guidLess,
            dbSize,
            schemaVersion,
            GetEdgeStats(),
            refSourceExtensions,
            GetCsStats());
    }

    // ---- C# semantic graph -------------------------------------------------------------

    /// <summary>Assembly names currently persisted — used to decide whether an assembly is "new" (never analyzed) and therefore dirty regardless of its files' change state.</summary>
    public IReadOnlyList<string> GetAssemblyNames()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT name FROM assemblies;";
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    /// <summary>One assembly's mode fingerprint, as currently persisted (see <see cref="GetAssemblyModeFingerprints"/>).</summary>
    public readonly record struct AssemblyModeFingerprint(string Name, long? CsprojMtimeTicks);

    /// <summary>
    /// Every persisted assembly's mode fingerprint —
    /// <see cref="UnBramble.Core.UnBrambleEngine.RunCsAnalysis"/>'s skip gate compares each entry's
    /// <c>CsprojMtimeTicks</c> against a fresh <c>File.Exists</c>/<c>GetLastWriteTimeUtc</c> stat
    /// of <c>{ProjectRoot}/{Name}.csproj</c> — a stat, not a parse — to decide whether a csproj
    /// appeared, disappeared, or changed since the last sweep even though no script/asmdef file
    /// itself is dirty. An empty result means "nothing recorded yet" and the caller's gate must
    /// treat that as a mismatch (never skip on an unknown assembly set).
    /// </summary>
    public IReadOnlyList<AssemblyModeFingerprint> GetAssemblyModeFingerprints()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT name, csproj_mtime FROM assemblies;";
        using var reader = command.ExecuteReader();
        var result = new List<AssemblyModeFingerprint>();
        while (reader.Read())
        {
            result.Add(new AssemblyModeFingerprint(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1)));
        }

        return result;
    }

    /// <summary>
    /// Refreshes ONLY the `csproj_mtime` column for every currently-discovered assembly unit,
    /// regardless of whether that unit was "dirty" this sweep. This is deliberately separate from
    /// <see cref="ReplaceAssemblyAnalyses"/>'s own per-assembly upsert (which only touches
    /// dirty/analyzed units and also advances `mode` and `analyzed_utc`): a non-dirty Semantic
    /// assembly still gets its compilation rebuilt every run RunCsAnalysis actually executes
    /// (needed for cross-assembly resolution to stay correct), so its csproj fingerprint is known
    /// and worth persisting even though nothing forced a symbol-level re-analysis — otherwise the
    /// skip gate could see a stale fingerprint for that one assembly forever and needlessly keep
    /// re-running the phase (never WRONG, but not the intended win either). `mode` is deliberately
    /// left untouched here on purpose, not merely unhandled: it stays whatever
    /// <see cref="ReplaceAssemblyAnalyses"/> last recorded for that name until the unit is
    /// actually dirty again — e.g. `DeadCandidatesTests.Finding7_...` depends on a
    /// semantic-mode assembly's `mode` row staying "semantic" after its generated csproj is
    /// deleted (with no other tracked file touched), specifically so
    /// `GetSemanticAssemblyNames()` still finds it and the "generated csproj missing" gate fires
    /// with its exact expected message. Every unit passed here is assumed to already have an
    /// `assemblies` row (true by construction: a brand-new unit is always "dirty" on first
    /// discovery and goes through <see cref="ReplaceAssemblyAnalyses"/>'s upsert first) -- this
    /// is a plain UPDATE, not an upsert, and silently no-ops for a name that doesn't exist rather
    /// than crashing. `analyzed_utc` is untouched too (CsIncrementalTests'
    /// EditingUnrelatedAsset test depends on it staying stable for non-dirty assemblies across
    /// sweeps).
    /// </summary>
    public void UpdateAssemblyModeFingerprints(IReadOnlyList<AssemblyModeFingerprint> fingerprints)
    {
        if (fingerprints.Count == 0)
        {
            return;
        }

        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE assemblies SET csproj_mtime = @csprojMtime WHERE name = @name;";
        var nameP = command.Parameters.Add("@name", SqliteType.Text);
        var csprojMtimeP = command.Parameters.Add("@csprojMtime", SqliteType.Integer);

        foreach (var fp in fingerprints)
        {
            nameP.Value = fp.Name;
            csprojMtimeP.Value = (object?)fp.CsprojMtimeTicks ?? DBNull.Value;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Per-file "declaration shape" fingerprint for the given file ids, as CURRENTLY persisted:
    /// the set of (a) this file's own declared symbols (`"S:{kind}:{doc_id}"`) and (b) this
    /// file's own outgoing inherit/override refs (`"R:{ref_kind}:{target_doc_id}"`). Used by
    /// <see cref="UnBramble.Core.UnBrambleEngine.RunCsAnalysis"/>'s per-file extraction scoping:
    /// comparing this BEFORE set against the freshly re-extracted AFTER set for the same file(s) answers "could
    /// this edit change how ANY OTHER file in the same compilation resolves a reference" —
    /// Roslyn documentation-comment ids already encode a method's full parameter-type signature,
    /// so any signature-affecting edit (added/removed/renamed member, changed parameter types)
    /// necessarily changes at least one declared doc_id; a base-type/interface swap doesn't
    /// necessarily change the file's own doc_id set (`T:Foo` stays `T:Foo`) but does change the
    /// inherit-ref component, which is why both are folded into one comparable token set rather
    /// than checking `symbols` alone. A file id with no rows in either table still gets an entry
    /// (an empty set) so a caller can tell "no prior shape" (e.g. a brand-new file) apart from
    /// "not asked about" without a separate existence check.
    /// </summary>
    public IReadOnlyDictionary<long, HashSet<string>> GetDeclarationShapes(IReadOnlyCollection<long> fileIds)
    {
        var result = new Dictionary<long, HashSet<string>>();
        if (fileIds.Count == 0)
        {
            return result;
        }

        using var symbolsCommand = _connection.CreateCommand();
        symbolsCommand.CommandText = "SELECT kind, doc_id FROM symbols WHERE file_id = @id;";
        var symbolsIdParam = symbolsCommand.Parameters.Add("@id", SqliteType.Integer);

        using var refsCommand = _connection.CreateCommand();
        refsCommand.CommandText = "SELECT ref_kind, target_doc_id FROM symbol_refs WHERE source_file_id = @id AND ref_kind IN ('inherit', 'override');";
        var refsIdParam = refsCommand.Parameters.Add("@id", SqliteType.Integer);

        foreach (var fileId in fileIds.Distinct())
        {
            var tokens = new HashSet<string>(StringComparer.Ordinal);

            symbolsIdParam.Value = fileId;
            using (var reader = symbolsCommand.ExecuteReader())
            {
                while (reader.Read())
                {
                    tokens.Add($"S:{reader.GetString(0)}:{reader.GetString(1)}");
                }
            }

            refsIdParam.Value = fileId;
            using (var reader = refsCommand.ExecuteReader())
            {
                while (reader.Read())
                {
                    tokens.Add($"R:{reader.GetString(0)}:{reader.GetString(1)}");
                }
            }

            result[fileId] = tokens;
        }

        return result;
    }

    /// <summary>
    /// Per-file EXACT-row-content fingerprint (<see cref="CsFileRowFingerprint"/>) for the given
    /// file ids, as CURRENTLY persisted — the "before" side of the full-unit DB-write scoping:
    /// unlike <see cref="GetDeclarationShapes"/>' declaration-shape proxy (valid only for the self-edit
    /// case's "could OTHER files resolve differently" question), this covers every column <see
    /// cref="ReplaceAssemblyAnalyses"/> writes, so fingerprint equality against a freshly
    /// re-extracted file means re-writing that file's rows would be a byte-level no-op (modulo
    /// row-id churn) and can safely be skipped even after a full compilation rebuild.
    ///
    /// Encoding choices are all fail-toward-rewrite: a symbol row is fingerprinted WITH its
    /// owning assembly's name (an INNER join — a row bound to a missing assembly simply drops
    /// out and forces a difference), so a file that moved between assemblies with identical
    /// content still gets re-written under its new assembly id; a ref's persisted
    /// `source_symbol_id` is compared as its resolved doc_id via LEFT JOIN (a dangling id — e.g.
    /// its symbol row was deleted by a later scoped write of a partial-type sibling file —
    /// compares as null against the fresh side's non-null doc id and forces a rewrite, which
    /// also re-heals the binding). Every requested file id gets an entry (the empty fingerprint
    /// if it has no rows at all), same convention as <see cref="GetDeclarationShapes"/>.
    /// </summary>
    public IReadOnlyDictionary<long, string> GetFileRowFingerprints(IReadOnlyCollection<long> fileIds)
    {
        var result = new Dictionary<long, string>();
        if (fileIds.Count == 0)
        {
            return result;
        }

        using var symbolsCommand = _connection.CreateCommand();
        symbolsCommand.CommandText = """
            SELECT a.name, s.kind, s.doc_id, s.name, s.line, s.is_entry_point, s.attrs, s.entry_reason
            FROM symbols s
            JOIN assemblies a ON a.id = s.assembly_id
            WHERE s.file_id = @id;
            """;
        var symbolsIdParam = symbolsCommand.Parameters.Add("@id", SqliteType.Integer);

        using var refsCommand = _connection.CreateCommand();
        refsCommand.CommandText = """
            SELECT srcSym.doc_id, sr.target_doc_id, sr.ref_kind, sr.line, sr.confidence
            FROM symbol_refs sr
            LEFT JOIN symbols srcSym ON srcSym.id = sr.source_symbol_id
            WHERE sr.source_file_id = @id;
            """;
        var refsIdParam = refsCommand.Parameters.Add("@id", SqliteType.Integer);

        using var hintsCommand = _connection.CreateCommand();
        hintsCommand.CommandText = "SELECT name, kind, line, type_name FROM name_hints WHERE source_file_id = @id;";
        var hintsIdParam = hintsCommand.Parameters.Add("@id", SqliteType.Integer);

        foreach (var fileId in fileIds.Distinct())
        {
            var tokens = new List<string>();

            symbolsIdParam.Value = fileId;
            using (var reader = symbolsCommand.ExecuteReader())
            {
                while (reader.Read())
                {
                    tokens.Add(CsFileRowFingerprint.SymbolToken(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.IsDBNull(4) ? null : CsFileRowFingerprint.FormatLine(reader.GetInt64(4)),
                        reader.GetInt64(5) != 0,
                        reader.IsDBNull(6) ? null : reader.GetString(6),
                        reader.IsDBNull(7) ? null : reader.GetString(7)));
                }
            }

            refsIdParam.Value = fileId;
            using (var reader = refsCommand.ExecuteReader())
            {
                while (reader.Read())
                {
                    tokens.Add(CsFileRowFingerprint.RefToken(
                        reader.IsDBNull(0) ? null : reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        CsFileRowFingerprint.FormatLine(reader.GetInt64(3)),
                        reader.GetString(4)));
                }
            }

            hintsIdParam.Value = fileId;
            using (var reader = hintsCommand.ExecuteReader())
            {
                while (reader.Read())
                {
                    tokens.Add(CsFileRowFingerprint.NameHintToken(
                        reader.GetString(0),
                        reader.GetString(1),
                        CsFileRowFingerprint.FormatLine(reader.GetInt64(2)),
                        reader.IsDBNull(3) ? null : reader.GetString(3)));
                }
            }

            result[fileId] = CsFileRowFingerprint.Compute(tokens);
        }

        return result;
    }

    /// <summary>
    /// Replaces the stored analysis for a set of assemblies in one transaction: upserts each
    /// `assemblies` row (by name), replaces its `symbols`/`symbol_refs` rows wholesale (delete
    /// by file id, then insert — same discipline as <see cref="ReplaceFileReferences"/>).
    /// `source_symbol_id` is resolved from each ref's `SourceSymbolDocId` against the
    /// freshly-inserted symbols of the SAME assembly (a ref's containing member is always in
    /// the same assembly as the ref itself); `target_doc_id` is stored as plain text and
    /// resolved against `symbols.doc_id` only at query time (never baked here — same lesson as
    /// path_refs).
    /// </summary>
    public void ReplaceAssemblyAnalyses(IReadOnlyList<CsAssemblyAnalysis> analyses)
    {
        if (analyses.Count == 0)
        {
            return;
        }

        using var transaction = _connection.BeginTransaction();
        ExecuteNonQuery(_connection, "UPDATE build_reachable_state SET valid = 0, graph_generation = graph_generation + 1 WHERE id = 1;", transaction);
        var nowUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        foreach (var analysis in analyses)
        {
            var assemblyId = UpsertAssembly(analysis, nowUtc, transaction);

            ExecuteDeleteByFileId(transaction, "DELETE FROM symbol_refs WHERE source_file_id = @id;", analysis.ScriptFileIds);
            ExecuteDeleteByFileId(transaction, "DELETE FROM symbols WHERE file_id = @id;", analysis.ScriptFileIds);

            var docIdToRowId = new Dictionary<string, long>(StringComparer.Ordinal);

            using (var insertSymbol = _connection.CreateCommand())
            {
                insertSymbol.Transaction = transaction;
                insertSymbol.CommandText = """
                    INSERT INTO symbols (assembly_id, file_id, kind, doc_id, name, line, is_entry_point, attrs, entry_reason)
                    VALUES (@assemblyId, @fileId, @kind, @docId, @name, @line, @isEntryPoint, @attrs, @entryReason)
                    RETURNING id;
                    """;
                var assemblyIdP = insertSymbol.Parameters.Add("@assemblyId", SqliteType.Integer);
                var fileIdP = insertSymbol.Parameters.Add("@fileId", SqliteType.Integer);
                var kindP = insertSymbol.Parameters.Add("@kind", SqliteType.Text);
                var docIdP = insertSymbol.Parameters.Add("@docId", SqliteType.Text);
                var nameP = insertSymbol.Parameters.Add("@name", SqliteType.Text);
                var lineP = insertSymbol.Parameters.Add("@line", SqliteType.Integer);
                var isEntryPointP = insertSymbol.Parameters.Add("@isEntryPoint", SqliteType.Integer);
                var attrsP = insertSymbol.Parameters.Add("@attrs", SqliteType.Text);
                var entryReasonP = insertSymbol.Parameters.Add("@entryReason", SqliteType.Text);

                foreach (var symbol in analysis.Symbols)
                {
                    assemblyIdP.Value = assemblyId;
                    fileIdP.Value = symbol.FileId;
                    kindP.Value = symbol.Kind;
                    docIdP.Value = symbol.DocId;
                    nameP.Value = symbol.Name;
                    lineP.Value = (object?)symbol.Line ?? DBNull.Value;
                    isEntryPointP.Value = symbol.IsEntryPoint ? 1 : 0;
                    attrsP.Value = (object?)symbol.Attrs ?? DBNull.Value;
                    entryReasonP.Value = (object?)symbol.EntryReason ?? DBNull.Value;
                    var rowId = (long)insertSymbol.ExecuteScalar()!;
                    // First declaration wins on a doc_id collision (e.g. a partial class/method
                    // split across files) -- good enough for "containing member" display; never
                    // a crash either way.
                    docIdToRowId.TryAdd(symbol.DocId, rowId);
                }
            }

            using (var insertRef = _connection.CreateCommand())
            {
                insertRef.Transaction = transaction;
                insertRef.CommandText = """
                    INSERT INTO symbol_refs (source_file_id, source_symbol_id, target_doc_id, ref_kind, line, confidence)
                    VALUES (@fileId, @sourceSymbolId, @targetDocId, @refKind, @line, @confidence);
                    """;
                var fileIdP = insertRef.Parameters.Add("@fileId", SqliteType.Integer);
                var sourceSymbolIdP = insertRef.Parameters.Add("@sourceSymbolId", SqliteType.Integer);
                var targetDocIdP = insertRef.Parameters.Add("@targetDocId", SqliteType.Text);
                var refKindP = insertRef.Parameters.Add("@refKind", SqliteType.Text);
                var lineP = insertRef.Parameters.Add("@line", SqliteType.Integer);
                var confidenceP = insertRef.Parameters.Add("@confidence", SqliteType.Text);

                foreach (var r in analysis.Refs)
                {
                    fileIdP.Value = r.SourceFileId;
                    sourceSymbolIdP.Value = r.SourceSymbolDocId is not null && docIdToRowId.TryGetValue(r.SourceSymbolDocId, out var rowId)
                        ? rowId
                        : DBNull.Value;
                    targetDocIdP.Value = r.TargetDocId;
                    refKindP.Value = r.RefKind;
                    lineP.Value = r.Line;
                    confidenceP.Value = r.Confidence;
                    insertRef.ExecuteNonQuery();
                }
            }

            // SendMessage-family literals ('cs-name-literal') and
            // disabled-region identifiers ('cs-disabled'), same delete-then-insert-per-file
            // discipline as everything else here. .cs files never share file ids with asset
            // sources, so this can never collide with ReplaceFileReferences' own name_hints
            // writes for the same source file.
            ExecuteDeleteByFileId(transaction, "DELETE FROM name_hints WHERE source_file_id = @id;", analysis.ScriptFileIds);

            using (var insertNameHint = _connection.CreateCommand())
            {
                insertNameHint.Transaction = transaction;
                insertNameHint.CommandText = """
                    INSERT INTO name_hints (source_file_id, name, kind, line, type_name)
                    VALUES (@fileId, @name, @kind, @line, @typeName);
                    """;
                var fileIdP = insertNameHint.Parameters.Add("@fileId", SqliteType.Integer);
                var nameP = insertNameHint.Parameters.Add("@name", SqliteType.Text);
                var kindP = insertNameHint.Parameters.Add("@kind", SqliteType.Text);
                var lineP = insertNameHint.Parameters.Add("@line", SqliteType.Integer);
                var typeNameP = insertNameHint.Parameters.Add("@typeName", SqliteType.Text);

                foreach (var h in analysis.NameHints)
                {
                    fileIdP.Value = h.SourceFileId;
                    nameP.Value = h.Name;
                    kindP.Value = h.Kind;
                    lineP.Value = h.Line;
                    typeNameP.Value = (object?)h.TypeName ?? DBNull.Value;
                    insertNameHint.ExecuteNonQuery();
                }
            }
        }

        transaction.Commit();
    }

    private long UpsertAssembly(CsAssemblyAnalysis analysis, string nowUtc, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO assemblies (name, asmdef_file_id, mode, analyzed_utc, mode_reason)
            VALUES (@name, @asmdefFileId, @mode, @analyzedUtc, @modeReason)
            ON CONFLICT(name) DO UPDATE SET asmdef_file_id = excluded.asmdef_file_id, mode = excluded.mode, analyzed_utc = excluded.analyzed_utc, mode_reason = excluded.mode_reason
            RETURNING id;
            """;
        command.Parameters.AddWithValue("@name", analysis.AssemblyName);
        command.Parameters.AddWithValue("@asmdefFileId", (object?)analysis.AsmdefFileId ?? DBNull.Value);
        command.Parameters.AddWithValue("@mode", analysis.Mode == CsAnalysisMode.Semantic ? "semantic" : "syntactic");
        command.Parameters.AddWithValue("@analyzedUtc", nowUtc);
        command.Parameters.AddWithValue("@modeReason", (object?)analysis.ModeReason ?? DBNull.Value);
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// Cs-kind edges for who-uses on a `.cs` file: files whose
    /// `symbol_refs` target a symbol declared in <paramref name="targetFileId"/>, joined
    /// against `symbols` at QUERY time (never baked). Same-file self-calls are excluded (the
    /// asset graph excludes self-refs the same way). Direct (depth 1) only.
    /// </summary>
    public IReadOnlyList<EdgeResult> GetCsReferencersOfFile(long targetFileId)
    {
        const string sql = """
            SELECT sf.path, sr.line, sr.ref_kind, sr.confidence, srcSym.doc_id, tgtSym.doc_id
            FROM symbol_refs sr
            JOIN symbols tgtSym ON tgtSym.doc_id = sr.target_doc_id AND tgtSym.file_id = @fileId
            JOIN files sf ON sf.id = sr.source_file_id
            LEFT JOIN symbols srcSym ON srcSym.id = sr.source_symbol_id
            WHERE sr.source_file_id != @fileId
            ORDER BY sf.path, sr.line;
            """;
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@fileId", targetFileId);

        using var reader = command.ExecuteReader();
        var targetPath = GetFilePathById(targetFileId);
        var result = new List<EdgeResult>();
        while (reader.Read())
        {
            var sourcePath = reader.GetString(0);
            var line = checked((int)reader.GetInt64(1));
            var refKind = reader.GetString(2);
            var confidence = reader.GetString(3);
            var sourceSymbol = reader.IsDBNull(4) ? null : StripDocIdPrefix(reader.GetString(4));
            var targetDocId = reader.GetString(5);
            var targetSymbol = StripDocIdPrefix(targetDocId);

            result.Add(new EdgeResult(
                sourcePath, targetPath, targetDocId, line, "cs", Depth: 1, Resolved: true, Builtin: false,
                ClassId: null, GameObject: null, MethodName: null, Via: null,
                Confidence: confidence, TargetSymbol: targetSymbol, SourceSymbol: sourceSymbol, RefKind: refKind));
        }

        return result;
    }

    /// <summary>
    /// Cs-kind edges for `uses` on a `.cs` file: the outgoing symmetric counterpart of
    /// <see cref="GetCsReferencersOfFile"/> — every symbol_refs row sourced from
    /// <paramref name="sourceFileId"/>, joined against `symbols` at query time to find the
    /// declaring (target) file. Generalizes the direct-only merge (which previously only applied
    /// to who-uses) to `uses` as well, so a direct query on a `.cs` file shows the same seam on
    /// both sides. Direct (depth 1) only, same scope as its sibling.
    /// </summary>
    public IReadOnlyList<EdgeResult> GetCsDependenciesOfFile(long sourceFileId)
    {
        const string sql = """
            SELECT tf.path, sr.line, sr.ref_kind, sr.confidence, srcSym.doc_id, sr.target_doc_id
            FROM symbol_refs sr
            JOIN symbols tgtSym ON tgtSym.doc_id = sr.target_doc_id
            JOIN files tf ON tf.id = tgtSym.file_id
            LEFT JOIN symbols srcSym ON srcSym.id = sr.source_symbol_id
            WHERE sr.source_file_id = @fileId AND tgtSym.file_id != @fileId
            ORDER BY tf.path, sr.line;
            """;
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@fileId", sourceFileId);

        using var reader = command.ExecuteReader();
        var sourcePath = GetFilePathById(sourceFileId);
        var result = new List<EdgeResult>();
        while (reader.Read())
        {
            var targetPath = reader.GetString(0);
            var line = checked((int)reader.GetInt64(1));
            var refKind = reader.GetString(2);
            var confidence = reader.GetString(3);
            var sourceSymbol = reader.IsDBNull(4) ? null : StripDocIdPrefix(reader.GetString(4));
            var targetDocId = reader.GetString(5);
            var targetSymbol = StripDocIdPrefix(targetDocId);

            result.Add(new EdgeResult(
                sourcePath ?? targetPath, targetPath, targetDocId, line, "cs", Depth: 1, Resolved: true, Builtin: false,
                ClassId: null, GameObject: null, MethodName: null, Via: null,
                Confidence: confidence, TargetSymbol: targetSymbol, SourceSymbol: sourceSymbol, RefKind: refKind));
        }

        return result;
    }

    /// <summary>Strips a doc_id's leading kind-letter prefix ("M:", "T:", ...) for display, e.g. "M:Foo.Jump" -> "Foo.Jump".</summary>
    private static string StripDocIdPrefix(string docId) =>
        docId.Length > 2 && docId[1] == ':' ? docId[2..] : docId;

    private string? GetFilePathById(long fileId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT path FROM files WHERE id = @id;";
        command.Parameters.AddWithValue("@id", fileId);
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// Resolves a `cs-refs` query argument: exact doc_id, then a
    /// `Type.Member`-style match against each kind-prefixed doc_id form, then a fuzzy
    /// name/doc_id substring search. Ambiguity at any non-fuzzy stage returns candidates rather
    /// than guessing across stages.
    /// </summary>
    public CsSymbolResolution ResolveCsSymbol(string query)
    {
        var exact = FindSymbolsByDocId(query);
        if (exact.Count == 1)
        {
            return new CsSymbolResolution(exact[0].DocId, []);
        }

        if (exact.Count > 1)
        {
            return new CsSymbolResolution(null, exact);
        }

        var prefixed = new List<CsSymbolMatch>();
        foreach (var prefix in new[] { "M", "T", "F", "P", "E" })
        {
            prefixed.AddRange(FindSymbolsByDocId($"{prefix}:{query}"));
        }

        if (prefixed.Count == 1)
        {
            return new CsSymbolResolution(prefixed[0].DocId, []);
        }

        if (prefixed.Count > 1)
        {
            return new CsSymbolResolution(null, prefixed);
        }

        var fuzzy = FindSymbolsFuzzy(query, limit: 20);
        if (fuzzy.Count == 1)
        {
            return new CsSymbolResolution(fuzzy[0].DocId, []);
        }

        return new CsSymbolResolution(null, fuzzy);
    }

    /// <summary>
    /// Looks up a resolved symbol's declaring file and shape: `who-uses &lt;symbol&gt;` needs this to (a) find F, the file to seed the
    /// file-level walk at, and (b) decide the basename rule — is S a type whose name equals F's
    /// basename (Unity's MonoBehaviour/ScriptableObject attachment convention)? First
    /// declaration wins on a doc_id naming multiple rows (partial types), same "good enough for
    /// display, never a crash" discipline as <see cref="ReplaceAssemblyAnalyses"/>.
    /// </summary>
    public CsSymbolInfo? GetSymbolInfo(string docId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT s.kind, s.name, f.id, f.path, f.guid
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE s.doc_id = @docId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@docId", docId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new CsSymbolInfo(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private List<CsSymbolMatch> FindSymbolsByDocId(string docId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT s.doc_id, s.kind, s.name, f.path
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE s.doc_id = @docId;
            """;
        command.Parameters.AddWithValue("@docId", docId);
        return ReadSymbolMatches(command);
    }

    private List<CsSymbolMatch> FindSymbolsFuzzy(string fragment, int limit)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT s.doc_id, s.kind, s.name, f.path
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE s.name LIKE @pattern ESCAPE '\' OR s.doc_id LIKE @pattern ESCAPE '\'
            ORDER BY s.name
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@pattern", "%" + EscapeLike(fragment) + "%");
        command.Parameters.AddWithValue("@limit", limit);
        return ReadSymbolMatches(command);
    }

    private static List<CsSymbolMatch> ReadSymbolMatches(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var result = new List<CsSymbolMatch>();
        while (reader.Read())
        {
            result.Add(new CsSymbolMatch(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return result;
    }

    /// <summary>Symbol-level reverse lookup: every `symbol_refs` row targeting the resolved doc_id, resolved at query time.</summary>
    public IReadOnlyList<CsRefEntry> GetCsRefsByDocId(string docId)
    {
        const string sql = """
            SELECT sf.path, sr.line, srcSym.doc_id, sr.ref_kind, sr.confidence
            FROM symbol_refs sr
            JOIN files sf ON sf.id = sr.source_file_id
            LEFT JOIN symbols srcSym ON srcSym.id = sr.source_symbol_id
            WHERE sr.target_doc_id = @docId
            ORDER BY sf.path, sr.line;
            """;
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@docId", docId);
        using var reader = command.ExecuteReader();
        var result = new List<CsRefEntry>();
        while (reader.Read())
        {
            result.Add(new CsRefEntry(
                reader.GetString(0),
                checked((int)reader.GetInt64(1)),
                reader.IsDBNull(2) ? null : StripDocIdPrefix(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return result;
    }

    public CsStats GetCsStats()
    {
        var types = ExecuteScalarInt("SELECT COUNT(*) FROM symbols WHERE kind = 'type';");
        var members = ExecuteScalarInt("SELECT COUNT(*) FROM symbols WHERE kind IN ('method','property','field','event');");
        var refsCount = ExecuteScalarInt("SELECT COUNT(*) FROM symbol_refs;");
        var totalAssemblies = ExecuteScalarInt("SELECT COUNT(*) FROM assemblies;");
        var syntacticAssemblies = ExecuteScalarInt("SELECT COUNT(*) FROM assemblies WHERE mode = 'syntactic';");
        var nameHints = ExecuteScalarInt("SELECT COUNT(*) FROM name_hints;");
        return new CsStats(types, members, refsCount, totalAssemblies, syntacticAssemblies, nameHints);
    }

    // ---- UnityEvent name linking ---------------------------------------------------------

    /// <summary>
    /// The full matching cascade, evaluated at query time. Loads every
    /// guid-carrying UnityEvent call entry (`refs.method_name IS NOT NULL`) and resolves each
    /// one independently: type selection from `target_type_name` ONLY (no fallback — an absent
    /// value means no type is resolvable, full stop, since the guid identifies the containing
    /// asset, never a script), then member selection on that type, then — only if nothing is
    /// declared directly — a breadth-first walk of the project-internal `inherit` chain. See
    /// <see cref="ResolveOneEventCall"/> for the per-call cascade.
    /// </summary>
    public IReadOnlyList<EventLinkResult> ResolveEventLinks()
    {
        var calls = LoadEventCallEntries();
        if (calls.Count == 0)
        {
            return [];
        }

        var results = new List<EventLinkResult>(calls.Count);
        foreach (var call in calls)
        {
            results.AddRange(ResolveOneEventCall(call));
        }

        return results;
    }

    /// <summary>Matched event links whose target member's doc_id is exactly <paramref name="docId"/>
    /// — shaped as depth-0 `kind='event'` <see cref="EdgeResult"/> rows, the event-sourced
    /// counterpart of <see cref="GetCsRefsByDocId"/>.</summary>
    public IReadOnlyList<EdgeResult> GetEventLinksTargetingDocId(string docId) =>
        [.. ResolveEventLinks().Where(l => l.TargetDocId == docId).Select(l => ToEventEdgeResult(l, depth: 0))];

    /// <summary>Matched event links whose target member is declared in <paramref name="fileId"/>
    /// — merged into a `.cs`-file who-uses answer, direct AND transitive, the event-sourced
    /// counterpart of <see cref="GetCsReferencersOfFile"/>.</summary>
    public IReadOnlyList<EdgeResult> GetEventLinksTargetingFile(long fileId) =>
        [.. ResolveEventLinks().Where(l => l.TargetFileId == fileId).Select(l => ToEventEdgeResult(l, depth: 1))];

    /// <summary>
    /// Guid-less same-asset UnityEvent bindings (`name_hints` rows with kind='unityevent-local')
    /// whose captured `type_name`+`name` match the declaring type+member of the resolved symbol
    /// <paramref name="docId"/>: when `type_name` is present and matches a symbol declaring
    /// method M, `who-uses &lt;symbol&gt;` shows an advisory annotation. Matched by raw text
    /// against docId's own type/method portions (both `name_hints.type_name` and this comparison
    /// are already raw/unresolved text — consistent with how `type_name` is treated everywhere
    /// else in this schema, never a baked FK). Always advisory: no resolvable component chain
    /// exists to prove a guid-less binding.
    /// </summary>
    public IReadOnlyList<EdgeResult> GetLocalEventBindingsTargetingDocId(string docId)
    {
        if (!TryParseMemberDocId(docId, out var typeQualifiedName, out var methodName))
        {
            return [];
        }

        const string sql = """
            SELECT sf.path, nh.line
            FROM name_hints nh
            JOIN files sf ON sf.id = nh.source_file_id
            WHERE nh.kind = 'unityevent-local' AND nh.name = @method AND nh.type_name = @type
            ORDER BY sf.path, nh.line;
            """;
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@method", methodName);
        command.Parameters.AddWithValue("@type", typeQualifiedName);

        using var reader = command.ExecuteReader();
        var targetSymbol = StripDocIdPrefix(docId);
        var result = new List<EdgeResult>();
        while (reader.Read())
        {
            result.Add(new EdgeResult(
                reader.GetString(0), TargetPath: null, docId, checked((int)reader.GetInt64(1)), "event",
                Depth: 0, Resolved: true, Builtin: false, ClassId: null, GameObject: null, MethodName: null,
                Via: null, Confidence: EdgeConfidence.Advisory, TargetSymbol: targetSymbol, SourceSymbol: null,
                RefKind: "unityevent-local", ConfidenceLabel: EdgeConfidence.Advisory, Implicit: true));
        }

        return result;
    }

    private static EdgeResult ToEventEdgeResult(EventLinkResult link, int depth) => new(
        link.SourceFilePath, link.TargetFilePath, link.TargetDocId!, link.Line, "event", depth,
        Resolved: true, Builtin: false, ClassId: null, GameObject: link.GameObjectName, MethodName: null,
        Via: null, Confidence: link.Confidence, TargetSymbol: StripDocIdPrefix(link.TargetDocId!), SourceSymbol: null,
        RefKind: link.MatchKind, ConfidenceLabel: link.Confidence, Implicit: true, PropertyPath: link.PropertyPath);

    // ---- asmdef precompiledReferences (dll_refs) ------------------------------------------

    /// <summary>
    /// The `.asmdef`s whose `precompiledReferences` name <paramref name="targetPath"/>'s file name
    /// — merged into a who-uses answer as depth-1 `kind='dll'` edges, the same C#-side merge
    /// idiom as <see cref="GetEventLinksTargetingFile"/> (see the dll_refs DDL comment for why
    /// these deliberately don't live in `all_refs`). This is the cheap, indexed direction: one
    /// lookup on `target_name_norm`.
    ///
    /// Confidence follows Unity's own resolution rule. A name matching exactly one file in the
    /// project is deterministic and machine-checkable, so `proven`; two or more files sharing the
    /// name is the ambiguous case Unity itself errors on, and since this tool then cannot tell
    /// which one was meant it stamps `advisory` on all of them rather than guessing — a downgrade
    /// on ambiguity, never an upgrade by heuristic.
    /// </summary>
    public IReadOnlyList<EdgeResult> GetDllReferencersOfFile(string targetPath)
    {
        var name = NormalizeFileName(targetPath);
        if (name.Length == 0)
        {
            return [];
        }

        var confidence = CountFilesWithFileName(name) > 1 ? EdgeConfidence.Advisory : EdgeConfidence.Proven;

        const string sql = """
            SELECT sf.path, d.line, d.target_name_raw
            FROM dll_refs d
            JOIN files sf ON sf.id = d.source_file_id
            WHERE d.target_name_norm = @name
            ORDER BY sf.path, d.line;
            """;
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@name", name);

        using var reader = command.ExecuteReader();
        var result = new List<EdgeResult>();
        while (reader.Read())
        {
            result.Add(ToDllEdgeResult(reader.GetString(0), targetPath, reader.GetString(2), checked((int)reader.GetInt64(1)), confidence));
        }

        return result;
    }

    /// <summary>
    /// The precompiled assemblies <paramref name="sourceFileId"/> (an `.asmdef`) names, resolved
    /// to files by file name — the forward direction, merged into a `uses` answer. Unlike
    /// <see cref="GetDllReferencersOfFile"/> this direction cannot use an index (matching a bare
    /// name against `files.path` needs a leading-wildcard LIKE), which is exactly why it runs
    /// per-name here for the handful of names on ONE asmdef rather than as an `all_refs` branch
    /// every query would pay for. A name that resolves to nothing stays in the answer as an
    /// unresolved edge — a `precompiledReferences` entry naming a DLL that isn't in the project is
    /// real breakage and must surface, not vanish.
    /// </summary>
    public IReadOnlyList<EdgeResult> GetDllDependenciesOfFile(long sourceFileId)
    {
        const string sql = """
            SELECT sf.path, d.line, d.target_name_raw, d.target_name_norm
            FROM dll_refs d
            JOIN files sf ON sf.id = d.source_file_id
            WHERE d.source_file_id = @id
            ORDER BY d.line;
            """;
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@id", sourceFileId);

        var rows = new List<(string SourcePath, int Line, string Raw, string Norm)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), checked((int)reader.GetInt64(1)), reader.GetString(2), reader.GetString(3)));
            }
        }

        var result = new List<EdgeResult>();
        foreach (var row in rows)
        {
            var targets = FindFilePathsByFileName(row.Norm);
            if (targets.Count == 0)
            {
                result.Add(new EdgeResult(
                    row.SourcePath, TargetPath: null, row.Raw, row.Line, "dll", Depth: 1,
                    Resolved: false, Builtin: false, ClassId: null, GameObject: null, MethodName: null,
                    Via: null, Confidence: null, TargetSymbol: null, SourceSymbol: null,
                    RefKind: "precompiled-reference", ConfidenceLabel: null));
                continue;
            }

            var confidence = targets.Count > 1 ? EdgeConfidence.Advisory : EdgeConfidence.Proven;
            result.AddRange(targets.Select(t => ToDllEdgeResult(row.SourcePath, t, row.Raw, row.Line, confidence)));
        }

        return result;
    }

    /// <summary>
    /// asmdef→plugin-assembly edges as plain (source_file_id, target_file_id) pairs, for the
    /// liveness fixed point. Load-bearing, not a nicety: a plugin DLL is an ordinary
    /// `dead-candidates` candidate, so without these edges one referenced ONLY by name from an
    /// asmdef has no inbound edge at all and gets emitted as `provenDead` — a false positive of
    /// exactly the class the asymmetric-risk invariant forbids. Ambiguous names (several files
    /// sharing one file name) seed EVERY candidate, deliberately over-approximating in the safe
    /// direction, the same way file-granular propagation does everywhere else here.
    /// </summary>
    public IReadOnlyList<(long SourceFileId, long TargetFileId)> GetDllRefFileEdges()
    {
        const string sql = "SELECT DISTINCT source_file_id, target_name_norm FROM dll_refs;";
        using var command = _connection.CreateCommand();
        command.CommandText = sql;

        var rows = new List<(long SourceId, string Norm)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        var result = new List<(long, long)>();
        foreach (var (sourceId, norm) in rows)
        {
            result.AddRange(FindFileIdsByFileName(norm).Select(targetId => (sourceId, targetId)));
        }

        return result;
    }

    private static EdgeResult ToDllEdgeResult(string sourcePath, string targetPath, string rawName, int line, string confidence) => new(
        sourcePath, targetPath, rawName, line, "dll", Depth: 1,
        Resolved: true, Builtin: false, ClassId: null, GameObject: null, MethodName: null,
        Via: null, Confidence: confidence, TargetSymbol: null, SourceSymbol: null,
        RefKind: "precompiled-reference", ConfidenceLabel: confidence);

    /// <summary>Lowercased last path segment — the key `dll_refs.target_name_norm` stores and
    /// matches on. Mirrors <c>ReferenceParser.NormalizeDllName</c>; the two must agree or nothing
    /// ever matches.</summary>
    private static string NormalizeFileName(string path)
    {
        var idx = path.LastIndexOf('/');
        return (idx < 0 ? path : path[(idx + 1)..]).ToLowerInvariant();
    }

    private int CountFilesWithFileName(string nameNorm)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM files WHERE path = @name OR path LIKE @suffix ESCAPE '\\';";
        command.Parameters.AddWithValue("@name", nameNorm);
        command.Parameters.AddWithValue("@suffix", "%/" + EscapeLike(nameNorm));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private List<string> FindFilePathsByFileName(string nameNorm) =>
        [.. QueryFilesByFileName(nameNorm, "path").Select(r => (string)r)];

    private List<long> FindFileIdsByFileName(string nameNorm) =>
        [.. QueryFilesByFileName(nameNorm, "id").Select(r => (long)r)];

    /// <summary>
    /// Files whose last path segment equals <paramref name="nameNorm"/>. The leading-wildcard LIKE
    /// cannot use an index — acceptable ONLY because every caller runs it for the few names on one
    /// asmdef (or one liveness pass), never per query row. `files.path` is COLLATE NOCASE, so the
    /// comparison is already case-insensitive; the `path = @name` arm covers a project-root file
    /// with no directory component at all.
    /// </summary>
    private List<object> QueryFilesByFileName(string nameNorm, string column)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM files WHERE path = @name OR path LIKE @suffix ESCAPE '\\' ORDER BY path;";
        command.Parameters.AddWithValue("@name", nameNorm);
        command.Parameters.AddWithValue("@suffix", "%/" + EscapeLike(nameNorm));
        using var reader = command.ExecuteReader();
        var result = new List<object>();
        while (reader.Read())
        {
            result.Add(reader.GetValue(0));
        }

        return result;
    }

    private sealed record EventCallEntry(string SourceFilePath, int Line, string? SourceFileId, string? GameObjectName, string MethodName, string? TargetTypeName, string? PropertyPath);

    private List<EventCallEntry> LoadEventCallEntries()
    {
        const string sql = """
            SELECT sf.path, r.line, r.source_fileid, go.name, r.method_name, r.target_type_name, r.property_path
            FROM refs r
            JOIN files sf ON sf.id = r.source_file_id
            LEFT JOIN component_gameobject cg ON cg.file_id = r.source_file_id AND cg.component_fileid = r.source_fileid
            LEFT JOIN gameobjects go ON go.file_id = r.source_file_id AND go.go_fileid = cg.go_fileid
            WHERE r.method_name IS NOT NULL
            ORDER BY sf.path, r.line;
            """;
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var result = new List<EventCallEntry>();
        while (reader.Read())
        {
            result.Add(new EventCallEntry(
                reader.GetString(0),
                checked((int)reader.GetInt64(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return result;
    }

    /// <summary>The per-call cascade (4 steps).</summary>
    private List<EventLinkResult> ResolveOneEventCall(EventCallEntry call)
    {
        var (typeDocId, typeSemantic) = ResolveEventTargetType(call.TargetTypeName);
        if (typeDocId is null)
        {
            // Step 1 absent-case: no type resolvable at all -- straight to step 4.
            return [BuildUnmatched(call)];
        }

        var declared = FindDeclaredMembers(typeDocId, call.MethodName);
        if (declared.Count == 1)
        {
            var d = declared[0];
            // Step 2, exactly one: proven iff the matched member's OWN assembly is
            // semantic-mode AND the type-selection cross-check itself came from a
            // semantic-mode candidate set -- either half being merely name-matched
            // (syntactic) is enough to downgrade (labels only ever get downgraded by
            // ambiguity, never upgraded).
            var confidence = d.Semantic && typeSemantic ? EdgeConfidence.Proven : EdgeConfidence.Advisory;
            return [BuildMatched(call, d, "declared", confidence)];
        }

        if (declared.Count > 1)
        {
            // Step 2, multiple (overloads): never pick one -- advisory link to every one.
            return [.. declared.Select(d => BuildMatched(call, d, "overload", EdgeConfidence.Advisory))];
        }

        var inherited = WalkInheritedMembers(typeDocId, call.MethodName);
        if (inherited.Count > 0)
        {
            // Step 3: any match found via the inherited walk is capped at advisory.
            return [.. inherited.Select(d => BuildMatched(call, d, "inherited", EdgeConfidence.Advisory))];
        }

        // Step 4: no member match anywhere in the chain.
        return [BuildUnmatched(call)];
    }

    private static EventLinkResult BuildMatched(EventCallEntry call, MemberCandidate member, string matchKind, string confidence) =>
        new(call.SourceFilePath, call.Line, call.GameObjectName, call.MethodName, call.TargetTypeName,
            member.DocId, member.FilePath, member.FileId, matchKind, confidence, call.PropertyPath);

    private static EventLinkResult BuildUnmatched(EventCallEntry call) =>
        new(call.SourceFilePath, call.Line, call.GameObjectName, call.MethodName, call.TargetTypeName,
            null, null, null, "unmatched", EdgeConfidence.Advisory, call.PropertyPath);

    private sealed record TypeCandidate(string AssemblyName, string Mode);

    /// <summary>
    /// Step 1: type selection from `m_TargetAssemblyTypeName` only (never a guid-to-.cs-file
    /// fallback). A null/empty raw value resolves to "no type" unconditionally. When a type name
    /// IS present, splits into type/assembly parts, finds every project `T:` symbol with that
    /// exact qualified name, and cross-checks the assembly part against each candidate's
    /// declaring assembly when both are known: a cross-check MATCH narrows to the matching
    /// candidate(s); a cross-check MISS (e.g. a stale name left over from an asmdef rename) falls
    /// back to the full candidate set rather than declaring the type unresolvable, since the
    /// type-name half of `m_TargetAssemblyTypeName` is the far more reliable one in practice — a
    /// deliberate judgment call, documented here rather than left implicit. Still-ambiguous
    /// candidates spanning more than one distinct assembly after that never resolve — never
    /// guess which one.
    /// </summary>
    private (string? TypeDocId, bool AssemblySemantic) ResolveEventTargetType(string? rawTargetTypeName)
    {
        if (string.IsNullOrEmpty(rawTargetTypeName))
        {
            return (null, false);
        }

        var commaIdx = rawTargetTypeName.IndexOf(',');
        var typePart = (commaIdx < 0 ? rawTargetTypeName : rawTargetTypeName[..commaIdx]).Trim();
        var assemblyPart = commaIdx < 0 ? null : rawTargetTypeName[(commaIdx + 1)..].Trim();
        if (typePart.Length == 0)
        {
            return (null, false);
        }

        var typeDocId = "T:" + typePart;
        var candidates = QueryTypeCandidates(typeDocId);
        if (candidates.Count == 0)
        {
            return (null, false);
        }

        if (!string.IsNullOrEmpty(assemblyPart))
        {
            var crossChecked = candidates.Where(c => string.Equals(c.AssemblyName, assemblyPart, StringComparison.Ordinal)).ToList();
            if (crossChecked.Count > 0)
            {
                candidates = crossChecked;
            }
        }

        var distinctAssemblies = candidates.Select(c => c.AssemblyName).Distinct(StringComparer.Ordinal).ToList();
        if (distinctAssemblies.Count > 1)
        {
            return (null, false);
        }

        return (typeDocId, string.Equals(candidates[0].Mode, "semantic", StringComparison.Ordinal));
    }

    private List<TypeCandidate> QueryTypeCandidates(string typeDocId)
    {
        const string sql = """
            SELECT DISTINCT a.name, a.mode
            FROM symbols s
            JOIN assemblies a ON a.id = s.assembly_id
            WHERE s.kind = 'type' AND s.doc_id = @docId;
            """;
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@docId", typeDocId);
        using var reader = command.ExecuteReader();
        var result = new List<TypeCandidate>();
        while (reader.Read())
        {
            result.Add(new TypeCandidate(reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    private sealed record MemberCandidate(string DocId, long? FileId, string? FilePath, bool Semantic);

    /// <summary>
    /// Step 2: method symbols named <paramref name="methodName"/> declared DIRECTLY on the type
    /// identified by <paramref name="typeDocId"/> (not inherited). Matched by doc_id shape: an
    /// exact "M:{Type}.{Method}" (no overload) or a "M:{Type}.{Method}(" prefix (an overload's
    /// parameter-list suffix) — never a looser LIKE that could also match an unrelated longer
    /// method name (e.g. "Jump" vs "JumpHigh") or a nested type's member (e.g. "Foo.Bar.Baz" vs
    /// "Foo.Baz"). The LIKE pattern's own text is escaped (method/type names can legally contain
    /// '_', a LIKE wildcard).
    /// </summary>
    private List<MemberCandidate> FindDeclaredMembers(string typeDocId, string methodName)
    {
        var typeQualifiedName = typeDocId[2..];
        var exact = $"M:{typeQualifiedName}.{methodName}";
        var overloadPrefixPattern = EscapeLike(exact + "(") + "%";

        const string sql = """
            SELECT s.doc_id, s.file_id, f.path, a.mode
            FROM symbols s
            LEFT JOIN files f ON f.id = s.file_id
            JOIN assemblies a ON a.id = s.assembly_id
            WHERE s.kind = 'method' AND s.name = @methodName
              AND (s.doc_id = @exact OR s.doc_id LIKE @overloadPattern ESCAPE '\')
            ORDER BY s.doc_id;
            """;
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@methodName", methodName);
        command.Parameters.AddWithValue("@exact", exact);
        command.Parameters.AddWithValue("@overloadPattern", overloadPrefixPattern);
        using var reader = command.ExecuteReader();
        var result = new List<MemberCandidate>();
        while (reader.Read())
        {
            result.Add(new MemberCandidate(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                string.Equals(reader.GetString(3), "semantic", StringComparison.Ordinal)));
        }

        return result;
    }

    /// <summary>
    /// Step 3: breadth-first walk of the project-internal `inherit` chain, from
    /// <paramref name="typeDocId"/>'s own base-list edges outward, checking each newly reached
    /// base type for a DECLARED match (step 2, not further inheritance) before continuing to ITS
    /// bases. Stops at the first level (breadth-wise) where any match is found. Cycle-guarded
    /// (visited set) — a real project can't have an inherit cycle, but a syntactic-mode
    /// text-guess edge theoretically could. The base-list text doesn't distinguish a base CLASS
    /// from an implemented INTERFACE (both extractors record every base-list entry identically
    /// as `ref_kind='inherit'`) — this walk treats them alike, the conservative direction (more
    /// candidates considered, never fewer); not exercised by the committed fixture (single-class
    /// chain only), documented here as a known simplification.
    /// </summary>
    private List<MemberCandidate> WalkInheritedMembers(string typeDocId, string methodName)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { typeDocId };
        var frontier = new List<string> { typeDocId };

        while (frontier.Count > 0)
        {
            var nextFrontier = new List<string>();
            var levelMatches = new List<MemberCandidate>();

            foreach (var current in frontier)
            {
                foreach (var baseDocId in QueryInheritTargets(current))
                {
                    if (!visited.Add(baseDocId))
                    {
                        continue;
                    }

                    levelMatches.AddRange(FindDeclaredMembers(baseDocId, methodName));
                    nextFrontier.Add(baseDocId);
                }
            }

            if (levelMatches.Count > 0)
            {
                return levelMatches;
            }

            frontier = nextFrontier;
        }

        return [];
    }

    /// <summary>The immediate base-list doc_ids of the type declaring `symbols` row(s) whose own
    /// doc_id is <paramref name="typeDocId"/> — `symbol_refs` rows with `ref_kind='inherit'`
    /// sourced from that type's OWN declaration symbol (exactly how both extractors record a
    /// base-list entry: `SourceSymbolDocId` = the declaring type's own doc_id).</summary>
    private List<string> QueryInheritTargets(string typeDocId)
    {
        const string sql = """
            SELECT DISTINCT sr.target_doc_id
            FROM symbol_refs sr
            JOIN symbols s ON s.id = sr.source_symbol_id
            WHERE sr.ref_kind = 'inherit' AND s.doc_id = @typeDocId;
            """;
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@typeDocId", typeDocId);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    /// <summary>Splits a member doc_id ("M:BasePawn.Poke") into its declaring type's qualified
    /// name ("BasePawn") and the member's own simple name ("Poke") — a plain text split on the
    /// LAST '.' before any overload parameter-list suffix, matching the doc_id shapes both
    /// extractors produce. False for a non-member doc_id (no kind-letter member prefix, or no
    /// qualifying '.' to split on).</summary>
    private static bool TryParseMemberDocId(string docId, out string typeQualifiedName, out string methodName)
    {
        typeQualifiedName = "";
        methodName = "";
        if (docId.Length < 3 || docId[1] != ':' || docId[0] is not ('M' or 'F' or 'P' or 'E'))
        {
            return false;
        }

        var body = docId[2..];
        var parenIdx = body.IndexOf('(');
        var withoutParams = parenIdx < 0 ? body : body[..parenIdx];
        var dotIdx = withoutParams.LastIndexOf('.');
        if (dotIdx <= 0 || dotIdx == withoutParams.Length - 1)
        {
            return false;
        }

        typeQualifiedName = withoutParams[..dotIdx];
        methodName = withoutParams[(dotIdx + 1)..];
        return true;
    }

    public IReadOnlyList<ResolveMatch> Resolve(string query)
    {
        var exact = FindByExactPath(query);
        if (exact is not null)
        {
            return [exact];
        }

        if (RegexPatterns.BareGuid().IsMatch(query))
        {
            var byGuid = FindByGuid(query.ToLowerInvariant());
            if (byGuid.Count > 0)
            {
                return byGuid;
            }
        }

        return FindByPathSubstring(query, limit: 20);
    }

    /// <summary>Dumps the full files table (small-scale use only: tests, `stats`/debug tooling).</summary>
    public IReadOnlyList<FileRecord> GetAllFiles() =>
        [.. LoadAllFiles().Values.Select(r => new FileRecord(r.Path, r.Guid, r.Kind, r.Mtime, r.Size, r.MetaMtime, r.IdentityOnly))];

    /// <summary>Reads back the persisted real-root -> project-prefix mapping (see <see cref="ReplaceRoots"/>).</summary>
    public IReadOnlyList<RootMapping> GetRoots()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT real_path, project_prefix FROM roots;";
        using var reader = command.ExecuteReader();
        var result = new List<RootMapping>();
        while (reader.Read())
        {
            result.Add(new RootMapping(reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    internal Dictionary<string, FileRow> LoadAllFiles()
    {
        var result = new Dictionary<string, FileRow>(StringComparer.OrdinalIgnoreCase);
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, path, guid, kind, mtime, size, meta_mtime, identity_only FROM files;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new FileRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                FileKindExtensions.FromDbString(reader.GetString(3)),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.GetInt64(7) != 0);
            result[row.Path] = row;
        }

        return result;
    }

    private void DeleteFiles(IReadOnlyList<long> ids, SqliteTransaction transaction)
    {
        if (ids.Count == 0)
        {
            return;
        }

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM files WHERE id = @id;";
        var idParam = command.Parameters.Add("@id", SqliteType.Integer);
        foreach (var id in ids)
        {
            idParam.Value = id;
            command.ExecuteNonQuery();
        }
    }

    private void UpdateFile(long id, ScannedFileEntry entry, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE files
            SET guid = @guid, kind = @kind, mtime = @mtime, size = @size, meta_mtime = @metaMtime, identity_only = @identityOnly
            WHERE id = @id;
            """;
        AddFileParameters(command, entry);
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
    }

    private void InsertFile(ScannedFileEntry entry, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO files (path, guid, kind, mtime, size, meta_mtime, identity_only)
            VALUES (@path, @guid, @kind, @mtime, @size, @metaMtime, @identityOnly);
            """;
        command.Parameters.AddWithValue("@path", entry.Path);
        AddFileParameters(command, entry);
        command.ExecuteNonQuery();
    }

    private static void AddFileParameters(SqliteCommand command, ScannedFileEntry entry)
    {
        command.Parameters.AddWithValue("@guid", (object?)entry.Guid ?? DBNull.Value);
        command.Parameters.AddWithValue("@kind", entry.Kind.ToDbString());
        command.Parameters.AddWithValue("@mtime", entry.Mtime);
        command.Parameters.AddWithValue("@size", entry.Size);
        command.Parameters.AddWithValue("@metaMtime", (object?)entry.MetaMtime ?? DBNull.Value);
        command.Parameters.AddWithValue("@identityOnly", entry.IdentityOnly ? 1 : 0);
    }

    private List<string> FindPathsByGuid(string guid, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT path FROM files WHERE guid = @guid;";
        command.Parameters.AddWithValue("@guid", guid);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private ResolveMatch? FindByExactPath(string path)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT path, guid, kind, identity_only FROM files WHERE path = @path LIMIT 1;";
        command.Parameters.AddWithValue("@path", path);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadMatch(reader) : null;
    }

    private List<ResolveMatch> FindByGuid(string guid)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT path, guid, kind, identity_only FROM files WHERE guid = @guid ORDER BY path;";
        command.Parameters.AddWithValue("@guid", guid);
        using var reader = command.ExecuteReader();
        var result = new List<ResolveMatch>();
        while (reader.Read())
        {
            result.Add(ReadMatch(reader));
        }

        return result;
    }

    private List<ResolveMatch> FindByPathSubstring(string fragment, int limit)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT path, guid, kind, identity_only FROM files WHERE path LIKE @pattern ESCAPE '\\' ORDER BY path LIMIT @limit;";
        command.Parameters.AddWithValue("@pattern", "%" + EscapeLike(fragment) + "%");
        command.Parameters.AddWithValue("@limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<ResolveMatch>();
        while (reader.Read())
        {
            result.Add(ReadMatch(reader));
        }

        return result;
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("%", "\\%", StringComparison.Ordinal)
             .Replace("_", "\\_", StringComparison.Ordinal);

    private static ResolveMatch ReadMatch(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.IsDBNull(1) ? null : reader.GetString(1),
        FileKindExtensions.FromDbString(reader.GetString(2)),
        reader.GetInt64(3) != 0);

    private string? QueryMetaValue(string key)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta_kv WHERE key = @key;";
        command.Parameters.AddWithValue("@key", key);
        return command.ExecuteScalar() as string;
    }

    private void SetMetaValue(string key, string value)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO meta_kv (key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);
        command.ExecuteNonQuery();
    }

    private int ExecuteScalarInt(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        var result = command.ExecuteScalar();
        return result is null or DBNull ? 0 : checked((int)(long)result);
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // ---- Liveness -------------------------------------------------------------------------
    //
    // Root materialization is a set of query-time path/join predicates over the EXISTING
    // `files`/`symbols`/`assemblies` tables -- no new tables. The fixed point uses a TEMP TABLE
    // (`live_files`) and bulk SQL per propagation pass; screens are computed in C# over a
    // handful of small raw-row loads, since correctness (never trade correctness for speed)
    // matters far more than optimizing a verb that isn't on the hot query path.

    public sealed record LivenessFileRow(long Id, string Path, bool IsFolder, bool IdentityOnly);

    /// <summary>Every `files` row, undifferentiated -- the caller (UnBrambleEngine) partitions
    /// into NeverDead/candidates/roots using the path and convention-exclusion rules.</summary>
    public IReadOnlyList<LivenessFileRow> GetAllFileRowsForLiveness()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, path, kind, identity_only FROM files;";
        using var reader = command.ExecuteReader();
        var result = new List<LivenessFileRow>();
        while (reader.Read())
        {
            result.Add(new LivenessFileRow(
                reader.GetInt64(0), reader.GetString(1),
                reader.GetString(2) == "folder", reader.GetInt64(3) != 0));
        }

        return result;
    }

    /// <summary>Every non-identity-only, non-folder file under `ProjectSettings/` is a root.</summary>
    public IReadOnlyList<long> GetProjectSettingsFileIds() =>
        QueryIdsWhere("identity_only = 0 AND kind != 'folder' AND path LIKE 'ProjectSettings/%'");

    /// <summary>Any file whose path contains a `/Resources/` segment (or starts with
    /// `Resources/` at the root of a scanned root) is a root.</summary>
    public IReadOnlyList<long> GetResourcesFileIds() =>
        QueryIdsWhere("identity_only = 0 AND kind != 'folder' AND (path LIKE '%/Resources/%' OR path LIKE 'Resources/%')");

    /// <summary>Any file under `Assets/StreamingAssets/` is a root.</summary>
    public IReadOnlyList<long> GetStreamingAssetsFileIds() =>
        QueryIdsWhere("identity_only = 0 AND kind != 'folder' AND path LIKE 'Assets/StreamingAssets/%'");

    private List<long> QueryIdsWhere(string whereClause)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT id FROM files WHERE {whereClause};";
        using var reader = command.ExecuteReader();
        var result = new List<long>();
        while (reader.Read())
        {
            result.Add(reader.GetInt64(0));
        }

        return result;
    }

    /// <summary>The Addressables root file node, when it exists in the index (the caller
    /// only calls this once the Addressables gate has confirmed the version).</summary>
    public long? GetAddressablesRootFileId()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id FROM files WHERE path = 'Assets/AddressableAssetsData/AddressableAssetSettings.asset' LIMIT 1;";
        var result = command.ExecuteScalar();
        return result is long l ? l : null;
    }

    /// <summary>Declaring files of unconditional entry points (`entry_reason` 'attribute'
    /// or 'main') in semantic-mode assemblies. Conditional (lifecycle) entry points are
    /// deliberately excluded -- they become live through ordinary reachability once their file
    /// is reachable, never counted as unconditional roots.</summary>
    public IReadOnlyList<long> GetUnconditionalEntryPointFileIds()
    {
        const string sql = """
            SELECT DISTINCT s.file_id
            FROM symbols s
            JOIN assemblies a ON a.id = s.assembly_id
            WHERE s.is_entry_point = 1 AND s.entry_reason IN ('attribute', 'main')
              AND a.mode = 'semantic' AND s.file_id IS NOT NULL;
            """;
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var result = new List<long>();
        while (reader.Read())
        {
            result.Add(reader.GetInt64(0));
        }

        return result;
    }

    public IReadOnlyList<string> GetSemanticAssemblyNames() => QueryAssemblyNamesByMode("semantic");

    public IReadOnlyList<string> GetSyntacticAssemblyNames() => QueryAssemblyNamesByMode("syntactic");

    private List<string> QueryAssemblyNamesByMode(string mode)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT name FROM assemblies WHERE mode = @mode ORDER BY name;";
        command.Parameters.AddWithValue("@mode", mode);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    /// <summary>One syntactic-mode assembly's name plus why it's syntactic (<see
    /// cref="CsModeReasons"/>, null if the assembly predates schema v6 and was never
    /// re-analyzed since). <see cref="IsPackageSourced"/> mirrors <see
    /// cref="UnBramble.Core.Scanning.Scanner.IsPackageSourcedPath"/> against the assembly's asmdef
    /// path (false when the asmdef row was never linked, e.g. a predefined assembly).</summary>
    public readonly record struct SyntacticAssemblyDetail(string Name, string? Reason, bool IsPackageSourced);

    /// <summary>Named, attributed counterpart of <see cref="GetSyntacticAssemblyNames"/> — feeds
    /// the query-time/stats attribution that names WHICH assemblies are syntactic and WHY,
    /// instead of just reporting a count.</summary>
    public IReadOnlyList<SyntacticAssemblyDetail> GetSyntacticAssemblyDetails()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT a.name, a.mode_reason, f.path
            FROM assemblies a
            LEFT JOIN files f ON f.id = a.asmdef_file_id
            WHERE a.mode = 'syntactic'
            ORDER BY a.name;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<SyntacticAssemblyDetail>();
        while (reader.Read())
        {
            var asmdefPath = reader.IsDBNull(2) ? null : reader.GetString(2);
            result.Add(new SyntacticAssemblyDetail(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                asmdefPath is not null && Scanner.IsPackageSourcedPath(asmdefPath)));
        }

        return result;
    }

    /// <summary>
    /// Total guid/path/C#-symbol reference edges INTO any file under <paramref name="assemblyName"/>'s
    /// asmdef directory, FROM a source file OUTSIDE that same directory (an internal reference from
    /// one file in the package to another doesn't count -- the question is whether anything ELSE in
    /// the project depends on it). Zero is real signal, not silence: guid/path resolution
    /// (<c>all_refs</c>) is unaffected by C# analysis mode, so this catches a prefab/scene component
    /// reference regardless of whether the assembly itself is syntactic; <c>symbol_refs</c> adds
    /// genuine C#-level call/inherit edges on top for whatever the assembly's own mode could still
    /// prove. Returns 0 (not an error) for an assembly with no linked asmdef row (a predefined
    /// assembly) -- callers only call this for package-sourced assemblies, which always have one.
    /// </summary>
    public int CountExternalReferencers(string assemblyName)
    {
        string? asmdefPath;
        using (var dirCommand = _connection.CreateCommand())
        {
            dirCommand.CommandText = """
                SELECT f.path
                FROM assemblies a
                JOIN files f ON f.id = a.asmdef_file_id
                WHERE a.name = @name;
                """;
            dirCommand.Parameters.AddWithValue("@name", assemblyName);
            asmdefPath = dirCommand.ExecuteScalar() as string;
        }

        var slash = asmdefPath?.LastIndexOf('/') ?? -1;
        if (asmdefPath is null || slash < 0)
        {
            return 0;
        }

        var likePattern = EscapeLike(asmdefPath[..(slash + 1)]) + "%";

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM (
              SELECT ar.source_file_id
              FROM all_refs ar
              JOIN files tf ON tf.id = ar.target_file_id
              JOIN files sf ON sf.id = ar.source_file_id
              WHERE tf.path LIKE @pattern ESCAPE '\'
                AND sf.path NOT LIKE @pattern ESCAPE '\'
              UNION ALL
              SELECT sr.source_file_id
              FROM symbol_refs sr
              JOIN symbols tsym ON tsym.doc_id = sr.target_doc_id
              JOIN files tf ON tf.id = tsym.file_id
              JOIN files sf ON sf.id = sr.source_file_id
              WHERE tf.path LIKE @pattern ESCAPE '\'
                AND sf.path NOT LIKE @pattern ESCAPE '\'
            );
            """;
        command.Parameters.AddWithValue("@pattern", likePattern);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>
    /// Speculative name-match fallback for a symbol who-uses query: scans `confidence='syntactic'`
    /// `symbol_refs` rows (text-derived target doc-ids — from a syntactic-mode assembly, or from a
    /// semantic assembly's own overload-ambiguity fallback, see <c>SemanticCsExtractor</c>) whose
    /// target doc-id's trailing identifier exactly matches <paramref name="simpleName"/>. These are
    /// leads, never proof (a syntactic target doc-id carries no type information, so "Push" here
    /// could just as easily belong to some unrelated class's own `Push` method) — the caller stamps
    /// every returned row speculative rather than deriving a label from `Confidence` the normal way.
    /// SQL only narrows by substring (no index helps a `%name%` scan); <see cref="MatchesSyntacticTarget"/>
    /// does the exact segment-boundary check so "PrePush" can't match a query for "Push".
    /// <paramref name="isTypeQuery"/> additionally matches `M:...Type.#ctor` calls (a syntactic
    /// `new Foo()` is recorded as a call to `Foo.#ctor`, not as a `T:Foo` reference).
    /// </summary>
    public IReadOnlyList<CsNameMatchEntry> GetSyntacticNameMatchRefs(string excludeDocId, string simpleName, bool isTypeQuery, long? excludeSourceFileId)
    {
        const string sql = """
            SELECT sf.path, sr.line, srcSym.doc_id, sr.ref_kind, sr.target_doc_id
            FROM symbol_refs sr
            JOIN files sf ON sf.id = sr.source_file_id
            LEFT JOIN symbols srcSym ON srcSym.id = sr.source_symbol_id
            WHERE sr.confidence = 'syntactic'
              AND sr.target_doc_id != @excludeDocId
              AND (@excludeSourceFileId IS NULL OR sr.source_file_id != @excludeSourceFileId)
              AND sr.target_doc_id LIKE @pattern ESCAPE '\'
            ORDER BY sf.path, sr.line;
            """;
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@excludeDocId", excludeDocId);
        command.Parameters.AddWithValue("@excludeSourceFileId", (object?)excludeSourceFileId ?? DBNull.Value);
        command.Parameters.AddWithValue("@pattern", "%" + EscapeLike(simpleName) + "%");
        using var reader = command.ExecuteReader();
        var result = new List<CsNameMatchEntry>();
        while (reader.Read())
        {
            var targetDocId = reader.GetString(4);
            if (!MatchesSyntacticTarget(targetDocId, simpleName, isTypeQuery))
            {
                continue;
            }

            result.Add(new CsNameMatchEntry(
                reader.GetString(0),
                checked((int)reader.GetInt64(1)),
                reader.IsDBNull(2) ? null : StripDocIdPrefix(reader.GetString(2)),
                reader.GetString(3),
                targetDocId));
        }

        return result;
    }

    /// <summary>Exact segment-boundary check backing <see cref="GetSyntacticNameMatchRefs"/>: the
    /// text after the doc-id's kind prefix, split on the LAST '.', must equal
    /// <paramref name="simpleName"/> exactly — a plain `LIKE '%Push'` would also match
    /// "PrePush", which shares no boundary with the queried name at all.</summary>
    private static bool MatchesSyntacticTarget(string targetDocId, string simpleName, bool isTypeQuery)
    {
        if (targetDocId.Length < 2 || targetDocId[1] != ':')
        {
            return false;
        }

        var prefix = targetDocId[0];
        var body = targetDocId[2..];

        if (isTypeQuery)
        {
            if (prefix == 'M' && body.EndsWith(".#ctor", StringComparison.Ordinal))
            {
                return LastDotSegment(body[..^".#ctor".Length]) == simpleName;
            }

            return prefix == 'T' && LastDotSegment(body) == simpleName;
        }

        return prefix == 'M' && !body.EndsWith(".#ctor", StringComparison.Ordinal) && LastDotSegment(body) == simpleName;
    }

    private static string LastDotSegment(string value)
    {
        var dot = value.LastIndexOf('.');
        return dot >= 0 ? value[(dot + 1)..] : value;
    }

    /// <summary>Resolves a set of project-relative paths to their file ids (paths with no
    /// matching row are simply absent from the result -- never a crash).</summary>
    public IReadOnlyDictionary<string, long> GetFileIdsByPaths(IEnumerable<string> paths)
    {
        var distinctPaths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (distinctPaths.Count == 0)
        {
            return result;
        }

        using var command = _connection.CreateCommand();
        var inClause = BuildInClauseText("path", distinctPaths.Count, "p", out var paramNames);
        command.CommandText = $"SELECT id, path FROM files WHERE {inClause};";
        for (var i = 0; i < distinctPaths.Count; i++)
        {
            command.Parameters.AddWithValue(paramNames[i], distinctPaths[i]);
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(1)] = reader.GetInt64(0);
        }

        return result;
    }

    private static string BuildInClauseText(string column, int count, string prefix, out List<string> paramNames)
    {
        paramNames = [];
        for (var i = 0; i < count; i++)
        {
            paramNames.Add($"@{prefix}{i}");
        }

        return count == 0 ? "0 = 1" : $"{column} IN ({string.Join(",", paramNames)})";
    }

    // ---- Fixed point: temp-table propagation workspace ----------------------------------------

    /// <summary>(Re)initializes the per-run liveness workspace -- safe to call repeatedly on the
    /// same long-lived connection (each call clears prior state).</summary>
    public void InitLivenessWorkspace()
    {
        ExecuteNonQuery(_connection, "CREATE TEMP TABLE IF NOT EXISTS live_files (id INTEGER PRIMARY KEY);");
        ExecuteNonQuery(_connection, "DELETE FROM live_files;");
        ExecuteNonQuery(_connection, "CREATE TEMP TABLE IF NOT EXISTS seeded_extra_edges (source_id INTEGER, target_id INTEGER);");
        ExecuteNonQuery(_connection, "DELETE FROM seeded_extra_edges;");
    }

    public void SeedLiveFiles(IEnumerable<long> ids)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO live_files (id) VALUES (@id);";
        var idParam = command.Parameters.Add("@id", SqliteType.Integer);
        foreach (var id in ids)
        {
            idParam.Value = id;
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Propagation source for every real file→file edge that lives OUTSIDE `unified_walk_edges`,
    /// as plain (source_file_id, target_file_id) pairs: matched (proven or advisory) UnityEvent
    /// bindings, and asmdef→plugin-assembly `precompiledReferences` edges. Seeded ONCE per run
    /// (neither source depends on liveness itself) and then unioned into every propagation pass
    /// below -- equivalent to, and simpler than, a separate interleaved step, since neither step
    /// order nor repetition changes the fixed point reached. Safe to call more than once; the
    /// seeded sets simply accumulate.
    /// </summary>
    public void SeedExtraWalkEdges(IEnumerable<(long SourceFileId, long TargetFileId)> edges)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO seeded_extra_edges (source_id, target_id) VALUES (@s, @t);";
        var sourceParam = command.Parameters.Add("@s", SqliteType.Integer);
        var targetParam = command.Parameters.Add("@t", SqliteType.Integer);
        foreach (var (source, target) in edges)
        {
            sourceParam.Value = source;
            targetParam.Value = target;
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// One relaxation pass over `unified_walk_edges` (THE SAME view `who-uses`
    /// walks -- the coherence guarantee holds by construction, not by audit, because this is
    /// literally that view) plus the edges seeded by <see cref="SeedExtraWalkEdges"/>. Returns
    /// true if any new file became live this pass; the caller loops until false (the inner fixed
    /// point).
    /// </summary>
    public bool PropagateLiveFilesOnce()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO live_files (id)
            SELECT uw.target_file_id
            FROM unified_walk_edges uw
            JOIN live_files lf ON lf.id = uw.source_file_id
            UNION
            SELECT ee.target_id
            FROM seeded_extra_edges ee
            JOIN live_files lf ON lf.id = ee.source_id;
            """;
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>Drains the same reachability relation as <see cref="PropagateLiveFilesOnce"/>
    /// in one recursive SQLite statement. The small-step method remains useful to focused tests;
    /// production reachability uses this form so a long dependency chain does not rescan the
    /// complete edge view once per graph depth.</summary>
    public void PropagateLiveFilesToFixedPoint()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE
            walk_edges(source_id, target_id) AS (
                SELECT source_file_id, target_file_id FROM unified_walk_edges
                UNION ALL
                SELECT source_id, target_id FROM seeded_extra_edges
            ),
            reachable(id) AS (
                SELECT id FROM live_files
                UNION
                SELECT e.target_id
                FROM walk_edges e
                JOIN reachable r ON r.id = e.source_id
            )
            INSERT OR IGNORE INTO live_files (id)
            SELECT id FROM reachable;
            """;
        command.ExecuteNonQuery();
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public bool TryGetBuildReachabilityCache(string configKey, out HashSet<string> paths)
    {
        paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT f.path
            FROM build_reachable_state s
            LEFT JOIN build_reachable_cache c ON 1 = 1
            LEFT JOIN files f ON f.id = c.file_id
            WHERE s.id = 1 AND s.valid = 1 AND s.config_key = @configKey;
            """;
        command.Parameters.AddWithValue("@configKey", configKey);
        using var reader = command.ExecuteReader();
        var valid = false;
        while (reader.Read())
        {
            valid = true;
            if (!reader.IsDBNull(0))
            {
                paths.Add(reader.GetString(0));
            }
        }

        return valid;
    }

    /// <summary>Copies the already-computed <c>live_files</c> workspace into the persistent
    /// cache and marks it valid atomically, so another process sees either the previous invalid
    /// state or the complete new snapshot—never a partial reachability set.</summary>
    public long GetBuildReachabilityGraphGeneration()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT graph_generation FROM build_reachable_state WHERE id = 1;";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public bool ReplaceBuildReachabilityCacheFromWorkspace(string configKey, long expectedGraphGeneration)
    {
        using var transaction = _connection.BeginTransaction();
        using (var claim = _connection.CreateCommand())
        {
            claim.Transaction = transaction;
            claim.CommandText = """
                UPDATE build_reachable_state
                   SET valid = 0
                 WHERE id = 1 AND graph_generation = @expectedGraphGeneration;
                """;
            claim.Parameters.AddWithValue("@expectedGraphGeneration", expectedGraphGeneration);
            if (claim.ExecuteNonQuery() == 0)
            {
                transaction.Rollback();
                return false;
            }
        }

        ExecuteNonQuery(_connection, "DELETE FROM build_reachable_cache;", transaction);
        ExecuteNonQuery(_connection, "INSERT INTO build_reachable_cache (file_id) SELECT id FROM live_files;", transaction);
        using (var state = _connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText = "UPDATE build_reachable_state SET valid = 1, config_key = @configKey WHERE id = 1;";
            state.Parameters.AddWithValue("@configKey", configKey);
            state.ExecuteNonQuery();
        }
        transaction.Commit();
        return true;
    }

    public void InvalidateBuildReachabilityCache() =>
        ExecuteNonQuery(_connection, "UPDATE build_reachable_state SET valid = 0, graph_generation = graph_generation + 1 WHERE id = 1;");


    public IReadOnlyList<long> GetLiveFileIds()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id FROM live_files;";
        using var reader = command.ExecuteReader();
        var result = new List<long>();
        while (reader.Read())
        {
            result.Add(reader.GetInt64(0));
        }

        return result;
    }

    /// <summary>The live_files workspace projected to paths — what the who-uses build-reachable
    /// tag needs (EdgeResults carry paths, not file ids).</summary>
    public IReadOnlyList<string> GetLiveFilePaths()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT f.path FROM live_files lf JOIN files f ON f.id = lf.id;";
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    // ---- Screens: raw negative-evidence loads (matched against the live set in C#) ------------

    public sealed record NameHintScreenRow(long SourceFileId, string Name, string Kind);

    /// <summary>name_hints rows of the given kinds, undifferentiated by liveness -- the caller
    /// filters to rows sourced from a currently-live file each fixed-point pass.</summary>
    public IReadOnlyList<NameHintScreenRow> GetNameHintRows(params string[] kinds)
    {
        if (kinds.Length == 0)
        {
            return [];
        }

        var inClause = BuildInClauseText("kind", kinds.Length, "k", out var paramNames);
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT source_file_id, name, kind FROM name_hints WHERE {inClause};";
        for (var i = 0; i < kinds.Length; i++)
        {
            command.Parameters.AddWithValue(paramNames[i], kinds[i]);
        }

        using var reader = command.ExecuteReader();
        var result = new List<NameHintScreenRow>();
        while (reader.Read())
        {
            result.Add(new NameHintScreenRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        }

        return result;
    }

    // ---- who-uses/uses `disabled-region-refs-possible` blind spot -----------------------------
    // Deliberately NOT liveness-scoped, unlike the disabled-region SCREEN above: who-uses/
    // uses answers have no "live set" fixed point in play (that machinery is dead-candidates-
    // only), and the point here is a coarse, always-available positive signal ("this name also
    // appears in code your current defines excluded"), not a liveness proof. Name-only matching
    // is inherent to the capture itself (disabled-region text carries no semantic doc_id -- only
    // raw identifier tokens), so both queries below intentionally accept the same
    // false-positive direction (a same-named-but-unrelated identifier) that the screen accepts.

    /// <summary>
    /// True if any `#if`-disabled-region identifier (`name_hints` rows with `kind='cs-disabled'`)
    /// anywhere in the project textually matches `simpleName`. Used
    /// by `WhoUsesSymbol` (`UnBrambleEngine`) once a query resolves to a specific symbol -- the
    /// precise case of the `disabled-region-refs-possible` blind spot.
    /// </summary>
    public bool HasDisabledRegionNameHint(string simpleName)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM name_hints WHERE kind = 'cs-disabled' AND name = @name);";
        command.Parameters.AddWithValue("@name", simpleName);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    /// <summary>
    /// True if any `#if`-disabled-region identifier anywhere in the project textually matches
    /// the name of any symbol DECLARED in file `fileId`. The fallback used by plain file-shaped
    /// who-uses/uses queries (no single resolved symbol to check -- e.g. `who-uses Foo.cs`):
    /// coarser than <see cref="HasDisabledRegionNameHint"/> (any of the file's members can
    /// trigger it, not just the one queried), the same direction every other blind spot in this
    /// table already errs in.
    ///
    /// A whole-file platform-gated `.cs` file (the same zero-symbols
    /// case as `LivenessModels.ScreenReasons.NoExtractedSymbols`, `UnBrambleEngine.
    /// RunDeadCandidates`) has NO rows in `symbols` at all, so the JOIN above can never match --
    /// there is nothing of the file's OWN for `symbols.name` to collide against, regardless of
    /// what disabled-region text elsewhere references it. Querying such a file (e.g. `who-uses
    /// AndroidWorker.cs`) would silently omit the blind-spot flag even though the file could not
    /// be analyzed under the current defines at all. Falls back to the file's own name (Unity's
    /// attachment convention: a MonoBehaviour's type name equals its file's base name, and
    /// disabled-region name_hints are raw identifier tokens -- `new AndroidWorker()` in a
    /// disabled region textually matches "AndroidWorker.cs"'s stem) restricted to files that
    /// produced literally zero symbols, so the precise per-symbol path above is unchanged for
    /// every file that COULD be analyzed.
    /// </summary>
    public bool HasDisabledRegionNameHintForFile(long fileId, string filePath)
    {
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                SELECT EXISTS(
                    SELECT 1 FROM name_hints nh
                    JOIN symbols s ON s.name = nh.name
                    WHERE nh.kind = 'cs-disabled' AND s.file_id = @fileId
                );
                """;
            command.Parameters.AddWithValue("@fileId", fileId);
            if (Convert.ToInt64(command.ExecuteScalar()) != 0)
            {
                return true;
            }
        }

        using (var zeroSymbols = _connection.CreateCommand())
        {
            zeroSymbols.CommandText = "SELECT NOT EXISTS(SELECT 1 FROM symbols WHERE file_id = @fileId);";
            zeroSymbols.Parameters.AddWithValue("@fileId", fileId);
            if (Convert.ToInt64(zeroSymbols.ExecuteScalar()) == 0)
            {
                return false;
            }
        }

        var stem = Path.GetFileNameWithoutExtension(filePath);
        using var fallback = _connection.CreateCommand();
        fallback.CommandText = "SELECT EXISTS(SELECT 1 FROM name_hints WHERE kind = 'cs-disabled' AND name = @stem);";
        fallback.Parameters.AddWithValue("@stem", stem);
        return Convert.ToInt64(fallback.ExecuteScalar()) != 0;
    }

    public sealed record SourceKeyedRow(long SourceFileId, string Key);

    /// <summary>Path-ref name collision screen input: every path-kind
    /// unresolved ref, as (source_file_id, target_path_norm).</summary>
    public IReadOnlyList<SourceKeyedRow> GetUnresolvedPathRefRows()
    {
        const string sql = "SELECT source_file_id, target_key FROM unresolved_refs WHERE kind = 'path';";
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var result = new List<SourceKeyedRow>();
        while (reader.Read())
        {
            result.Add(new SourceKeyedRow(reader.GetInt64(0), reader.GetString(1)));
        }

        return result;
    }

    /// <summary>Syntactic-text collision screen input: every
    /// `symbol_refs` row whose `target_doc_id` resolves to no `symbols` row, as (source_file_id,
    /// trailing identifier of the unresolved doc_id).</summary>
    public IReadOnlyList<SourceKeyedRow> GetUnresolvedSymbolRefTrailingIdentifiers()
    {
        const string sql = """
            SELECT sr.source_file_id, sr.target_doc_id
            FROM symbol_refs sr
            WHERE NOT EXISTS (SELECT 1 FROM symbols s WHERE s.doc_id = sr.target_doc_id);
            """;
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var result = new List<SourceKeyedRow>();
        while (reader.Read())
        {
            result.Add(new SourceKeyedRow(reader.GetInt64(0), ExtractTrailingIdentifier(reader.GetString(1))));
        }

        return result;
    }

    /// <summary>Strips a doc_id's kind-letter prefix and any overload parameter-list suffix,
    /// then takes the text after the LAST '.' -- "M:Foo.JumpHigh(System.Int32)" -&gt; "JumpHigh".</summary>
    private static string ExtractTrailingIdentifier(string docId)
    {
        var body = docId.Length > 2 && docId[1] == ':' ? docId[2..] : docId;
        var parenIdx = body.IndexOf('(');
        var withoutParams = parenIdx < 0 ? body : body[..parenIdx];
        var dotIdx = withoutParams.LastIndexOf('.');
        return dotIdx < 0 ? withoutParams : withoutParams[(dotIdx + 1)..];
    }

    /// <summary>Unmatched-UnityEvent-name screen input: every UNMATCHED guid-carrying event
    /// call's raw method name, as (source_file_id, raw method name) -- the source asset's own
    /// liveness is what makes the name enter the screen set, not the (nonexistent) match.</summary>
    public IReadOnlyList<SourceKeyedRow> GetUnmatchedEventRawNameRows()
    {
        var unmatched = ResolveEventLinks().Where(l => !l.IsMatched).ToList();
        if (unmatched.Count == 0)
        {
            return [];
        }

        var idByPath = GetFileIdsByPaths(unmatched.Select(l => l.SourceFilePath));
        var result = new List<SourceKeyedRow>();
        foreach (var link in unmatched)
        {
            if (idByPath.TryGetValue(link.SourceFilePath, out var fileId))
            {
                result.Add(new SourceKeyedRow(fileId, link.RawMethodName));
            }
        }

        return result;
    }

    /// <summary>Edge source: matched (proven or advisory) event links, as
    /// (source_file_id, target_file_id) pairs.</summary>
    public IReadOnlyList<(long SourceFileId, long TargetFileId)> GetMatchedEventLinkFileEdges()
    {
        var matched = ResolveEventLinks().Where(l => l.IsMatched && l.TargetFileId is not null).ToList();
        if (matched.Count == 0)
        {
            return [];
        }

        var idByPath = GetFileIdsByPaths(matched.Select(l => l.SourceFilePath));
        var result = new List<(long, long)>();
        foreach (var link in matched)
        {
            if (idByPath.TryGetValue(link.SourceFilePath, out var sourceId))
            {
                result.Add((sourceId, link.TargetFileId!.Value));
            }
        }

        return result;
    }

    public sealed record CsSymbolNameRow(long FileId, string Name, string Kind);

    /// <summary>Every declared symbol's (file_id, name, kind) -- feeds the name-hint-collision
    /// (method names only), disabled-region (any kind), and syntactic-text-collision (any kind)
    /// screens, each filtering by kind as appropriate.</summary>
    public IReadOnlyList<CsSymbolNameRow> GetCsSymbolNamesByFile()
    {
        const string sql = "SELECT file_id, name, kind FROM symbols WHERE file_id IS NOT NULL;";
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var result = new List<CsSymbolNameRow>();
        while (reader.Read())
        {
            result.Add(new CsSymbolNameRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        }

        return result;
    }

    public sealed record CsSymbolAttrsRow(long FileId, string Attrs);

    /// <summary>Attribute screen input: every symbol carrying at least
    /// one attribute, as (file_id, space-joined attribute simple names).</summary>
    public IReadOnlyList<CsSymbolAttrsRow> GetCsSymbolAttrsByFile()
    {
        const string sql = "SELECT file_id, attrs FROM symbols WHERE attrs IS NOT NULL AND file_id IS NOT NULL;";
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var result = new List<CsSymbolAttrsRow>();
        while (reader.Read())
        {
            result.Add(new CsSymbolAttrsRow(reader.GetInt64(0), reader.GetString(1)));
        }

        return result;
    }

    /// <summary>Interface/virtual-dispatch guard input: every `inherit`-
    /// kind cs_file_refs edge, as (source_file_id, target_file_id) -- a candidate whose type
    /// inherits/implements a LIVE target is screened (Roslyn resolves dispatch to the base/
    /// interface doc_id, not the implementation's).</summary>
    public IReadOnlyList<(long SourceFileId, long TargetFileId)> GetInheritEdges()
    {
        const string sql = "SELECT source_file_id, target_file_id FROM cs_file_refs WHERE ref_kind = 'inherit';";
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var result = new List<(long, long)>();
        while (reader.Read())
        {
            result.Add((reader.GetInt64(0), reader.GetInt64(1)));
        }

        return result;
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
