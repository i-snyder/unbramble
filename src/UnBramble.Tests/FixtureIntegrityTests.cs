using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace UnBramble.Tests;

/// <summary>
/// Validates the committed fixture MiniProject against expected-edges.json.
/// No product parser is used here (test-local helpers only) — this only proves the fixture
/// itself is internally consistent and byte-faithful. Parsing correctness is covered elsewhere.
/// </summary>
public class FixtureIntegrityTests
{
    // Fixtures/** is copied next to the test binary (see UnBramble.Tests.csproj); never
    // index the committed copy in place from later, mutating tests — copy it first.
    private static readonly string FixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MiniProject");
    private static readonly string ExpectedEdgesPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "expected-edges.json");

    private static readonly Regex GuidLineRegex = new(@"^guid:\s*([0-9a-f]{32})\s*$", RegexOptions.Multiline);
    private static readonly Regex LowerHex32 = new(@"^[0-9a-f]{32}$");

    private static ExpectedEdges LoadExpectedEdges()
    {
        var json = File.ReadAllText(ExpectedEdgesPath);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var edges = JsonSerializer.Deserialize<ExpectedEdges>(json, options);
        Assert.NotNull(edges);
        return edges!;
    }

    [Fact]
    public void ExpectedEdgesJson_Parses()
    {
        var edges = LoadExpectedEdges();
        Assert.NotEmpty(edges.Guids);
        Assert.NotEmpty(edges.GuidEdges);
    }

    [Fact]
    public void EveryLedgerPath_ExistsWithSiblingMeta()
    {
        var edges = LoadExpectedEdges();
        foreach (var (relPath, _) in edges.Guids)
        {
            var fullPath = Path.Combine(FixtureRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
            var metaPath = fullPath + ".meta";

            if (Directory.Exists(fullPath))
            {
                Assert.True(File.Exists(metaPath), $"folder '{relPath}' is missing its sibling .meta at '{metaPath}'");
            }
            else
            {
                Assert.True(File.Exists(fullPath), $"ledger path '{relPath}' does not exist at '{fullPath}'");
                Assert.True(File.Exists(metaPath), $"file '{relPath}' is missing its sibling .meta at '{metaPath}'");
            }
        }
    }

    [Fact]
    public void EveryMetaGuidLine_MatchesLedgerValue_AndIsUniqueLowercaseHex32()
    {
        var edges = LoadExpectedEdges();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (relPath, expectedGuid) in edges.Guids)
        {
            Assert.Matches(LowerHex32, expectedGuid);

            if (seen.TryGetValue(expectedGuid, out var earlierPath))
            {
                Assert.Fail($"guid '{expectedGuid}' is used by both '{earlierPath}' and '{relPath}' — ledger guids must be unique");
            }
            seen[expectedGuid] = relPath;

            var fullPath = Path.Combine(FixtureRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
            var metaPath = fullPath + ".meta";
            Assert.True(File.Exists(metaPath), $"expected .meta at '{metaPath}' for ledger entry '{relPath}'");

            var metaContent = File.ReadAllText(metaPath);
            var match = GuidLineRegex.Match(metaContent);
            Assert.True(match.Success, $"'{metaPath}' has no top-level 'guid:' line");
            Assert.Equal(expectedGuid, match.Groups[1].Value);
        }
    }

    [Fact]
    public void EditorSettings_HasForceTextSerializationMode()
    {
        var path = Path.Combine(FixtureRoot, "ProjectSettings", "EditorSettings.asset");
        var content = File.ReadAllText(path);
        Assert.Contains("m_SerializationMode: 2", content);
    }

    [Fact]
    public void HiddenAssetNegatives_ExistOrAreAbsentAsSpecified()
    {
        Assert.True(File.Exists(Path.Combine(FixtureRoot, "Assets", "Ignored~", "Skipped.mat")));
        Assert.True(File.Exists(Path.Combine(FixtureRoot, "Assets", "Ignored~", "Skipped.mat.meta")));
        Assert.True(File.Exists(Path.Combine(FixtureRoot, "Assets", ".hidden.mat")));
        Assert.False(File.Exists(Path.Combine(FixtureRoot, "Assets", ".hidden.mat.meta")));
        Assert.False(File.Exists(Path.Combine(FixtureRoot, "Assets", "UI", "Ghost.uxml")));
    }

    [Fact]
    public void ExpectedEdges_ResolvesToTargets_ExistInLedgerOrAreProjectSettings()
    {
        var edges = LoadExpectedEdges();

        foreach (var edge in edges.GuidEdges)
        {
            if (edge.ResolvesTo is null) continue;
            Assert.True(
                edges.Guids.ContainsKey(edge.ResolvesTo) || edge.ResolvesTo.StartsWith("ProjectSettings/", StringComparison.Ordinal),
                $"guidEdge resolvesTo '{edge.ResolvesTo}' is not a ledger key or a ProjectSettings/ path");
        }

        foreach (var edge in edges.PathEdges)
        {
            if (edge.ResolvesTo is null) continue;
            Assert.True(
                edges.Guids.ContainsKey(edge.ResolvesTo) || edge.ResolvesTo.StartsWith("ProjectSettings/", StringComparison.Ordinal),
                $"pathEdge resolvesTo '{edge.ResolvesTo}' is not a ledger key or a ProjectSettings/ path");
        }
    }

    [Fact]
    public void ExpectedEdges_SourcePaths_ExistOnDisk()
    {
        var edges = LoadExpectedEdges();
        var sources = edges.GuidEdges.Select(e => e.Source)
            .Concat(edges.PathEdges.Select(e => e.Source))
            .Distinct();

        foreach (var source in sources)
        {
            var fullPath = Path.Combine(FixtureRoot, source.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(fullPath), $"expected-edges.json source '{source}' does not exist at '{fullPath}'");
        }
    }

    [Fact]
    public void ShaderGraphFixture_ProtectsEscapedJsonPositiveAndDashedUuidNegative()
    {
        var path = Path.Combine(FixtureRoot, "Assets", "Shaders", "Custom.shadergraph");
        var content = File.ReadAllText(path);
        Assert.Contains("\\\"guid\\\":\\\"eeeeeeeeeeeeeeeeeeeeeeeeeeeeee05\\\"", content);
        Assert.Contains("b1fb53e3-", content);
    }

    private sealed class ExpectedEdges
    {
        [JsonPropertyName("guids")]
        public Dictionary<string, string> Guids { get; set; } = new();

        [JsonPropertyName("guidEdges")]
        public List<GuidEdge> GuidEdges { get; set; } = new();

        [JsonPropertyName("pathEdges")]
        public List<PathEdge> PathEdges { get; set; } = new();

        [JsonPropertyName("builtinGuids")]
        public List<string> BuiltinGuids { get; set; } = new();

        [JsonPropertyName("unresolvedGuids")]
        public List<string> UnresolvedGuids { get; set; } = new();

        [JsonPropertyName("identityOnlyPaths")]
        public List<string> IdentityOnlyPaths { get; set; } = new();

        [JsonPropertyName("forbiddenSourcePaths")]
        public List<string> ForbiddenSourcePaths { get; set; } = new();

        [JsonPropertyName("forbiddenTargetGuids")]
        public List<string> ForbiddenTargetGuids { get; set; } = new();

        [JsonPropertyName("forbiddenTargetSubstrings")]
        public List<string> ForbiddenTargetSubstrings { get; set; } = new();
    }

    private sealed class GuidEdge
    {
        [JsonPropertyName("source")]
        public string Source { get; set; } = "";

        [JsonPropertyName("targetGuid")]
        public string TargetGuid { get; set; } = "";

        [JsonPropertyName("resolvesTo")]
        public string? ResolvesTo { get; set; }

        [JsonPropertyName("methodName")]
        public string? MethodName { get; set; }

        [JsonPropertyName("gameObject")]
        public string? GameObject { get; set; }

        [JsonPropertyName("builtin")]
        public bool Builtin { get; set; }

        [JsonPropertyName("viaMeta")]
        public bool ViaMeta { get; set; }
    }

    private sealed class PathEdge
    {
        [JsonPropertyName("source")]
        public string Source { get; set; } = "";

        [JsonPropertyName("raw")]
        public string Raw { get; set; } = "";

        [JsonPropertyName("resolvesTo")]
        public string? ResolvesTo { get; set; }
    }
}
