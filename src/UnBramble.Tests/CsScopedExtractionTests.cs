using System.Collections.Concurrent;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using UnBramble.Core;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// Per-file extraction/DB-write scoping (<see cref="UnBramble.Core.UnBrambleEngine"/>'s private
/// <c>TryExtractScoped</c>) lets a body-only edit to one file in a multi-file Semantic-mode unit
/// skip re-extracting AND re-writing every OTHER file's rows in that unit — the fix for a live
/// finding that a dirty unit's DB write cost (delete-then-reinsert of every file's symbols/refs,
/// not just the edited one) dominated a watch push even with the compilation cache already warm.
///
/// Every scenario here asserts BOTH (a) parity against a from-scratch <c>RunIndex(full: true)</c>
/// rebuild of the identical final tree (never-wrong), and (b), via the <c>[cs-cache]</c>
/// diagnostics sink threaded through <see cref="UnBrambleEngine.EnableWatchCompilationCache"/>,
/// that the SPECIFIC code path under test (scoped / escalated-then-full-unit / full-unit-for-a-
/// untouched-dependent) actually fired — a test that only checked parity could pass vacuously
/// via a silent fallback to the safe-but-untested full-unit path.
///
/// All scenarios force Semantic mode for both "Core" and "Game" (see <see
/// cref="WriteSemanticModeCsproj"/>, the same generated-IDE-shaped-csproj injection
/// ReferenceCaptureTests/CsSessionModelTests use) — the scoped path is unreachable in Syntactic mode
/// (never even attempted; <c>scopedExtractionEligible</c> is hard-false whenever
/// <c>_watchCompilationCache</c> is null, and separately whenever a unit isn't Semantic).
/// </summary>
public class CsScopedExtractionTests
{
    // ---- (a) BODY-ONLY EDIT: the target case ------------------------------------------------

    [Fact]
    public void BodyOnlyEdit_UsesScopedExtraction_OtherFilesRowsUntouched()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        AddScript(fixture, "ScopedA.cs", """
            public class ScopedA
            {
                public void DoWork()
                {
                    int x = 1;
                }
            }
            """);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        var log = new ConcurrentQueue<string>();
        engine.EnableWatchCompilationCache(log.Enqueue);
        engine.RunIndex(full: false);

        // Foo.cs is untouched by this test throughout -- its rows are the "did scoping actually
        // leave everything else alone" control. Captured AFTER pass 1 (baseline row ids), not
        // before, since pass 1 is always a full-unit extraction (cache miss) regardless.
        var fooSymbolsBefore = GetSymbolIds(engine.DbPath, "Assets/Scripts/Foo.cs");
        var fooRefsBefore = GetSymbolRefIds(engine.DbPath, "Assets/Scripts/Foo.cs");
        Assert.NotEmpty(fooSymbolsBefore);
        Assert.NotEmpty(fooRefsBefore);

        File.WriteAllText(fixture.Combine("Assets", "Scripts", "ScopedA.cs"), """
            public class ScopedA
            {
                public void DoWork()
                {
                    int x = 1;
                    int y = x + 1;
                }
            }
            """);
        Bump(fixture.Combine("Assets", "Scripts", "ScopedA.cs"), 5);

        log.Clear();
        engine.RunIndex(full: false);

        // The intended code path actually fired: a successful scoped extraction line, not an
        // escalation, and no full-unit fallback for Game this pass.
        Assert.Contains(log, l => l.Contains("Game extract (scoped): files="));
        Assert.DoesNotContain(log, l => l.Contains("Game extract (scoped): ESCALATED"));
        Assert.DoesNotContain(log, l => l.Contains("Game extract (full-unit)"));

        // Foo.cs's rows were never deleted-then-reinserted: same primary keys, not just
        // same-looking content after a silent full rewrite.
        Assert.Equal(fooSymbolsBefore, GetSymbolIds(engine.DbPath, "Assets/Scripts/Foo.cs"));
        Assert.Equal(fooRefsBefore, GetSymbolRefIds(engine.DbPath, "Assets/Scripts/Foo.cs"));

