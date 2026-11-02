using System.Collections.Concurrent;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UnBramble.Core.CSharp;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

public class CsProjectReferenceResolverTests
{
    [Theory]
    [InlineData("Package\t.Events.csproj")]
    [InlineData("Package\n.Events.csproj")]
    public void DelimiterCharactersInProjectInclude_AreRejected(string include)
    {
        using var directory = TempDir.Create();
        var result = CsProjectReferenceResolver.Resolve(directory.Root, [include]);

        Assert.Null(result.Inputs);
        Assert.Empty(result.MetadataPaths);
    }

    [Fact]
    public void SharedInputCapture_DeduplicatesCaseVariantWindowsPaths()
    {
        using var directory = TempDir.Create();
        var path = Path.Combine(directory.Root, "Input.csproj");
        File.WriteAllText(path, "<Project />");
        var snapshot = CsProjectReferenceInputs.CaptureFile(path);
        var captures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Assert.True(CsProjectReferenceInputs.Matches(snapshot, captures));

        var caseVariant = path.ToUpperInvariant() + snapshot[snapshot.IndexOf('\t')..];
        Assert.True(CsProjectReferenceInputs.Matches(caseVariant, captures));
        Assert.Single(captures);
    }

    [Fact]
    public void MissingProjectAndDll_AreTrackedBeforeEitherExists()
    {
        using var directory = TempDir.Create();
        var result = CsProjectReferenceResolver.Resolve(directory.Root, ["Package.Events.csproj"]);
        var target = Path.Combine(directory.Root, "Package.Events.csproj");
        var dll = Path.Combine(directory.Root, "Library", "ScriptAssemblies", "Package.Events.dll");

        Assert.Empty(result.MetadataPaths);
        Assert.Contains(target, result.Inputs);
        Assert.Contains(dll, result.Inputs);
        Assert.True(CsProjectReferenceInputs.Matches(result.Inputs));

        File.WriteAllText(target, "<Project />");
        Assert.False(CsProjectReferenceInputs.Matches(result.Inputs));
    }

    [Theory]
    [InlineData("Assets/Events.cs")]
    [InlineData("Library/PackageCache/../Outside/Events.cs")]
    public void MixedOrEscapingCompileInputs_NeverBecomeExternalMetadata(string secondSource)
    {
        using var directory = TempDir.Create();
        WritePackageProject(directory.Root, ["Library/PackageCache/com.example.events@1.0.0/Events.cs", secondSource]);
        EmitPackageDll(directory.Root, "Package.Events");

        var result = CsProjectReferenceResolver.Resolve(directory.Root, ["Package.Events.csproj"]);

        Assert.Empty(result.ExternalAssemblyNames);
        Assert.Empty(result.MetadataPaths);
    }

    [Fact]
    public void PackageOutput_WithDifferentAssemblyIdentityIsRejected()
    {
        using var directory = TempDir.Create();
        WritePackageProject(directory.Root, ["Library/PackageCache/com.example.events@1.0.0/Events.cs"]);
        EmitPackageDll(directory.Root, "Unrelated.Assembly");

        var result = CsProjectReferenceResolver.Resolve(directory.Root, ["Package.Events.csproj"]);

        Assert.Equal(["Package.Events"], result.ExternalAssemblyNames);
        Assert.Empty(result.MetadataPaths);
    }

    [Fact]
    public void TargetProjectChangedWithoutConsumerEdit_InvalidatesModeCacheAndCompilerShape()
    {
        using var directory = TempDir.Create();
        var target = WritePackageProject(directory.Root, ["Library/PackageCache/com.example.events@1.0.0/Events.cs"]);
        var dll = EmitPackageDll(directory.Root, "Package.Events");
        var consumerPath = Path.Combine(directory.Root, "Consumer.csproj");
        File.WriteAllText(consumerPath, "<Project><ItemGroup><ProjectReference Include=\"Package.Events.csproj\" /></ItemGroup></Project>");
        var consumerMtime = File.GetLastWriteTimeUtc(consumerPath);
        var cache = new CsSessionModelCache();
        var first = cache.GetModeDecision(directory.Root, "Consumer");
        Assert.Contains(dll, first.MetadataReferencePaths);

        WritePackageProject(directory.Root, ["Assets/Events.cs"]);
        File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(target).AddSeconds(5));
        var changed = cache.GetModeDecision(directory.Root, "Consumer");

