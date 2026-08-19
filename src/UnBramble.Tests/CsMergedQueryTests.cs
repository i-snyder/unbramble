using UnBramble.Core;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// `who-uses` on a `.cs` file merges in C#-kind edges, and — the load-bearing regression check —
/// the C# addition must not disturb the plain asset-graph edges. The expected-edges equality
/// tests (EdgeExtractionTests, InventoryCorrectnessTests, TransitiveQueryTests, AnnotationTests,
/// WatcherHostTests) are left completely untouched and remain green in the same test run as
/// these — that IS the regression check; nothing here re-implements them.
/// </summary>
public class CsMergedQueryTests
{
    [Fact]
    public void WhoUses_CoreUtilCs_ReturnsTheCsEdge()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveQueryTarget("Assets/Scripts/Core/CoreUtil.cs");
        var answer = engine.WhoUses(resolution.Target!, transitive: false, depthCap: UnBrambleEngine.DefaultDepthCap);

        Assert.Contains(answer.Results, r => r.Kind == "cs" && r.SourcePath == "Assets/Scripts/Foo.cs" && r.RefKind == "call");
        // CoreUtil.cs has no guid/path referencers at all (nothing points at its guid directly
        // -- only its owning Game/Core asmdef relationship does, which is a separate edge).
        Assert.DoesNotContain(answer.Results, r => r.Kind is "guid" or "path");
    }

    /// <summary>
    /// Real-project validation regression: the query target was passed as an ABSOLUTE path with
    /// forward slashes (`D:/…/&lt;project&gt;/Assets/Editor/X.cs`) and resolved to "no match"
    /// because the store only knows project-relative paths. A fully-qualified path under the
    /// project root must resolve to the same target its project-relative form does.
    /// </summary>
    [Fact]
    public void ResolveQueryTarget_AbsolutePathInsideProject_ResolvesLikeProjectRelative()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var absoluteForwardSlash = $"{fixture.Root.Replace('\\', '/')}/Assets/Scripts/Core/CoreUtil.cs";
        var resolution = engine.ResolveQueryTarget(absoluteForwardSlash);

        Assert.NotNull(resolution.Target);
        Assert.Equal("Assets/Scripts/Core/CoreUtil.cs", resolution.Target!.Path);
    }

    [Fact]
    public void WhoUses_FooCs_StillReturnsExactlyTheFourAssetEdges_NoCsEdges()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveQueryTarget("Assets/Scripts/Foo.cs");
        var answer = engine.WhoUses(resolution.Target!, transitive: false, depthCap: UnBrambleEngine.DefaultDepthCap);

        // Nothing in the fixture calls into Foo's own members from C#, so the C#-kind addition
        // adds zero "cs" edges here -- the merged output for Foo.cs's cs-kind slice must be
        // identical to the plain asset-graph answer. A fifth, "event"-kind edge comes from
        // Level.unity's UnityEvent binding (m_MethodName: Jump, m_TargetAssemblyTypeName: Foo,
        // Game), which links to M:Foo.Jump (declared directly) -- this base fixture has no
        // generated IDE csproj (syntactic mode), so the match is advisory, not proven.
        Assert.Equal(5, answer.Results.Count);
        Assert.DoesNotContain(answer.Results, r => r.Kind == "cs");
        Assert.Contains(answer.Results, r => r.Kind == "guid" && r.SourcePath == "Assets/Prefabs/Player.prefab");
        Assert.Contains(answer.Results, r => r.Kind == "guid" && r.SourcePath == "Assets/Scenes/Level.unity");
        Assert.Contains(answer.Results, r => r.Kind == "guid" && r.SourcePath == "Assets/Data/A.asset");
        Assert.Contains(answer.Results, r => r.Kind == "guid" && r.SourcePath == "Assets/Data/B.asset");
        Assert.Contains(answer.Results, r => r.Kind == "event" && r.SourcePath == "Assets/Scenes/Level.unity"
            && r.TargetSymbol == "Foo.Jump" && r.ConfidenceLabel == "advisory");
    }

    /// <summary>
    /// Regression: `cs-refs` read `symbol_refs` alone, so a method whose only caller was a
    /// UnityEvent binding came back "0 referencers" — the answer an agent reads as "safe to
    /// delete". (`who-uses &lt;symbol&gt;` had the binding all along; the two symbol-level surfaces
    /// had drifted.) Foo.Jump is bound from Level.unity in the fixture and nothing in C# calls it.
    /// </summary>
    [Fact]
    public void CsRefs_MethodBoundOnlyByAUnityEvent_SurfacesTheBinding()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var answer = engine.GetCsRefsAnswer("M:Foo.Jump");

        Assert.Empty(answer.Refs);
        Assert.All(answer.EventRefs, r => Assert.Equal("event", r.Kind));
        Assert.All(answer.EventRefs, r => Assert.Equal("Foo.Jump", r.TargetSymbol));

        // Both binding forms have to come through, since they reach the answer by different
        // routes: Level.unity's is the guid-carrying cascade's matched link, Player.prefab's is a
        // guid-less same-asset binding (button and handler in one prefab — the common case), which
        // has no resolvable target chain at all and is always advisory.
        Assert.Contains(answer.EventRefs, r => r.SourcePath == "Assets/Scenes/Level.unity" && r.RefKind == "declared");
        Assert.Contains(answer.EventRefs, r => r.SourcePath == "Assets/Prefabs/Player.prefab" && r.RefKind == "unityevent-local");
    }

    /// <summary>The same bindings `who-uses &lt;symbol&gt;` reports — one shared accessor, so the
    /// two surfaces cannot drift again.</summary>
    [Fact]
    public void CsRefs_EventBindings_MatchWhoUsesSymbols()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var csRefsBindings = engine.GetCsRefsAnswer("M:Foo.Jump").EventRefs
            .Select(r => (r.SourcePath, r.Line)).OrderBy(x => x).ToList();
        var whoUsesBindings = engine.WhoUsesSymbol("M:Foo.Jump", transitive: false, depthCap: UnBrambleEngine.DefaultDepthCap)
            .Results.Where(r => r.Kind == "event" && r.Depth == 0)
            .Select(r => (r.SourcePath, r.Line)).OrderBy(x => x).ToList();

        Assert.Equal(whoUsesBindings, csRefsBindings);
    }

    /// <summary>`cs-refs` carried no caveat footer at all, which made its "0 referencers" the only
    /// unqualified zero the tool could print.</summary>
    [Fact]
    public void CsRefs_Answer_CarriesTheSameBlindSpotsEveryOtherAnswerDoes()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var answer = engine.GetCsRefsAnswer("M:CoreUtil.Ping");

        Assert.Contains("string-path-loading", answer.BlindSpots);
        Assert.Contains("reflection", answer.BlindSpots);
    }

    [Fact]
    public void Cli_CsRefs_Json_IncludesEventResultsAndBlindSpots()
    {
        using var fixture = FixtureCopy.Create();
        Assert.Equal(0, CliRunner.Run("init", fixture.Root).ExitCode);

        var (exitCode, stdOut, _) = CliRunner.Run("cs-refs", "Foo.Jump", "-p", fixture.Root, "--json");

        Assert.Equal(0, exitCode);
        Assert.Contains("\"eventResults\":[", stdOut, StringComparison.Ordinal);
        Assert.Contains("Assets/Scenes/Level.unity", stdOut, StringComparison.Ordinal);
        Assert.Contains("\"blindSpots\":[", stdOut, StringComparison.Ordinal);
        // The symbol_refs count must keep meaning exactly what it always meant — bindings are
        // counted separately, never folded in.
        Assert.Contains("\"count\":0", stdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_CsRefs_Text_NamesTheUnityEventBinding()
    {
        using var fixture = FixtureCopy.Create();
        Assert.Equal(0, CliRunner.Run("init", fixture.Root).ExitCode);

        var (exitCode, stdOut, _) = CliRunner.Run("cs-refs", "Foo.Jump", "-p", fixture.Root);

        Assert.Equal(0, exitCode);
        Assert.Contains("UnityEvent binding", stdOut, StringComparison.Ordinal);
        Assert.Contains("Assets/Scenes/Level.unity", stdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void CsRefs_CoreUtilPing_JsonIncludesConfidenceKindAndTarget()
    {
        using var fixture = FixtureCopy.Create();

        var (initExit, _, _) = CliRunner.Run("init", fixture.Root);
        Assert.Equal(0, initExit);

        var (exitCode, stdOut, _) = CliRunner.Run("cs-refs", "CoreUtil.Ping", "-p", fixture.Root, "--json");
        Assert.Equal(0, exitCode);
        Assert.Contains("\"docId\":\"M:CoreUtil.Ping\"", stdOut);
        Assert.Contains("Assets/Scripts/Foo.cs", stdOut);
        Assert.Contains("\"refKind\":\"call\"", stdOut);
    }

    [Fact]
    public void WhoUses_CoreUtilCs_Json_LabelsCsKindAndConfidence()
    {
        using var fixture = FixtureCopy.Create();

        var (initExit, _, _) = CliRunner.Run("init", fixture.Root);
        Assert.Equal(0, initExit);

        var (exitCode, stdOut, _) = CliRunner.Run("who-uses", "Assets/Scripts/Core/CoreUtil.cs", "-p", fixture.Root, "--json");
        Assert.Equal(0, exitCode);
        Assert.Contains("\"kind\":\"cs\"", stdOut);
        Assert.Contains("\"confidence\":", stdOut);
        Assert.Contains("\"targetSymbol\":\"CoreUtil.Ping\"", stdOut);
    }
}
