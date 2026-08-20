using System.Collections.Concurrent;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using UnBramble.Core;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// Parity tests for <see cref="UnBramble.Core.CSharp.CsSessionModelCache"/> (the WatchSession
/// restructure part 1): a single long-lived engine driven through multiple incremental passes
/// must end with EXACTLY the same analysis rows as a from-scratch rebuild over the same final
/// file tree — "never wrong, only sometimes slower". Row ids and file ids can legitimately
/// differ between the two runs, so every comparison is a path-keyed projection (see
/// <see cref="DumpProjections"/>), never raw ids. Each test covers one invalidation edge of the
/// session cache: steady-state reuse, add, delete, asmdef content edit, guid-change rebuild,
/// csproj (mode-decision) rewrite, and a foreign-connection write (PRAGMA data_version).
/// </summary>
public class CsSessionModelTests
{
    private const string CoreGuid = "66666666666666666666666666666612";

    // ---- (a) steady state: pure content edit, discovery cache actually REUSED --------------

    [Fact]
    public void SteadyStateEdit_DiscoveryCacheReused_ParityWithFreshRebuild()
    {
        using var fixture = FixtureCopy.Create();
        var engine = UnBrambleEngine.Open(fixture.Root);

        // Diagnostics sink (thread-safe: units within a level run on Parallel.ForEach workers)
        // so the test can PROVE the cached path was exercised, not just that parity held --
        // otherwise a bug that silently never reuses would pass every parity assertion.
        var log = new ConcurrentQueue<string>();
        engine.EnableWatchCompilationCache(log.Enqueue);

        engine.RunIndex(full: false);
        Assert.Contains(log, l => l.Contains("discovery: rebuilt"));

        var coreUtil = fixture.Combine("Assets", "Scripts", "Core", "CoreUtil.cs");
        File.AppendAllText(coreUtil, "\n// touched\npublic static class CoreUtilExtra { public static void Pong() { } }\n");
        Bump(coreUtil, 5);

        log.Clear();
        engine.RunIndex(full: false);
        Assert.Contains(log, l => l.Contains("session-cache REUSED"));

        // Reuse of the cached unit list must still fully reflect the edit itself.
        var incremental = AssertParityWithFreshRebuild(fixture, engine);
        Assert.Contains(incremental.Symbols, s => s.Contains("|T:CoreUtilExtra|"));
        Assert.Contains(incremental.Symbols, s => s.Contains("|M:CoreUtilExtra.Pong|"));
    }

    // ---- (b) add a brand-new script, then edit it (reuse AFTER an invalidation) -------------

    [Fact]
    public void AddNewScript_ThenEditIt_ParityWithFreshRebuild()
    {
        using var fixture = FixtureCopy.Create();
        var engine = UnBrambleEngine.Open(fixture.Root);
        var log = new ConcurrentQueue<string>();
        engine.EnableWatchCompilationCache(log.Enqueue);
        engine.RunIndex(full: false);

        // Pass 2: a brand-new .cs (with the fixture's standard MonoImporter .meta pairing)
        // appears in the Game assembly dir -- Added != 0 must invalidate the discovery cache.
        var newScript = fixture.Combine("Assets", "Scripts", "NewSquire.cs");
        File.WriteAllText(newScript, "public class NewSquire\n{\n    public void Greet()\n    {\n    }\n}\n");
        WriteCsMeta(newScript, "beefbeefbeefbeefbeefbeefbeef0001");
        log.Clear();
        engine.RunIndex(full: false);
        Assert.Contains(log, l => l.Contains("discovery: rebuilt"));

        // Pass 3: edit the file added in pass 2 -- exercises reuse of the REBUILT cache (the
        // new file's id must be present in the cached unit list, or its rows go stale).
        File.AppendAllText(newScript, "\npublic class NewSquireTail { public void Wave() { } }\n");
        Bump(newScript, 10);
        log.Clear();
        engine.RunIndex(full: false);
        Assert.Contains(log, l => l.Contains("session-cache REUSED"));

        var incremental = AssertParityWithFreshRebuild(fixture, engine);
        Assert.Contains(incremental.Symbols, s => s.Contains("|T:NewSquire|"));
        Assert.Contains(incremental.Symbols, s => s.Contains("|T:NewSquireTail|"));
    }

