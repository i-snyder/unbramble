using System.Collections.Concurrent;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using UnBramble.Core;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// Per-file DB-write scoping AFTER a full-unit extraction (<see cref="UnBrambleEngine"/>'s
/// private <c>ScopeFullUnitAnalysisToChangedFiles</c>) — the fix for a live finding that a
/// Unity-regenerated csproj mid-watch (FULL-REBUILD reason=csproj-mtime-changed) correctly
/// rebuilt the compilation and correctly fell back to full-unit extraction, but then paid a
/// full-unit delete-reinsert of EVERY file's rows (~500s+ on the real Assembly-CSharp) even
/// though nearly none of the unit's files extract any differently after a csproj regeneration.
///
/// The mechanism is deliberately an EXACT-row-content diff (<c>CsFileRowFingerprint</c> /
/// <c>UnBrambleStore.GetFileRowFingerprints</c>), NOT the declaration-shape proxy
/// <c>TryExtractScoped</c> uses: after a full rebuild the changed input is the environment
/// itself, and a DefineConstants change can flip an <c>#if</c> inside a method BODY — changing a
/// file's call refs while its declaration shape stays byte-identical — so shape equality proves
/// nothing here. Scenario (d) covers exactly that would-be false negative.
///
/// Same discipline as CsScopedExtractionTests: every scenario asserts BOTH (a) parity against a
/// from-scratch rebuild of the identical final tree (never-wrong) and (b), via the
/// <c>[cs-cache]</c> diagnostics sink, that the specific code path under test (FULL-REBUILD →
/// full-unit extraction → scoped db-write) actually fired, plus row-id byte-stability for files
/// whose rows didn't change (proving no delete-reinsert happened for them, not merely that the
/// content survived a silent full rewrite).
/// </summary>
public class CsFullRebuildDbScopingTests
{
    // ---- (a) csproj-mtime full rebuild + body-only ref edit: write scoped to the one file ----

    [Fact]
    public void CsprojMtimeFullRebuild_BodyRefEdit_DbWriteScopedToChangedFileOnly()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        AddScript(fixture, "RebuildDep.cs", """
            public static class RebuildDep
            {
                public static void One() { }
                public static void Two() { }
            }
            """);
        AddScript(fixture, "RebuildCaller.cs", """
            public class RebuildCaller
            {
                public void Go()
                {
                    RebuildDep.One();
                }
            }
            """);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        var log = new ConcurrentQueue<string>();
        engine.EnableWatchCompilationCache(log.Enqueue);
        engine.RunIndex(full: false);

        // Controls: Foo.cs (committed fixture file) and RebuildDep.cs are untouched by the edit
        // below — their row ids must survive the csproj-triggered full rebuild byte-stable.
        var fooSymbolsBefore = GetSymbolIds(engine.DbPath, "Assets/Scripts/Foo.cs");
        var fooRefsBefore = GetSymbolRefIds(engine.DbPath, "Assets/Scripts/Foo.cs");
        var depSymbolsBefore = GetSymbolIds(engine.DbPath, "Assets/Scripts/RebuildDep.cs");
        Assert.NotEmpty(fooSymbolsBefore);
        Assert.NotEmpty(depSymbolsBefore);

        // Body-only edit (same declaration shape, different call target) PLUS a csproj mtime
        // bump — what a Unity reimport regenerating the csproj alongside a script edit looks
        // like. The mtime move forces ProcessSemanticUnitWithWatchCache's FULL-REBUILD path,
        // which makes TryExtractScoped ineligible by design.
        File.WriteAllText(fixture.Combine("Assets", "Scripts", "RebuildCaller.cs"), """
            public class RebuildCaller
            {
                public void Go()
                {
                    RebuildDep.Two();
                }
            }
            """);
        Bump(fixture.Combine("Assets", "Scripts", "RebuildCaller.cs"), 5);
        Bump(Path.Combine(fixture.Root, "Game.csproj"), 30);

        log.Clear();
        engine.RunIndex(full: false);

        // The exact chain under test fired: csproj-mtime full rebuild, full-unit extraction (no
        // scoped-extraction attempt), then a db-write scoped to exactly the one changed file.
        Assert.Contains(log, l => l.Contains("Game build: FULL-REBUILD reason=csproj-mtime-changed"));
        Assert.DoesNotContain(log, l => l.Contains("Game extract (scoped)"));
        Assert.Contains(log, l => l.Contains("Game extract (full-unit)"));
        Assert.Contains(log, l => l.Contains("Game db-scope (full-unit): changed=1/"));

