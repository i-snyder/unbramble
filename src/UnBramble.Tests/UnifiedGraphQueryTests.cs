using Microsoft.Data.Sqlite;
using UnBramble.Core;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// Covers the unified guid + C# ("cross-seam") walk: who-uses/uses queries that cross between
/// the asset-guid graph and the C# symbol graph in a single walk, plus the resulting unified
/// output contract (derived confidence labels, blind spots, --json schema versioning,
/// symbol-argument who-uses, and CLI disambiguation between a path and a symbol argument).
///
/// Existing regression suites (EdgeExtractionTests, InventoryCorrectnessTests,
/// TransitiveQueryTests, AnnotationTests, CsMergedQueryTests, WatcherHostTests) are left
/// untouched and must stay green alongside these — that's the regression check. Their exact-set
/// assertions don't grow here: the base fixture's only cs edge (Foo.cs -&gt; CoreUtil.Ping) has
/// neither endpoint reachable from/to Foo.cs's own closures, so those pre-existing exact-set
/// walks are unaffected by the seam. What's new here are query shapes those suites never
/// exercised at all: who-uses CoreUtil.cs transitively, uses Player.prefab transitively.
/// </summary>
public class UnifiedGraphQueryTests
{
    // ---- cs_file_refs / unified_walk_edges views exist and are shaped correctly -----------

