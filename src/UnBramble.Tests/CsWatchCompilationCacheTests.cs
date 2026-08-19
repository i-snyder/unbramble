using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Data.Sqlite;
using UnBramble.Core;
using UnBramble.Core.CSharp;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// Parity/correctness tests for the watch-only persistent Roslyn compilation cache
/// (<see cref="CsCompilationCache"/>) that fixes `watch` rebuilding every Semantic-mode assembly
/// from scratch on every single-file save. The gold-standard pattern already used elsewhere in
/// this repo (see <see cref="CsExtractionTests"/>) is a parity test: run the SAME final edit
/// through both a from-scratch analysis and the incremental cache-aware path, and assert the two
/// produce byte-for-byte identical extracted symbols/refs -- not just "doesn't crash".
///
/// Every engine-level scenario here forces Semantic mode by writing a synthetic generated
/// `&lt;AssemblyName&gt;.csproj` into an isolated <see cref="FixtureCopy"/> (never the committed
/// fixture -- every other test in this repo relies on MiniProject staying in Syntactic mode by
/// default), with `&lt;Reference&gt;/&lt;HintPath&gt;` entries pointing at the current runtime's own
/// trusted platform assemblies -- the same "no real Unity DLLs on disk" trick
/// <see cref="CsExtractionTests"/> already uses for injected BCL references, just routed through a
/// real generated-csproj file on disk so <c>CsModeSelector</c> actually picks Semantic mode end to
/// end through <see cref="UnBrambleEngine"/>, exactly like a real `watch` process would see.
/// </summary>
public class CsWatchCompilationCacheTests
{
    private static IReadOnlyList<string> BclReferencePaths() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);

    /// <summary>Writes a minimal generated-IDE-csproj stand-in at the fixture root, exactly what
    /// <c>CsModeSelector.Determine</c> looks for: `&lt;DefineConstants&gt;` (optional) plus one
    /// `&lt;Reference&gt;/&lt;HintPath&gt;` per trusted platform assembly, so Mode A (semantic) engages
    /// with real, resolvable BCL types instead of an empty/degraded compilation.</summary>
    private static void WriteSemanticCsproj(string fixtureRoot, string assemblyName, IReadOnlyList<string>? defines = null)
    {
        var propertyGroup = new XElement("PropertyGroup");
        if (defines is { Count: > 0 })
        {
            propertyGroup.Add(new XElement("DefineConstants", string.Join(';', defines)));
        }

        var itemGroup = new XElement(
            "ItemGroup",
            BclReferencePaths().Select(path => new XElement(
                "Reference",
                new XAttribute("Include", Path.GetFileNameWithoutExtension(path)),
                new XElement("HintPath", path))));

        var doc = new XDocument(new XElement("Project", propertyGroup, itemGroup));
        doc.Save(Path.Combine(fixtureRoot, assemblyName + ".csproj"));
    }

    private static void Touch(string path, double secondsAhead) =>
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(secondsAhead));

    private readonly record struct SymbolRow(string Path, string Kind, string DocId, string Name, int? Line, bool IsEntryPoint, string? Attrs, string? EntryReason);

    private readonly record struct RefRow(string SourcePath, string? SourceSymbolDocId, string TargetDocId, string RefKind, int Line, string Confidence);

    private static List<SymbolRow> ReadSymbols(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        // Joined against `files` (never raw file_id) so surrogate-key differences between two
        // independently-built databases can't produce a false mismatch -- only path identity
        // matters for parity here, same reasoning CsIncrementalTests applies to `assemblies`.
        cmd.CommandText = """
            SELECT f.path, s.kind, s.doc_id, s.name, s.line, s.is_entry_point, s.attrs, s.entry_reason
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            ORDER BY f.path, s.doc_id, s.line;
            """;
        using var reader = cmd.ExecuteReader();
        var result = new List<SymbolRow>();
        while (reader.Read())
        {
            result.Add(new SymbolRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.GetInt32(5) != 0,
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return result;
    }

    private static List<RefRow> ReadRefs(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT sf.path, ssym.doc_id, sr.target_doc_id, sr.ref_kind, sr.line, sr.confidence
            FROM symbol_refs sr
            JOIN files sf ON sf.id = sr.source_file_id
            LEFT JOIN symbols ssym ON ssym.id = sr.source_symbol_id
            ORDER BY sf.path, sr.target_doc_id, sr.ref_kind, sr.line;
            """;
        using var reader = cmd.ExecuteReader();
        var result = new List<RefRow>();
        while (reader.Read())
        {
            result.Add(new RefRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5)));
        }

        return result;
    }

    private static Dictionary<string, string> ReadModes(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, mode FROM assemblies;";
        using var reader = cmd.ExecuteReader();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    // ---- Scenario 1: same-assembly single-file edit (add a new public method) ---------------

    [Fact]
    public void SameAssemblyEdit_AddsNewPublicMethod_MatchesFullRebuild()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticCsproj(fixture.Root, "Core");
        WriteSemanticCsproj(fixture.Root, "Game");

        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.EnableWatchCompilationCache();
        engine.RunIndex(full: false); // Cold pass: cache miss for every unit -> full rebuild.

        var modes = ReadModes(engine.DbPath);
        Assert.Equal("semantic", modes["Core"]);
        Assert.Equal("semantic", modes["Game"]);

        var coreUtilPath = fixture.Combine("Assets", "Scripts", "Core", "CoreUtil.cs");
        var edited = File.ReadAllText(coreUtilPath).Replace(
            "public static void Ping()\n    {\n    }",
            "public static void Ping()\n    {\n    }\n\n    public static void NewMethod()\n    {\n    }");
        Assert.Contains("NewMethod", edited); // guard against a silent no-op Replace if the fixture's formatting ever drifts
        File.WriteAllText(coreUtilPath, edited);
        Touch(coreUtilPath, 5);

        engine.ApplyTargetedUpdate(["Assets/Scripts/Core/CoreUtil.cs"]);

        var incrementalSymbols = ReadSymbols(engine.DbPath);
        Assert.Contains(incrementalSymbols, s => s.DocId == "M:CoreUtil.NewMethod" && s.Kind == "method");
        Assert.Contains(incrementalSymbols, s => s.DocId == "M:CoreUtil.Ping" && s.Kind == "method");

        // Baseline: an independent, never-cached engine over a SEPARATE fixture copy that starts
        // directly from the final edited state -- a true from-scratch analysis to compare against.
        using var baselineFixture = FixtureCopy.Create();
        WriteSemanticCsproj(baselineFixture.Root, "Core");
        WriteSemanticCsproj(baselineFixture.Root, "Game");
        File.WriteAllText(baselineFixture.Combine("Assets", "Scripts", "Core", "CoreUtil.cs"), edited);

        using var baselineEngine = UnBrambleEngine.Open(baselineFixture.Root);
        baselineEngine.RunIndex(full: false);

        Assert.Equal(ReadSymbols(baselineEngine.DbPath), incrementalSymbols);
        Assert.Equal(ReadRefs(baselineEngine.DbPath), ReadRefs(engine.DbPath));
    }

    /// <summary>
    /// Direct, low-level proof of "nothing else in that assembly's tree gets needlessly
    /// reprocessed" (the actual perf claim, not just a correctness one): edits ONE file via
    /// <see cref="CsCompilationCache.ApplyIncrementalEdits"/> and asserts the OTHER file's
    /// <see cref="SyntaxTree"/> object in the result is the exact same instance as before the
    /// edit -- Roslyn's structural sharing, observed directly, not inferred from timing.
    /// </summary>
    [Fact]
    public void ApplyIncrementalEdits_UntouchedFile_KeepsSameSyntaxTreeObject()
    {
        var coreUnit = new AssemblyUnit(
            "Core", 1, "Assets/Scripts/Core/Core.asmdef", [],
            [(2L, "Assets/Scripts/Core/CoreUtil.cs"), (3L, "Assets/Scripts/Core/UnityStubs.cs")], false);
        var modeDecision = new CsModeDecision(CsAnalysisMode.Semantic, [], BclReferencePaths());

        using var fixture = FixtureCopy.Create();
        string ToFullPath(string projectRelativePath) => Path.Combine(fixture.Root, projectRelativePath.Replace('/', Path.DirectorySeparatorChar));

        var cache = new CsCompilationCache();
        var built = CsProjectAnalyzer.BuildCompilationForCache(coreUnit, modeDecision, new Dictionary<string, CSharpCompilation>(), ToFullPath, cache);
        var initialUnit = new CachedUnit(built.Compilation, built.FileIdByTree, built.TreeByPath, built.DepRefs, modeDecision.CsprojMtimeTicks, CsCompilationCache.ComputeUnitShapeHash(coreUnit, modeDecision));

        var untouchedTreeBefore = initialUnit.TreeByPath["Assets/Scripts/Core/UnityStubs.cs"];
        var touchedTreeBefore = initialUnit.TreeByPath["Assets/Scripts/Core/CoreUtil.cs"];

        var coreUtilPath = fixture.Combine("Assets", "Scripts", "Core", "CoreUtil.cs");
        File.WriteAllText(coreUtilPath, File.ReadAllText(coreUtilPath) + "\n// edited\n");

        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: modeDecision.Defines);
        var edit = CsCompilationCache.ApplyIncrementalEdits(initialUnit, coreUnit, ToFullPath, parseOptions, ["Assets/Scripts/Core/CoreUtil.cs"]);

        var untouchedTreeAfter = edit.TreeByPath["Assets/Scripts/Core/UnityStubs.cs"];
        var touchedTreeAfter = edit.TreeByPath["Assets/Scripts/Core/CoreUtil.cs"];

        Assert.Same(untouchedTreeBefore, untouchedTreeAfter);
        Assert.NotSame(touchedTreeBefore, touchedTreeAfter);

        // The untouched file's tree in the resulting COMPILATION (not just the bookkeeping dict)
        // is the same object too -- proves the compilation itself carries it over by reference.
        Assert.Contains(untouchedTreeAfter, edit.Compilation.SyntaxTrees);
    }

    // ---- Scenario 2: dependency-assembly edit changing a public API a dependent calls --------

    [Fact]
    public void DependencyApiRename_PropagatesToDependentViaReplaceReference_MatchesFullRebuild()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticCsproj(fixture.Root, "Core");
        WriteSemanticCsproj(fixture.Root, "Game");

        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.EnableWatchCompilationCache();
        engine.RunIndex(full: false);

        var coreUtilPath = fixture.Combine("Assets", "Scripts", "Core", "CoreUtil.cs");
        var fooPath = fixture.Combine("Assets", "Scripts", "Foo.cs");

        var coreUtilOriginal = File.ReadAllText(coreUtilPath);
        var fooOriginal = File.ReadAllText(fooPath);
        Assert.Contains("public static void Ping()", coreUtilOriginal);
        Assert.Contains("CoreUtil.Ping();", fooOriginal);

        var coreUtilEdited = coreUtilOriginal.Replace("Ping", "Pong");
        var fooEdited = fooOriginal.Replace("CoreUtil.Ping();", "CoreUtil.Pong();");

        File.WriteAllText(coreUtilPath, coreUtilEdited);
        Touch(coreUtilPath, 5);
        File.WriteAllText(fooPath, fooEdited);
        Touch(fooPath, 6);

        engine.ApplyTargetedUpdate(["Assets/Scripts/Core/CoreUtil.cs", "Assets/Scripts/Foo.cs"]);

        var incrementalRefs = ReadRefs(engine.DbPath);

        // The real proof this is ReplaceReference propagation working, not a lucky name match:
        // Foo.Start's call resolves to the RENAMED symbol with semantic (not syntactic/fallback)
        // confidence, and the old target is gone -- a stale reference to Core's pre-rename
        // compilation would either fail to resolve Pong at all (syntactic fallback) or, if
        // silently never swapped, would leave Game's compilation referencing a Core compilation
        // that still only has Ping.
        Assert.Contains(incrementalRefs, r => r.RefKind == "call" && r.TargetDocId == "M:CoreUtil.Pong"
            && r.SourceSymbolDocId == "M:Foo.Start" && r.Confidence == "semantic");
        Assert.DoesNotContain(incrementalRefs, r => r.TargetDocId == "M:CoreUtil.Ping");

        using var baselineFixture = FixtureCopy.Create();
        WriteSemanticCsproj(baselineFixture.Root, "Core");
        WriteSemanticCsproj(baselineFixture.Root, "Game");
        File.WriteAllText(baselineFixture.Combine("Assets", "Scripts", "Core", "CoreUtil.cs"), coreUtilEdited);
        File.WriteAllText(baselineFixture.Combine("Assets", "Scripts", "Foo.cs"), fooEdited);

        using var baselineEngine = UnBrambleEngine.Open(baselineFixture.Root);
        baselineEngine.RunIndex(full: false);

        Assert.Equal(ReadSymbols(baselineEngine.DbPath), ReadSymbols(engine.DbPath));
        Assert.Equal(ReadRefs(baselineEngine.DbPath), incrementalRefs);
    }

    // ---- Scenario 3: a csproj/define change must force a full rebuild, not reuse stale state -

    [Fact]
    public void CsprojDefineChange_ForcesFullRebuild_UntouchedFilesReparsedUnderNewDefines()
    {
        using var fixture = FixtureCopy.Create();

        // CoreUtil.cs starts with a method gated behind a define that is NOT yet set -- under
        // Semantic mode with an empty define set, `ConditionalMarker` is compiled out entirely
        // (a disabled-region name_hint, not a `symbols` row), same as AndroidOnly.cs elsewhere in
        // this fixture.
        var coreUtilPath = fixture.Combine("Assets", "Scripts", "Core", "CoreUtil.cs");
        var coreUtilOriginal = File.ReadAllText(coreUtilPath);
        var coreUtilWithConditional = coreUtilOriginal.Replace(
            "public static void Ping()\n    {\n    }",
            "public static void Ping()\n    {\n    }\n\n#if UNBRAMBLE_CACHE_TEST_DEFINE\n    public static void ConditionalMarker()\n    {\n    }\n#endif");
        Assert.Contains("ConditionalMarker", coreUtilWithConditional);
        File.WriteAllText(coreUtilPath, coreUtilWithConditional);

        WriteSemanticCsproj(fixture.Root, "Core"); // no defines yet
        WriteSemanticCsproj(fixture.Root, "Game");

        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.EnableWatchCompilationCache();
        engine.RunIndex(full: false);

        var beforeSymbols = ReadSymbols(engine.DbPath);
        Assert.DoesNotContain(beforeSymbols, s => s.DocId == "M:CoreUtil.ConditionalMarker");

        // Mark Core dirty via a DIFFERENT file (UnityStubs.cs) in the same unit -- CoreUtil.cs
        // itself is deliberately left untouched, so the only thing that can make
        // ConditionalMarker visible is the FULL unit rebuild the csproj/define change forces,
        // never the narrower "just reparse the dirty file(s)" incremental path (which would
        // leave CoreUtil.cs's stale, pre-define SyntaxTree in place via structural sharing).
        var unityStubsPath = fixture.Combine("Assets", "Scripts", "Core", "UnityStubs.cs");
        File.WriteAllText(unityStubsPath, File.ReadAllText(unityStubsPath) + "\n// touched\n");
        Touch(unityStubsPath, 5);

        // Now flip the define on via a rewritten csproj -- this is the "structural shape" change
        // CsCompilationCache.ComputeUnitShapeHash exists to catch.
        WriteSemanticCsproj(fixture.Root, "Core", ["UNBRAMBLE_CACHE_TEST_DEFINE"]);
        Touch(Path.Combine(fixture.Root, "Core.csproj"), 6);

        engine.RunIndex(full: false); // pull-path sweep: picks up both the touched .cs and the csproj mtime move.

        var afterSymbols = ReadSymbols(engine.DbPath);
        Assert.Contains(afterSymbols, s => s.DocId == "M:CoreUtil.ConditionalMarker" && s.Kind == "method");
        Assert.Contains(afterSymbols, s => s.DocId == "M:CoreUtil.Ping"); // untouched-by-name symbol still present too

        using var baselineFixture = FixtureCopy.Create();
        File.WriteAllText(baselineFixture.Combine("Assets", "Scripts", "Core", "CoreUtil.cs"), coreUtilWithConditional);
        File.WriteAllText(baselineFixture.Combine("Assets", "Scripts", "Core", "UnityStubs.cs"), File.ReadAllText(unityStubsPath));
        WriteSemanticCsproj(baselineFixture.Root, "Core", ["UNBRAMBLE_CACHE_TEST_DEFINE"]);
        WriteSemanticCsproj(baselineFixture.Root, "Game");

        using var baselineEngine = UnBrambleEngine.Open(baselineFixture.Root);
        baselineEngine.RunIndex(full: false);

        Assert.Equal(ReadSymbols(baselineEngine.DbPath), afterSymbols);
        Assert.Equal(ReadRefs(baselineEngine.DbPath), ReadRefs(engine.DbPath));
    }

    // ---- Cache-disabled (default) path is provably unaffected --------------------------------

    [Fact]
    public void WatchCacheDisabled_SemanticAnalysis_StillWorksExactlyAsWithoutTheFeature()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticCsproj(fixture.Root, "Core");
        WriteSemanticCsproj(fixture.Root, "Game");

        // No EnableWatchCompilationCache() call -- every one-shot CLI path (init/index/etc.)
        // never calls it either, so this exercises the exact same code every existing test does.
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var modes = ReadModes(engine.DbPath);
        Assert.Equal("semantic", modes["Core"]);
        Assert.Contains(ReadRefs(engine.DbPath), r => r.RefKind == "call" && r.TargetDocId == "M:CoreUtil.Ping" && r.Confidence == "semantic");
    }
}
