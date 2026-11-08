using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Data.Sqlite;
using UnBramble.Core;
using UnBramble.Core.CSharp;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

public class TypeReceiverReferenceTests
{
    [Theory]
    [InlineData("Registry.Register(1);", "M:Demo.Registry.Register(System.Int32)", "call")]
    [InlineData("Demo.Registry.Register(1);", "M:Demo.Registry.Register(System.Int32)", "call")]
    [InlineData("global::Demo.Registry.Register(1);", "M:Demo.Registry.Register(System.Int32)", "call")]
    [InlineData("Alias.Register(1);", "M:Demo.Registry.Register(System.Int32)", "call")]
    [InlineData("_ = Registry.Count;", "F:Demo.Registry.Count", "member-access")]
    [InlineData("_ = Registry.Enabled;", "P:Demo.Registry.Enabled", "member-access")]
    [InlineData("System.Action<int> callback = Registry.Register;", "M:Demo.Registry.Register(System.Int32)", "call")]
    public void NamedTypeReceiver_RecordsOneExactTypeReferenceAlongsideMember(
        string statement, string memberDocId, string refKind)
    {
        var refs = Extract($$"""
            using Demo;
            using Alias = Demo.Registry;
            namespace Demo
            {
                public static class Registry
                {
                    public static int Count;
                    public static bool Enabled => true;
                    public static void Register(int value) {}
                }
            }
            public class Caller
            {
                public void Run()
                {
                    {{statement}}
                }
            }
            """);

        var typeRef = Assert.Single(refs, r => r.SourceSymbolDocId == "M:Caller.Run" &&
            r.TargetDocId == "T:Demo.Registry");
        Assert.Equal("type-ref", typeRef.RefKind);
        Assert.Equal("semantic", typeRef.Confidence);
        var memberRef = Assert.Single(refs, r => r.SourceSymbolDocId == "M:Caller.Run" &&
            r.TargetDocId == memberDocId);
        Assert.Equal(refKind, memberRef.RefKind);
        Assert.Equal("semantic", memberRef.Confidence);
        Assert.Equal(memberRef.Line, typeRef.Line);
    }

    [Fact]
    public void GenericTypeReceiver_ReferencesDeclaredTypeAndPreservesTypeArgument()
    {
        var refs = Extract("""
            public class Payload {}
            public static class Registry<T>
            {
                public static void Register() {}
            }
            public class Caller
            {
                public void Run() { Registry<Payload>.Register(); }
            }
            """);

        var receiver = Assert.Single(refs, r => r.SourceSymbolDocId == "M:Caller.Run" &&
            r.TargetDocId == "T:Registry`1");
        Assert.Equal("type-ref", receiver.RefKind);
        Assert.Equal("semantic", receiver.Confidence);
        Assert.Single(refs, r => r.SourceSymbolDocId == "M:Caller.Run" &&
            r.TargetDocId == "T:Payload" && r.RefKind == "type-ref" && r.Confidence == "semantic");
    }

    [Theory]
    [InlineData("Demo.Outer.Inner.Register();", "T:Demo.Outer", "T:Demo.Outer.Inner")]
    [InlineData("Demo.Outer<int>.Inner<string>.Register();", "T:Demo.Outer`1", "T:Demo.Outer`1.Inner`1")]
    public void NestedTypeReceiver_RecordsEachExplicitTypeOnce(string statement, string outerDocId, string innerDocId)
    {
        var refs = Extract($$"""
            namespace Demo
            {
                public class Outer
                {
                    public static class Inner
                    {
                        public static void Register() {}
                    }
                }
                public class Outer<T>
                {
                    public static class Inner<U>
                    {
                        public static void Register() {}
                    }
                }
            }
            public class Caller
            {
                public void Run() { {{statement}} }
            }
            """);

        foreach (var docId in new[] { outerDocId, innerDocId })
        {
            var receiver = Assert.Single(refs, r => r.SourceSymbolDocId == "M:Caller.Run" && r.TargetDocId == docId);
            Assert.Equal("type-ref", receiver.RefKind);
            Assert.Equal("semantic", receiver.Confidence);
        }
    }

    [Fact]
    public void InstanceReceiver_ShadowingAType_DoesNotInventTypeReference()
    {
        var refs = Extract("""
            public static class Registry
            {
                public static void Register() {}
            }
            public class Worker
            {
                public void Register() {}
            }
            public class Caller
            {
                public void Run(Worker Registry)
                {
                    Registry.Register();
                }
            }
            """);

        Assert.DoesNotContain(refs, r => r.SourceSymbolDocId == "M:Caller.Run(Worker)" && r.TargetDocId == "T:Registry");
        // The parameter declaration supplies one Worker type ref; its instance use adds none.
        Assert.Single(refs, r => r.SourceSymbolDocId == "M:Caller.Run(Worker)" &&
            r.RefKind == "type-ref" && r.TargetDocId == "T:Worker");
        Assert.Single(refs, r => r.SourceSymbolDocId == "M:Caller.Run(Worker)" &&
            r.TargetDocId == "M:Worker.Register" && r.RefKind == "call" && r.Confidence == "semantic");
    }

