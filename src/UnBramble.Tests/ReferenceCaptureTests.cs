using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using UnBramble.Core;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// Asserts that scanning captures the right rows in refs / name_hints / symbol_refs / symbols —
/// capture only, no liveness/dead-candidates assertions (see DeadCandidatesTests for those) and no
/// expected-events.json linking assertions (see UnityEventLinkingTests for those). Covers:
/// refs.target_type_name capture for a guid-carrying UnityEvent call, .anim/.prefab name-hint
/// capture (with and without a guid), and several C# semantic-mode captures that need real
/// symbol resolution: generic type arguments, method-group/delegate-conversion references,
/// disabled-region (#if) trivia, and declaration-site type references.
///
/// The committed MiniProject fixture deliberately has no generated IDE csproj, so it lands in
/// syntactic mode by default (see the CLI tests that assert this). <see
/// cref="WriteSemanticModeCsprojs"/> writes throwaway generated-shaped csprojs into THIS test's
/// own isolated <see cref="FixtureCopy"/> only, never touching the committed fixture or any
/// other test's copy, to force Mode A (semantic) for both Game and Core (Game needs Core's real
/// compilation to resolve the UnityEngine stub types at all).
/// </summary>
public class ReferenceCaptureTests
{
    // ---- refs.target_type_name capture (guid-carrying UnityEvent call) --------------------

    [Fact]
    public void F8_GuidCarryingUnityEventCall_CapturesTargetTypeName()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        using var conn = Open(engine.DbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.target_type_name
            FROM refs r
            JOIN files sf ON sf.id = r.source_file_id
            WHERE sf.path = 'Assets/Scenes/Level.unity' AND r.method_name = 'Jump';
            """;
        var value = cmd.ExecuteScalar() as string;
        Assert.Equal("Foo, Game", value);
    }

    // ---- .anim m_FunctionName -> name_hints(kind='anim-event') ----------------------------

    [Fact]
    public void F11_AnimationEvent_CapturesNameHint()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var hints = LoadNameHints(engine.DbPath, "Assets/Anims/Hit.anim");
        var hint = Assert.Single(hints, h => h.Kind == "anim-event");
        Assert.Equal("OnHitFrame", hint.Name);
        Assert.Null(hint.TypeName);

        // The distractor "functionName: unused" line (lowercase, no m_ prefix) must never be
        // captured -- only the literal m_FunctionName field counts.
        Assert.DoesNotContain(hints, h => h.Name == "unused");
    }

    [Fact]
    public void F11_AnimationClip_IsReachableFromLevelUnity()
    {
        // Not a liveness assertion (see DeadCandidatesTests for that) -- just confirms the fixture
        // wiring itself ("clip referenced from Level.unity") is a real guid edge, the same
        // discipline EdgeExtractionTests already applies to every other ledger row.
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveQueryTarget("Assets/Anims/Hit.anim");
        var answer = engine.WhoUses(resolution.Target!, transitive: false, depthCap: 1);
        Assert.Contains(answer.Results, r => r.SourcePath == "Assets/Scenes/Level.unity");
    }

    // ---- guid-less UnityEvent -> name_hints(kind='unityevent-local') ----------------------

    [Fact]
    public void F18_GuidlessLocalUnityEvent_CapturesNameHintWithTypePart()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var hints = LoadNameHints(engine.DbPath, "Assets/Prefabs/Player.prefab");
        var hint = Assert.Single(hints, h => h.Kind == "unityevent-local" && h.Name == "LocalPoke");
        Assert.Equal("Foo", hint.TypeName);

        // Still zero refs rows for the guid-less binding -- this capture is purely additive
        // (name_hints only); the existing negative-fixture guarantee (see AnnotationTests) is
        // untouched.
        using var conn = Open(engine.DbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM refs r JOIN files sf ON sf.id = r.source_file_id
            WHERE sf.path = 'Assets/Prefabs/Player.prefab' AND r.method_name = 'LocalPoke';
            """;
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    // ---- semantic-mode C# captures ---------------------------------------------------------

    [Fact]
    public void SemanticModeCaptures_F12_F13_F17_F19_F20()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        var summary = engine.RunIndex(full: false);
        Assert.Equal(0, summary.Stats.Cs.SyntacticAssemblies); // both Core and Game landed in Mode A

        var fooFileId = RequireFileId(engine.DbPath, "Assets/Scripts/Foo.cs");

        // AddComponent<Spawned>() -- a generic type argument in an invocation -- produces a
        // type-ref row resolving to Spawned's declared type.
        var spawnedDocId = RequireSymbolDocId(engine.DbPath, "T:Spawned", "Assets/Scripts/Spawned.cs");
        AssertSymbolRefExists(engine.DbPath, fooFileId, spawnedDocId, "type-ref");

        // `System.Action h = Helper.Handle;` -- a method-group / delegate-conversion reference --
        // produces an ordinary semantic call-kind ref.
        var handleDocId = RequireSymbolDocId(engine.DbPath, "M:Helper.Handle", "Assets/Scripts/Helper.cs");
        AssertSymbolRefExists(engine.DbPath, fooFileId, handleDocId, "call", requireSemantic: true);

        // `public Bar config;` -- a declaration-site field type -- produces a type-ref row.
        var barDocId = RequireSymbolDocId(engine.DbPath, "T:Bar", "Assets/Scripts/Bar.cs");
        AssertSymbolRefExists(engine.DbPath, fooFileId, barDocId, "type-ref");

        // symbols.attrs: Bar's [Serializable] attribute is persisted, with the "Attribute" suffix
        // stripped.
        Assert.Equal("Serializable", RequireAttrs(engine.DbPath, barDocId));

        // SendMessage("OnHitFrame") -- a by-name dispatch literal -- captured as negative
        // evidence, never an edge. Deliberately the same name as the animation event tested above.
        var csHints = LoadNameHints(engine.DbPath, "Assets/Scripts/Foo.cs");
        Assert.Contains(csHints, h => h.Kind == "cs-name-literal" && h.Name == "OnHitFrame");

        // Identifiers inside the #if UNITY_ANDROID-disabled block are captured, even though they
        // were never parsed as real syntax at all.
        Assert.Contains(csHints, h => h.Kind == "cs-disabled" && h.Name == "AndroidOnly");
        Assert.Contains(csHints, h => h.Kind == "cs-disabled" && h.Name == "DoSomething");

        // And AndroidOnly.cs/Spawned.cs/Helper.cs/Bar.cs must NOT have any resolvable inbound
        // edge from elsewhere -- the whole point of each regression is that they'd otherwise be
        // invisible to a later liveness walk.
        AssertNoOtherInboundSymbolRef(engine.DbPath, "T:AndroidOnly", "Assets/Scripts/AndroidOnly.cs");
    }

