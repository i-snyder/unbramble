using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UnBramble.Core;
using UnBramble.Core.CSharp;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

public class CsLanguageVersionTests
{
    private const string Source = """
        using System;
        using System.Collections.Generic;
        // Model the competing overloads exposed by Unity's older reference assemblies.
        // Host framework libraries can carry different overload-priority attributes.
        public static class LegacyArrayExtensions
        {
            public static IEnumerable<T> Reverse<T>(this IEnumerable<T> values) => values;
            public static void Reverse<T>(this Span<T> values) {}
        }
        public static class Entry
        {
            public static void Main() {}
            public static IEnumerable<int> Values() => new[] { 1, 2, 3 }.Reverse();
        }
        """;

    [Theory]
    [InlineData(null, LanguageVersion.Latest)]
    [InlineData("", LanguageVersion.Latest)]
    [InlineData("12.0", LanguageVersion.CSharp12)]
    [InlineData("latest", LanguageVersion.Latest)]
    [InlineData("preview", LanguageVersion.Preview)]
    public void GeneratedLanguageVersion_IsReadWithoutEvaluatingProject(string? text, LanguageVersion expected)
    {
        using var temp = TempDir.Create();
        WriteProject(temp.Root, text);

        var mode = CsModeSelector.Determine(temp.Root, "Assembly-CSharp");

        Assert.Equal(CsAnalysisMode.Semantic, mode.Mode);
        Assert.Equal(expected, mode.LanguageVersion);
    }

    [Theory]
    [InlineData("99.0")]
    [InlineData("invalid")]
    public void UnsupportedExplicitLanguageVersion_DegradesWithAnAttributedReason(string text)
    {
        using var temp = TempDir.Create();
        WriteProject(temp.Root, text);

        var mode = CsModeSelector.Determine(temp.Root, "Assembly-CSharp");

        Assert.Equal(CsAnalysisMode.Syntactic, mode.Mode);
        Assert.Equal(CsModeReasons.CsprojUnsupportedLanguageVersion, mode.SyntacticReason);
    }

    [Fact]
    public void CSharp12ArrayReverse_PreservesSequenceOverloadInsteadOfNewerSpanConversion()
    {
        using var temp = TempDir.Create();
        File.WriteAllText(Path.Combine(temp.Root, "Entry.cs"), Source);
        WriteProject(temp.Root, "12.0");
        var unit = new AssemblyUnit("Assembly-CSharp", null, null, [], [(1L, "Entry.cs")], false);
        var mode = CsModeSelector.Determine(temp.Root, unit.Name);
        var (compilation, _) = CsProjectAnalyzer.BuildCompilation(unit, mode,
            new Dictionary<string, CSharpCompilation>(), relative => Path.Combine(temp.Root, relative));
        var tree = Assert.Single(compilation.SyntaxTrees);
        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var symbol = Assert.IsAssignableFrom<IMethodSymbol>(compilation.GetSemanticModel(tree).GetSymbolInfo(invocation).Symbol);

        Assert.Equal(LanguageVersion.CSharp12, ((CSharpParseOptions)tree.Options).LanguageVersion);
        Assert.Equal("LegacyArrayExtensions", symbol.ContainingType.Name);
        Assert.Equal("IEnumerable", symbol.ReducedFrom!.Parameters[0].Type.Name);
        Assert.False(symbol.ReturnsVoid);
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);

