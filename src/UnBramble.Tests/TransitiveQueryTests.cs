using System.Reflection;
using Microsoft.Data.Sqlite;
using UnBramble.Core;
using UnBramble.Core.Store;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>Section 6.6: transitive who-uses set/depth correctness and cycle termination.</summary>
public class TransitiveQueryTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void ConvergingRoutes_CompleteWithinWorkBudget_PreserveDepthAndCap(bool forward, bool cycle)
    {
        using var temp = TempDir.Create();
        using var store = UnBrambleStore.OpenOrCreate(Path.Combine(temp.Root, "graph.db"), "6000.0.0f1");
        // Access the live connection only to impose a deterministic SQLite work budget.
        // A regression must fail promptly instead of hanging the test runner on 3^16 routes.
        var connection = (SqliteConnection)typeof(UnBrambleStore)
            .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;

        const int layerCount = 16;
        var layers = new List<long[]> { new long[] { 1 } };
        for (var layer = 0; layer < layerCount; layer++)
        {
            layers.Add(Enumerable.Range(0, 3).Select(i => (long)(2 + layer * 3 + i)).ToArray());
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO files(id, path, kind, mtime, size) VALUES (@id, @path, 'asset', 0, 0);";
            var idParameter = command.Parameters.Add("@id", SqliteType.Integer);
            var pathParameter = command.Parameters.Add("@path", SqliteType.Text);
            foreach (var id in layers.SelectMany(layer => layer))
            {
                idParameter.Value = id;
                pathParameter.Value = $"Assets/Node{id}.asset";
                command.ExecuteNonQuery();
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO path_refs(source_file_id, target_path_raw, target_path_norm, line)
                SELECT @source, path, path, 1 FROM files WHERE id = @target;
                """;
            var sourceParameter = command.Parameters.Add("@source", SqliteType.Integer);
            var targetParameter = command.Parameters.Add("@target", SqliteType.Integer);
            for (var layer = 1; layer <= layerCount; layer++)
            {
                foreach (var source in layers[layer - 1])
                foreach (var target in layers[layer])
                {
                    sourceParameter.Value = source;
                    targetParameter.Value = target;
                    command.ExecuteNonQuery();
                }
            }

            if (cycle)
            {
                foreach (var source in layers[^1])
                {
                    sourceParameter.Value = source;
                    targetParameter.Value = 1;
                    command.ExecuteNonQuery();
                }
            }
        }

        // Forward starts at the single root; reverse starts at one of the final leaves.
        var seed = forward ? 1 : layers[^1][0];
        var callbacks = 0;
        SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 1000, _ => ++callbacks > 2000 ? 1 : 0, null);
        try
        {
            var closure = forward ? store.GetForwardClosure(seed, 30) : store.GetReverseClosure(seed, 30);
            var expected = new Dictionary<long, int>();
            for (var layer = forward ? 1 : 0; layer < (forward || cycle ? layers.Count : layerCount); layer++)
            {
                foreach (var id in layers[layer])
                {
                    if (id != seed)
                    {
                        expected[id] = forward ? layer : layer == layerCount ? layerCount + 1 : layerCount - layer;
                    }
                }
            }

            Assert.Equal(expected.OrderBy(kv => kv.Key), closure.OrderBy(kv => kv.Key));
            callbacks = 0;
            Assert.Equal(cycle, store.WalkHitDepthCap(forward, seed, 30));
            callbacks = 0;
            Assert.True(store.WalkHitDepthCap(forward, seed, layerCount));
        }
        finally
        {
            SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
        }
    }

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

        // A.asset <-> B.asset is a genuine 2-cycle. Deduplicating (file, depth) states still
        // revisits each file at new depths, so the cap must stop the walk.
        var resolution = engine.ResolveQueryTarget("Assets/Data/A.asset");

        // Calling this and getting a return value is part of what's under test.
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
