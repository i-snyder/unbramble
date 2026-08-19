using System.Xml.Linq;
using UnBramble.Core;
using UnBramble.Core.CSharp;
using UnBramble.Core.Query;
using UnBramble.Core.Store;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// Real-usage feedback: on a partial-semantic project (some assemblies semantic, one
/// syntactic-only because its generated .csproj was stale/missing), a symbol who-uses on an
/// interface method returned 0 results even though the syntactic assembly held real call sites —
/// syntactic text-derived doc-ids can't join semantic Roslyn doc-ids. This covers the fix: naming
/// WHICH assemblies are syntactic and WHY (query footer/JSON/stats), an explicit
/// "possible false negative" signal, and a speculative name-match fallback that surfaces the
/// otherwise-invisible syntactic call sites as leads rather than silence.
///
/// Uses the same throwaway-semantic-csproj pattern as Milestone08a/c/eTests (duplicated locally,
/// not shared) so specific assemblies can be pinned semantic while others are left syntactic.
/// </summary>
public class SyntacticAttributionTests
{
    // ==== EdgeConfidence.AnswerLevel: speculative must never downgrade a real answer ============

    [Fact]
    public void AnswerLevel_ProvenAndSpeculative_StaysProven()
    {
        var results = new[] { Edge(EdgeConfidence.Proven), Edge(EdgeConfidence.Speculative) };
        Assert.Equal(EdgeConfidence.Proven, EdgeConfidence.AnswerLevel(results));
    }

    [Fact]
    public void AnswerLevel_AdvisoryAndSpeculative_StaysAdvisory()
    {
        var results = new[] { Edge(EdgeConfidence.Advisory), Edge(EdgeConfidence.Speculative) };
        Assert.Equal(EdgeConfidence.Advisory, EdgeConfidence.AnswerLevel(results));
    }

    [Fact]
    public void AnswerLevel_OnlySpeculative_BecomesSpeculative()
    {
        var results = new[] { Edge(EdgeConfidence.Speculative) };
        Assert.Equal(EdgeConfidence.Speculative, EdgeConfidence.AnswerLevel(results));
    }

    [Fact]
    public void AnswerLevel_OnlyUnresolved_IsNull()
    {
        var results = new[] { Edge(null) };
        Assert.Null(EdgeConfidence.AnswerLevel(results));
    }

    private static EdgeResult Edge(string? confidenceLabel) => new(
        "Assets/Scripts/Src.cs", null, "M:Src.Method", 1, "cs", 0,
        Resolved: true, Builtin: false, ClassId: null, GameObject: null, MethodName: null, Via: null,
        Confidence: null, TargetSymbol: "Src.Method", SourceSymbol: null, RefKind: "call",
        ConfidenceLabel: confidenceLabel);

    // ==== CsModeSelector: reason attribution ======================================================

    [Fact]
    public void CsModeSelector_NoCsprojOnDisk_ReasonIsNoCsproj()
    {
        using var dir = TempDir.Create();
        var decision = CsModeSelector.Determine(dir.Root, "Missing");
        Assert.Equal(CsAnalysisMode.Syntactic, decision.Mode);
        Assert.Equal(CsModeReasons.NoCsproj, decision.SyntacticReason);
    }

    [Fact]
    public void CsModeSelector_CsprojWithNoDefinesOrReferences_ReasonIsCsprojUnusable()
    {
        using var dir = TempDir.Create();
        var ns = XNamespace.Get("http://schemas.microsoft.com/developer/msbuild/2003");
        new XDocument(new XElement(ns + "Project", new XAttribute("ToolsVersion", "Current"))).Save(Path.Combine(dir.Root, "Empty.csproj"));

        var decision = CsModeSelector.Determine(dir.Root, "Empty");
        Assert.Equal(CsAnalysisMode.Syntactic, decision.Mode);
        Assert.Equal(CsModeReasons.CsprojUnusable, decision.SyntacticReason);
    }