        // This is a binding difference, not just acceptance of newer syntax: latest C# prefers
        // an array-to-span conversion and selects a void-returning in-place Reverse overload.
        var (latestCompilation, _) = CsProjectAnalyzer.BuildCompilation(unit, mode with { LanguageVersion = LanguageVersion.Latest },
            new Dictionary<string, CSharpCompilation>(), relative => Path.Combine(temp.Root, relative));
        var latestTree = Assert.Single(latestCompilation.SyntaxTrees);
        var latestInvocation = latestTree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var latestSymbol = Assert.IsAssignableFrom<IMethodSymbol>(latestCompilation.GetSemanticModel(latestTree).GetSymbolInfo(latestInvocation).Symbol);
        Assert.True(latestSymbol.ReturnsVoid);
        Assert.Contains(latestCompilation.GetDiagnostics(), d => d.Id == "CS0029");
    }

    [Fact]
    public void LanguageVersionChanges_InvalidateCompilationShape()
    {
        var unit = new AssemblyUnit("Assembly-CSharp", null, null, [], [], false);
        var mode = new CsModeDecision(CsAnalysisMode.Semantic, [], [], LanguageVersion: LanguageVersion.CSharp12);

        Assert.NotEqual(CsCompilationCache.ComputeUnitShapeHash(unit, mode),
            CsCompilationCache.ComputeUnitShapeHash(unit, mode with { LanguageVersion = LanguageVersion.Latest }));
    }

    [Fact]
    public void WatchProjectOnlyLanguageChange_RefreshesBindingsAndSubsequentSourceEditsKeepVersion()
    {
        using var temp = TempDir.Create();
        void Write(string relative, string text)
        {
            var path = Path.Combine(temp.Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Write("ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 2022.3.30f1\n");
        Write("ProjectSettings/EditorSettings.asset", "%YAML 1.1\n--- !u!159 &1\nEditorSettings:\n  m_SerializationMode: 2\n");
        Write("Assets/Entry.cs", Source);
        Write("Assets/Entry.cs.meta", "fileFormatVersion: 2\nguid: 11111111111111111111111111111111\n");
        WriteProject(temp.Root, "12.0");
        using var engine = UnBrambleEngine.Open(temp.Root);
        engine.EnableWatchCompilationCache();
        engine.RunIndex(full: false);
        Assert.Empty(engine.GetCsBindingFailures());

        var projectPath = Path.Combine(temp.Root, "Assembly-CSharp.csproj");
        var previousProjectTime = File.GetLastWriteTimeUtc(projectPath);
        WriteProject(temp.Root, "latest");
        File.SetLastWriteTimeUtc(projectPath, previousProjectTime.AddSeconds(1));
        engine.RunIndex(full: false);
        var failure = Assert.Single(engine.GetCsBindingFailures());
        Assert.Contains("CS0029", failure.ErrorCodes);

        previousProjectTime = File.GetLastWriteTimeUtc(projectPath);
        WriteProject(temp.Root, "12.0");
        File.SetLastWriteTimeUtc(projectPath, previousProjectTime.AddSeconds(1));
        engine.RunIndex(full: false);
        Assert.Empty(engine.GetCsBindingFailures());

        var sourcePath = Path.Combine(temp.Root, "Assets", "Entry.cs");
        var previousSourceTime = File.GetLastWriteTimeUtc(sourcePath);
        File.AppendAllText(sourcePath, "\n// Source-only edit uses the cached project's selected language version.\n");
        File.SetLastWriteTimeUtc(sourcePath, previousSourceTime.AddSeconds(1));
        engine.ApplyTargetedUpdate(["Assets/Entry.cs"]);
        Assert.Empty(engine.GetCsBindingFailures());
    }

    private static void WriteProject(string root, string? languageVersion)
    {
        var ns = XNamespace.Get("http://schemas.microsoft.com/developer/msbuild/2003");
        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(path => Path.GetDirectoryName(path) == runtimeDirectory);
        var project = new XElement(ns + "Project",
            new XElement(ns + "PropertyGroup", languageVersion is null ? null : new XElement(ns + "LangVersion", languageVersion)),
            new XElement(ns + "ItemGroup", paths.Select(path => new XElement(ns + "Reference",
                new XAttribute("Include", Path.GetFileNameWithoutExtension(path)), new XElement(ns + "HintPath", path)))));
        new XDocument(project).Save(Path.Combine(root, "Assembly-CSharp.csproj"));
    }
}