    // ---- (c) delete a script between passes --------------------------------------------------

    [Fact]
    public void DeleteScript_ParityWithFreshRebuild_SymbolsGone()
    {
        using var fixture = FixtureCopy.Create();
        var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        File.Delete(fixture.Combine("Assets", "Scripts", "Helper.cs"));
        File.Delete(fixture.Combine("Assets", "Scripts", "Helper.cs.meta"));
        engine.RunIndex(full: false);

        var incremental = AssertParityWithFreshRebuild(fixture, engine);
        Assert.DoesNotContain(incremental.Symbols, s => s.Contains("|M:Helper.Handle|"));
        Assert.DoesNotContain(incremental.Symbols, s => s.Contains("Assets/Scripts/Helper.cs|"));
    }

    // ---- (d) asmdef content edit: remove then re-add Game's reference to Core ---------------

    [Fact]
    public void AsmdefReferenceRemovedAndReadded_InvalidatesDiscoveryCache_ParityWithFreshRebuild()
    {
        using var fixture = FixtureCopy.Create();
        var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var asmdefPath = fixture.Combine("Assets", "Scripts", "Game.asmdef");
        var originalAsmdef = File.ReadAllText(asmdefPath);

        // Pass 2: rewrite Game.asmdef WITHOUT its reference to Core (leaves a valid empty-ish
        // references array). A dirty .asmdef path must invalidate the discovery cache.
        File.WriteAllText(asmdefPath, originalAsmdef.Replace($"\"GUID:{CoreGuid}\"", "", StringComparison.Ordinal));
        Bump(asmdefPath, 5);
        engine.RunIndex(full: false);
        var afterReferenceRemoved = ReadAnalyzedUtc(engine.DbPath);

        // Pass 3: pure Core content edit. With the reference gone, correctly-invalidated
        // discovery means Game is NOT a transitive dependent anymore -- a stale cached unit
        // graph (still holding the Game -> Core edge) would re-analyze Game here.
        var coreUtil = fixture.Combine("Assets", "Scripts", "Core", "CoreUtil.cs");
        File.AppendAllText(coreUtil, "\n// touched once\n");
        Bump(coreUtil, 10);
        engine.RunIndex(full: false);
        var afterCoreEditNoRef = ReadAnalyzedUtc(engine.DbPath);
        Assert.NotEqual(afterReferenceRemoved["Core"], afterCoreEditNoRef["Core"]);
        Assert.Equal(afterReferenceRemoved["Game"], afterCoreEditNoRef["Game"]);

        // Pass 4: restore the reference (same semantics as the committed fixture, plus a
        // trailing newline so content provably changed).
        File.WriteAllText(asmdefPath, originalAsmdef + "\n");
        Bump(asmdefPath, 15);
        engine.RunIndex(full: false);
        var afterReferenceRestored = ReadAnalyzedUtc(engine.DbPath);

        // Pass 5: another pure Core edit -- now Game MUST be re-analyzed again (a unit graph
        // stale from pass 3 would still be missing the restored Game -> Core edge).
        File.AppendAllText(coreUtil, "\n// touched twice\n");
        Bump(coreUtil, 20);
        engine.RunIndex(full: false);
        var afterCoreEditWithRef = ReadAnalyzedUtc(engine.DbPath);
        Assert.NotEqual(afterReferenceRestored["Core"], afterCoreEditWithRef["Core"]);
        Assert.NotEqual(afterReferenceRestored["Game"], afterCoreEditWithRef["Game"]);

        AssertParityWithFreshRebuild(fixture, engine);
    }

    // ---- (e) guid-change rebuild: cached file ids must be invalidated ------------------------