        // Untouched files' rows were never deleted-then-reinserted: same primary keys.
        Assert.Equal(fooSymbolsBefore, GetSymbolIds(engine.DbPath, "Assets/Scripts/Foo.cs"));
        Assert.Equal(fooRefsBefore, GetSymbolRefIds(engine.DbPath, "Assets/Scripts/Foo.cs"));
        Assert.Equal(depSymbolsBefore, GetSymbolIds(engine.DbPath, "Assets/Scripts/RebuildDep.cs"));

        // The changed file's rows WERE rewritten with the new resolution.
        var callerFileId = RequireFileId(engine.DbPath, "Assets/Scripts/RebuildCaller.cs");
        var callTargets = GetCallTargets(engine.DbPath, callerFileId);
        Assert.Contains(callTargets, t => t.Contains("RebuildDep.Two", StringComparison.Ordinal));
        Assert.DoesNotContain(callTargets, t => t.Contains("RebuildDep.One", StringComparison.Ordinal));

        AssertParityWithFreshRebuild(fixture, engine);
    }

    // ---- (b) full rebuild where an UNEDITED file's resolution changes: it must be rewritten ---

    [Fact]
    public void CsprojMtimeFullRebuild_UneditedFilesResolutionChanged_ThatFileIsRewrittenToo()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        AddScript(fixture, "RebuildHost.cs", """
            public static class RebuildHost
            {
                public static void Handle(int x) { }
            }
            """);
        AddScript(fixture, "RebuildHostCaller.cs", """
            public class RebuildHostCaller
            {
                public void Go()
                {
                    RebuildHost.Handle(1);
                }
            }
            """);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        var log = new ConcurrentQueue<string>();
        engine.EnableWatchCompilationCache(log.Enqueue);
        engine.RunIndex(full: false);

        var fooSymbolsBefore = GetSymbolIds(engine.DbPath, "Assets/Scripts/Foo.cs");
        var callerFileId = RequireFileId(engine.DbPath, "Assets/Scripts/RebuildHostCaller.cs");
        Assert.Contains(GetCallTargets(engine.DbPath, callerFileId), t => t.Contains("Int32", StringComparison.Ordinal));

        // Only the HOST is edited (its declaration shape changes: Handle(Int32) -> Handle(Int64)),
        // plus the csproj mtime bump forcing the full rebuild. The CALLER file is never touched,
        // but its persisted call ref's target_doc_id is now stale — the exact-content diff must
        // catch it and rewrite the caller too, not just the edited host.
        File.WriteAllText(fixture.Combine("Assets", "Scripts", "RebuildHost.cs"), """
            public static class RebuildHost
            {
                public static void Handle(long x) { }
            }
            """);
        Bump(fixture.Combine("Assets", "Scripts", "RebuildHost.cs"), 5);
        Bump(Path.Combine(fixture.Root, "Game.csproj"), 30);

        log.Clear();
        engine.RunIndex(full: false);

        Assert.Contains(log, l => l.Contains("Game build: FULL-REBUILD reason=csproj-mtime-changed"));
        Assert.Contains(log, l => l.Contains("Game extract (full-unit)"));
        // Exactly two files genuinely changed: the edited host and the unedited caller.
        Assert.Contains(log, l => l.Contains("Game db-scope (full-unit): changed=2/"));

        // The unedited caller's ref now resolves the NEW signature — proving the scoped write
        // included it (a false negative here would have left the stale Int32 target).
        var callTargetsAfter = GetCallTargets(engine.DbPath, callerFileId);
        Assert.Contains(callTargetsAfter, t => t.Contains("Int64", StringComparison.Ordinal));
        Assert.DoesNotContain(callTargetsAfter, t => t.Contains("Int32", StringComparison.Ordinal));

        // Genuinely-unchanged files still kept their exact rows.
        Assert.Equal(fooSymbolsBefore, GetSymbolIds(engine.DbPath, "Assets/Scripts/Foo.cs"));

        AssertParityWithFreshRebuild(fixture, engine);
    }

    // ---- (c) full rebuild with zero row-level changes: nothing is rewritten at all ------------

    [Fact]
    public void CsprojMtimeFullRebuild_IdenticalContentTouch_ZeroFilesRewritten()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        // The touched file must have NO cs name_hints (no #if-disabled regions, no
        // SendMessage-family literals): ReplaceFileReferences deletes a TOUCHED file's
        // name_hints rows earlier in the same pass (the cs full write is what restores them),
        // so a touched file WITH hints genuinely must be rewritten — Foo.cs, with its big
        // #if UNITY_ANDROID block, is exactly such a file and deliberately NOT used here.
        AddScript(fixture, "RebuildQuiet.cs", """
            public class RebuildQuiet
            {
                public void Noop()
                {
                    int x = 1;
                }
            }
            """);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        var log = new ConcurrentQueue<string>();
        engine.EnableWatchCompilationCache(log.Enqueue);
        engine.RunIndex(full: false);

        var fooSymbolsBefore = GetSymbolIds(engine.DbPath, "Assets/Scripts/Foo.cs");
        var fooRefsBefore = GetSymbolRefIds(engine.DbPath, "Assets/Scripts/Foo.cs");
        var quietSymbolsBefore = GetSymbolIds(engine.DbPath, "Assets/Scripts/RebuildQuiet.cs");
        Assert.NotEmpty(fooSymbolsBefore);
        Assert.NotEmpty(quietSymbolsBefore);

        // Same-content rewrite + mtime bump (the unit IS dirty; extraction runs) plus the csproj
        // bump (full rebuild; TryExtractScoped ineligible). No file's rows can actually differ,
        // so the db-scope diff must conclude changed=0 and rewrite nothing — including the
        // "edited" file itself.
        var quietPath = fixture.Combine("Assets", "Scripts", "RebuildQuiet.cs");
        File.WriteAllText(quietPath, File.ReadAllText(quietPath));
        Bump(quietPath, 5);
        Bump(Path.Combine(fixture.Root, "Game.csproj"), 30);

        log.Clear();
        engine.RunIndex(full: false);

        Assert.Contains(log, l => l.Contains("Game build: FULL-REBUILD reason=csproj-mtime-changed"));
        Assert.Contains(log, l => l.Contains("Game extract (full-unit)"));
        Assert.Contains(log, l => l.Contains("Game db-scope (full-unit): changed=0/"));

        Assert.Equal(fooSymbolsBefore, GetSymbolIds(engine.DbPath, "Assets/Scripts/Foo.cs"));
        Assert.Equal(fooRefsBefore, GetSymbolRefIds(engine.DbPath, "Assets/Scripts/Foo.cs"));
        Assert.Equal(quietSymbolsBefore, GetSymbolIds(engine.DbPath, "Assets/Scripts/RebuildQuiet.cs"));

        AssertParityWithFreshRebuild(fixture, engine);
    }

    // ---- (d) the case declaration-shape diffing would get WRONG: a body-level #if flip --------

    [Fact]
    public void DefinesChangeFullRebuild_BodyOnlyIfFlip_AffectedFileRewrittenDespiteIdenticalShape()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        AddScript(fixture, "IfFlipTargets.cs", """
            public static class IfFlipTargets
            {
                public static void Alpha() { }
                public static void Beta() { }
            }
            """);
        AddScript(fixture, "IfFlipCaller.cs", """
            public class IfFlipCaller
            {
                public void Go()
                {
            #if REBUILD_SCOPE_TEST
                    IfFlipTargets.Beta();
            #else
                    IfFlipTargets.Alpha();
            #endif
                }
            }
            """);
        // Hint-free touch target (see the zero-files test for why the touched file must not
        // have cs name_hints of its own — a hint-carrying file like Foo.cs genuinely has to be
        // rewritten after any touch, which would obscure this scenario's changed-count).
        AddScript(fixture, "IfFlipQuiet.cs", """
            public class IfFlipQuiet
            {
                public void Noop()
                {
                    int x = 1;
                }
            }
            """);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        var log = new ConcurrentQueue<string>();
        engine.EnableWatchCompilationCache(log.Enqueue);
        engine.RunIndex(full: false);

        var callerFileId = RequireFileId(engine.DbPath, "Assets/Scripts/IfFlipCaller.cs");
        Assert.Contains(GetCallTargets(engine.DbPath, callerFileId), t => t.Contains("Alpha", StringComparison.Ordinal));
        var targetsSymbolsBefore = GetSymbolIds(engine.DbPath, "Assets/Scripts/IfFlipTargets.cs");

        // Unity regenerates Game.csproj with a NEW define set (full rebuild — the whole point of
        // the csproj-mtime/shape-hash safety fallback), and an unrelated file is touched with
        // identical content so the unit is dirty. IfFlipCaller.cs itself is NEVER touched and
        // its declaration shape (T:IfFlipCaller + M:IfFlipCaller.Go) is IDENTICAL under both
        // define sets — only its body's active #if branch changed. A declaration-shape-based
        // skip would wrongly leave the stale Alpha call ref persisted; the exact-row-content
        // fingerprint must catch the difference and rewrite the caller.
        WriteSemanticModeCsproj(fixture.Root, "Game", defineConstants: "REBUILD_SCOPE_TEST", bumpSeconds: 30);
        var quietPath = fixture.Combine("Assets", "Scripts", "IfFlipQuiet.cs");
        File.WriteAllText(quietPath, File.ReadAllText(quietPath));
        Bump(quietPath, 5);

        log.Clear();
        engine.RunIndex(full: false);

        Assert.Contains(log, l => l.Contains("Game build: FULL-REBUILD reason=csproj-mtime-changed"));
        Assert.Contains(log, l => l.Contains("Game extract (full-unit)"));
        // Exactly one file's rows genuinely changed: the never-edited IfFlipCaller.cs.
        Assert.Contains(log, l => l.Contains("Game db-scope (full-unit): changed=1/"));

        var callTargetsAfter = GetCallTargets(engine.DbPath, callerFileId);
        Assert.Contains(callTargetsAfter, t => t.Contains("Beta", StringComparison.Ordinal));
        Assert.DoesNotContain(callTargetsAfter, t => t.Contains("Alpha", StringComparison.Ordinal));

        // IfFlipTargets.cs declares both branches' targets — its rows are define-insensitive and
        // must have been skipped (byte-stable ids), same for the identically-rewritten Foo.cs.
        Assert.Equal(targetsSymbolsBefore, GetSymbolIds(engine.DbPath, "Assets/Scripts/IfFlipTargets.cs"));

        AssertParityWithFreshRebuild(fixture, engine);
    }

    // ---- shared helpers (same shapes as CsScopedExtractionTests) ------------------------------

    private sealed record AnalysisProjections(
        List<string> Symbols,
        List<string> SymbolRefs,
        List<string> NameHints,
        List<string> Assemblies);

    private static AnalysisProjections AssertParityWithFreshRebuild(FixtureCopy fixture, UnBrambleEngine engine)
    {
        var incremental = DumpProjections(engine.DbPath);
        engine.Dispose();

        using var freshEngine = UnBrambleEngine.Open(fixture.Root);
        freshEngine.RunIndex(full: true);
        var fresh = DumpProjections(freshEngine.DbPath);

        Assert.Equal(fresh.Symbols, incremental.Symbols);
        Assert.Equal(fresh.SymbolRefs, incremental.SymbolRefs);
        Assert.Equal(fresh.NameHints, incremental.NameHints);
        Assert.Equal(fresh.Assemblies, incremental.Assemblies);
        return incremental;
    }

    private static AnalysisProjections DumpProjections(string dbPath)
    {
        using var conn = Open(dbPath);

        var symbols = QueryRows(conn, """
            SELECT IFNULL(f.path, ''), a.name, s.kind, s.doc_id, s.name, IFNULL(s.line, -1),
                   s.is_entry_point, IFNULL(s.attrs, ''), IFNULL(s.entry_reason, '')
            FROM symbols s
            JOIN assemblies a ON a.id = s.assembly_id
            LEFT JOIN files f ON f.id = s.file_id;
            """);

        var symbolRefs = QueryRows(conn, """
            SELECT sf.path, IFNULL(ss.doc_id, ''), r.target_doc_id, r.ref_kind, r.line, r.confidence
            FROM symbol_refs r
            JOIN files sf ON sf.id = r.source_file_id
            LEFT JOIN symbols ss ON ss.id = r.source_symbol_id;
            """);

        var nameHints = QueryRows(conn, """
            SELECT f.path, nh.name, nh.kind, nh.line, IFNULL(nh.type_name, '')
            FROM name_hints nh
            JOIN files f ON f.id = nh.source_file_id;
            """);

        var assemblies = QueryRows(conn, "SELECT name, mode FROM assemblies;");

        return new AnalysisProjections(symbols, symbolRefs, nameHints, assemblies);
    }

    private static List<string> QueryRows(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var fields = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                fields[i] = Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? "";
            }

            rows.Add("|" + string.Join("|", fields) + "|");
        }

        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    private static List<long> GetSymbolIds(string dbPath, string path)
    {
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.id FROM symbols s JOIN files f ON f.id = s.file_id WHERE f.path = @path ORDER BY s.id;
            """;
        cmd.Parameters.AddWithValue("@path", path);
        using var reader = cmd.ExecuteReader();
        var result = new List<long>();
        while (reader.Read())
        {
            result.Add(reader.GetInt64(0));
        }

        return result;
    }

    private static List<long> GetSymbolRefIds(string dbPath, string path)
    {
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.id FROM symbol_refs r JOIN files f ON f.id = r.source_file_id WHERE f.path = @path ORDER BY r.id;
            """;
        cmd.Parameters.AddWithValue("@path", path);
        using var reader = cmd.ExecuteReader();
        var result = new List<long>();
        while (reader.Read())
        {
            result.Add(reader.GetInt64(0));
        }

        return result;
    }

    private static List<string> GetCallTargets(string dbPath, long sourceFileId)
    {
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT target_doc_id FROM symbol_refs WHERE source_file_id = @id AND ref_kind = 'call';
            """;
        cmd.Parameters.AddWithValue("@id", sourceFileId);
        using var reader = cmd.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static long RequireFileId(string dbPath, string path)
    {
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM files WHERE path = @path;";
        cmd.Parameters.AddWithValue("@path", path);
        var result = cmd.ExecuteScalar();
        Assert.True(result is long, $"no files row for '{path}'");
        return (long)result!;
    }

    private static SqliteConnection Open(string dbPath)
    {
        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        return conn;
    }

    // ---- mutation / fixture helpers ------------------------------------------------------------

    private static void Bump(string path, int seconds) =>
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(seconds));

    private static void AddScript(FixtureCopy fixture, string fileName, string content)
    {
        var path = fixture.Combine("Assets", "Scripts", fileName);
        File.WriteAllText(path, content);
        WriteCsMeta(path, Guid.NewGuid().ToString("N"));
    }

    private static void WriteCsMeta(string csPath, string guid) =>
        File.WriteAllText(csPath + ".meta", $$"""
            fileFormatVersion: 2
            guid: {{guid}}
            MonoImporter:
              externalObjects: {}
              serializedVersion: 2
              defaultReferences: []
              executionOrder: 0
              icon: {instanceID: 0}
              userData:
              assetBundleName:
              assetBundleVariant:

            """);

    private static void WriteSemanticModeCsprojs(string fixtureRoot)
    {
        WriteSemanticModeCsproj(fixtureRoot, "Core", defineConstants: null, bumpSeconds: 0);
        WriteSemanticModeCsproj(fixtureRoot, "Game", defineConstants: null, bumpSeconds: 0);
    }

    /// <summary>Same generated-IDE-shaped csproj injection as CsSessionModelTests (references +
    /// optional DefineConstants), forcing Mode A (semantic) for the assembly.</summary>
    private static void WriteSemanticModeCsproj(string fixtureRoot, string assemblyName, string? defineConstants, int bumpSeconds)
    {
        var ns = XNamespace.Get("http://schemas.microsoft.com/developer/msbuild/2003");
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var project = new XElement(ns + "Project", new XAttribute("ToolsVersion", "Current"));
        if (defineConstants is not null)
        {
            project.Add(new XElement(ns + "PropertyGroup", new XElement(ns + "DefineConstants", defineConstants)));
        }

        project.Add(new XElement(
            ns + "ItemGroup",
            paths.Select(p => new XElement(
                ns + "Reference",
                new XAttribute("Include", Path.GetFileNameWithoutExtension(p)),
                new XElement(ns + "HintPath", p)))));

        var csprojPath = Path.Combine(fixtureRoot, assemblyName + ".csproj");
        new XDocument(project).Save(csprojPath);
        if (bumpSeconds > 0)
        {
            Bump(csprojPath, bumpSeconds);
        }
    }
}