    [Fact]
    public void CsFileRefsView_ProjectsFooCsToCoreUtilCs()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        using var conn = Open(engine.DbPath);
        using var cmd = conn.CreateCommand();
        // Scoped to the CoreUtil.cs target specifically: Foo.cs also has a second cs_file_refs
        // row (Foo.cs -> BasePawn.cs, an "inherit" edge, since Foo inherits BasePawn -- see
        // UnityEventLinkingTests) with no defined row order between the two -- this view-shape check
        // must not depend on which one a plain SELECT happens to return first.
        cmd.CommandText = """
            SELECT sf.path, tf.path, cfr.kind
            FROM cs_file_refs cfr
            JOIN files sf ON sf.id = cfr.source_file_id
            JOIN files tf ON tf.id = cfr.target_file_id
            WHERE sf.path = 'Assets/Scripts/Foo.cs' AND tf.path = 'Assets/Scripts/Core/CoreUtil.cs';
            """;
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), "cs_file_refs has no row for Foo.cs -> CoreUtil.cs");
        Assert.Equal("Assets/Scripts/Core/CoreUtil.cs", reader.GetString(1));
        Assert.Equal("cs", reader.GetString(2));
    }

    [Fact]
    public void UnifiedWalkEdgesView_UnionsGuidAndCsEdges()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        using var conn = Open(engine.DbPath);

        // A guid edge (Player.prefab -> Foo.cs) and a cs edge (Foo.cs -> CoreUtil.cs) must both
        // appear as plain (source_file_id, target_file_id) pairs in the same view.
        Assert.True(EdgeExists(conn, "Assets/Prefabs/Player.prefab", "Assets/Scripts/Foo.cs"));
        Assert.True(EdgeExists(conn, "Assets/Scripts/Foo.cs", "Assets/Scripts/Core/CoreUtil.cs"));

        // No self-edges, no NULL-target rows (unresolved guid/path refs must not leak in as
        // walkable edges -- unified_walk_edges is defined to already exclude them).
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM unified_walk_edges WHERE target_file_id IS NULL OR source_file_id = target_file_id;";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    private static bool EdgeExists(SqliteConnection conn, string sourcePath, string targetPath)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM unified_walk_edges uw
            JOIN files sf ON sf.id = uw.source_file_id
            JOIN files tf ON tf.id = uw.target_file_id
            WHERE sf.path = @source AND tf.path = @target;
            """;
        cmd.Parameters.AddWithValue("@source", sourcePath);
        cmd.Parameters.AddWithValue("@target", targetPath);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    // ---- transitive who-uses/uses cross the seam -------------------------------------------

    [Fact]
    public void WhoUses_CoreUtilCs_Transitive_CrossesSeamIntoAssetGraph()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveQueryTarget("Assets/Scripts/Core/CoreUtil.cs");
        var answer = engine.WhoUses(resolution.Target!, transitive: true, depthCap: UnBrambleEngine.DefaultDepthCap);

        var byPath = answer.Results
            .GroupBy(r => r.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Min(r => r.Depth), StringComparer.OrdinalIgnoreCase);

        // One file-level hop per edge regardless of kind: the cs hop Foo.cs -> CoreUtil.cs is
        // depth 1, exactly like a guid hop would be; everything that reaches Foo.cs by guid is
        // then depth 2, one hop further -- the same set
        // TransitiveQueryTests.WhoUses_FooCs_Transitive_YieldsExactSetWithMinDepth asserts for
        // Foo.cs itself, shifted by exactly one hop for reaching CoreUtil.cs through Foo.cs first.
        var expected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Assets/Scripts/Foo.cs"] = 1,
            ["Assets/Data/A.asset"] = 2,
            ["Assets/Data/B.asset"] = 2,
            ["Assets/Prefabs/Player.prefab"] = 2,
            ["Assets/Scenes/Level.unity"] = 2,
            ["Assets/Prefabs/Enemy.prefab"] = 3,
            ["ProjectSettings/EditorBuildSettings.asset"] = 3,
        };

        Assert.Equal(expected.Count, byPath.Count);
        foreach (var (path, depth) in expected)
        {
            Assert.True(byPath.TryGetValue(path, out var actualDepth), $"missing '{path}' from transitive who-uses result");
            Assert.Equal(depth, actualDepth);
        }

        // The depth-1 hop is the cs edge itself.
        var depth1 = Assert.Single(answer.Results, r => r.Depth == 1);
        Assert.Equal("cs", depth1.Kind);
        Assert.Equal("Foo.cs".Length > 0 ? "Assets/Scripts/Foo.cs" : null, depth1.SourcePath);
    }

    [Fact]
    public void Uses_PlayerPrefab_Transitive_CrossesSeamIntoCsGraph()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveQueryTarget("Assets/Prefabs/Player.prefab");
        var answer = engine.Uses(resolution.Target!, transitive: true, depthCap: UnBrambleEngine.DefaultDepthCap);

        // Player.prefab -guid-> Foo.cs (depth 1) -cs-> CoreUtil.cs (depth 2): the transitive
        // `uses` walk crosses from the guid graph into the cs graph in a single walk, so
        // CoreUtil.cs is reachable from Player.prefab even though they're connected only
        // through an intermediate C# edge.
        Assert.Contains(answer.Results, r => r.TargetPath == "Assets/Scripts/Foo.cs" && r.Depth == 1 && r.Kind == "guid");
        Assert.Contains(answer.Results, r => r.TargetPath == "Assets/Scripts/Core/CoreUtil.cs" && r.Depth == 2 && r.Kind == "cs");
    }

    // ---- derived confidence labels -----------------------------------------------------------

    [Fact]
    public void WhoUses_FooCs_Direct_GuidEdgesAreProven()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveQueryTarget("Assets/Scripts/Foo.cs");
        var answer = engine.WhoUses(resolution.Target!, transitive: false, depthCap: 1);

        // A fifth edge sits on top of the four guid ones: Level.unity's UnityEvent binding,
        // matched (declared) to M:Foo.Jump but capped at advisory in this base (syntactic-mode)
        // fixture -- see UnityEventLinkingTests and CsMergedQueryTests' sibling assertion. The four
        // GUID edges themselves stay proven; only the answer-level confidence (weakest-link) is
        // dragged down by the event edge.
        var guidResults = answer.Results.Where(r => r.Kind == "guid").ToList();
        Assert.Equal(4, guidResults.Count);
        Assert.All(guidResults, r => Assert.Equal("proven", r.ConfidenceLabel));
        var eventEdge = Assert.Single(answer.Results, r => r.Kind == "event");
        Assert.Equal("advisory", eventEdge.ConfidenceLabel);
        Assert.Equal("advisory", answer.Confidence);
    }

    [Fact]
    public void WhoUses_CoreUtilCs_Direct_SyntacticCsEdgeIsAdvisory()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        // The base committed fixture has no generated IDE csproj, so both assemblies land in
        // syntactic mode (CsExtractionTests confirms this) -- syntactic-confidence rows derive
        // to "advisory", never "proven".
        var resolution = engine.ResolveQueryTarget("Assets/Scripts/Core/CoreUtil.cs");
        var answer = engine.WhoUses(resolution.Target!, transitive: false, depthCap: 1);

        var csEdge = Assert.Single(answer.Results, r => r.Kind == "cs");
        Assert.Equal("syntactic", csEdge.Confidence);
        Assert.Equal("advisory", csEdge.ConfidenceLabel);
        Assert.Equal("advisory", answer.Confidence);
    }

    [Fact]
    public void WhoUses_Transitive_ChainWeakestPropagatesThroughAdvisoryHop()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        // Reaching Player.prefab (a plain, proven guid hop from CoreUtil.cs's perspective)
        // still crosses the depth-1 advisory cs hop (Foo.cs) first -- a chain is as strong as
        // its weakest link: the whole path's weakest label applies, not just the last hop.
        var resolution = engine.ResolveQueryTarget("Assets/Scripts/Core/CoreUtil.cs");
        var answer = engine.WhoUses(resolution.Target!, transitive: true, depthCap: UnBrambleEngine.DefaultDepthCap);

        var playerEdge = Assert.Single(answer.Results, r => r.SourcePath == "Assets/Prefabs/Player.prefab");
        Assert.Equal("advisory", playerEdge.ConfidenceLabel);
        Assert.Equal("advisory", answer.Confidence);
    }

    [Fact]
    public void Builtin_GuidEdge_IsLabeledProven_DespiteUnresolved()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveQueryTarget("Assets/Scenes/Level.unity");
        var answer = engine.Uses(resolution.Target!, transitive: false, depthCap: 1);

        var builtinEdge = Assert.Single(answer.Results, r => r.Builtin && r.GameObject == null && r.Line == 18);
        Assert.False(builtinEdge.Resolved);
        Assert.Equal("proven", builtinEdge.ConfidenceLabel);

        var unresolvedEdge = Assert.Single(answer.Results, r => !r.Resolved && !r.Builtin);
        Assert.Null(unresolvedEdge.ConfidenceLabel);
    }

    // ---- blind spots ---------------------------------------------------------------------------

    [Fact]
    public void WhoUses_BlindSpots_AlwaysIncludeStringPathAndReflection_AndSyntacticFlag()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveQueryTarget("Assets/Scripts/Foo.cs");
        var answer = engine.WhoUses(resolution.Target!, transitive: false, depthCap: 1);

        Assert.Contains("string-path-loading", answer.BlindSpots);
        Assert.Contains("reflection", answer.BlindSpots);
        // Base fixture: both assemblies land in syntactic mode (no generated csproj).
        Assert.Contains("syntactic-assemblies-present", answer.BlindSpots);
        Assert.DoesNotContain("depth-truncated", answer.BlindSpots);

        // addressables-unconfirmed: reserved, no who-uses/uses trigger wired yet.
        // csproj-stale: wired (UnBrambleEngine.IsAnyCsprojStale), but the base fixture has no
        // generated csproj at all -- zero semantic assemblies means CheckCsprojFreshness has
        // nothing to compare, so it can never fire here regardless. See DeadCandidatesTests'
        // WhoUses_CsprojStale_* tests for the semantic-mode fresh/stale coverage.
        Assert.DoesNotContain("addressables-unconfirmed", answer.BlindSpots);
        Assert.DoesNotContain("csproj-stale", answer.BlindSpots);
    }

    [Fact]
    public void WhoUses_Transitive_DepthCapped_SetsDepthTruncatedBlindSpot()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveQueryTarget("Assets/Scripts/Core/CoreUtil.cs");
        var answer = engine.WhoUses(resolution.Target!, transitive: true, depthCap: 1);

        Assert.True(answer.Truncated);
        Assert.Contains("depth-truncated", answer.BlindSpots);
    }

    // ---- --json envelope versioning ------------------------------------------------------------

    [Fact]
    public void JsonAnswers_CarryUnBrambleSchemaVersion1()
    {
        using var fixture = FixtureCopy.Create();
        var (initExit, _, _) = CliRunner.Run("init", fixture.Root);
        Assert.Equal(0, initExit);

        var (whoUsesExit, whoUsesOut, _) = CliRunner.Run("who-uses", "Assets/Scripts/Foo.cs", "-p", fixture.Root, "--json");
        Assert.Equal(0, whoUsesExit);
        Assert.Contains("\"unbrambleSchema\":1", whoUsesOut);
        Assert.Contains("\"confidence\":\"proven\"", whoUsesOut);
        Assert.Contains("\"blindSpots\":[", whoUsesOut);

        var (usesExit, usesOut, _) = CliRunner.Run("uses", "Assets/Scripts/Foo.cs", "-p", fixture.Root, "--json");
        Assert.Equal(0, usesExit);
        Assert.Contains("\"unbrambleSchema\":1", usesOut);

        var (statsExit, statsOut, _) = CliRunner.Run("stats", "-p", fixture.Root, "--json");
        Assert.Equal(0, statsExit);
        Assert.Contains("\"unbrambleSchema\":1", statsOut);

        var (resolveExit, resolveOut, _) = CliRunner.Run("resolve", "Assets/Scripts/Foo.cs", "-p", fixture.Root, "--json");
        Assert.Equal(0, resolveExit);
        Assert.Contains("\"unbrambleSchema\":1", resolveOut);
    }

    // ---- symbol-argument who-uses ----------------------------------------------------------

    [Fact]
    public void WhoUses_SymbolArgument_CoreUtilPing_ReturnsDepth0ReferencerAndNoFileContext()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveCsSymbol("CoreUtil.Ping");
        Assert.Equal("M:CoreUtil.Ping", resolution.DocId);

        var answer = engine.WhoUsesSymbol(resolution.DocId!, transitive: false, depthCap: 1);

        // CoreUtil.cs has no guid/path referencers at all (CsMergedQueryTests already
        // establishes this) -- the file-context section must come back empty, not error.
        var single = Assert.Single(answer.Results);
        Assert.Equal(0, single.Depth);
        Assert.Equal("Assets/Scripts/Foo.cs", single.SourcePath);
        Assert.Equal("CoreUtil.Ping", single.TargetSymbol);
        Assert.Equal("advisory", single.ConfidenceLabel); // syntactic mode
    }

    [Fact]
    public void WhoUses_SymbolArgument_MainTypeMatchingBasename_FileContextIsProven()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        // "T:Foo" is a type whose name equals Foo.cs's basename -- Unity's own script-to-component
        // attachment convention -- so the asset-level referencers of Foo.cs are surfaced as
        // PROVEN evidence for the type itself.
        var resolution = engine.ResolveCsSymbol("T:Foo");
        Assert.Equal("T:Foo", resolution.DocId);

        var answer = engine.WhoUsesSymbol(resolution.DocId!, transitive: false, depthCap: 1);

        var fileContext = answer.Results.Where(r => r.Depth >= 1).ToList();
        Assert.Equal(4, fileContext.Count);
        Assert.All(fileContext, r => Assert.Equal("proven", r.ConfidenceLabel));
    }

    [Fact]
    public void WhoUses_SymbolArgument_Member_FileContextIsAdvisoryDespiteMainTypeMatch()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        // Foo.Jump is a MEMBER, not the main type -- even though Foo (the type) matches Foo.cs's
        // basename, the prefab/scene attachments don't prove they reach `Jump` specifically.
        // Nothing is omitted -- the attachments still show up, just advisory instead of
        // silently proven or silently dropped.
        var resolution = engine.ResolveCsSymbol("M:Foo.Jump");
        Assert.Equal("M:Foo.Jump", resolution.DocId);

        var answer = engine.WhoUsesSymbol(resolution.DocId!, transitive: false, depthCap: 1);

        var fileContext = answer.Results.Where(r => r.Depth >= 1).ToList();
        Assert.Equal(4, fileContext.Count);
        Assert.All(fileContext, r => Assert.Equal("advisory", r.ConfidenceLabel));
        Assert.Equal("advisory", answer.Confidence);

        // Nothing calls Foo.Jump from C# in this fixture, but two separate UnityEvent bindings
        // link to this exact symbol -- depth 0 carries both, never picking one over the other:
        //  - Level.unity's guid-carrying binding (m_MethodName: Jump, m_TargetAssemblyTypeName:
        //    Foo, Game), matched via the main resolution cascade. This base fixture has no
        //    generated IDE csproj (both assemblies land in syntactic mode), so the match is
        //    capped at advisory (a match is only proven under semantic mode) -- see
        //    UnityEventLinkingTests for the proven case under semantic mode.
        //  - Player.prefab's own guid-less "onLocalJump" binding (captured into name_hints by
        //    the C# capture tests in ReferenceCaptureTests), whose type_name="Foo" DOES resolve to a
        //    symbol declaring Jump -- a guid-less annotation is always advisory, since no
        //    resolvable component chain proves it.
        var depth0 = answer.Results.Where(r => r.Depth == 0).ToList();
        Assert.Equal(2, depth0.Count);
        Assert.All(depth0, r => Assert.Equal("event", r.Kind));
        Assert.All(depth0, r => Assert.Equal("advisory", r.ConfidenceLabel));
        Assert.Contains(depth0, r => r.SourcePath == "Assets/Scenes/Level.unity" && r.RefKind == "declared");
        Assert.Contains(depth0, r => r.SourcePath == "Assets/Prefabs/Player.prefab" && r.RefKind == "unityevent-local");
    }

    [Fact]
    public void CliWhoUses_SymbolArgument_CoreUtilPing_Json_RoundTrips()
    {
        using var fixture = FixtureCopy.Create();
        var (initExit, _, _) = CliRunner.Run("init", fixture.Root);
        Assert.Equal(0, initExit);

        var (exitCode, stdOut, _) = CliRunner.Run("who-uses", "CoreUtil.Ping", "-p", fixture.Root, "--json");
        Assert.Equal(0, exitCode);
        Assert.Contains("\"symbol\":\"M:CoreUtil.Ping\"", stdOut);
        Assert.Contains("\"kind\":\"cs\"", stdOut);
        Assert.Contains("Assets/Scripts/Foo.cs", stdOut);
    }

    // ---- disambiguation rule ---------------------------------------------------------------------

    [Fact]
    public void CliWhoUses_AmbiguousBetweenPathAndSymbol_RequiresDisambiguation()
    {
        using var fixture = FixtureCopy.Create();
        var (initExit, _, _) = CliRunner.Run("init", fixture.Root);
        Assert.Equal(0, initExit);

        // "Foo" resolves as BOTH a fuzzy path match (Assets/Scripts/Foo.cs, the only path
        // containing "Foo") and a C# symbol (T:Foo) -- never guess.
        var (exitCode, _, stdErr) = CliRunner.Run("who-uses", "Foo", "-p", fixture.Root);
        Assert.Equal(2, exitCode);
        Assert.Contains("ambiguous", stdErr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--symbol", stdErr);
    }

    [Fact]
    public void CliWhoUses_ExplicitDocIdPrefix_BypassesAmbiguityCheck()
    {
        using var fixture = FixtureCopy.Create();
        var (initExit, _, _) = CliRunner.Run("init", fixture.Root);
        Assert.Equal(0, initExit);

        var (exitCode, stdOut, _) = CliRunner.Run("who-uses", "T:Foo", "-p", fixture.Root, "--json");
        Assert.Equal(0, exitCode);
        Assert.Contains("\"symbol\":\"T:Foo\"", stdOut);
    }

    [Fact]
    public void CliWhoUses_SymbolFlag_ForcesSymbolResolution()
    {
        using var fixture = FixtureCopy.Create();
        var (initExit, _, _) = CliRunner.Run("init", fixture.Root);
        Assert.Equal(0, initExit);

        var (exitCode, stdOut, _) = CliRunner.Run("who-uses", "Foo", "--symbol", "-p", fixture.Root, "--json");
        Assert.Equal(0, exitCode);
        Assert.Contains("\"symbol\":\"T:Foo\"", stdOut);
    }

    // ---- --kind filter -------------------------------------------------------------------------

    [Fact]
    public void CliWhoUses_KindFilter_RestrictsToRequestedKind()
    {
        using var fixture = FixtureCopy.Create();
        var (initExit, _, _) = CliRunner.Run("init", fixture.Root);
        Assert.Equal(0, initExit);

        var (exitCode, stdOut, _) = CliRunner.Run("who-uses", "Assets/Scripts/Core/CoreUtil.cs", "-p", fixture.Root, "--kind", "guid", "--json");
        Assert.Equal(0, exitCode);
        Assert.Contains("\"results\":[]", stdOut);
    }

    // ---- cs-refs stays a working, unchanged-shape alias (regression) --------------------------

    [Fact]
    public void CsRefs_StillWorks_RawConfidenceUnchanged()
    {
        using var fixture = FixtureCopy.Create();
        var (initExit, _, _) = CliRunner.Run("init", fixture.Root);
        Assert.Equal(0, initExit);

        var (exitCode, stdOut, _) = CliRunner.Run("cs-refs", "CoreUtil.Ping", "-p", fixture.Root, "--json");
        Assert.Equal(0, exitCode);
        Assert.Contains("\"confidence\":\"syntactic\"", stdOut); // raw mode, NOT the derived label -- pinned, do not "fix"
        Assert.Contains("\"unbrambleSchema\":1", stdOut);
    }

    private static SqliteConnection Open(string dbPath)
    {
        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        return conn;
    }
}