    [Fact]
    public void InheritedStaticMember_ReferencesWrittenDerivedTypeAndDeclaredBaseMethod()
    {
        var refs = Extract("""
            public class Base
            {
                public static void Register() {}
            }
            public class Derived : Base {}
            public class Caller
            {
                public void Run() { Derived.Register(); }
            }
            """);

        Assert.Single(refs, r => r.SourceSymbolDocId == "M:Caller.Run" &&
            r.TargetDocId == "T:Derived" && r.RefKind == "type-ref" && r.Confidence == "semantic");
        Assert.Single(refs, r => r.SourceSymbolDocId == "M:Caller.Run" &&
            r.TargetDocId == "M:Base.Register" && r.RefKind == "call" && r.Confidence == "semantic");
        Assert.DoesNotContain(refs, r => r.SourceSymbolDocId == "M:Caller.Run" && r.TargetDocId == "T:Base");
    }

    [Fact]
    public void UnresolvedReceiver_DoesNotProduceProvenTypeReference()
    {
        var refs = Extract("""
            public class Caller
            {
                public void Run()
                {
                    MissingRegistry.Register();
                }
            }
            """, allowErrors: true);

        var call = Assert.Single(refs, r => r.RefKind == "call");
        Assert.Equal("syntactic", call.Confidence);
        Assert.DoesNotContain(refs, r => r.Line == call.Line && r.RefKind == "type-ref" && r.Confidence == "semantic");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cli_TypeAndExactMethodQueries_ReturnTheSameEightProvenCallSites(bool upgradeOldIndex)
    {
        using var fixture = FixtureCopy.Create();
        WriteScript(fixture.Root, "Registry.cs", """
            namespace Demo;
            public static class Registry
            {
                public static void Register(int value) {}
            }
            """);
        for (var i = 1; i <= 8; i++)
        {
            WriteScript(fixture.Root, $"RegistryCaller{i}.cs", $$"""
                public class RegistryCaller{{i}}
                {
                    public void Run() { Demo.Registry.Register({{i}}); }
                }
                """);
        }

        // Core is semantic; Game remains syntactic so a zero type result would carry the
        // false-negative flag, while real callers must leave the coverage caveat visible.
        WriteSemanticModeCsproj(fixture.Root, "Core");

        if (upgradeOldIndex)
        {
            string dbPath;
            using (var engine = UnBrambleEngine.Open(fixture.Root))
            {
                engine.RunIndex(full: false);
                dbPath = engine.DbPath;
            }

            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM symbol_refs WHERE target_doc_id = 'T:Demo.Registry';
                UPDATE meta_kv SET value = '14' WHERE key = 'schema_version';
                """;
            command.ExecuteNonQuery();
        }

        var methodSites = ReadCallSites("M:Demo.Registry.Register(System.Int32)", "call");
        var typeSites = ReadCallSites("T:Demo.Registry", "type-ref");
        Assert.Equal(methodSites, typeSites);

        string[] ReadCallSites(string docId, string refKind)
        {
            var (exitCode, stdout, stderr) = CliRunner.Run("who-uses", docId, "--symbol", "--json", "-p", fixture.Root);
            Assert.True(exitCode == 0, stderr);
            using var json = JsonDocument.Parse(stdout);
            var root = json.RootElement;
            Assert.Equal(docId, root.GetProperty("symbol").GetString());
            Assert.False(root.GetProperty("possibleFalseNegative").GetBoolean());
            Assert.Equal("proven", root.GetProperty("confidence").GetString());
            Assert.Contains(root.GetProperty("blindSpots").EnumerateArray(),
                spot => spot.GetString() == "syntactic-assemblies-present");
            var results = root.GetProperty("results").EnumerateArray().ToArray();
            Assert.Equal(8, results.Length);
            Assert.All(results, result =>
            {
                Assert.Equal(docId, result.GetProperty("targetKey").GetString());
                Assert.Equal(refKind, result.GetProperty("refKind").GetString());
                Assert.Equal("proven", result.GetProperty("confidence").GetString());
                Assert.Equal(0, result.GetProperty("depth").GetInt32());
            });
            return results.Select(result => $"{result.GetProperty("source").GetString()}:{result.GetProperty("line").GetInt32()}")
                .Order(StringComparer.Ordinal).ToArray();
        }
    }

    private static IReadOnlyList<CsSymbolRefRow> Extract(string source, bool allowErrors = false)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("ReceiverTests", [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        if (!allowErrors)
        {
            Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        }

        return SemanticCsExtractor.Extract(compilation, new Dictionary<SyntaxTree, long> { [tree] = 1 }).Refs;
    }

    private static void WriteScript(string root, string name, string source)
    {
        var path = Path.Combine(root, "Assets", "Scripts", "Core", name);
        File.WriteAllText(path, source);
        File.WriteAllText(path + ".meta", $"fileFormatVersion: 2\nguid: {Guid.NewGuid():N}\n");
    }

    private static void WriteSemanticModeCsproj(string root, string assemblyName)
    {
        var ns = XNamespace.Get("http://schemas.microsoft.com/developer/msbuild/2003");
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var references = new XElement(ns + "ItemGroup", paths.Select(path => new XElement(
            ns + "Reference", new XAttribute("Include", Path.GetFileNameWithoutExtension(path)),
            new XElement(ns + "HintPath", path))));
        new XDocument(new XElement(ns + "Project", new XAttribute("ToolsVersion", "Current"), references))
            .Save(Path.Combine(root, assemblyName + ".csproj"));
    }
}