        AssertParityWithFreshRebuild(fixture, engine);
    }

    // ---- (b) SIGNATURE CHANGE: must escalate -------------------------------------------------

    [Fact]
    public void SignatureChange_Escalates_ToFullUnitExtraction()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        AddScript(fixture, "ScopedSigA.cs", """
            public class ScopedSigA
            {
                public void DoWork()
                {
                }
            }
            """);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        var log = new ConcurrentQueue<string>();
        engine.EnableWatchCompilationCache(log.Enqueue);
        engine.RunIndex(full: false);

        // Add a new public method -- a real member-shape change: a new declared doc_id.
        File.WriteAllText(fixture.Combine("Assets", "Scripts", "ScopedSigA.cs"), """
            public class ScopedSigA
            {
                public void DoWork()
                {
                }

                public void NewPublicMethod()
                {
                }
            }
            """);
        Bump(fixture.Combine("Assets", "Scripts", "ScopedSigA.cs"), 5);

        log.Clear();
        engine.RunIndex(full: false);

        Assert.Contains(log, l => l.Contains("Game extract (scoped): ESCALATED (declaration shape changed"));
        Assert.Contains(log, l => l.Contains("Game extract (full-unit)"));

        var incremental = AssertParityWithFreshRebuild(fixture, engine);
        Assert.Contains(incremental.Symbols, s => s.Contains("|M:ScopedSigA.NewPublicMethod|"));
    }

    // ---- (c) BASE-TYPE CHANGE: must escalate via the inherit-ref component ------------------

    [Fact]
    public void BaseTypeChange_EscalatesViaInheritRef_OtherFileSeesNewBaseMembers()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        AddScript(fixture, "ScopedBaseA.cs", """
            public class ScopedBaseA
            {
                public void Shared() { }
                public void OnlyInA() { }
            }
            """);
        AddScript(fixture, "ScopedBaseB.cs", """
            public class ScopedBaseB
            {
                public void Shared() { }
                public void OnlyInB() { }
            }
            """);
        AddScript(fixture, "ScopedDerived.cs", """
            public class ScopedDerived : ScopedBaseA
            {
            }
            """);
        AddScript(fixture, "ScopedCaller.cs", """
            public class ScopedCaller
            {
                public void Use()
                {
                    var d = new ScopedDerived();
                    d.Shared();
                }
            }
            """);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        var log = new ConcurrentQueue<string>();
        engine.EnableWatchCompilationCache(log.Enqueue);
        engine.RunIndex(full: false);

        var callerFileId = RequireFileId(engine.DbPath, "Assets/Scripts/ScopedCaller.cs");
        Assert.Contains(
            GetCallTargets(engine.DbPath, callerFileId),
            t => t.Contains("ScopedBaseA", StringComparison.Ordinal));

        // ScopedDerived's OWN declared doc_id (T:ScopedDerived) does not change -- only its
        // emitted "inherit" ref target does (ScopedBaseA -> ScopedBaseB). This is precisely why
        // TryExtractScoped/GetDeclarationShapes fold inherit/override refs into the shape token
        // set alongside declared symbols: a base-type swap alone wouldn't show up as a declared-
        // symbol difference.
        File.WriteAllText(fixture.Combine("Assets", "Scripts", "ScopedDerived.cs"), """
            public class ScopedDerived : ScopedBaseB
            {
            }
            """);
        Bump(fixture.Combine("Assets", "Scripts", "ScopedDerived.cs"), 5);

        log.Clear();
        engine.RunIndex(full: false);

        Assert.Contains(log, l => l.Contains("Game extract (scoped): ESCALATED (declaration shape changed"));
        Assert.Contains(log, l => l.Contains("Game extract (full-unit)"));

        AssertParityWithFreshRebuild(fixture, engine);

        // The unedited ScopedCaller.cs's call now resolves through the NEW base -- proves the
        // escalated full-unit re-extraction actually re-derived cross-file resolution, not just
        // ScopedDerived's own rows.
        var callTargetsAfter = GetCallTargets(engine.DbPath, callerFileId);
        Assert.Contains(callTargetsAfter, t => t.Contains("ScopedBaseB", StringComparison.Ordinal));
        Assert.DoesNotContain(callTargetsAfter, t => t.Contains("ScopedBaseA", StringComparison.Ordinal));
    }

    // ---- (d) CROSS-FILE RIPPLE CORRECTNESS ---------------------------------------------------

    [Fact]
    public void CrossFileRipple_ScopedEditToCaller_SeesRealSemanticResolution_HostFileUntouched()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        AddScript(fixture, "ScopedOverloadHost.cs", """
            public class ScopedOverloadHost
            {
                public static void Handle(int x) { }
                public static void Handle(string s) { }
            }
            """);
        AddScript(fixture, "ScopedOverloadCaller.cs", """
            public class ScopedOverloadCaller
            {
                public void Call()
                {
                    ScopedOverloadHost.Handle(1);
                }
            }
            """);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        var log = new ConcurrentQueue<string>();
        engine.EnableWatchCompilationCache(log.Enqueue);
        engine.RunIndex(full: false);

        var hostSymbolsBefore = GetSymbolIds(engine.DbPath, "Assets/Scripts/ScopedOverloadHost.cs");
        var hostRefsBefore = GetSymbolRefIds(engine.DbPath, "Assets/Scripts/ScopedOverloadHost.cs");

        var callerFileId = RequireFileId(engine.DbPath, "Assets/Scripts/ScopedOverloadCaller.cs");
        Assert.Contains(GetCallTargets(engine.DbPath, callerFileId), t => t.Contains("Int32", StringComparison.Ordinal));

        // Body-only edit to the CALLER: same signature, but switches which overload it invokes.
        File.WriteAllText(fixture.Combine("Assets", "Scripts", "ScopedOverloadCaller.cs"), """
            public class ScopedOverloadCaller
            {
                public void Call()
                {
                    ScopedOverloadHost.Handle("x");
                }
            }
            """);
        Bump(fixture.Combine("Assets", "Scripts", "ScopedOverloadCaller.cs"), 5);

        log.Clear();
        engine.RunIndex(full: false);

        Assert.Contains(log, l => l.Contains("Game extract (scoped): files="));
        Assert.DoesNotContain(log, l => l.Contains("Game extract (scoped): ESCALATED"));
        Assert.DoesNotContain(log, l => l.Contains("Game extract (full-unit)"));

        // Proves scoping only narrowed the DB WRITE, not the semantic model the edited file was
        // extracted against: the caller's own new ref correctly resolves the OTHER overload,
        // which requires real symbol resolution against the whole unit's compilation.
        var callTargetsAfter = GetCallTargets(engine.DbPath, callerFileId);
        Assert.Contains(callTargetsAfter, t => t.Contains("String", StringComparison.Ordinal));
        Assert.DoesNotContain(callTargetsAfter, t => t.Contains("Int32", StringComparison.Ordinal));

        // The host file (declares both overloads, never edited) is untouched at the row level.
        Assert.Equal(hostSymbolsBefore, GetSymbolIds(engine.DbPath, "Assets/Scripts/ScopedOverloadHost.cs"));
        Assert.Equal(hostRefsBefore, GetSymbolRefIds(engine.DbPath, "Assets/Scripts/ScopedOverloadHost.cs"));

        AssertParityWithFreshRebuild(fixture, engine);
    }

    // ---- (e) MULTI-FILE EDIT IN ONE PASS -----------------------------------------------------

    [Fact]
    public void MultiFileEditInOnePass_ScopedCoversBothFiles_ThirdFileStable()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        AddScript(fixture, "ScopedMultiA.cs", """
            public class ScopedMultiA
            {
                public void DoWork()
                {
                    int x = 1;
                }
            }
            """);
        AddScript(fixture, "ScopedMultiB.cs", """
            public class ScopedMultiB
            {
                public void DoWork()
                {
                    int x = 1;
                }
            }
            """);
        AddScript(fixture, "ScopedMultiC.cs", """
            public class ScopedMultiC
            {
                public void Noop()
                {
                }
            }
            """);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        var log = new ConcurrentQueue<string>();
        engine.EnableWatchCompilationCache(log.Enqueue);
        engine.RunIndex(full: false);

        var cSymbolsBefore = GetSymbolIds(engine.DbPath, "Assets/Scripts/ScopedMultiC.cs");
        var cRefsBefore = GetSymbolRefIds(engine.DbPath, "Assets/Scripts/ScopedMultiC.cs");

        File.WriteAllText(fixture.Combine("Assets", "Scripts", "ScopedMultiA.cs"), """
            public class ScopedMultiA
            {
                public void DoWork()
                {
                    int x = 1;
                    int y = x + 2;
                }
            }
            """);
        File.WriteAllText(fixture.Combine("Assets", "Scripts", "ScopedMultiB.cs"), """
            public class ScopedMultiB
            {
                public void DoWork()
                {
                    int x = 1;
                    int y = x + 3;
                }
            }
            """);
        Bump(fixture.Combine("Assets", "Scripts", "ScopedMultiA.cs"), 5);
        Bump(fixture.Combine("Assets", "Scripts", "ScopedMultiB.cs"), 5);

        log.Clear();
        engine.RunIndex(full: false);

        Assert.Contains(log, l => l.Contains("Game extract (scoped): files=2/"));
        Assert.DoesNotContain(log, l => l.Contains("Game extract (scoped): ESCALATED"));
        Assert.DoesNotContain(log, l => l.Contains("Game extract (full-unit)"));

        Assert.Equal(cSymbolsBefore, GetSymbolIds(engine.DbPath, "Assets/Scripts/ScopedMultiC.cs"));
        Assert.Equal(cRefsBefore, GetSymbolRefIds(engine.DbPath, "Assets/Scripts/ScopedMultiC.cs"));

        AssertParityWithFreshRebuild(fixture, engine);
    }

    // ---- (f) DEPENDENCY-SWAP CASE: must NOT attempt scoping for the dependent ----------------

    [Fact]
    public void DependencySwap_DependentDoesNotScope_UsesFullUnitExtraction()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        var log = new ConcurrentQueue<string>();
        engine.EnableWatchCompilationCache(log.Enqueue);
        engine.RunIndex(full: false);

        // Body-only edit to the DEPENDED-ON unit (Core) -- no signature change, so Core itself
        // remains scoped-eligible. Game references Core (the committed fixture's Game.asmdef),
        // so Game becomes dirty too, but only via ApplyDependencyReferenceSwaps -- none of
        // Game's OWN files changed.
        var coreUtil = fixture.Combine("Assets", "Scripts", "Core", "CoreUtil.cs");
        File.WriteAllText(coreUtil, """
            public static class CoreUtil
            {
                public static void Ping()
                {
                    int z = 1;
                }
            }
            """);
        Bump(coreUtil, 5);

        log.Clear();
        engine.RunIndex(full: false);

        // Core: scoped extraction fired for its own body-only edit.
        Assert.Contains(log, l => l.Contains("Core extract (scoped): files="));

        // Game: reprocessed (dirty via the dependency swap) but via full-unit extraction, never
        // attempting the scoped path -- HasDependencySwaps must gate it off even though Game's
        // own compilation was cheaply derived (not a full rebuild) this pass.
        Assert.DoesNotContain(log, l => l.Contains("Game extract (scoped)"));
        Assert.Contains(log, l => l.Contains("Game extract (full-unit)"));
        Assert.Contains(log, l => l.Contains("Game build:") && l.Contains("dep-swaps=[Core]"));

        AssertParityWithFreshRebuild(fixture, engine);
    }

    // ---- shared helpers -----------------------------------------------------------------------

    private sealed record AnalysisProjections(
        List<string> Symbols,
        List<string> SymbolRefs,
        List<string> NameHints,
        List<string> Assemblies);

    /// <summary>
    /// Same discipline as CsSessionModelTests.AssertParityWithFreshRebuild: dumps the
    /// incrementally-built (scoped-extraction-using) engine's analysis projections, then rebuilds
    /// the SAME final tree from scratch and asserts the two are identical. Disposes
    /// <paramref name="engine"/> (the fresh engine needs the DB file's write lock).
    /// </summary>
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

    /// <summary>Writes a new .cs file plus its standard MonoImporter .meta into Assets/Scripts
    /// (the Game asmdef's directory) -- new files get a fresh random guid each call.</summary>
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

    /// <summary>
    /// Same throwaway generated-IDE-shaped csproj injection ReferenceCaptureTests/CsSessionModelTests
    /// use to force Mode A (semantic) for both assemblies -- written into THIS test's isolated
    /// fixture copy only. The scoped-extraction path (TryExtractScoped) is unreachable outside
    /// Semantic mode.
    /// </summary>
    private static void WriteSemanticModeCsprojs(string fixtureRoot)
    {
        WriteSemanticModeCsproj(fixtureRoot, "Core");
        WriteSemanticModeCsproj(fixtureRoot, "Game");
    }

    private static void WriteSemanticModeCsproj(string fixtureRoot, string assemblyName)
    {
        var ns = XNamespace.Get("http://schemas.microsoft.com/developer/msbuild/2003");
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var itemGroup = new XElement(
            ns + "ItemGroup",
            paths.Select(p => new XElement(
                ns + "Reference",
                new XAttribute("Include", Path.GetFileNameWithoutExtension(p)),
                new XElement(ns + "HintPath", p))));
        var project = new XElement(ns + "Project", new XAttribute("ToolsVersion", "Current"), itemGroup);
        new XDocument(project).Save(Path.Combine(fixtureRoot, assemblyName + ".csproj"));
    }
}