    [Fact]
    public void CsModeSelector_MalformedCsprojXml_ReasonIsCsprojParseFailed()
    {
        using var dir = TempDir.Create();
        File.WriteAllText(Path.Combine(dir.Root, "Broken.csproj"), "<Project><Unclosed>");

        var decision = CsModeSelector.Determine(dir.Root, "Broken");
        Assert.Equal(CsAnalysisMode.Syntactic, decision.Mode);
        Assert.Equal(CsModeReasons.CsprojParseFailed, decision.SyntacticReason);
    }

    // ==== The real-usage repro: partial-semantic project, symbol who-uses on the semantic side ===

    /// <summary>
    /// Core is pinned semantic (has the interface, no local caller); Game is left syntactic (no
    /// generated csproj — the base fixture never has one). Game's call to the interface method is
    /// therefore recorded with a text-derived doc-id ("M:_greeter.Greet") that cannot join the
    /// resolved Roslyn doc-id ("M:IGreeter.Greet") — exactly the shape of the reported false
    /// negative. The fix must surface Game's call as a speculative lead instead of silence.
    /// </summary>
    [Fact]
    public void WhoUsesSymbol_PartialSemantic_SyntacticCallerSurfacedAsSpeculative_NotSilentZero()
    {
        using var fixture = FixtureCopy.Create();
        WriteScript(fixture.Root, "Assets/Scripts/Core", "IGreeter.cs", """
            public interface IGreeter
            {
                void Greet();
            }
            """, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        WriteScript(fixture.Root, "Assets/Scripts", "Caller.cs", """
            public class Caller
            {
                private IGreeter _greeter;

                public void DoThing()
                {
                    _greeter.Greet();
                }
            }
            """, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        WriteSemanticModeCsproj(fixture.Root, "Core");

        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveCsSymbol("IGreeter.Greet");
        Assert.NotNull(resolution.DocId);

        var answer = engine.WhoUsesSymbol(resolution.DocId!, transitive: false, depthCap: UnBrambleEngine.DefaultDepthCap);

        var edge = Assert.Single(answer.Results);
        Assert.Equal("cs", edge.Kind);
        Assert.Equal("Assets/Scripts/Caller.cs", edge.SourcePath);
        Assert.Equal(EdgeConfidence.Speculative, edge.ConfidenceLabel);

        Assert.Equal(EdgeConfidence.Speculative, answer.Confidence);
        Assert.True(answer.PossibleFalseNegative);
        Assert.Contains(BlindSpots.SyntacticAssembliesPresent, answer.BlindSpots);

        Assert.NotNull(answer.SyntacticAssemblies);
        Assert.Equal(1, answer.SyntacticAssemblies!.Total);
        var named = Assert.Single(answer.SyntacticAssemblies.Sample);
        Assert.Equal("Game", named.Name);
        Assert.Equal(CsModeReasons.NoCsproj, named.Reason);
    }

    /// <summary>
    /// A speculative lead must never drag a real answer's confidence down, and must not trigger
    /// the false-negative warning when a genuine proven caller already answers the query. Core
    /// and Game are both pinned semantic; a third assembly (Extra), deliberately left without a
    /// generated csproj, has an unrelated method that merely shares the identifier "Greet" — a
    /// lead, never proof, and must sit alongside the proven edge without affecting it.
    /// </summary>
    [Fact]
    public void WhoUsesSymbol_SpeculativeMatchAlongsideProvenEdge_DoesNotDowngradeOrFlagFalseNegative()
    {
        using var fixture = FixtureCopy.Create();
        WriteScript(fixture.Root, "Assets/Scripts/Core", "IGreeter.cs", """
            public interface IGreeter
            {
                void Greet();
            }
            """, "cccccccccccccccccccccccccccccccc");
        WriteScript(fixture.Root, "Assets/Scripts/Core", "RealCaller.cs", """
            public class RealCaller
            {
                private IGreeter _greeter;

                public void DoReal()
                {
                    _greeter.Greet();
                }
            }
            """, "dddddddddddddddddddddddddddddddd");
        WriteAsmdef(fixture.Root, "Assets/Scripts/Extra", "Extra", "77777777777777777777777777777712");
        WriteScript(fixture.Root, "Assets/Scripts/Extra", "Noise.cs", """
            public class Noise
            {
                public void Call()
                {
                    var g = GetSomething();
                    g.Greet();
                }

                private object GetSomething() => null;
            }
            """, "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        WriteSemanticModeCsproj(fixture.Root, "Core");
        WriteSemanticModeCsproj(fixture.Root, "Game");

        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveCsSymbol("IGreeter.Greet");
        Assert.NotNull(resolution.DocId);

        var answer = engine.WhoUsesSymbol(resolution.DocId!, transitive: false, depthCap: UnBrambleEngine.DefaultDepthCap);

        Assert.Contains(answer.Results, r => r.SourcePath == "Assets/Scripts/Core/RealCaller.cs" && r.ConfidenceLabel == EdgeConfidence.Proven);
        Assert.Contains(answer.Results, r => r.SourcePath == "Assets/Scripts/Extra/Noise.cs" && r.ConfidenceLabel == EdgeConfidence.Speculative);

        Assert.Equal(EdgeConfidence.Proven, answer.Confidence);
        Assert.False(answer.PossibleFalseNegative);

        Assert.NotNull(answer.SyntacticAssemblies);
        Assert.Equal(1, answer.SyntacticAssemblies!.Total);
        Assert.Equal("Extra", Assert.Single(answer.SyntacticAssemblies.Sample).Name);
    }

    /// <summary>Type-query variant: a syntactic `new Widget()` is recorded as a call to
    /// "M:Widget.#ctor", not as a "T:Widget" reference — the fallback must special-case
    /// constructor calls to match a type-kind query.</summary>
    [Fact]
    public void WhoUsesSymbol_TypeQuery_MatchesSyntacticConstructorCall()
    {
        using var fixture = FixtureCopy.Create();
        WriteScript(fixture.Root, "Assets/Scripts/Core", "Widget.cs", """
            public class Widget
            {
            }
            """, "11111111111111111111111111111111");
        WriteAsmdef(fixture.Root, "Assets/Scripts/Extra", "Extra", "77777777777777777777777777777712");
        WriteScript(fixture.Root, "Assets/Scripts/Extra", "Spawner.cs", """
            public class Spawner
            {
                public void Spawn()
                {
                    var w = new Widget();
                }
            }
            """, "22222222222222222222222222222222");
        WriteSemanticModeCsproj(fixture.Root, "Core");
        WriteSemanticModeCsproj(fixture.Root, "Game");

        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveCsSymbol("Widget");
        Assert.Equal("T:Widget", resolution.DocId);

        var answer = engine.WhoUsesSymbol(resolution.DocId!, transitive: false, depthCap: UnBrambleEngine.DefaultDepthCap);

        var edge = Assert.Single(answer.Results);
        Assert.Equal("Assets/Scripts/Extra/Spawner.cs", edge.SourcePath);
        Assert.Equal(EdgeConfidence.Speculative, edge.ConfidenceLabel);
        Assert.Equal("Widget.#ctor", edge.TargetSymbol);
        Assert.True(answer.PossibleFalseNegative);
    }

    // ==== CLI surfaces: footer attribution, false-negative warning, JSON, stats ==================

    [Fact]
    public void Cli_WhoUsesSymbol_PartialSemantic_TextFooter_NamesAssemblyAndWarnsFalseNegative()
    {
        using var fixture = FixtureCopy.Create();
        WriteScript(fixture.Root, "Assets/Scripts/Core", "IGreeter.cs", """
            public interface IGreeter
            {
                void Greet();
            }
            """, "33333333333333333333333333333333");
        WriteScript(fixture.Root, "Assets/Scripts", "Caller.cs", """
            public class Caller
            {
                private IGreeter _greeter;

                public void DoThing()
                {
                    _greeter.Greet();
                }
            }
            """, "44444444444444444444444444444444");
        WriteSemanticModeCsproj(fixture.Root, "Core");

        _ = CliRunner.Run("init", "-p", fixture.Root);
        var (exitCode, stdOut, _) = CliRunner.Run("who-uses", "IGreeter.Greet", "-p", fixture.Root, "--symbol");

        Assert.Equal(0, exitCode);
        Assert.Contains("speculative (name-match in syntactic assemblies):", stdOut);
        Assert.Contains("Assets/Scripts/Caller.cs", stdOut);
        Assert.Contains("syntactic assemblies: Game (no generated .csproj)", stdOut);
        Assert.Contains("open the project in the Unity Editor once (or open a .cs file in your IDE while Unity is running) to regenerate .csproj files, then re-index", stdOut);
        Assert.Contains("warning: no proven caller was found", stdOut);
    }

    [Fact]
    public void Cli_WhoUsesSymbol_PartialSemantic_Json_CarriesAttributionAndFalseNegativeFlag()
    {
        using var fixture = FixtureCopy.Create();
        WriteScript(fixture.Root, "Assets/Scripts/Core", "IGreeter.cs", """
            public interface IGreeter
            {
                void Greet();
            }
            """, "55555555555555555555555555555555");
        WriteScript(fixture.Root, "Assets/Scripts", "Caller.cs", """
            public class Caller
            {
                private IGreeter _greeter;

                public void DoThing()
                {
                    _greeter.Greet();
                }
            }
            """, "66666666666666666666666666666699");
        WriteSemanticModeCsproj(fixture.Root, "Core");

        _ = CliRunner.Run("init", "-p", fixture.Root);
        var (exitCode, stdOut, _) = CliRunner.Run("who-uses", "IGreeter.Greet", "-p", fixture.Root, "--symbol", "--json");

        Assert.Equal(0, exitCode);
        Assert.Contains("\"possibleFalseNegative\":true", stdOut);
        Assert.Contains("\"confidence\":\"speculative\"", stdOut);
        Assert.Contains("\"syntacticAssemblies\":{\"total\":1,\"assemblies\":[{\"name\":\"Game\",\"reason\":\"no-csproj\"}]", stdOut);
    }

    [Fact]
    public void Cli_Stats_ListsSyntacticAssembliesByNameWithReason()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsproj(fixture.Root, "Core");
        // Game deliberately left without a generated csproj.

        var (initExit, _, _) = CliRunner.Run("init", fixture.Root);
        Assert.Equal(0, initExit);

        var (exitCode, stdOut, _) = CliRunner.Run("stats", "-p", fixture.Root);
        Assert.Equal(0, exitCode);
        Assert.Contains("Syntactic assemblies (1/2): Game (no generated .csproj)", stdOut);

        var (jsonExit, jsonOut, _) = CliRunner.Run("stats", "-p", fixture.Root, "--json");
        Assert.Equal(0, jsonExit);
        Assert.Contains("\"syntacticAssemblies\":[{\"name\":\"Game\",\"reason\":\"no-csproj\"}]", jsonOut);
    }

    /// <summary>
    /// Real-usage feedback (a package installed under Packages/, no generated .csproj even after
    /// reopening the project in Unity): "open Unity once" is the wrong remediation for a
    /// package-sourced asmdef, since Unity's csproj auto-regeneration for packages is gated by a
    /// separate, unchecked-by-default Preferences setting. Both `stats` and the query footer
    /// should swap in the package-specific hint instead of the generic one whenever the syntactic
    /// assembly's asmdef lives under Packages/ (or LocalPackages/) -- as long as Unity actually
    /// compiled it at some point (a compiled DLL under Library/ScriptAssemblies/ is the signal
    /// that distinguishes this ordinary case from the stronger orphaned/broken diagnoses below).
    /// </summary>
    [Fact]
    public void Cli_Stats_PackageSourcedSyntacticAssembly_ShowsPackageSpecificRemediationHint()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsproj(fixture.Root, "Core");
        WriteSemanticModeCsproj(fixture.Root, "Game");
        WriteAsmdef(fixture.Root, "Packages/com.vendor.tool", "com.vendor.tool", "88888888888888888888888888888888");
        WriteScript(fixture.Root, "Packages/com.vendor.tool", "VendorTool.cs", """
            public class VendorTool
            {
            }
            """, "99999999999999999999999999999999");
        // com.vendor.tool deliberately left without a generated csproj.
        WriteCompiledMarkerDll(fixture.Root, "com.vendor.tool");

        var (initExit, _, _) = CliRunner.Run("init", fixture.Root);
        Assert.Equal(0, initExit);

        var (exitCode, stdOut, _) = CliRunner.Run("stats", "-p", fixture.Root);
        Assert.Equal(0, exitCode);
        Assert.Contains("Syntactic assemblies (1/3): com.vendor.tool (no generated .csproj)", stdOut);
        Assert.Contains("Preferences > External Tools", stdOut);
        Assert.Contains("Local/Embedded/Registry/Git packages", stdOut);
        // The generic "open Unity once" hint alone (with no package caveat) must NOT be the one
        // printed here -- it would send the user chasing a fix that can't work for a package.
        Assert.DoesNotContain("to regenerate .csproj files, then re-index", stdOut);
        // Neither of the stronger never-compiled diagnoses apply -- Unity DID compile this one.
        Assert.DoesNotContain("looks orphaned", stdOut);
        Assert.DoesNotContain("looks broken", stdOut);
    }

    /// <summary>
    /// Real-usage feedback (the CC3 Unity Tools case): a package dropped under LocalPackages/ but
    /// never added to Packages/manifest.json is invisible to Unity's Package Manager -- no csproj
    /// checkbox or reopen will ever fix it, because Unity never compiles it at all (no DLL under
    /// Library/ScriptAssemblies/). Combined with zero references anywhere else in the project, the
    /// CLI should say so plainly ("looks orphaned") instead of repeating the routine package hint,
    /// which would send the user chasing a fix that structurally cannot work.
    /// </summary>
    [Fact]
    public void Cli_Stats_NeverCompiledPackageWithNoReferencers_LooksOrphaned()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsproj(fixture.Root, "Core");
        WriteSemanticModeCsproj(fixture.Root, "Game");
        WriteAsmdef(fixture.Root, "LocalPackages/OrphanedTool", "com.orphaned.tool", "aa111111111111111111111111111111");
        WriteScript(fixture.Root, "LocalPackages/OrphanedTool", "OrphanedTool.cs", """
            public class OrphanedTool
            {
            }
            """, "bb111111111111111111111111111111");
        // No Library/ScriptAssemblies/com.orphaned.tool.dll (Unity never compiled it) and nothing
        // elsewhere in Assets/ references OrphanedTool.cs.

        var (initExit, _, _) = CliRunner.Run("init", fixture.Root);
        Assert.Equal(0, initExit);

        var (exitCode, stdOut, _) = CliRunner.Run("stats", "-p", fixture.Root);
        Assert.Equal(0, exitCode);
        Assert.Contains("looks orphaned", stdOut);
        Assert.Contains("com.orphaned.tool", stdOut);
        Assert.Contains("never compiled by Unity", stdOut);
        Assert.Contains("0 references found anywhere in the project", stdOut);
        Assert.DoesNotContain("looks broken", stdOut);
    }

