using System.Text.Json;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// who-uses' build-reachable tag (proven referencers can still be
/// irrelevant test/dead content, with nothing to tell them apart).
/// Positive claims only: [build-reachable] is proven forward reachability from the liveness
/// roots; the other case reads "not proven build-reachable" — never "unreachable".
/// </summary>
public class BuildReachabilityTests
{
    [Fact]
    public void WhoUses_ReferencerReachableFromBuildScene_Tagged()
    {
        using var fixture = FixtureCopy.Create();
        // Level.unity is an enabled Build Settings scene — a liveness root's direct content.
        var (exit, stdOut, _) = CliRunner.Run("who-uses", "Assets/Materials/Rock.mat", "-p", fixture.Root);

        Assert.Equal(0, exit);
        Assert.Contains("Assets/Scenes/Level.unity", stdOut);
        Assert.Contains("[build-reachable]", stdOut);
    }

    [Fact]
    public void WhoUses_DeadContentReferencer_NotProven()
    {
        using var fixture = FixtureCopy.Create();
        // Orphan.mat (Assets/Dead/) references orphan.png; nothing reaches the Dead folder
        // from any liveness root.
        var (exit, stdOut, _) = CliRunner.Run("who-uses", "Assets/Dead/orphan.png", "-p", fixture.Root);

        Assert.Equal(0, exit);
        Assert.Contains("Assets/Dead/Orphan.mat", stdOut);
        Assert.Contains("[not proven build-reachable]", stdOut);
    }

    [Fact]
    public void WhoUses_Json_CarriesBuildReachablePerResult()
    {
        using var fixture = FixtureCopy.Create();
        var (exit, stdOut, _) = CliRunner.Run("who-uses", "Assets/Materials/Rock.mat", "-p", fixture.Root, "--json");

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(stdOut);
        var results = doc.RootElement.GetProperty("results").EnumerateArray().ToList();
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.True(r.TryGetProperty("buildReachable", out _)));
        Assert.Contains(results, r =>
            r.GetProperty("source").GetString() == "Assets/Scenes/Level.unity" &&
            r.GetProperty("buildReachable").GetBoolean());
    }

    [Fact]
    public void Uses_Json_DoesNotCarryBuildReachable()
    {
        using var fixture = FixtureCopy.Create();
        var (exit, stdOut, _) = CliRunner.Run("uses", "Assets/Scenes/Level.unity", "-p", fixture.Root, "--json");

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(stdOut);
        var results = doc.RootElement.GetProperty("results").EnumerateArray().ToList();
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.False(r.TryGetProperty("buildReachable", out _)));
    }
}