        Assert.Equal(consumerMtime, File.GetLastWriteTimeUtc(consumerPath));
        Assert.NotSame(first, changed);
        Assert.DoesNotContain(dll, changed.MetadataReferencePaths);
        var unit = new AssemblyUnit("Consumer", null, null, ["Package.Events"], [], false);
        Assert.NotEqual(CsCompilationCache.ComputeUnitShapeHash(unit, first), CsCompilationCache.ComputeUnitShapeHash(unit, changed));
    }

    [Fact]
    public void SameMtimePrimaryProjectRepair_WithDifferentSizeRefreshesMode()
    {
        using var directory = TempDir.Create();
        var path = Path.Combine(directory.Root, "Consumer.csproj");
        File.WriteAllText(path, "<Project>");
        var originalMtime = File.GetLastWriteTimeUtc(path);
        var cache = new CsSessionModelCache();
        var first = cache.GetModeDecision(directory.Root, "Consumer");
        Assert.Equal(CsAnalysisMode.Syntactic, first.Mode);
        Assert.Equal(CsModeReasons.CsprojParseFailed, first.SyntacticReason);

        File.WriteAllText(path, "<Project><PropertyGroup><DefineConstants>UNITY_EDITOR</DefineConstants></PropertyGroup></Project>");
        File.SetLastWriteTimeUtc(path, originalMtime);
        var repaired = cache.GetModeDecision(directory.Root, "Consumer");

        Assert.Equal(first.CsprojMtimeTicks, repaired.CsprojMtimeTicks);
        Assert.Equal(CsAnalysisMode.Semantic, repaired.Mode);
        Assert.Equal(["UNITY_EDITOR"], repaired.Defines);
    }

    [Fact]
    public void SharedPassMetadataCache_DllReplacementUsesNewApiWithoutChangingEarlierCompilation()
    {
        using var directory = TempDir.Create();
        var dll = EmitPackageDll(directory.Root, "Package.Events", "public class OldApi {}");
        var source = Path.Combine(directory.Root, "Consumer.cs");
        File.WriteAllText(source, "public class Consumer { public OldApi value; }");
        var unit = new AssemblyUnit("Consumer", null, null, [], [(1, "Consumer.cs")], false);
        var platformPaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var mode = new CsModeDecision(CsAnalysisMode.Semantic, [], [.. platformPaths, dll]);
        var cache = new ConcurrentDictionary<string, PortableExecutableReference>(StringComparer.OrdinalIgnoreCase);
        var (first, _) = CsProjectAnalyzer.BuildCompilation(unit, mode, new Dictionary<string, CSharpCompilation>(), _ => source, cache);
        Assert.DoesNotContain(first.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);

        var replacement = EmitPackageDll(directory.Root, "Package.Events", "public class NewApi {}", "replacement.dll");
        File.SetLastWriteTimeUtc(replacement, File.GetLastWriteTimeUtc(dll).AddSeconds(10));
        File.Move(replacement, dll, overwrite: true);
        File.WriteAllText(source, "public class Consumer { public NewApi value; }");
        var (second, _) = CsProjectAnalyzer.BuildCompilation(unit, mode, new Dictionary<string, CSharpCompilation>(), _ => source, cache);

        Assert.DoesNotContain(second.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        Assert.NotNull(second.GetTypeByMetadataName("NewApi"));
        Assert.Null(second.GetTypeByMetadataName("OldApi"));
        Assert.NotNull(first.GetTypeByMetadataName("OldApi"));
        Assert.Null(first.GetTypeByMetadataName("NewApi"));
    }

    [Fact]
    public void UnreadableBindingContext_FailsHealthWithoutBecomingOwnedGraphSource()
    {
        using var directory = TempDir.Create();
        var ownedPath = Path.Combine(directory.Root, "Owned.cs");
        File.WriteAllText(ownedPath, "public class Owned {}");
        var missingContext = Path.Combine(directory.Root, "MissingContext.cs");
        var unit = new AssemblyUnit("Package.Runtime", null, null, [], [(1, "Owned.cs")], false);
        var platformPaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var mode = new CsModeDecision(CsAnalysisMode.Semantic, [], platformPaths, BindingOnlySourcePaths: [missingContext], IsPackageExtension: true);

        var built = CsProjectAnalyzer.BuildCompilationForCache(unit, mode, new Dictionary<string, CSharpCompilation>(), _ => ownedPath, new CsCompilationCache());

        Assert.Equal(2, built.Compilation.SyntaxTrees.Count());
        Assert.Single(built.FileIdByTree);
        Assert.Single(built.TreeByPath);
        Assert.Contains("CS1029", CsCompilationHealth.Capture(built.Compilation).ErrorCodes);
        var (symbols, _, _) = SemanticCsExtractor.Extract(built.Compilation, built.FileIdByTree);
        Assert.All(symbols, symbol => Assert.Equal(1, symbol.FileId));
    }

    private static string WritePackageProject(string root, IReadOnlyList<string> compileIncludes)
    {
        var target = Path.Combine(root, "Package.Events.csproj");
        new XDocument(new XElement("Project",
            new XElement("PropertyGroup", new XElement("AssemblyName", "Package.Events")),
            new XElement("ItemGroup", compileIncludes.Select(path => new XElement("Compile", new XAttribute("Include", path)))))).Save(target);
        return target;
    }

    private static string EmitPackageDll(string root, string assemblyName, string source = "public class PackageEvent {}", string filename = "Package.Events.dll")
    {
        var directory = Path.Combine(root, "Library", "ScriptAssemblies");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, filename);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create(assemblyName, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        return path;
    }
}