    /// <summary>
    /// Same never-compiled package, but this time something in Assets/ still references one of
    /// its scripts by guid (e.g. a MonoBehaviour on a prefab/asset). That's the opposite
    /// conclusion from the orphaned case above: not dead code, but a currently-BROKEN dependency
    /// (a missing-script reference in the Unity Editor) -- the CLI must not tell the user to
    /// delete something that's still actively (if brokenly) depended on.
    /// </summary>
    [Fact]
    public void Cli_Stats_NeverCompiledPackageWithReferencers_LooksBroken()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsproj(fixture.Root, "Core");
        WriteSemanticModeCsproj(fixture.Root, "Game");
        WriteAsmdef(fixture.Root, "LocalPackages/BrokenTool", "com.broken.tool", "cc111111111111111111111111111111");
        WriteScript(fixture.Root, "LocalPackages/BrokenTool", "BrokenTool.cs", """
            public class BrokenTool
            {
            }
            """, "dd111111111111111111111111111111");
        WriteAssetReferencingScript(fixture.Root, "Assets/Data", "UsesBrokenTool.asset", "dd111111111111111111111111111111");
        // No Library/ScriptAssemblies/com.broken.tool.dll -- Unity never compiled it -- but the
        // asset above still points at its script by guid.

        var (initExit, _, _) = CliRunner.Run("init", fixture.Root);
        Assert.Equal(0, initExit);