    [Fact]
    public void GuidRebuild_InvalidatesCachedFileIds_ParityWithFreshRebuild()
    {
        using var fixture = FixtureCopy.Create();
        var engine = UnBrambleEngine.Open(fixture.Root);
        var log = new ConcurrentQueue<string>();
        engine.EnableWatchCompilationCache(log.Enqueue);
        engine.RunIndex(full: false);

        // Pass 2: Foo.cs keeps its path and content but its .meta guid changes -- the sweep
        // deletes and re-inserts the files row under a NEW file id (Added == Removed == 0).
        // Without SweepDiff.AnyGuidRebuild invalidation the cached AssemblyUnit would still
        // carry the OLD file id and the re-extraction would write orphaned/FK-violating rows.
        var fooPath = fixture.Combine("Assets", "Scripts", "Foo.cs");
        var metaPath = fooPath + ".meta";
        File.WriteAllText(metaPath, File.ReadAllText(metaPath)
            .Replace("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa01", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa99", StringComparison.Ordinal));
        Bump(metaPath, 5);
        Bump(fooPath, 5);
        log.Clear();
        engine.RunIndex(full: false);
        Assert.Contains(log, l => l.Contains("discovery: rebuilt"));

        // The rebuild really happened: the row now carries the new guid.
        Assert.Contains(engine.GetAllFiles(), f =>
            string.Equals(f.Path, "Assets/Scripts/Foo.cs", StringComparison.OrdinalIgnoreCase)
            && string.Equals(f.Guid, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa99", StringComparison.OrdinalIgnoreCase));

        // Pass 3: now edit the rebuilt file's CONTENT -- steady-state reuse of the
        // post-rebuild cache, which must hold the NEW file id.
        File.AppendAllText(fooPath, "\npublic class FooTail { public void Wave() { } }\n");
        Bump(fooPath, 10);
        log.Clear();
        engine.RunIndex(full: false);
        Assert.Contains(log, l => l.Contains("session-cache REUSED"));

        var incremental = AssertParityWithFreshRebuild(fixture, engine);
        Assert.Contains(incremental.Symbols, s => s.Contains("|T:FooTail|"));
    }

    // ---- (f) mode-decision cache: csproj rewrite with new DefineConstants --------------------

    [Fact]
    public void CsprojRewriteWithNewDefines_ModeDecisionCacheInvalidated_ParityWithFreshRebuild()
    {
        using var fixture = FixtureCopy.Create();

        // Same throwaway generated-IDE-shaped csproj injection ReferenceCaptureTests uses to force
        // Mode A (semantic) for both assemblies -- written into THIS test's isolated copy only.
        WriteSemanticModeCsproj(fixture.Root, "Core", defineConstants: null, bumpSeconds: 0);
        WriteSemanticModeCsproj(fixture.Root, "Game", defineConstants: null, bumpSeconds: 0);

        var engine = UnBrambleEngine.Open(fixture.Root);
        var summary = engine.RunIndex(full: false);
        Assert.Equal(0, summary.Stats.Cs.SyntacticAssemblies);

        // Baseline: under the empty define set, Foo.cs's #if UNITY_ANDROID block is disabled
        // text captured only as cs-disabled name hints.
        var baseline = DumpProjections(engine.DbPath);
        Assert.Contains(baseline.NameHints, h => h.Contains("|AndroidOnly|cs-disabled|"));

        // Pass 2: Unity regenerates Game.csproj with different DefineConstants (bumped mtime),
        // and a Game script is touched so the unit is actually re-extracted. The mtime-gated
        // mode-decision cache must re-parse the csproj and pick up UNITY_ANDROID -- a stale
        // decision would keep extracting with the old (empty) define set.
        WriteSemanticModeCsproj(fixture.Root, "Game", defineConstants: "UNITY_ANDROID", bumpSeconds: 10);
        var fooPath = fixture.Combine("Assets", "Scripts", "Foo.cs");
        File.AppendAllText(fooPath, "\n// touched\n");
        Bump(fooPath, 10);
        engine.RunIndex(full: false);

        var incremental = AssertParityWithFreshRebuild(fixture, engine);

        // The formerly-disabled block is now real syntax: its cs-disabled hints are gone and a
        // real resolved reference to AndroidOnly exists.
        Assert.DoesNotContain(incremental.NameHints, h => h.Contains("|AndroidOnly|cs-disabled|"));
        Assert.Contains(incremental.SymbolRefs, r => r.Contains("|T:AndroidOnly|"));
    }

    // ---- (g) cross-connection write: PRAGMA data_version invalidation ------------------------

    [Fact]
    public void ForeignConnectionWrite_DataVersionInvalidation_ParityWithFreshRebuild()
    {
        using var fixture = FixtureCopy.Create();
        var engine = UnBrambleEngine.Open(fixture.Root);
        var log = new ConcurrentQueue<string>();
        engine.EnableWatchCompilationCache(log.Enqueue);
        engine.RunIndex(full: false);

        // A SEPARATE connection commits a write behind the engine's back (what a concurrent
        // `unbramble index` from another process looks like) -- the engine's own diff never sees
        // it, so only the data_version check can force re-discovery instead of trusting the
        // cached file ids.
        using (var foreign = new SqliteConnection($"Data Source={engine.DbPath}"))
        {
            foreign.Open();
            using var cmd = foreign.CreateCommand();
            cmd.CommandText = "UPDATE files SET mtime = mtime - 1 WHERE path = 'Assets/Scripts/Core/CoreUtil.cs';";
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }

        var coreUtil = fixture.Combine("Assets", "Scripts", "Core", "CoreUtil.cs");
        File.AppendAllText(coreUtil, "\n// touched after foreign write\n");
        Bump(coreUtil, 5);
        log.Clear();
        engine.RunIndex(full: false);

        // The content edit alone would have allowed reuse (added/removed/rebuild all zero, no
        // asmdef dirty) -- ONLY the moved data_version can force this rebuild.
        Assert.Contains(log, l => l.Contains("discovery: rebuilt"));
        Assert.DoesNotContain(log, l => l.Contains("session-cache REUSED"));

        AssertParityWithFreshRebuild(fixture, engine);
    }

    // ---- shared parity helper -----------------------------------------------------------------

    private sealed record AnalysisProjections(
        List<string> Symbols,
        List<string> SymbolRefs,
        List<string> NameHints,
        List<string> Assemblies);

    /// <summary>
    /// Dumps the incrementally-built engine's analysis projections, then rebuilds the SAME
    /// final tree from scratch (fresh engine, <c>RunIndex(full: true)</c> -- drops all data
    /// tables first) and asserts the two are identical. Disposes <paramref name="engine"/>
    /// (the fresh engine needs the DB file's write lock). Returns the incremental projections
    /// so callers can make additional content assertions.
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

    /// <summary>
    /// Path-keyed, id-free projections of the four C#-analysis-owned tables: autoincrement row
    /// ids and file ids legitimately differ between an incrementally-evolved DB and a
    /// from-scratch rebuild, so every id is joined away to a path/doc_id before comparison.
    /// Each row is one '|'-joined string; each list is Ordinal-sorted.
    /// </summary>
    private static AnalysisProjections DumpProjections(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

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

    // ---- mutation helpers ----------------------------------------------------------------------

    private static void Bump(string path, int seconds) =>
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(seconds));

    /// <summary>Standard MonoImporter .meta the fixture pairs with every Assets .cs file.</summary>
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

    private static Dictionary<string, string> ReadAnalyzedUtc(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, analyzed_utc FROM assemblies;";
        using var reader = cmd.ExecuteReader();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);
        }

        return result;
    }

    /// <summary>
    /// Same generated-IDE-shaped csproj injection as ReferenceCaptureTests.WriteSemanticModeCsproj
    /// (every trusted platform assembly as a Reference/HintPath), plus an optional
    /// DefineConstants PropertyGroup so scenario (f) can rewrite it with a different define set.
    /// </summary>
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
        Bump(csprojPath, bumpSeconds);
    }
}
