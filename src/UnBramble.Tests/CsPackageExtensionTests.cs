using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Data.Sqlite;
using UnBramble.Core;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>Assets scripts extending a cached package retain owned graph evidence while package sources supply binding context only.</summary>
public class CsPackageExtensionTests
{
    private const string PackageAssembly = "Package.Runtime";
    private const string PackageContextPath = "Library/PackageCache/com.example.package@1.0.0/Runtime/PackageApi.cs";
    private const string ExtensionPath = "Assets/Extensions/PackageApi.Extension.cs";
    private const string DependencyPath = "Assets/Dependency/ProjectDependency.cs";
    private const string ConsumerPath = "Assets/Scripts/Entry.cs";
    private const string ExtensionDocId = "M:SamplePackage.PackageApi.CurrentExtension";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AssetAsmrefToCachedPackage_BindsOwnedPartialExtensionAgainstPackageContextInsteadOfStaleDll(bool watchCache)
    {
        using var fixture = PackageFixture.Create(consumerCallsExtension: true);
        using var engine = OpenEngine(fixture.Root, watchCache);

        var index = engine.RunIndex(full: false);

        Assert.Equal(3, index.Stats.Cs.TotalAssemblies);
        AssertHealthyExtensionCall(engine, ExtensionDocId);
        Assert.Contains(engine.GetCsRefs("P:SamplePackage.PackageApi.PackageValue"), row =>
            row.SourcePath == ExtensionPath && row.RefKind == "member-access" && row.Confidence == "semantic");
        Assert.Contains(engine.GetCsRefs("P:ProjectCode.ProjectDependency.Value"), row =>
            row.SourcePath == ExtensionPath && row.RefKind == "member-access" && row.Confidence == "semantic");
        Assert.Contains(engine.GetAllFiles(), file => file.Path == PackageContextPath && file.IdentityOnly);
        AssertOwnedSymbolAndNoCacheSymbols(engine, ExtensionDocId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PackageContextAndOwnedEdits_RebindPartialAssemblyAndConsumer(bool watchCache)
    {
        using var fixture = PackageFixture.Create(consumerCallsExtension: true);
        using var engine = OpenEngine(fixture.Root, watchCache);
        engine.RunIndex(full: false);
        AssertHealthyExtensionCall(engine, ExtensionDocId);

        fixture.ReplaceSource(PackageContextPath, "PackageValue", "UpdatedPackageValue");
        engine.RunIndex(full: false);
        Assert.Contains(engine.GetCsBindingFailures(), failure => failure.AssemblyName == PackageAssembly);
        Assert.Contains("cs-binding-incomplete", engine.GetCsRefsAnswer(ExtensionDocId).BlindSpots);

        fixture.ReplaceSource(ExtensionPath, "PackageValue", "UpdatedPackageValue");
        UpdateOwnedSource(engine, ExtensionPath, watchCache);
        AssertHealthyExtensionCall(engine, ExtensionDocId);
        Assert.Contains(engine.GetCsRefs("P:SamplePackage.PackageApi.UpdatedPackageValue"), row => row.SourcePath == ExtensionPath && row.Confidence == "semantic");

        // The installed DLL stays stale throughout. An owned API edit must invalidate both
        // package context callers and the separately compiled consumer rather than bind that DLL.
        fixture.ReplaceSource(ExtensionPath, "CurrentExtension", "NextExtension");
        UpdateOwnedSource(engine, ExtensionPath, watchCache);
        Assert.NotEmpty(engine.GetCsBindingFailures());
        Assert.DoesNotContain(engine.GetCsRefs(ExtensionDocId), row => row.Confidence == "semantic");

        fixture.ReplaceSource(ConsumerPath, "CurrentExtension", "NextExtension");
        fixture.ReplaceSource(PackageContextPath, "CurrentExtension", "NextExtension");
        engine.RunIndex(full: false);
        const string nextExtension = "M:SamplePackage.PackageApi.NextExtension";
        AssertHealthyExtensionCall(engine, nextExtension);
        Assert.Empty(engine.GetCsRefs(ExtensionDocId));
        AssertOwnedSymbolAndNoCacheSymbols(engine, nextExtension);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExtensionCalledOnlyByUnindexedPackageContext_IsScreenedAndPreservesProjectDependencies(bool watchCache)
    {
        using var fixture = PackageFixture.Create(consumerCallsExtension: false);
        using var engine = OpenEngine(fixture.Root, watchCache);
        engine.RunIndex(full: false);

        Assert.Empty(engine.GetCsBindingFailures());
        var refs = engine.GetCsRefsAnswer(ExtensionDocId);
        Assert.Empty(refs.Refs);
        Assert.Contains("package-cache-callers-possible", refs.BlindSpots);
        var result = engine.RunDeadCandidates();
        Assert.True(result.Available, string.Join("; ", result.UnavailableReasons));
        Assert.DoesNotContain(result.ProvenDead, row => row.Path == ExtensionPath || row.Path == DependencyPath);
        Assert.Contains(result.AdvisoryDead, row => row.Path == ExtensionPath && row.Reason == "package-extension-guard");
        AssertOwnedSymbolAndNoCacheSymbols(engine, ExtensionDocId);
    }

    private static UnBrambleEngine OpenEngine(string root, bool watchCache)
    {
        var engine = UnBrambleEngine.Open(root);
        if (watchCache)
        {
            engine.EnableWatchCompilationCache();
        }

        return engine;
    }

    private static void UpdateOwnedSource(UnBrambleEngine engine, string path, bool watchCache)
    {
        if (watchCache)
        {
            engine.ApplyTargetedUpdate([path]);
        }
        else
        {
            engine.RunIndex(full: false);
        }
    }

    private static void AssertHealthyExtensionCall(UnBrambleEngine engine, string docId)
    {
        Assert.Empty(engine.GetCsBindingFailures());
        var call = Assert.Single(engine.GetCsRefs(docId));
        Assert.Equal(ConsumerPath, call.SourcePath);
        Assert.Equal("call", call.RefKind);
        Assert.Equal("semantic", call.Confidence);
        var answer = engine.GetCsRefsAnswer(docId);
        Assert.DoesNotContain("cs-binding-incomplete", answer.BlindSpots);
        Assert.Contains("package-cache-callers-possible", answer.BlindSpots);
    }

    private static void AssertOwnedSymbolAndNoCacheSymbols(UnBrambleEngine engine, string docId)
    {
        using var connection = new SqliteConnection($"Data Source={engine.DbPath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT f.path FROM symbols s JOIN files f ON f.id = s.file_id WHERE s.doc_id = @docId;";
        command.Parameters.AddWithValue("@docId", docId);
        Assert.Equal(ExtensionPath, command.ExecuteScalar() as string);
        command.Parameters.Clear();
        command.CommandText = "SELECT COUNT(*) FROM symbols s JOIN files f ON f.id = s.file_id WHERE f.identity_only = 1;";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
        command.CommandText = "SELECT COUNT(*) FROM symbol_refs sr JOIN files f ON f.id = sr.source_file_id WHERE f.identity_only = 1;";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
    }

    private sealed class PackageFixture : IDisposable
    {
        private readonly TempDir _temporary;
        public string Root => _temporary.Root;

        private PackageFixture(TempDir temporary)
        {
            _temporary = temporary;
        }

        public static PackageFixture Create(bool consumerCallsExtension)
        {
            var fixture = new PackageFixture(TempDir.Create());
            fixture.Write("ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 2022.3.30f1\n");
            fixture.Write("ProjectSettings/EditorSettings.asset", "%YAML 1.1\n--- !u!159 &1\nEditorSettings:\n  m_SerializationMode: 2\n");
            fixture.Write("Packages/manifest.json", "{\"dependencies\":{}}");
            fixture.WriteAsset("Library/PackageCache/com.example.package@1.0.0/Runtime/Package.Runtime.asmdef", "{\"name\":\"Package.Runtime\",\"references\":[\"Project.Dependency\"]}", "11111111111111111111111111111111");
            fixture.WriteAsset("Assets/Extensions/PackageExtension.asmref", "{\"reference\":\"GUID:11111111111111111111111111111111\"}", "22222222222222222222222222222222");
            fixture.WriteAsset(ExtensionPath, "namespace SamplePackage { public static partial class PackageApi { public static int CurrentExtension() => PackageValue + ProjectCode.ProjectDependency.Value; } }", "33333333333333333333333333333333");
            fixture.WriteAsset("Assets/Dependency/Project.Dependency.asmdef", "{\"name\":\"Project.Dependency\"}", "44444444444444444444444444444444");
            fixture.WriteAsset(DependencyPath, "namespace ProjectCode { public static class ProjectDependency { public static int Value => 1; } }", "55555555555555555555555555555555");
            var entryBody = consumerCallsExtension ? "SamplePackage.PackageApi.CurrentExtension();" : "";
            fixture.WriteAsset(ConsumerPath, "public static class Entry { public static void Main() { " + entryBody + " } }", "66666666666666666666666666666666");
            fixture.WriteAsset(PackageContextPath, "namespace SamplePackage { public static partial class PackageApi { public static int PackageValue => 1; public static int InvokeOwnedExtension() => CurrentExtension(); } }", "77777777777777777777777777777777");
            var staleOutput = fixture.EmitStalePackage();
            fixture.WriteProject("Project.Dependency", [DependencyPath], [], staleOutput: null);
            fixture.WriteProject(PackageAssembly, [PackageContextPath, ExtensionPath], ["Project.Dependency"], staleOutput);
            fixture.WriteProject("Assembly-CSharp", [ConsumerPath], [PackageAssembly], staleOutput);
            return fixture;
        }

        public void ReplaceSource(string relative, string oldValue, string newValue)
        {
            var path = FullPath(relative);
            var previous = File.GetLastWriteTimeUtc(path);
            File.WriteAllText(path, File.ReadAllText(path).Replace(oldValue, newValue, StringComparison.Ordinal));
            File.SetLastWriteTimeUtc(path, previous.AddSeconds(1));
        }

        private string EmitStalePackage()
        {
            var output = FullPath("Library/ScriptAssemblies/" + PackageAssembly + ".dll");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var compilation = CSharpCompilation.Create(PackageAssembly,
                [CSharpSyntaxTree.ParseText("namespace SamplePackage { public static partial class PackageApi { public static int PackageValue => 100; public static int StaleExtension() => 0; } }")],
                BclPaths().Select(path => MetadataReference.CreateFromFile(path)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var emitted = compilation.Emit(output);
            Assert.True(emitted.Success, string.Join("; ", emitted.Diagnostics));
            return output;
        }

        private void WriteProject(string assemblyName, IReadOnlyList<string> sources, IReadOnlyList<string> projectReferences, string? staleOutput)
        {
            var ns = XNamespace.Get("http://schemas.microsoft.com/developer/msbuild/2003");
            var items = new XElement(ns + "ItemGroup", BclPaths().Select(path =>
                new XElement(ns + "Reference", new XAttribute("Include", Path.GetFileNameWithoutExtension(path)), new XElement(ns + "HintPath", path))));
            foreach (var source in sources)
            {
                items.Add(new XElement(ns + "Compile", new XAttribute("Include", source)));
            }

            foreach (var reference in projectReferences)
            {
                items.Add(new XElement(ns + "ProjectReference", new XAttribute("Include", reference + ".csproj")));
            }

            if (staleOutput is not null)
            {
                items.Add(new XElement(ns + "Reference", new XAttribute("Include", PackageAssembly), new XElement(ns + "HintPath", staleOutput)));
            }

            new XDocument(new XElement(ns + "Project", new XElement(ns + "PropertyGroup", new XElement(ns + "AssemblyName", assemblyName)), items))
                .Save(FullPath(assemblyName + ".csproj"));
        }

        private static IReadOnlyList<string> BclPaths()
        {
            var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
            return ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => Path.GetDirectoryName(path) == runtimeDirectory).ToArray();
        }

        private void WriteAsset(string relative, string content, string guid)
        {
            Write(relative, content);
            Write(relative + ".meta", "fileFormatVersion: 2\nguid: " + guid + "\n");
        }

        private void Write(string relative, string content)
        {
            var path = FullPath(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        private string FullPath(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

        public void Dispose() => _temporary.Dispose();
    }
}
