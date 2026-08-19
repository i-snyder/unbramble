using Microsoft.CodeAnalysis.CSharp;
using UnBramble.Core.CSharp;

namespace UnBramble.Tests;

/// <summary>Semantic extraction against the fixture, entry-point marking, unresolved-call handling, and Mode B end-to-end.</summary>
public class CsExtractionTests
{
    private static readonly string FixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MiniProject");

    // Tests construct the compilation through the same code path CsExtraction/ModeSelection use
    // in production, with injected BCL metadata references standing in for Mode A's generated-
    // csproj-supplied references -- the whole .NET runtime's trusted platform assemblies, the
    // standard trick for building a working Roslyn compilation inside a test host without a
    // real .csproj on disk.
    private static IReadOnlyList<string> BclReferencePaths() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);

    [Fact]
    public void SemanticExtraction_Fixture_MatchesSection6Expectations()
    {
        var coreUnit = new AssemblyUnit(
            "Core", 1, "Assets/Scripts/Core/Core.asmdef", [],
            [(2L, "Assets/Scripts/Core/CoreUtil.cs"), (3L, "Assets/Scripts/Core/UnityStubs.cs")], false);
        var gameUnit = new AssemblyUnit(
            "Game", 4, "Assets/Scripts/Game.asmdef", ["Core"],
            [(5L, "Assets/Scripts/Foo.cs"), (6L, "Assets/Scripts/BasePawn.cs")], false);

        var modeDecision = new CsModeDecision(CsAnalysisMode.Semantic, [], BclReferencePaths());

        var (coreCompilation, coreTreeMap) = CsProjectAnalyzer.BuildCompilation(coreUnit, modeDecision, new Dictionary<string, CSharpCompilation>(), ToFullPath);
        var (coreSymbols, _, _) = SemanticCsExtractor.Extract(coreCompilation, coreTreeMap);

        var dependencyCache = new Dictionary<string, CSharpCompilation> { ["Core"] = coreCompilation };
        var (gameCompilation, gameTreeMap) = CsProjectAnalyzer.BuildCompilation(gameUnit, modeDecision, dependencyCache, ToFullPath);
        var (gameSymbols, gameRefs, _) = SemanticCsExtractor.Extract(gameCompilation, gameTreeMap);

        // Declared-in-fixture symbols.
        Assert.Contains(coreSymbols, s => s.DocId == "T:CoreUtil" && s.Kind == "type");
        Assert.Contains(coreSymbols, s => s.DocId == "M:CoreUtil.Ping" && s.Kind == "method" && !s.IsEntryPoint);
        Assert.Contains(coreSymbols, s => s.DocId == "T:UnityEngine.MonoBehaviour" && s.Kind == "type");

        Assert.Contains(gameSymbols, s => s.DocId == "T:Foo" && s.Kind == "type");
        var start = gameSymbols.Single(s => s.DocId == "M:Foo.Start");
        Assert.True(start.IsEntryPoint, "Foo.Start (Unity lifecycle name, MonoBehaviour descent) must be marked an entry point");
        var jump = gameSymbols.Single(s => s.DocId == "M:Foo.Jump");
        Assert.False(jump.IsEntryPoint, "Foo.Jump is not a Unity lifecycle method name");

        // Cross-assembly call Foo.Start -> CoreUtil.Ping: real semantic resolution across the
        // Game -> Core compilation reference, not name matching (confidence == 'semantic' proves it).
        Assert.Contains(gameRefs, r => r.RefKind == "call" && r.TargetDocId == "M:CoreUtil.Ping"
            && r.SourceSymbolDocId == "M:Foo.Start" && r.Confidence == "semantic");

        // Inherit: Foo -> BasePawn -> UnityEngine.MonoBehaviour (Foo inherits BasePawn instead
        // of MonoBehaviour directly, so the UnityEvent matching cascade's inherited-member walk
        // has a real multi-level chain to climb). Both hops are real semantic resolution,
        // proving the chain is walkable end to end, not just Foo's own immediate base.
        Assert.Contains(gameRefs, r => r.RefKind == "inherit" && r.TargetDocId == "T:BasePawn"
            && r.SourceSymbolDocId == "T:Foo" && r.Confidence == "semantic");
        Assert.Contains(gameRefs, r => r.RefKind == "inherit" && r.TargetDocId == "T:UnityEngine.MonoBehaviour"
            && r.SourceSymbolDocId == "T:BasePawn" && r.Confidence == "semantic");

        // BasePawn.Poke is declared (not inherited) -- the target of the inherited-member walk.
        Assert.Contains(gameSymbols, s => s.DocId == "M:BasePawn.Poke" && s.Kind == "method");
    }

    [Fact]
    public void EntryPointMarking_SyntheticSources_LifecycleName_Attribute_And_StaticMain()
    {
        const string source = """
            namespace UnityEngine
            {
                public class Object {}
                public class Component : Object {}
                public class Behaviour : Component {}
                public class MonoBehaviour : Behaviour {}
                public class RuntimeInitializeOnLoadMethodAttribute : System.Attribute {}
            }

            public class Foo : UnityEngine.MonoBehaviour
            {
                void Start() {}
                void Jump() {}
            }

            public static class Bootstrap
            {
                [UnityEngine.RuntimeInitializeOnLoadMethod]
                static void Boot() {}

                static void Main() {}
            }
            """;

        var (symbols, _, _) = ExtractSingleFileSemantic(source);

        Assert.True(symbols.Single(s => s.DocId == "M:Foo.Start").IsEntryPoint);
        Assert.False(symbols.Single(s => s.DocId == "M:Foo.Jump").IsEntryPoint);
        Assert.True(symbols.Single(s => s.DocId == "M:Bootstrap.Boot").IsEntryPoint);
        Assert.True(symbols.Single(s => s.DocId == "M:Bootstrap.Main").IsEntryPoint);

        // entry_reason discriminates an unconditional entry point ('attribute'/'main') from a
        // conditional one ('lifecycle' -- only live if the declaring file itself is reachable);
        // null for non-entry-point symbols.
        Assert.Equal("lifecycle", symbols.Single(s => s.DocId == "M:Foo.Start").EntryReason);
        Assert.Null(symbols.Single(s => s.DocId == "M:Foo.Jump").EntryReason);
        Assert.Equal("attribute", symbols.Single(s => s.DocId == "M:Bootstrap.Boot").EntryReason);
        Assert.Equal("main", symbols.Single(s => s.DocId == "M:Bootstrap.Main").EntryReason);
    }

    [Fact]
    public void UnresolvableCall_RecordedWithSyntacticConfidence_NotDropped()
    {
        const string source = """
            public class Foo
            {
                void Bar()
                {
                    SomeUndefinedThing.DoStuff();
                }
            }
            """;

        var (_, refs, _) = ExtractSingleFileSemantic(source);

        var call = Assert.Single(refs, r => r.RefKind == "call" && r.TargetDocId.Contains("DoStuff", StringComparison.Ordinal));
        Assert.Equal("syntactic", call.Confidence);
    }

    [Fact]
    public void CliRun_RawFixture_LandsInSyntacticMode_AndCsRefsLabelsConfidence()
    {
        using var fixture = TestSupport.FixtureCopy.Create();

        var (initExit, _, initErr) = TestSupport.CliRunner.Run("init", fixture.Root);
        Assert.Equal(0, initExit);
        Assert.Contains("C# analysis: syntactic mode", initErr);

        var (refsExit, refsOut, _) = TestSupport.CliRunner.Run("cs-refs", "CoreUtil.Ping", "-p", fixture.Root, "--json");
        Assert.Equal(0, refsExit);
        Assert.Contains("\"confidence\":\"syntactic\"", refsOut);
    }

    [Fact]
    public void CliRun_WithoutUnityStubs_StillCompletesAnalysis_AndLabelsConfidence()
    {
        using var fixture = TestSupport.FixtureCopy.Create();
        File.Delete(fixture.Combine("Assets", "Scripts", "Core", "UnityStubs.cs"));
        File.Delete(fixture.Combine("Assets", "Scripts", "Core", "UnityStubs.cs.meta"));

        var (initExit, _, initErr) = TestSupport.CliRunner.Run("init", fixture.Root);
        Assert.Equal(0, initExit);
        Assert.Contains("C# analysis: syntactic mode", initErr);

        var (refsExit, refsOut, _) = TestSupport.CliRunner.Run("cs-refs", "CoreUtil.Ping", "-p", fixture.Root, "--json");
        Assert.Equal(0, refsExit);
        Assert.Contains("\"confidence\":\"syntactic\"", refsOut);
    }

    // ---- Review finding #1: enum/delegate symbol extraction ---------------------------------

    [Fact]
    public void EnumDeclaration_ProducesTypeSymbolRow_AndFieldTypeRefResolvesToIt()
    {
        // The exact join-failure shape from the review: a live class's declaration-site field
        // type-ref to an enum must resolve to a REAL `symbols` row (kind='type') so the
        // cs_file_refs view's `JOIN symbols tgt ON tgt.doc_id = sr.target_doc_id` succeeds --
        // before the fix, VisitEnumDeclaration didn't exist, so T:GameState had no symbols row
        // at all despite the type-ref itself resolving fine.
        const string source = """
            public enum GameState { Idle, Running }

            public class Foo
            {
                public GameState state;
            }
            """;

        var (symbols, refs, _) = ExtractSingleFileSemantic(source);

        var enumSymbol = Assert.Single(symbols, s => s.DocId == "T:GameState");
        Assert.Equal("type", enumSymbol.Kind);

        Assert.Contains(refs, r => r.RefKind == "type-ref" && r.TargetDocId == "T:GameState" && r.Confidence == "semantic");
    }

    [Fact]
    public void DelegateDeclaration_ProducesTypeSymbolRow_AndFieldTypeRefResolvesToIt()
    {
        const string source = """
            public delegate void ClickHandler();

            public class Foo
            {
                public ClickHandler onClick;
            }
            """;

        var (symbols, refs, _) = ExtractSingleFileSemantic(source);

        var delegateSymbol = Assert.Single(symbols, s => s.DocId == "T:ClickHandler");
        Assert.Equal("type", delegateSymbol.Kind);

        Assert.Contains(refs, r => r.RefKind == "type-ref" && r.TargetDocId == "T:ClickHandler" && r.Confidence == "semantic");
    }

    // ---- Review finding #2: bare (unqualified) method-group references ----------------------

    [Fact]
    public void BareMethodGroupReference_ViaUsingStatic_ProducesCallRef_NotDroppedSilently()
    {
        // Before the fix, method-group capture only fired inside VisitMemberAccessExpression
        // (qualified forms like `Helper.Handle`) -- a bare identifier reached through
        // `using static` produced no ref row at any granularity.
        const string source = """
            using static Helper;

            public static class Helper
            {
                public static void HandleBare() {}
            }

            public class Foo
            {
                void Start()
                {
                    System.Action b = HandleBare;
                }
            }
            """;

        var (_, refs, _) = ExtractSingleFileSemantic(source);

        Assert.Contains(refs, r => r.RefKind == "call" && r.TargetDocId == "M:Helper.HandleBare"
            && r.SourceSymbolDocId == "M:Foo.Start" && r.Confidence == "semantic");
    }

    [Fact]
    public void QualifiedMethodGroupReference_NotDoubleCounted_ByBareIdentifierVisitor()
    {
        // The new bare-identifier visitor must not ALSO fire for the "Handle" token inside a
        // qualified `Helper.Handle` member access (already captured by
        // VisitMemberAccessExpression) -- exactly one call-kind ref for this reference, not two.
        const string source = """
            public static class Helper
            {
                public static void Handle() {}
            }

            public class Foo
            {
                void Start()
                {
                    System.Action h = Helper.Handle;
                }
            }
            """;

        var (_, refs, _) = ExtractSingleFileSemantic(source);

        var matches = refs.Where(r => r.RefKind == "call" && r.TargetDocId == "M:Helper.Handle").ToList();
        Assert.Single(matches);
    }

    [Fact]
    public void BareInvocation_NotMiscountedAsAMethodGroupReference()
    {
        // A bare CALL (`Ping();`) is handled entirely by VisitInvocationExpression; the new
        // bare-identifier visitor must recognize the invocation-target position and skip it, or
        // this would double the ref count for every ordinary unqualified call in the fixture.
        const string source = """
            public class Foo
            {
                void Ping() {}

                void Start()
                {
                    Ping();
                }
            }
            """;

        var (_, refs, _) = ExtractSingleFileSemantic(source);

        var matches = refs.Where(r => r.RefKind == "call" && r.TargetDocId == "M:Foo.Ping").ToList();
        Assert.Single(matches);
    }

    // ---- Review finding #6: by-name dispatch API list typo ------------------------------------

    [Fact]
    public void BroadcastMessageLiteral_CapturedAsNameHint()
    {
        const string source = """
            public class Foo
            {
                void Start()
                {
                    BroadcastMessage("OnHitFrame");
                }
            }
            """;

        var (_, _, nameHints) = ExtractSingleFileSemantic(source);

        Assert.Contains(nameHints, h => h.Kind == "cs-name-literal" && h.Name == "OnHitFrame");
    }

    [Fact]
    public void SendMessageUpwardsLiteral_CapturedAsNameHint()
    {
        const string source = """
            public class Foo
            {
                void Start()
                {
                    SendMessageUpwards("OnHitFrame");
                }
            }
            """;

        var (_, _, nameHints) = ExtractSingleFileSemantic(source);

        Assert.Contains(nameHints, h => h.Kind == "cs-name-literal" && h.Name == "OnHitFrame");
    }

    private static (IReadOnlyList<CsSymbolRow> Symbols, IReadOnlyList<CsSymbolRefRow> Refs, IReadOnlyList<CsNameHintRow> NameHints) ExtractSingleFileSemantic(string source)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"unbramble-cs-test-{Guid.NewGuid():N}.cs");
        File.WriteAllText(tempFile, source);
        try
        {
            var unit = new AssemblyUnit("TestAsm", null, null, [], [(1L, "Synthetic.cs")], false);
            var modeDecision = new CsModeDecision(CsAnalysisMode.Semantic, [], BclReferencePaths());
            var (compilation, treeMap) = CsProjectAnalyzer.BuildCompilation(unit, modeDecision, new Dictionary<string, CSharpCompilation>(), _ => tempFile);
            return SemanticCsExtractor.Extract(compilation, treeMap);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private static string ToFullPath(string projectRelativePath) =>
        Path.Combine(FixtureRoot, projectRelativePath.Replace('/', Path.DirectorySeparatorChar));
}
