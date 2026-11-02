using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UnBramble.Core;
using UnBramble.Core.CSharp;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

public class CsProjectReferenceTests
{
    [Theory]
    [InlineData("")]
    [InlineData("http://schemas.microsoft.com/developer/msbuild/2003")]
    public void GeneratedProject_ReadsProjectReferenceNamesWithoutEvaluatingTargets(string xmlNamespace)
    {
        using var directory = TempDir.Create();
        var ns = XNamespace.Get(xmlNamespace);
        var marker = Path.Combine(directory.Root, "must-not-exist.txt");
        var project = new XElement(ns + "Project",
            new XElement(ns + "ItemGroup",
                new XElement(ns + "ProjectReference", new XAttribute("Include", @"..\Projects\Bridge.Events.csproj")),
                new XElement(ns + "ProjectReference", new XAttribute("Include", "../Projects/Bridge.Events.csproj")),
                new XElement(ns + "ProjectReference", new XAttribute("Include", "Tools.csproj"), new XElement(ns + "ReferenceOutputAssembly", "false"))),
            new XElement(ns + "Target", new XAttribute("Name", "BeforeBuild"),
                new XElement(ns + "WriteLinesToFile", new XAttribute("File", marker), new XAttribute("Lines", "executed"))));
        new XDocument(project).Save(Path.Combine(directory.Root, "Assembly-CSharp.csproj"));

        var decision = CsModeSelector.Determine(directory.Root, "Assembly-CSharp");

        Assert.Equal(CsAnalysisMode.Semantic, decision.Mode);
        Assert.Equal(["Bridge.Events"], decision.ProjectReferenceNames);
        Assert.Empty(decision.MetadataReferencePaths);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void EffectiveReferences_OverlayGeneratedEdgesWithoutChangingCachedDiscovery()
    {
        var discovered = new AssemblyUnit("Assembly-CSharp", null, null, ["Core"], [], false);
        var mode = new CsModeDecision(CsAnalysisMode.Semantic, [], [], ProjectReferenceNames: ["Bridge", "Core"]);

        var effective = AssemblyUnitDiscovery.WithProjectReferences(discovered, mode);

        Assert.Equal(["Core"], discovered.References);
        Assert.Equal(["Bridge", "Core"], effective.References);
        var (ordered, warnings) = AssemblyUnitDiscovery.TopologicalOrder([
            effective,
            new AssemblyUnit("Bridge", 1, "Bridge.asmdef", [], [], false),
            new AssemblyUnit("Core", 2, "Core.asmdef", [], [], false),
        ]);
        Assert.Empty(warnings);
        Assert.Equal("Assembly-CSharp", ordered[^1].Name);
    }

    [Fact]
    public void ModeCache_ProjectReferenceChangesRefreshWithoutDiscoveryChanges()
    {
        using var directory = TempDir.Create();
        var path = Path.Combine(directory.Root, "Assembly-CSharp.csproj");
        File.WriteAllText(path, "<Project><ItemGroup><ProjectReference Include=\"Bridge.csproj\" /></ItemGroup></Project>");
        var cache = new CsSessionModelCache();
        var first = cache.GetModeDecision(directory.Root, "Assembly-CSharp");
        Assert.Equal(["Bridge"], first.ProjectReferenceNames);
        Assert.Same(first, cache.GetModeDecision(directory.Root, "Assembly-CSharp"));

        File.WriteAllText(path, "<Project><PropertyGroup><DefineConstants>UNITY_EDITOR</DefineConstants></PropertyGroup></Project>");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(10));
        var changed = cache.GetModeDecision(directory.Root, "Assembly-CSharp");

        Assert.NotSame(first, changed);
        Assert.Empty(changed.ProjectReferenceNames!);
        Assert.Equal(["UNITY_EDITOR"], changed.Defines);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedProject_UnsafeCompilerOptionControlsBindingHealth(bool allowUnsafe)
    {
        using var directory = TempDir.Create();
        WriteGeneratedProject(directory.Root, "UnsafeAssembly", []);
        var projectPath = Path.Combine(directory.Root, "UnsafeAssembly.csproj");
        var project = XDocument.Load(projectPath);
        project.Root!.Add(new XElement("PropertyGroup", new XElement("AllowUnsafeBlocks", allowUnsafe ? "True" : "False")));
        project.Save(projectPath);
        var sourcePath = Path.Combine(directory.Root, "UnsafeUse.cs");
        File.WriteAllText(sourcePath, "public unsafe class UnsafeUse { public static int Read(int* pointer) => *pointer; }");
        var unit = new AssemblyUnit("UnsafeAssembly", null, null, [], [(1, "UnsafeUse.cs")], false);
        var mode = CsModeSelector.Determine(directory.Root, unit.Name);

        var (compilation, _) = CsProjectAnalyzer.BuildCompilation(unit, mode, new Dictionary<string, CSharpCompilation>(), _ => sourcePath);
        var health = CsCompilationHealth.Capture(compilation);

        Assert.Equal(allowUnsafe, mode.AllowUnsafeBlocks);
        Assert.Equal(allowUnsafe, compilation.Options.AllowUnsafe);
        if (allowUnsafe)
        {
            Assert.Equal(0, health.ErrorCount);
        }
        else
        {
            Assert.Contains("CS0227", health.ErrorCodes);
        }

        Assert.NotEqual(CsCompilationCache.ComputeUnitShapeHash(unit, mode), CsCompilationCache.ComputeUnitShapeHash(unit, mode with { AllowUnsafeBlocks = !allowUnsafe }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceCompilation_ReplacesStaleDllByAssemblyIdentity(bool watchCache)
    {
        using var directory = TempDir.Create();
        // The filename intentionally differs from the metadata assembly name.
        var stalePath = EmitAssembly(directory.Root, "renamed-legacy.dll", "public static class EventBridge { public static event System.Action Old; }");
        var publisher = Compilation("Bridge", "public static class EventBridge { public static event System.Action Removed; }");
        var source = Path.Combine(directory.Root, "Subscriber.cs");
        File.WriteAllText(source, "public class Subscriber { void Use() { EventBridge.Removed += Handle; EventBridge.Removed -= Handle; } void Handle() {} }");
        var unit = new AssemblyUnit("Assembly-CSharp", null, null, ["Bridge"], [(1, "Subscriber.cs")], false);
        var mode = new CsModeDecision(CsAnalysisMode.Semantic, [], [.. BclPaths(), stalePath], ProjectReferenceNames: ["Bridge"], ExternalProjectReferenceNames: ["Bridge"]);
        var dependencies = new Dictionary<string, CSharpCompilation> { ["Bridge"] = publisher };
        var (compilation, treeMap) = Build(unit, mode, dependencies, _ => source, watchCache);

        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(compilation.References.OfType<PortableExecutableReference>(), r => r.FilePath == stalePath);
        var (_, refs, _) = SemanticCsExtractor.Extract(compilation, treeMap);
        Assert.Equal(2, refs.Count(r => r.TargetDocId == "E:EventBridge.Removed" && r.RefKind == "member-access" && r.Confidence == "semantic"));
    }

    [Fact]
    public void MissingProjectSource_DoesNotSilentlyUseItsStaleDll()
    {
        using var directory = TempDir.Create();
        var stalePath = EmitAssembly(directory.Root, "Bridge.dll", "public static class EventBridge { public static event System.Action Removed; }");
        var source = Path.Combine(directory.Root, "Subscriber.cs");
        File.WriteAllText(source, "public class Subscriber { void Use() { EventBridge.Removed += Handle; } void Handle() {} }");
        var unit = new AssemblyUnit("Assembly-CSharp", null, null, ["Bridge"], [(1, "Subscriber.cs")], false);
        var mode = new CsModeDecision(CsAnalysisMode.Semantic, [], [.. BclPaths(), stalePath], ProjectReferenceNames: ["Bridge"]);

        var (compilation, treeMap) = CsProjectAnalyzer.BuildCompilation(unit, mode, new Dictionary<string, CSharpCompilation>(), _ => source);

        Assert.Contains(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        var (_, refs, _) = SemanticCsExtractor.Extract(compilation, treeMap);
        Assert.DoesNotContain(refs, r => r.TargetDocId == "E:EventBridge.Removed" && r.Confidence == "semantic");
    }

    [Fact]
    public void WatchReferenceSwap_UnavailableSourceRemovesOldCompilationReference()
    {
        var publisher = Compilation("Bridge", "public class BridgeType {}");
        var oldReference = publisher.ToMetadataReference();
        var subscriber = Compilation("Assembly-CSharp", "public class Subscriber { BridgeType value; }").AddReferences(oldReference);

        var (changed, refs) = CsCompilationCache.ApplyDependencyReferenceSwaps(
            subscriber,
            new Dictionary<string, CompilationReference> { ["Bridge"] = oldReference },
            ["Bridge"],
            new Dictionary<string, CSharpCompilation>());

        Assert.Empty(refs);
        Assert.DoesNotContain(oldReference, changed.References);
        Assert.Contains(changed.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error && d.Id == "CS0246");
    }

    [Fact]
    public void RemovedAndRecreatedAssembly_WatchCacheUsesSourceEditedUnderAnotherOwner()
    {
        using var directory = TempDir.Create();
        void Write(string relative, string content)
        {
            var path = Path.Combine(directory.Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        Write("ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 2022.3.30f1\n");
        Write("ProjectSettings/EditorSettings.asset", "%YAML 1.1\n--- !u!159 &1\nEditorSettings:\n  m_SerializationMode: 2\n");
        Write("Packages/manifest.json", "{\"dependencies\":{}}");
        const string asmdef = "Assets/Bridge/Bridge.asmdef";
        const string publisher = "Assets/Bridge/Publisher.cs";
        const string subscriber = "Assets/Scripts/Subscriber.cs";
        Write(asmdef, "{\"name\":\"Bridge\"}");
        Write(asmdef + ".meta", "fileFormatVersion: 2\nguid: 11111111111111111111111111111111\n");
        Write(publisher, "public static class EventSource { public static event System.Action Removed; }");
        Write(publisher + ".meta", "fileFormatVersion: 2\nguid: 22222222222222222222222222222222\n");
        Write(subscriber, "public class Subscriber { void Use() { EventSource.Removed += Handle; EventSource.Removed -= Handle; } void Handle() {} }");
        Write(subscriber + ".meta", "fileFormatVersion: 2\nguid: 33333333333333333333333333333333\n");
        foreach (var assembly in new[] { "Bridge", "Assembly-CSharp" })
        {
            var references = new XElement("ItemGroup", BclPaths().Select(path =>
                new XElement("Reference", new XAttribute("Include", Path.GetFileNameWithoutExtension(path)), new XElement("HintPath", path))));
            if (assembly == "Assembly-CSharp")
            {
                references.Add(new XElement("ProjectReference", new XAttribute("Include", "Bridge.csproj")));
            }

            new XDocument(new XElement("Project", references)).Save(Path.Combine(directory.Root, assembly + ".csproj"));
        }

        using var engine = UnBrambleEngine.Open(directory.Root);
        engine.EnableWatchCompilationCache();
        engine.RunIndex(full: false);
        Assert.Equal(2, engine.GetCsRefs("E:EventSource.Removed").Count);

        File.Delete(Path.Combine(directory.Root, asmdef));
        File.Delete(Path.Combine(directory.Root, asmdef + ".meta"));
        engine.RunIndex(full: false);
        Write(publisher, "public static class EventSource { public static event System.Action Gone; }");
        Write(subscriber, "public class Subscriber { void Use() { EventSource.Gone += Handle; EventSource.Gone -= Handle; } void Handle() {} }");
        engine.RunIndex(full: false);

        // Restore the same assembly while its generated project fingerprint stays unchanged.
        // Regenerate only the subscriber project to isolate reuse of the removed publisher.
        Write(asmdef, "{\"name\":\"Bridge\"}");
        Write(asmdef + ".meta", "fileFormatVersion: 2\nguid: 11111111111111111111111111111111\n");
        var subscriberProject = Path.Combine(directory.Root, "Assembly-CSharp.csproj");
        File.SetLastWriteTimeUtc(subscriberProject, File.GetLastWriteTimeUtc(subscriberProject).AddSeconds(10));
        engine.RunIndex(full: false);

        Assert.Empty(engine.GetCsBindingFailures());
        Assert.Empty(engine.GetCsRefs("E:EventSource.Removed"));
        Assert.Equal(2, engine.GetCsRefs("E:EventSource.Gone").Count);
    }

    [Fact]
    public void SourceDependencyAvailability_WatchCacheMatchesFreshDllAndSourceSelection()
    {
        using var directory = TempDir.Create();
        WriteUnitySettings(directory.Root);
        WriteAsset(directory.Root, "Assets/Bridge/Bridge.asmdef", "{\"name\":\"Bridge\"}", "11111111111111111111111111111111");
        WriteAsset(directory.Root, "Assets/Bridge/Publisher.cs", "public static class EventSource { public static event System.Action Removed; }", "22222222222222222222222222222222");
        WriteAsset(directory.Root, "Assets/Consumer/Consumer.asmdef", "{\"name\":\"Consumer\",\"references\":[\"Bridge\"]}", "33333333333333333333333333333333");
        WriteAsset(directory.Root, "Assets/Consumer/Subscriber.cs", "public class Subscriber { void Use() { EventSource.Removed += Handle; EventSource.Removed -= Handle; } void Handle() {} }", "44444444444444444444444444444444");
        var staleDll = EmitAssembly(directory.Root, "Bridge.dll", "public static class EventSource { public static event System.Action Removed; }");
        WriteGeneratedProject(directory.Root, "Consumer", [staleDll]);

        using var engine = UnBrambleEngine.Open(directory.Root);
        engine.EnableWatchCompilationCache();
        engine.RunIndex(full: false);
        Assert.Empty(engine.GetCsBindingFailures());
        Assert.Equal(2, engine.GetCsRefs("E:EventSource.Removed").Count);

        // No consumer input changes. Its raw asmdef edge now has a source compilation, so
        // a cached DLL reference must be replaced just as a fresh build would replace it.
        WriteGeneratedProject(directory.Root, "Bridge", []);
        engine.RunIndex(full: false);
        Assert.Empty(engine.GetCsBindingFailures());
        Assert.Equal(2, engine.GetCsRefs("E:EventSource.Removed").Count);

        // In the opposite transition, this generated project's DLL-only representation is
        // usable again. It must be restored rather than leave an unresolved cached reference.
        File.Delete(Path.Combine(directory.Root, "Bridge.csproj"));
        engine.RunIndex(full: false);
        Assert.Empty(engine.GetCsBindingFailures());
        Assert.Equal(2, engine.GetCsRefs("E:EventSource.Removed").Count);
    }

    [Fact]
    public void AsmrefOwnershipChange_WatchCacheRebindsUnchangedScriptsInExistingUnits()
    {
        using var directory = TempDir.Create();
        WriteUnitySettings(directory.Root);
        WriteAsset(directory.Root, "Assets/A/A.asmdef", "{\"name\":\"A\"}", "11111111111111111111111111111111");
        WriteAsset(directory.Root, "Assets/A/Anchor.cs", "public class AnchorA {}", "22222222222222222222222222222222");
        WriteAsset(directory.Root, "Assets/B/B.asmdef", "{\"name\":\"B\"}", "33333333333333333333333333333333");
        WriteAsset(directory.Root, "Assets/B/Anchor.cs", "public class AnchorB {}", "44444444444444444444444444444444");
        WriteAsset(directory.Root, "Assets/Shared/Shared.asmref", "{\"reference\":\"A\"}", "55555555555555555555555555555555");
        WriteAsset(directory.Root, "Assets/Shared/Moved.cs", "public static class Moved { public static void Touch() {} }", "66666666666666666666666666666666");
        WriteAsset(directory.Root, "Assets/Consumer/Consumer.asmdef", "{\"name\":\"Consumer\",\"references\":[\"A\"]}", "77777777777777777777777777777777");
        WriteAsset(directory.Root, "Assets/Consumer/Subscriber.cs", "public class Subscriber { void Use() { Moved.Touch(); } }", "88888888888888888888888888888888");
        foreach (var assembly in new[] { "A", "B", "Consumer" })
        {
            WriteGeneratedProject(directory.Root, assembly, []);
        }

        using var engine = UnBrambleEngine.Open(directory.Root);
        engine.EnableWatchCompilationCache();
        engine.RunIndex(full: false);
        Assert.Empty(engine.GetCsBindingFailures());
        Assert.Contains(engine.GetCsRefs("M:Moved.Touch"), r => r.Confidence == "semantic");

        var asmrefPath = Path.Combine(directory.Root, "Assets/Shared/Shared.asmref");
        File.WriteAllText(asmrefPath, "{\"reference\":\"B\"}");
        File.SetLastWriteTimeUtc(asmrefPath, File.GetLastWriteTimeUtc(asmrefPath).AddSeconds(5));
        engine.RunIndex(full: false);

        Assert.Contains(engine.GetCsBindingFailures(), f => f.AssemblyName == "Consumer");
        Assert.DoesNotContain(engine.GetCsRefs("M:Moved.Touch"), r => r.Confidence == "semantic");
    }

    private static void WriteUnitySettings(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        Directory.CreateDirectory(Path.Combine(root, "Packages"));
        File.WriteAllText(Path.Combine(root, "ProjectSettings/ProjectVersion.txt"), "m_EditorVersion: 2022.3.30f1\n");
        File.WriteAllText(Path.Combine(root, "ProjectSettings/EditorSettings.asset"), "%YAML 1.1\n--- !u!159 &1\nEditorSettings:\n  m_SerializationMode: 2\n");
        File.WriteAllText(Path.Combine(root, "Packages/manifest.json"), "{\"dependencies\":{}}");
    }

    private static void WriteAsset(string root, string relative, string content, string guid)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.WriteAllText(path + ".meta", $"fileFormatVersion: 2\nguid: {guid}\n");
    }

    private static void WriteGeneratedProject(string root, string name, IReadOnlyList<string> additionalReferences)
    {
        var references = new XElement("ItemGroup", BclPaths().Concat(additionalReferences).Select(path =>
            new XElement("Reference", new XAttribute("Include", Path.GetFileNameWithoutExtension(path)), new XElement("HintPath", path))));
        new XDocument(new XElement("Project", references)).Save(Path.Combine(root, name + ".csproj"));
    }

    private static (CSharpCompilation Compilation, Dictionary<SyntaxTree, long> Trees) Build(
        AssemblyUnit unit,
        CsModeDecision mode,
        Dictionary<string, CSharpCompilation> dependencies,
        Func<string, string> fullPath,
        bool watchCache)
    {
        if (watchCache)
        {
            var built = CsProjectAnalyzer.BuildCompilationForCache(unit, mode, dependencies, fullPath, new CsCompilationCache());
            return (built.Compilation, built.FileIdByTree);
        }

        return CsProjectAnalyzer.BuildCompilation(unit, mode, dependencies, fullPath);
    }

    private static IReadOnlyList<string> BclPaths() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);

    private static CSharpCompilation Compilation(string name, string source) =>
        CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], BclPaths().Select(p => MetadataReference.CreateFromFile(p)), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    private static string EmitAssembly(string root, string filename, string source)
    {
        var path = Path.Combine(root, filename);
        var result = Compilation("Bridge", source).Emit(path);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        return path;
    }
}
