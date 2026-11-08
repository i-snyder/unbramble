using System.Text.Json;
using UnBramble.Core;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

public class ResolveTruncationTests
{
    private const string Fragment = "ResolveLimitProbe";
    private const string CollisionGuid = "babe0000000000000000000000000001";

    [Theory]
    [InlineData(19, false)]
    [InlineData(20, false)]
    [InlineData(21, true)]
    public void DefaultLimit_DistinguishesCompleteAndTruncatedResults(int count, bool truncated)
    {
        using var fixture = FixtureCopy.Create();
        SeedMatches(fixture, count);
        var expected = Enumerable.Range(0, Math.Min(count, 20)).Select(MatchPath).ToArray();
        using (var engine = UnBrambleEngine.Open(fixture.Root))
        {
            engine.RunIndex(full: false);
            var result = engine.ResolveWithDetails(Fragment);
            Assert.Equal(20, result.Limit);
            Assert.Equal(truncated, result.Truncated);
            Assert.Equal(expected, result.Matches.Select(match => match.Path));
            Assert.Equal(expected, engine.Resolve(Fragment).Select(match => match.Path));
        }

        var (exit, stdout, _) = CliRunner.Run("resolve", Fragment, "-p", fixture.Root, "--json");
        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(stdout);
        Assert.Equal(20, json.RootElement.GetProperty("limit").GetInt32());
        Assert.Equal(truncated, json.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(expected, JsonPaths(json));
        Assert.False(json.RootElement.GetProperty("unresolvedGuid").GetBoolean());
    }

    [Theory]
    [InlineData("5", 5, true)]
    [InlineData("all", 21, false)]
    [InlineData("2147483647", 21, false)]
    public void ExplicitLimitAndAll_ControlFuzzyResults(string option, int expectedCount, bool truncated)
    {
        using var fixture = FixtureCopy.Create();
        SeedMatches(fixture, 21);
        int? limit = option == "all" ? null : int.Parse(option, System.Globalization.CultureInfo.InvariantCulture);
        using (var engine = UnBrambleEngine.Open(fixture.Root))
        {
            engine.RunIndex(full: false);
            var result = engine.ResolveWithDetails(Fragment, limit);
            Assert.Equal(limit, result.Limit);
            Assert.Equal(truncated, result.Truncated);
            Assert.Equal(expectedCount, result.Matches.Count);
        }

        string[] options = option == "all" ? ["--all"] : ["--limit", option];
        var (exit, stdout, _) = CliRunner.Run(["resolve", Fragment, "-p", fixture.Root, "--json", .. options]);
        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(stdout);
        Assert.Equal(Enumerable.Range(0, expectedCount).Select(MatchPath), JsonPaths(json));
        Assert.Equal(truncated, json.RootElement.GetProperty("truncated").GetBoolean());
        if (limit is null)
        {
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("limit").ValueKind);
        }
        else
        {
            Assert.Equal(limit, json.RootElement.GetProperty("limit").GetInt32());
        }
    }

