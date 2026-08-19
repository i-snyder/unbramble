using UnBramble.Core;
using UnBramble.Core.Parsing;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// `.asmdef` `precompiledReferences` — a managed plugin assembly referenced by FILE NAME, with no
/// guid and no path anywhere in the serialized form. Regression: these weren't indexed at
/// all, so `who-uses SomePlugin.dll` under-reported to 0 referencers while two asmdefs named it.
/// The severity is worse than the under-report, and that's what these tests pin: a plugin DLL is
/// an ordinary `dead-candidates` candidate, so one referenced ONLY this way had no inbound edge
/// and could be emitted as `provenDead`.
/// </summary>
public class PrecompiledReferenceTests
{
    private const string PluginDllPath = "Assets/Plugins/Vendor.dll";

    [Fact]
    public void WhoUses_PluginDll_ReturnsTheReferencingAsmdef()
    {
        using var fixture = FixtureCopy.Create();
        AddPluginDll(fixture);
        AddAsmdef(fixture, "Assets/Plugins/Vendor.asmdef", "VendorBridge", ["Vendor.dll"]);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveQueryTarget(PluginDllPath);
        var answer = engine.WhoUses(resolution.Target!, transitive: false, depthCap: UnBrambleEngine.DefaultDepthCap);

        var edge = Assert.Single(answer.Results, r => r.Kind == "dll");
        Assert.Equal("Assets/Plugins/Vendor.asmdef", edge.SourcePath);
        Assert.Equal("Vendor.dll", edge.TargetKey);
        Assert.Equal("precompiled-reference", edge.RefKind);
        // Exactly one file in the project carries this name, so the name match is deterministic.
        Assert.Equal("proven", edge.ConfidenceLabel);
    }

    /// <summary>Two files sharing a name is the case Unity itself errors on; this tool can't tell
    /// which was meant, so every candidate is surfaced and the label is DOWNGRADED to advisory —
    /// never dropped to a single guess.</summary>
    [Fact]
    public void WhoUses_AmbiguousPluginName_SurfacesEveryCandidateAsAdvisory()
    {
        using var fixture = FixtureCopy.Create();
        AddPluginDll(fixture);
        AddPluginDll(fixture, "Assets/OtherPlugins/Vendor.dll", "beefbeefbeefbeefbeefbeefbeefbe02");
        AddAsmdef(fixture, "Assets/Plugins/Vendor.asmdef", "VendorBridge", ["Vendor.dll"]);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveQueryTarget(PluginDllPath);
        var answer = engine.WhoUses(resolution.Target!, transitive: false, depthCap: UnBrambleEngine.DefaultDepthCap);

        var edge = Assert.Single(answer.Results, r => r.Kind == "dll");
        Assert.Equal("advisory", edge.ConfidenceLabel);
    }

    [Fact]
    public void Uses_Asmdef_ListsThePrecompiledAssemblyAsADependency()
    {
        using var fixture = FixtureCopy.Create();
        AddPluginDll(fixture);
        AddAsmdef(fixture, "Assets/Plugins/Vendor.asmdef", "VendorBridge", ["Vendor.dll"]);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveQueryTarget("Assets/Plugins/Vendor.asmdef");
        var answer = engine.Uses(resolution.Target!, transitive: false, depthCap: UnBrambleEngine.DefaultDepthCap);

        var edge = Assert.Single(answer.Results, r => r.Kind == "dll");
        Assert.Equal(PluginDllPath, edge.TargetPath);
        Assert.True(edge.Resolved);
    }

    /// <summary>
    /// The reason the liveness seeding exists. The asmdef sits under `Assets/Resources/`, which
    /// makes it a liveness root by Unity's own rule, so the DLL it names must come out
    /// build-reachable. Without the seeded asmdef→DLL edge the DLL has no inbound edge of any
    /// kind and `dead-candidates` would be free to prove it dead — the false-positive class the
    /// asymmetric-risk invariant forbids.
    /// </summary>
    [Fact]
    public void BuildReachable_PluginDllNamedByALiveAsmdef_IsReachable()
    {
        using var fixture = FixtureCopy.Create();
        AddPluginDll(fixture);
        AddAsmdef(fixture, "Assets/Resources/Vendor.asmdef", "VendorBridge", ["Vendor.dll"]);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var reachable = engine.ComputeBuildReachablePaths();

        Assert.Contains(PluginDllPath, reachable);
    }

    /// <summary>A `precompiledReferences` entry naming a DLL that isn't in the project is real
    /// breakage (the asmdef won't compile) and has to reach `uses --missing-only` / `stats
    /// --unresolved` / the exit-3 CI check like any other broken ref.</summary>
    [Fact]
    public void UnresolvedPrecompiledReference_SurfacesAsABrokenRef()
    {
        using var fixture = FixtureCopy.Create();
        AddAsmdef(fixture, "Assets/Plugins/Vendor.asmdef", "VendorBridge", ["NotInThisProject.dll"]);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var unresolved = engine.GetUnresolvedRefs();

        Assert.Contains(unresolved, u => u.Kind == "dll" && u.TargetKey == "NotInThisProject.dll"
            && u.SourcePath == "Assets/Plugins/Vendor.asmdef");
    }