    [Fact]
    public void EntryReason_DistinguishesUnconditionalFromConditionalEntryPoints()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        // Foo.Start: a MonoBehaviour lifecycle method name -- CONDITIONAL entry point.
        Assert.Equal("lifecycle", RequireEntryReason(engine.DbPath, "M:Foo.Start"));

        // Foo.Jump: not an entry point at all -- no reason.
        using var conn = Open(engine.DbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT entry_reason, is_entry_point FROM symbols WHERE doc_id = 'M:Foo.Jump';";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
        Assert.Equal(0L, reader.GetInt64(1));
    }

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>
    /// Writes throwaway generated-IDE-shaped csprojs for both "Core" and "Game" into ONE
    /// fixture copy, each referencing every trusted platform assembly (the same injection
    /// CsExtractionTests already uses for direct compilation-driven tests) -- enough for
    /// CsModeSelector to pick Mode A for both, and for Game's compilation to get a REAL
    /// reference to Core's compilation (UnityStubs.cs -- MonoBehaviour/Component/AddComponent
    /// -- lives in Core, not Game).
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

    private sealed record NameHint(string Name, string Kind, string? TypeName);

    private static List<NameHint> LoadNameHints(string dbPath, string sourcePath)
    {
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT nh.name, nh.kind, nh.type_name
            FROM name_hints nh
            JOIN files sf ON sf.id = nh.source_file_id
            WHERE sf.path = @path;
            """;
        cmd.Parameters.AddWithValue("@path", sourcePath);
        using var reader = cmd.ExecuteReader();
        var result = new List<NameHint>();
        while (reader.Read())
        {
            result.Add(new NameHint(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
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

    private static string RequireSymbolDocId(string dbPath, string docId, string expectedFilePath)
    {
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT f.path FROM symbols s JOIN files f ON f.id = s.file_id WHERE s.doc_id = @docId;
            """;
        cmd.Parameters.AddWithValue("@docId", docId);
        var path = cmd.ExecuteScalar() as string;
        Assert.True(path is not null, $"no symbols row for doc_id '{docId}' (semantic resolution failed)");
        Assert.Equal(expectedFilePath, path, ignoreCase: true);
        return docId;
    }

    private static void AssertSymbolRefExists(string dbPath, long sourceFileId, string targetDocId, string refKind, bool requireSemantic = false)
    {
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT confidence FROM symbol_refs
            WHERE source_file_id = @fileId AND target_doc_id = @docId AND ref_kind = @refKind;
            """;
        cmd.Parameters.AddWithValue("@fileId", sourceFileId);
        cmd.Parameters.AddWithValue("@docId", targetDocId);
        cmd.Parameters.AddWithValue("@refKind", refKind);
        using var reader = cmd.ExecuteReader();
        var found = false;
        while (reader.Read())
        {
            found = true;
            if (requireSemantic)
            {
                Assert.Equal("semantic", reader.GetString(0));
            }
        }

        Assert.True(found, $"no {refKind} symbol_refs row from file {sourceFileId} to '{targetDocId}'");
    }

    private static void AssertNoOtherInboundSymbolRef(string dbPath, string docId, string declaringFilePath)
    {
        var declaringFileId = RequireFileId(dbPath, declaringFilePath);
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM symbol_refs WHERE target_doc_id = @docId AND source_file_id != @fileId;";
        cmd.Parameters.AddWithValue("@docId", docId);
        cmd.Parameters.AddWithValue("@fileId", declaringFileId);
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    private static string? RequireAttrs(string dbPath, string docId)
    {
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT attrs FROM symbols WHERE doc_id = @docId;";
        cmd.Parameters.AddWithValue("@docId", docId);
        return cmd.ExecuteScalar() as string;
    }

    private static string? RequireEntryReason(string dbPath, string docId)
    {
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT entry_reason FROM symbols WHERE doc_id = @docId;";
        cmd.Parameters.AddWithValue("@docId", docId);
        return cmd.ExecuteScalar() as string;
    }

    private static SqliteConnection Open(string dbPath)
    {
        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        return conn;
    }
}
