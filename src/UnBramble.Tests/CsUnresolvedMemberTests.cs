using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UnBramble.Core.CSharp;

namespace UnBramble.Tests;

public class CsUnresolvedMemberTests
{
    [Fact]
    public void MissingAssembly_QualifiedSubscriptions_PreserveBothSitesWithoutInventingTargets()
    {
        const string source = """
            class Subscriber
            {
                void Bind()
                {
                    MissingBridge.Removed += Handle;
                    MissingBridge.Removed -= Handle;
                }
                void Handle() {}
            }
            """;

        var result = Extract(source);
        var evidence = result.Refs.Where(r => r.TargetDocId == "U:Removed").ToList();

        Assert.Equal(2, evidence.Count);
        Assert.Equal(new[] { 5, 6 }, evidence.Select(r => r.Line));
        Assert.All(evidence, r =>
        {
            Assert.Equal("member-access", r.RefKind);
            Assert.Equal("syntactic", r.Confidence);
            Assert.Equal("M:Subscriber.Bind", r.SourceSymbolDocId);
            Assert.DoesNotContain(result.Symbols, s => s.DocId == r.TargetDocId);
        });
    }

    [Fact]
    public void MissingBase_BareSubscriptions_PreserveBothSites()
    {
        const string source = """
            class Subscriber : MissingBase
            {
                void Bind()
                {
                    Removed += Handle;
                    Removed -= Handle;
                }
                void Handle() {}
            }
            """;

        var evidence = Extract(source).Refs.Where(r => r.TargetDocId == "U:Removed").ToList();

        Assert.Equal(2, evidence.Count);
        Assert.All(evidence, r => Assert.Equal("syntactic", r.Confidence));
    }

    [Fact]
    public void AmbiguousBareEvents_RecordNameEvidenceWithoutChoosingEitherCandidate()
    {
        const string source = """
            using System;
            using static First;
            using static Second;
            static class First { public static event Action Removed; }
            static class Second { public static event Action Removed; }
            class Subscriber
            {
                void Bind() { Removed += Handle; }
                void Handle() {}
            }
            """;

        var result = Extract(source);
        var eventUse = result.Tree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>().Single().Left;
        var symbolInfo = result.Compilation.GetSemanticModel(result.Tree).GetSymbolInfo(eventUse);
        Assert.Null(symbolInfo.Symbol);
        Assert.Equal(2, symbolInfo.CandidateSymbols.Length);
        var evidence = Assert.Single(result.Refs, r => r.TargetDocId == "U:Removed");
        Assert.Equal("syntactic", evidence.Confidence);
        Assert.DoesNotContain(result.Refs, r => r.TargetDocId is "E:First.Removed" or "E:Second.Removed");
    }

    [Fact]
    public void AmbiguousQualifiedProperties_DoNotProjectEitherDeclarationAsAnExactTarget()
    {
        const string source = """
            interface First { int Value { get; } }
            interface Second { int Value { get; } }
            interface Both : First, Second {}
            class Reader { int Read(Both target) => target.Value; }
            """;

        var result = Extract(source);
        var member = result.Tree.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>().Single();
        var symbolInfo = result.Compilation.GetSemanticModel(result.Tree).GetSymbolInfo(member);
        Assert.Null(symbolInfo.Symbol);
        Assert.Equal(2, symbolInfo.CandidateSymbols.Length);
        var evidence = Assert.Single(result.Refs, r => r.TargetDocId == "U:Value");
        Assert.Equal("syntactic", evidence.Confidence);
        Assert.DoesNotContain(result.Refs, r => r.TargetDocId is "P:First.Value" or "P:Second.Value");
    }

    [Fact]
    public void UnresolvedReadsAndWrites_IncludeConditionalMembersAndEscapedNames()
    {
        const string source = """
            class Reader
            {
                void Read()
                {
                    var a = MissingBridge.@event;
                    MissingBridge.State = null;
                    var b = MissingBridge?.Value;
                }
            }
            """;

        var evidence = Extract(source).Refs.Where(r => r.TargetDocId.StartsWith("U:", StringComparison.Ordinal)).ToList();

        Assert.Equal(new[] { "U:event", "U:State", "U:Value" }, evidence.Select(r => r.TargetDocId));
        Assert.All(evidence, r =>
        {
            Assert.Equal("member-access", r.RefKind);
            Assert.Equal("syntactic", r.Confidence);
        });
    }

    [Fact]
    public void ResolvedBareAndQualifiedEvents_KeepSemanticTargetsWithoutDuplicateNameRows()
    {
        const string source = """
            using System;
            using static Bridge;
            static class Bridge { public static event Action Removed; }
            class Subscriber
            {
                void Bind()
                {
                    Removed += Handle;
                    Removed -= Handle;
                    Bridge.Removed += Handle;
                    Bridge.Removed -= Handle;
                }
                void Handle() {}
            }
            """;

        var result = Extract(source);
        var eventRefs = result.Refs.Where(r => r.TargetDocId == "E:Bridge.Removed").ToList();

        Assert.Equal(4, eventRefs.Count);
        Assert.All(eventRefs, r =>
        {
            Assert.Equal("member-access", r.RefKind);
            Assert.Equal("semantic", r.Confidence);
        });
        Assert.DoesNotContain(result.Refs, r => r.TargetDocId.StartsWith("U:", StringComparison.Ordinal));
        Assert.Equal(4, result.Refs.Count(r => r.TargetDocId == "M:Subscriber.Handle"));
    }

    private static ExtractionResult Extract(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "Synthetic.cs");
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("MemberTests", [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var (symbols, refs, _) = SemanticCsExtractor.Extract(compilation, new Dictionary<SyntaxTree, long> { [tree] = 1 });
        return new ExtractionResult(compilation, tree, symbols, refs);
    }

    private sealed record ExtractionResult(
        CSharpCompilation Compilation,
        SyntaxTree Tree,
        IReadOnlyList<CsSymbolRow> Symbols,
        IReadOnlyList<CsSymbolRefRow> Refs);
}