    [Fact]
    public void ResolvedPrecompiledReference_IsNotReportedAsBroken()
    {
        using var fixture = FixtureCopy.Create();
        AddPluginDll(fixture);
        AddAsmdef(fixture, "Assets/Plugins/Vendor.asmdef", "VendorBridge", ["Vendor.dll"]);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        Assert.DoesNotContain(engine.GetUnresolvedRefs(), u => u.Kind == "dll");
    }

    [Fact]
    public void Cli_WhoUses_PluginDll_TextNamesTheFieldAndTheReferencingAsmdef()
    {
        using var fixture = FixtureCopy.Create();
        AddPluginDll(fixture);
        AddAsmdef(fixture, "Assets/Plugins/Vendor.asmdef", "VendorBridge", ["Vendor.dll"]);
        Assert.Equal(0, CliRunner.Run("init", fixture.Root).ExitCode);

        var (exitCode, stdOut, _) = CliRunner.Run("who-uses", PluginDllPath, "-p", fixture.Root);

        Assert.Equal(0, exitCode);
        Assert.Contains("1 dll", stdOut, StringComparison.Ordinal);
        Assert.Contains("precompiledReferences → Vendor.dll", stdOut, StringComparison.Ordinal);
        Assert.Contains("Assets/Plugins/Vendor.asmdef", stdOut, StringComparison.Ordinal);
    }

    // ---- parser-level shapes -------------------------------------------------------------

    /// <summary>Unity writes one entry per line, but a hand-edited or tool-written asmdef can put
    /// the whole array on one line — both forms must yield the same rows, with the right line
    /// numbers.</summary>
    [Theory]
    // The array text starts on line 3 of the file written below. Single-line form: both entries
    // land there. Unity's one-per-line form: the '[' is line 3, so "B.dll" is line 5.
    [InlineData("\"precompiledReferences\": [\"A.dll\", \"B.dll\"],", 3)]
    [InlineData("\"precompiledReferences\": [\n\"A.dll\",\n\"B.dll\"\n],", 5)]
    public void Parser_PrecompiledReferences_BothArrayLayouts(string arrayText, int expectedLastLine)
    {
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Root, "X.asmdef");
        File.WriteAllText(path, "{\n\"name\": \"X\",\n" + arrayText + "\n\"autoReferenced\": true\n}");

        var parsed = new ReferenceParser().ParseContentSource(path, "Assets/X.asmdef", ownGuid: null);

        Assert.Equal(["a.dll", "b.dll"], parsed.DllRefs.Select(d => d.TargetNameNorm));
        Assert.Equal(["A.dll", "B.dll"], parsed.DllRefs.Select(d => d.TargetNameRaw));
        Assert.Equal(expectedLastLine, parsed.DllRefs[^1].Line);
    }

    /// <summary>The empty array is by far the most common form in real projects — it must yield
    /// nothing at all, and in particular must not capture the key name as a value.</summary>
    [Fact]
    public void Parser_EmptyPrecompiledReferences_YieldsNoRows()
    {
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Root, "X.asmdef");
        File.WriteAllText(path, "{\n\"name\": \"X\",\n\"precompiledReferences\": [],\n\"references\": [\"GUID:66666666666666666666666666666612\"]\n}");

        var parsed = new ReferenceParser().ParseContentSource(path, "Assets/X.asmdef", ownGuid: null);

        Assert.Empty(parsed.DllRefs);
        // The ordinary guid pass must be untouched by the second extraction.
        Assert.Single(parsed.GuidRefs);
    }

    /// <summary>Entries after the array closes must not be swept up — `"references"` values sit
    /// right next to it in every real asmdef.</summary>
    [Fact]
    public void Parser_StringsAfterTheArrayCloses_AreNotCaptured()
    {
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Root, "X.asmdef");
        File.WriteAllText(path, "{\n\"precompiledReferences\": [\"A.dll\"],\n\"defineConstraints\": [\"UNITY_ANDROID\"]\n}");

        var parsed = new ReferenceParser().ParseContentSource(path, "Assets/X.asmdef", ownGuid: null);

        Assert.Equal(["a.dll"], parsed.DllRefs.Select(d => d.TargetNameNorm));
    }

    // ---- fixture helpers -----------------------------------------------------------------

    private static void AddPluginDll(FixtureCopy fixture, string projectPath = PluginDllPath, string guid = "beefbeefbeefbeefbeefbeefbeefbe01")
    {
        var full = Path.Combine(fixture.Root, projectPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // Content is irrelevant: a binary asset participates in the graph via its .meta alone.
        File.WriteAllBytes(full, [0x4D, 0x5A]);
        File.WriteAllText(full + ".meta", $"fileFormatVersion: 2\nguid: {guid}\nPluginImporter:\n  externalObjects: {{}}\n  userData:\n");
    }

    private static void AddAsmdef(FixtureCopy fixture, string projectPath, string name, string[] precompiled)
    {
        var full = Path.Combine(fixture.Root, projectPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var entries = string.Join(",\n        ", precompiled.Select(p => $"\"{p}\""));
        File.WriteAllText(full, $$"""
            {
                "name": "{{name}}",
                "references": [],
                "precompiledReferences": [
                    {{entries}}
                ],
                "autoReferenced": true
            }
            """);
        File.WriteAllText(full + ".meta", "fileFormatVersion: 2\nguid: 77777777777777777777777777777799\nAssemblyDefinitionImporter:\n  externalObjects: {}\n  userData:\n");
    }
}
