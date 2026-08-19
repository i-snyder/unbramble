using UnBramble.Core;
using UnBramble.Core.Model;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>The `resolve` contract, including case-insensitivity.</summary>
public class ResolveTests
{
    [Fact]
    public void Resolve_ExactPath_CaseInsensitive_FindsRow()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var matches = engine.Resolve("assets/scripts/foo.cs");

        var single = Assert.Single(matches);
        Assert.Equal("Assets/Scripts/Foo.cs", single.Path, ignoreCase: true);
        Assert.Equal(FileKind.Script, single.Kind);
    }

    [Fact]
    public void Resolve_ExactGuid_FindsRow()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var matches = engine.Resolve("dddddddddddddddddddddddddddddd04");

        var single = Assert.Single(matches);
        Assert.Equal("Assets/Materials/Rock.mat", single.Path, ignoreCase: true);
    }

    [Fact]
    public void Resolve_NameFragment_FuzzyMatchesByPathSubstring()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var matches = engine.Resolve("Foo");

        var single = Assert.Single(matches);
        Assert.Equal("Assets/Scripts/Foo.cs", single.Path, ignoreCase: true);
    }

    [Fact]
    public void Resolve_NoMatch_ReturnsEmpty()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var matches = engine.Resolve("ThisMatchesNothingAtAll");

        Assert.Empty(matches);
    }

    [Fact]
    public void Cli_Resolve_NoMatch_ExitsTwo()
    {
        using var fixture = FixtureCopy.Create();
        _ = CliRunner.Run("init", "-p", fixture.Root);

        var (exitCode, _, stdErr) = CliRunner.Run("resolve", "ThisMatchesNothingAtAll", "-p", fixture.Root);

        Assert.Equal(2, exitCode);
        Assert.Contains("no match", stdErr, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Consistency requirement: `who-uses` answers an unmatched bare guid gracefully (it's a valid
    /// target — direct refs by literal guid), while `resolve` hard-errored on the same input. An
    /// agent probing an unknown guid's identity reaches for `resolve` FIRST, so it hit the error
    /// path on a perfectly good question. A well-formed guid that no indexed asset carries is an
    /// ANSWER ("not in this index"), not a lookup failure.
    /// </summary>
    [Fact]
    public void Cli_Resolve_UnknownButWellFormedGuid_ReportsUnresolvedAndExitsZero()
    {
        using var fixture = FixtureCopy.Create();
        _ = CliRunner.Run("init", "-p", fixture.Root);

        var (exitCode, stdOut, _) = CliRunner.Run("resolve", "0123456789abcdef0123456789abcdef", "-p", fixture.Root);

        Assert.Equal(0, exitCode);
        Assert.Contains("unresolved", stdOut, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cli_Resolve_UnknownButWellFormedGuid_Json_FlagsUnresolvedGuid()
    {
        using var fixture = FixtureCopy.Create();
        _ = CliRunner.Run("init", "-p", fixture.Root);

        var (exitCode, stdOut, _) = CliRunner.Run("resolve", "0123456789abcdef0123456789abcdef", "-p", fixture.Root, "--json");

        Assert.Equal(0, exitCode);
        Assert.Contains("\"unresolvedGuid\":true", stdOut, StringComparison.Ordinal);
        Assert.Contains("\"matches\":[]", stdOut, StringComparison.Ordinal);
    }

    /// <summary>The graceful path must be scoped to guid-SHAPED input only — a non-guid query that
    /// matches nothing is still a failed lookup and still exits 2 (asserted above); and a query
    /// that DOES match must never be flagged as unresolved.</summary>
    [Fact]
    public void Cli_Resolve_MatchingQuery_Json_DoesNotFlagUnresolvedGuid()
    {
        using var fixture = FixtureCopy.Create();
        _ = CliRunner.Run("init", "-p", fixture.Root);

        var (exitCode, stdOut, _) = CliRunner.Run("resolve", "dddddddddddddddddddddddddddddd04", "-p", fixture.Root, "--json");

        Assert.Equal(0, exitCode);
        Assert.Contains("\"unresolvedGuid\":false", stdOut, StringComparison.Ordinal);
    }
}