        var (exitCode, stdOut, _) = CliRunner.Run("stats", "-p", fixture.Root);
        Assert.Equal(0, exitCode);
        Assert.Contains("looks broken", stdOut);
        Assert.Contains("com.broken.tool", stdOut);
        Assert.Contains("still referenced elsewhere in the project", stdOut);
        Assert.DoesNotContain("looks orphaned", stdOut);
    }

    // ---- helpers (same throwaway-csproj pattern as Milestone08a/c/eTests, duplicated locally) ----

    /// <summary>A zero-byte stand-in for a real Unity-compiled DLL: NeverCompiledByUnity only
    /// ever checks <see cref="File.Exists(string)"/> on Library/ScriptAssemblies/&lt;name&gt;.dll,
    /// never its contents.</summary>
    private static void WriteCompiledMarkerDll(string fixtureRoot, string assemblyName)
    {
        var dir = Path.Combine(fixtureRoot, "Library", "ScriptAssemblies");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, assemblyName + ".dll"), []);
    }

    private static void WriteAssetReferencingScript(string fixtureRoot, string relativeDir, string fileName, string scriptGuid)
    {
        var dir = Path.Combine(fixtureRoot, relativeDir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, $$"""
            %YAML 1.1
            %TAG !u! tag:unity3d.com,2011:
            --- !u!114 &11400000
            MonoBehaviour:
              m_ObjectHideFlags: 0
              m_CorrespondingSourceObject: {fileID: 0}
              m_PrefabInstance: {fileID: 0}
              m_PrefabAsset: {fileID: 0}
              m_GameObject: {fileID: 0}
              m_Enabled: 1
              m_EditorHideFlags: 0
              m_Script: {fileID: 11500000, guid: {{scriptGuid}}, type: 3}
              m_Name: {{Path.GetFileNameWithoutExtension(fileName)}}
              m_EditorClassIdentifier:
            """);
        File.WriteAllText(path + ".meta", $$"""
            fileFormatVersion: 2
            guid: ee111111111111111111111111111111
            NativeFormatImporter:
              externalObjects: {}
              mainObjectFileID: 11400000
              userData:
              assetBundleName:
              assetBundleVariant:
            """);
    }

    private static void WriteScript(string fixtureRoot, string relativeDir, string fileName, string source, string guid)
    {
        var dir = Path.Combine(fixtureRoot, relativeDir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, source);
        File.WriteAllText(path + ".meta", $$"""
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
    }

    private static void WriteAsmdef(string fixtureRoot, string relativeDir, string name, string guid)
    {
        var dir = Path.Combine(fixtureRoot, relativeDir);
        Directory.CreateDirectory(dir);
        var asmdefPath = Path.Combine(dir, name + ".asmdef");
        File.WriteAllText(asmdefPath, $$"""
            {
                "name": "{{name}}",
                "rootNamespace": "",
                "references": [],
                "includePlatforms": [],
                "excludePlatforms": [],
                "allowUnsafeCode": false,
                "overrideReferences": false,
                "precompiledReferences": [],
                "autoReferenced": true,
                "defineConstraints": [],
                "versionDefines": [],
                "noEngineReferences": false
            }
            """);
        File.WriteAllText(asmdefPath + ".meta", $$"""
            fileFormatVersion: 2
            guid: {{guid}}
            AssemblyDefinitionImporter:
              externalObjects: {}
              userData:
              assetBundleName:
              assetBundleVariant:
            """);
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
