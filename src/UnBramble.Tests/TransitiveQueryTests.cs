using UnBramble.Core;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>Section 6.6: transitive who-uses set/depth correctness and cycle termination.</summary>
public class TransitiveQueryTests
{
    [Fact]
    public void WhoUses_FooCs_Transitive_YieldsExactSetWithMinDepth()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveQueryTarget("Assets/Scripts/Foo.cs");
        var answer = engine.WhoUses(resolution.Target!, transitive: true, depthCap: UnBrambleEngine.DefaultDepthCap);

        var byPath = answer.Results
            .GroupBy(r => r.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Min(r => r.Depth), StringComparer.OrdinalIgnoreCase);

        var expected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Assets/Data/A.asset"] = 1,
            ["Assets/Data/B.asset"] = 1,
            ["Assets/Prefabs/Player.prefab"] = 1,
            ["Assets/Scenes/Level.unity"] = 1,
            ["Assets/Prefabs/Enemy.prefab"] = 2,
            ["ProjectSettings/EditorBuildSettings.asset"] = 2,
        };

        Assert.Equal(expected.Count, byPath.Count);
        foreach (var (path, depth) in expected)
        {
            Assert.True(byPath.TryGetValue(path, out var actualDepth), $"missing '{path}' from transitive who-uses result");
            Assert.Equal(depth, actualDepth);
        }
    }

    [Fact]
    public void WhoUses_AAsset_Transitive_TerminatesOnCycle_BAtDepth1_ASeedAbsent()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        // A.asset <-> B.asset is a genuine 2-cycle (A references B, B references A). A plain
        // UNION recursive CTE would never terminate here; the depth cap must be what stops it.
        var resolution = engine.ResolveQueryTarget("Assets/Data/A.asset");

        // The depth cap is the only cycle terminator (plain UNION never terminates a cycle);
        // simply calling this and getting a return value at all is part of what's under test.
        var answer = engine.WhoUses(resolution.Target!, transitive: true, depthCap: 10);

        var bEdges = answer.Results.Where(r => r.SourcePath.Equals("Assets/Data/B.asset", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(bEdges);
        Assert.Equal(1, bEdges.Min(r => r.Depth));

        Assert.DoesNotContain(answer.Results, r => r.SourcePath.Equals("Assets/Data/A.asset", StringComparison.OrdinalIgnoreCase));

        // The cap is the only thing that stopped the walk on a genuine cycle — the tool must
        // say so rather than silently imply the answer is exhaustive.
        Assert.True(answer.Truncated);
    }

    [Fact]
    public void Uses_Transitive_ForwardWalk_SurfacesUnresolvedEdgesAtEveryDepth()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveQueryTarget("Assets/Scenes/Level.unity");
        var answer = engine.Uses(resolution.Target!, transitive: true, depthCap: UnBrambleEngine.DefaultDepthCap);

        // The unresolved deadbeef ref is a dead end (no target_file_id) — it can never extend
        // the walk, but must still be surfaced as part of the transitive answer.
        Assert.Contains(answer.Results, r => !r.Resolved && !r.Builtin && r.TargetKey == "deadbeefdeadbeefdeadbeefdeadbeef");

        // Rock.mat is reached at depth 1; its own further dependency (rock.png, depth 2) must
        // also appear.
        Assert.Contains(answer.Results, r => r.TargetPath == "Assets/Textures/rock.png" && r.Depth == 2);
    }
}
