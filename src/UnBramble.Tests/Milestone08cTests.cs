using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using UnBramble.Core;
using UnBramble.Core.Query;
using UnBramble.Core.Store;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// Covers UnityEvent name linking: the matching cascade from a UnityEvent's `target_type_name` /
/// method name to a real C# symbol (type selection, member selection, the inherited-member walk,
/// and preservation of unmatched bindings as plain guid annotations), `kind='event'` who-uses
/// output, and implicit-entry-point marking for matched event handlers. Also covers
/// `expected-events.json`, the fixture's own ground-truth ledger for every guid-carrying
/// UnityEvent call — parser capture of those bindings is tested in Milestone08aTests; this class
/// asserts the linking/matching outcome for each one.
///
/// Uses the same throwaway-semantic-csproj pattern as <see cref="Milestone08aTests"/> (a local
/// copy of the helper, not a shared one) so the fixture's two assemblies land in Semantic mode:
/// a match is only proven when semantic resolution actually ran, so this class needs semantic
/// mode to exercise the proven case. <see cref="Milestone08bTests"/> and
/// <see cref="CsMergedQueryTests"/> separately cover the syntactic-mode (base fixture, no
/// generated csproj) case, where the very same binding still matches but is capped at advisory.
/// </summary>
public class Milestone08cTests
{
    private static readonly string ExpectedEventsPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "expected-events.json");

    // ---- expected-events.json: the cascade's own ground truth for every linked binding --------

    [Fact]
    public void ExpectedEvents_CascadeOutcomes_MatchLedgerExactly()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        using var store = UnBrambleStore.OpenOrCreate(engine.DbPath, engine.UnityVersion);
        var links = store.ResolveEventLinks();

        var ledger = LoadEventLedger();
        Assert.NotEmpty(ledger.Links);

        foreach (var expected in ledger.Links)
        {
            var actual = Assert.Single(links, l => l.SourceFilePath == expected.Source && l.RawMethodName == expected.MethodName);
            Assert.Equal(expected.TargetTypeName, actual.RawTargetTypeName);
            Assert.Equal(expected.Matched, actual.IsMatched);
            Assert.Equal(expected.TargetDocId, actual.TargetDocId);
            Assert.Equal(expected.MatchKind, actual.MatchKind);
            Assert.Equal(expected.Confidence, actual.Confidence);
        }

        // No extras: every guid-carrying event call in the fixture is accounted for by the
        // ledger (there are exactly three m_MethodName-bearing refs rows in the fixture).
        Assert.Equal(ledger.Links.Count, links.Count);
    }

    // ---- declared match, proven under semantic mode --------------------------------------------

    [Fact]
    public void F8_DeclaredMatch_ProvenUnderSemanticMode_SurfacesOnWhoUsesSymbol()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveCsSymbol("M:Foo.Jump");
        Assert.Equal("M:Foo.Jump", resolution.DocId);

        var answer = engine.WhoUsesSymbol(resolution.DocId!, transitive: false, depthCap: 1);

        // Foo.Jump also has a second, independent matched binding in this fixture (Player.prefab's
        // guid-less "onLocalJump" -- its type_name="Foo" resolves and Foo does declare Jump) --
        // scope this assertion to the guid-carrying, "declared" one specifically;
        // GuidlessLocalBinding_* below covers the other one directly.
        var eventEdge = Assert.Single(answer.Results, r => r.Kind == "event" && r.Depth == 0 && r.RefKind == "declared");
        Assert.Equal("Assets/Scenes/Level.unity", eventEdge.SourcePath);
        Assert.Equal("Foo.Jump", eventEdge.TargetSymbol);
        Assert.Equal("declared", eventEdge.RefKind);
        Assert.Equal("proven", eventEdge.ConfidenceLabel);
        Assert.True(eventEdge.Implicit, "a matched event-bound method is marked implicit");
    }

    // ---- inherited match, capped at advisory regardless of mode --------------------------------

    [Fact]
    public void F9_InheritedMatch_AdvisoryEvenUnderSemanticMode_WalksThroughBasePawn()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        // Foo itself never declares Poke -- only reachable by walking Foo's inherit chain up to
        // BasePawn.
        var resolution = engine.ResolveCsSymbol("M:BasePawn.Poke");
        Assert.Equal("M:BasePawn.Poke", resolution.DocId);

        var answer = engine.WhoUsesSymbol(resolution.DocId!, transitive: false, depthCap: 1);

        var eventEdge = Assert.Single(answer.Results, r => r.Kind == "event" && r.Depth == 0);
        Assert.Equal("Assets/Scenes/Level.unity", eventEdge.SourcePath);
        Assert.Equal("BasePawn.Poke", eventEdge.TargetSymbol);
        Assert.Equal("inherited", eventEdge.RefKind);
        Assert.Equal("advisory", eventEdge.ConfidenceLabel); // capped, never proven, even though the type resolved semantically
        Assert.True(eventEdge.Implicit);

        // who-uses Foo itself must NOT claim the Poke binding as its own -- Foo doesn't declare
        // it, so a query on Foo.Jump's sibling type-level symbol has nothing new to say about
        // Poke specifically.
        var fooJumpAnswer = engine.WhoUsesSymbol(engine.ResolveCsSymbol("M:Foo.Jump").DocId!, transitive: false, depthCap: 1);
        Assert.DoesNotContain(fooJumpAnswer.Results, r => r.TargetSymbol == "BasePawn.Poke");
    }

    // ---- unmatched event binding: never produces an event edge, guid annotation survives -------

    [Fact]
    public void F10_UnmatchedVanished_NeverProducesAnEventEdge_ButGuidAnnotationSurvivesUntouched()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        using var store = UnBrambleStore.OpenOrCreate(engine.DbPath, engine.UnityVersion);
        var links = store.ResolveEventLinks();

        var vanished = Assert.Single(links, l => l.RawMethodName == "Vanished");
        Assert.False(vanished.IsMatched);
        Assert.Null(vanished.TargetDocId);
        Assert.Null(vanished.TargetFileId);
        Assert.Equal("unmatched", vanished.MatchKind);
        Assert.Equal("advisory", vanished.Confidence);
        Assert.Equal("Foo, Game", vanished.RawTargetTypeName);

        // Never dropped: the underlying guid-carrying refs row (Level.unity -> Player.prefab, the
        // containing asset) keeps surfacing its raw m_MethodName annotation on the plain
        // asset-graph edge exactly as it always has -- an unmatched call was already a real,
        // resolved guid edge; event-linking only ever adds member-level knowledge on top for
        // matched cases, it never takes anything away for the unmatched one.
        var playerResolution = engine.ResolveQueryTarget("Assets/Prefabs/Player.prefab");
        var whoUsesPlayer = engine.WhoUses(playerResolution.Target!, transitive: false, depthCap: 1);
        Assert.Contains(whoUsesPlayer.Results, r => r.SourcePath == "Assets/Scenes/Level.unity" && r.MethodName == "Vanished" && r.Kind == "guid");

        // And no kind='event' edge is ever spuriously created for "Vanished" anywhere queryable
        // (there is no real symbol to attach one to).
        Assert.DoesNotContain(whoUsesPlayer.Results, r => r.Kind == "event" && r.MethodName == "Vanished");
    }

    // ---- weakest-link answer confidence crosses an advisory event hop --------------------------

    [Fact]
    public void F24_TransitiveWhoUsesBasePawnCs_AnswerConfidence_IsAdvisory_ChainWeakest()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveQueryTarget("Assets/Scripts/BasePawn.cs");
        var answer = engine.WhoUses(resolution.Target!, transitive: true, depthCap: UnBrambleEngine.DefaultDepthCap);

        // The advisory hop itself: the inherited-match event edge (see the inherited-match test
        // above), merged directly into BasePawn.cs's transitive answer at depth 1, bypassing the
        // deeper, fully-proven structural chain (Level.unity -guid-proven-> Player.prefab
        // -guid-proven-> Foo.cs -cs-inherit-proven-> BasePawn.cs). Both routes are real; the
        // chain-weakest rule means the whole answer is only as strong as the weakest one.
        var eventEdge = Assert.Single(answer.Results, r => r.Kind == "event");
        Assert.Equal("Assets/Scenes/Level.unity", eventEdge.SourcePath);
        Assert.Equal(1, eventEdge.Depth);
        Assert.Equal("advisory", eventEdge.ConfidenceLabel);

        // Level.unity is reached via BOTH routes -- never silently pick one -- and chain-weakest
        // aggregation labels every displayed entry for that node with the worst alternate.
        var levelEntries = answer.Results.Where(r => r.SourcePath == "Assets/Scenes/Level.unity").ToList();
        Assert.True(levelEntries.Count >= 2, "expected Level.unity via both the event hop and the deeper structural chain");
        Assert.All(levelEntries, r => Assert.Equal("advisory", r.ConfidenceLabel));

        Assert.Equal("advisory", answer.Confidence);
    }

    [Fact]
    public void CliF24_TransitiveWhoUsesBasePawnCs_Json_CarriesAdvisoryAnswerConfidence()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);

        var (initExit, _, _) = CliRunner.Run("init", fixture.Root);
        Assert.Equal(0, initExit);

        var (exitCode, stdOut, _) = CliRunner.Run("who-uses", "Assets/Scripts/BasePawn.cs", "--transitive", "-p", fixture.Root, "--json");
        Assert.Equal(0, exitCode);

        using var doc = JsonDocument.Parse(stdOut);
        Assert.Equal("advisory", doc.RootElement.GetProperty("confidence").GetString());
    }

    // ---- guid-less local bindings (unityevent-local): mechanism correctness, never-guess -----

    [Fact]
    public void GuidlessLocalBinding_TypeDeclaresTheMethod_AnnotatesAdvisory()
    {
        // Player.prefab's guid-less "onLocalJump" event captures type_name="Foo", name="Jump"
        // into name_hints -- and Foo DOES declare Jump -- so who-uses M:Foo.Jump must show an
        // advisory "bound as a same-asset UnityEvent target" annotation alongside the separate
        // guid-carrying match tested above. Never proven: no resolvable component chain exists
        // to prove a guid-less binding.
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        using var store = UnBrambleStore.OpenOrCreate(engine.DbPath, engine.UnityVersion);

        var link = Assert.Single(store.GetLocalEventBindingsTargetingDocId("M:Foo.Jump"));
        Assert.Equal("Assets/Prefabs/Player.prefab", link.SourcePath);
        Assert.Equal("event", link.Kind);
        Assert.Equal("unityevent-local", link.RefKind);
        Assert.Equal("advisory", link.ConfidenceLabel);
        Assert.True(link.Implicit);
    }

    [Fact]
    public void GuidlessLocalBinding_TypeDoesNotDeclareTheMethod_NeverAnnotatesAWrongSymbol()
    {
        // Player.prefab's guid-less "onLocalPoke" event captures type_name="Foo", name="LocalPoke"
        // into name_hints -- but Foo does not declare LocalPoke (only the unrelated, unattached
        // LocalPokeOwner.cs does). The guid-less annotation rule requires type_name to resolve to
        // a symbol that ACTUALLY DECLARES the named method before annotating -- "never guess"
        // applies here exactly as it does to the main cascade. Queried the only two ways this
        // binding could ever surface: keyed off Foo's own (real, but wrong) doc_id, and keyed
        // off LocalPokeOwner's (right method, but never named by this binding's type_name).
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        using var store = UnBrambleStore.OpenOrCreate(engine.DbPath, engine.UnityVersion);

        Assert.Empty(store.GetLocalEventBindingsTargetingDocId("M:LocalPokeOwner.LocalPoke"));
    }

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>Writes throwaway generated-IDE-shaped csprojs to force semantic mode -- see
    /// <see cref="Milestone08aTests.WriteSemanticModeCsprojs"/> for the full rationale;
    /// duplicated locally here rather than shared via a common test helper.</summary>
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

    private static EventLedger LoadEventLedger()
    {
        var json = File.ReadAllText(ExpectedEventsPath);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var ledger = JsonSerializer.Deserialize<EventLedger>(json, options);
        Assert.NotNull(ledger);
        return ledger!;
    }

    private sealed class EventLedger
    {
        [JsonPropertyName("links")]
        public List<EventLedgerLink> Links { get; set; } = [];
    }

    private sealed class EventLedgerLink
    {
        [JsonPropertyName("source")]
        public string Source { get; set; } = "";

        [JsonPropertyName("methodName")]
        public string MethodName { get; set; } = "";

        [JsonPropertyName("targetTypeName")]
        public string? TargetTypeName { get; set; }

        [JsonPropertyName("matched")]
        public bool Matched { get; set; }

        [JsonPropertyName("targetDocId")]
        public string? TargetDocId { get; set; }

        [JsonPropertyName("matchKind")]
        public string MatchKind { get; set; } = "";

        [JsonPropertyName("confidence")]
        public string Confidence { get; set; } = "";
    }
}