    [Fact]
    public void TextOutput_WarnsOnlyWhenTruncated_AndNamesExpansionOptions()
    {
        using var fixture = FixtureCopy.Create();
        SeedMatches(fixture, 21);

        var limited = CliRunner.Run("resolve", Fragment, "-p", fixture.Root);
        Assert.Equal(0, limited.ExitCode);
        Assert.Equal(20, limited.StdOut.Split('\n').Count(line => line.Contains(Fragment, StringComparison.Ordinal)));
        Assert.Contains("truncat", limited.StdErr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--limit", limited.StdErr, StringComparison.Ordinal);
        Assert.Contains("--all", limited.StdErr, StringComparison.Ordinal);

        var complete = CliRunner.Run("resolve", Fragment, "-p", fixture.Root, "--limit", "21");
        Assert.Equal(0, complete.ExitCode);
        Assert.DoesNotContain("truncat", complete.StdErr, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(21, complete.StdOut.Split('\n').Count(line => line.Contains(Fragment, StringComparison.Ordinal)));
    }

    [Fact]
    public void ExactPath_TakesPrecedenceOverFuzzyMatches_AndIsUncapped()
    {
        using var fixture = FixtureCopy.Create();
        const string name = Fragment + ".asset";
        var exactPath = "Assets/" + name;
        AddAsset(fixture, name);
        for (var i = 0; i < 21; i++)
        {
            AddAsset(fixture, name + $".copy{i:000}.asset");
        }

        using (var engine = UnBrambleEngine.Open(fixture.Root))
        {
            engine.RunIndex(full: false);
            var result = engine.ResolveWithDetails(exactPath.ToLowerInvariant(), limit: 1);
            Assert.Equal(exactPath, Assert.Single(result.Matches).Path);
            Assert.Null(result.Limit);
            Assert.False(result.Truncated);
        }

        var (exit, stdout, _) = CliRunner.Run("resolve", exactPath, "-p", fixture.Root, "--limit", "1", "--json");
        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(stdout);
        Assert.Equal(exactPath, Assert.Single(JsonPaths(json)));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("limit").ValueKind);
        Assert.False(json.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void ExactGuid_ReturnsEveryCollision_RegardlessOfFuzzyLimit()
    {
        using var fixture = FixtureCopy.Create();
        SeedMatches(fixture, 21, CollisionGuid);
        var expected = Enumerable.Range(0, 21).Select(MatchPath).ToArray();
        using (var engine = UnBrambleEngine.Open(fixture.Root))
        {
            engine.RunIndex(full: false);
            var result = engine.ResolveWithDetails(CollisionGuid.ToUpperInvariant(), limit: 1);
            Assert.Equal(expected, result.Matches.Select(match => match.Path));
            Assert.Null(result.Limit);
            Assert.False(result.Truncated);
            Assert.Equal(21, engine.Resolve(CollisionGuid).Count);
        }

        var (exit, stdout, _) = CliRunner.Run("resolve", CollisionGuid, "-p", fixture.Root, "--limit", "1", "--json");
        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(stdout);
        Assert.Equal(expected, JsonPaths(json));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("limit").ValueKind);
        Assert.False(json.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData("ResolveAbsentProbe", 2, false)]
    [InlineData("0123456789abcdef0123456789abcdef", 0, true)]
    public void EmptyResults_PreserveExitAndUnknownGuidContracts(string query, int expectedExit, bool unresolvedGuid)
    {
        using var fixture = FixtureCopy.Create();
        var (exit, stdout, _) = CliRunner.Run("resolve", query, "-p", fixture.Root, "--json");
        Assert.Equal(expectedExit, exit);
        using var json = JsonDocument.Parse(stdout);
        Assert.Empty(JsonPaths(json));
        Assert.False(json.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(20, json.RootElement.GetProperty("limit").GetInt32());
        Assert.Equal(unresolvedGuid, json.RootElement.GetProperty("unresolvedGuid").GetBoolean());
    }

    [Theory]
    [InlineData("Resolve_Probe")]
    [InlineData("Resolve%Probe")]
    public void FuzzySearch_TreatsLikeWildcardsAsLiteralCharacters(string fragment)
    {
        using var fixture = FixtureCopy.Create();
        AddAsset(fixture, fragment + ".asset");
        AddAsset(fixture, "ResolveXProbe.asset");
        AddAsset(fixture, "ResolveManyCharactersProbe.asset");
        var (exit, stdout, _) = CliRunner.Run("resolve", fragment, "-p", fixture.Root, "--all", "--json");
        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(stdout);
        Assert.Equal("Assets/" + fragment + ".asset", Assert.Single(JsonPaths(json)));
        Assert.False(json.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("nope")]
    [InlineData("2147483648")]
    public void Cli_RejectsInvalidLimitsBeforeIndexing(string limit)
    {
        using var fixture = FixtureCopy.Create();
        var result = CliRunner.Run("resolve", Fragment, "-p", fixture.Root, "--limit", limit);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--limit", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("positive integer", result.StdErr, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(fixture.Combine(".unbramble")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cli_RejectsAllCombinedWithLimit(bool allFirst)
    {
        using var fixture = FixtureCopy.Create();
        string[] options = allFirst ? ["--all", "--limit", "5"] : ["--limit", "5", "--all"];
        var result = CliRunner.Run(["resolve", Fragment, "-p", fixture.Root, .. options]);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--all", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("--limit", result.StdErr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.Combine(".unbramble")));
    }

    [Fact]
    public void Cli_RejectsMissingLimitValue()
    {
        using var fixture = FixtureCopy.Create();
        var result = CliRunner.Run("resolve", Fragment, "-p", fixture.Root, "--limit");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--limit", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("requires a value", result.StdErr, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Engine_RejectsNonPositiveLimits(int limit)
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.ResolveWithDetails(Fragment, limit));
    }

    private static string[] JsonPaths(JsonDocument json) =>
        [.. json.RootElement.GetProperty("matches").EnumerateArray().Select(match => match.GetProperty("path").GetString()!)];

    private static string MatchPath(int index) => $"Assets/{Fragment}{index:000}.asset";

    private static void SeedMatches(FixtureCopy fixture, int count, string? guid = null)
    {
        // Reverse insertion order ensures the result's path ordering comes from the query.
        for (var i = count - 1; i >= 0; i--)
        {
            AddAsset(fixture, $"{Fragment}{i:000}.asset", guid);
        }
    }

    private static void AddAsset(FixtureCopy fixture, string name, string? guid = null)
    {
        var path = fixture.Combine("Assets", name);
        File.WriteAllText(path, "%YAML 1.1\n--- !u!114 &11400000\nMonoBehaviour:\n  m_Name: Resolve probe\n");
        File.WriteAllText(path + ".meta", $"fileFormatVersion: 2\nguid: {guid ?? Guid.NewGuid().ToString("N")}\n");
    }
}
