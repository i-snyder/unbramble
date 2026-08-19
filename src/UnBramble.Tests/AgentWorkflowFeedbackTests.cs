using System.Text.Json;
using Microsoft.Data.Sqlite;
using UnBramble.Core;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>Regression coverage for the missing-reference audit workflow: missing-reference
/// triage across large assets and batches must be directly consumable by an agent.</summary>
public class AgentWorkflowFeedbackTests
{
    [Fact]
    public void RelativeWindowsPath_ResolvesAcrossQueryVerbs()
    {
        using var fixture = FixtureCopy.Create();
        Assert.Equal(0, CliRunner.Run("init", "-p", fixture.Root).ExitCode);

        var uses = CliRunner.Run("uses", @"Assets\Scenes\Level.unity", "-p", fixture.Root, "--missing-only");
        Assert.Equal(0, uses.ExitCode);
        Assert.Contains("deadbeefdeadbeefdeadbeefdeadbeef", uses.StdOut, StringComparison.OrdinalIgnoreCase);

        var resolve = CliRunner.Run("resolve", @"Assets\Materials\Rock.mat", "-p", fixture.Root);
        Assert.Equal(0, resolve.ExitCode);
        Assert.Contains("Assets/Materials/Rock.mat", resolve.StdOut, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("uses")]
    [InlineData("who-uses")]
    [InlineData("audit-assets")]
    [InlineData("stats")]
    public void VerbHelp_ExitsZeroWithoutOpeningAProject(string verb)
    {
        var result = CliRunner.Run(verb, "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Usage:", result.StdOut, StringComparison.Ordinal);
        Assert.Contains($"unbramble {verb}", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingOnly_SuccessAndExplicitCiExitAreDistinct()
    {
        using var fixture = FixtureCopy.Create();
        Assert.Equal(0, CliRunner.Run("init", "-p", fixture.Root).ExitCode);

        var success = CliRunner.Run("uses", "Assets/Scenes/Level.unity", "-p", fixture.Root, "--missing-only");
        var ciGate = CliRunner.Run("uses", "Assets/Scenes/Level.unity", "-p", fixture.Root, "--missing-only", "--fail-if-found");

        Assert.Equal(0, success.ExitCode);
        Assert.Equal(3, ciGate.ExitCode);
        Assert.Equal(success.StdOut, ciGate.StdOut);
    }

    [Fact]
    public void MissingJson_CarriesOwnerFieldComponentAndBuildContext()
    {
        using var fixture = FixtureCopy.Create();
        Assert.Equal(0, CliRunner.Run("init", "-p", fixture.Root).ExitCode);

        var result = CliRunner.Run("uses", "Assets/Scenes/Level.unity", "-p", fixture.Root, "--missing-only", "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StdOut);
        var item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("deadbeefdeadbeefdeadbeefdeadbeef", item.GetProperty("targetKey").GetString());
        Assert.Equal(114, item.GetProperty("classId").GetInt32());
        Assert.Equal("GameManager", item.GetProperty("gameObject").GetString());
        Assert.Equal("Assets/Scripts/Foo.cs", item.GetProperty("component").GetString());
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa01", item.GetProperty("componentScriptGuid").GetString());
        Assert.Equal("missingThing", item.GetProperty("propertyPath").GetString());
        Assert.False(item.GetProperty("isScriptReference").GetBoolean());
        Assert.True(item.GetProperty("buildReachable").GetBoolean());
    }

    [Fact]
    public void MissingScriptReference_IsExplicitlyClassified()
    {
        using var fixture = FixtureCopy.Create();
        Assert.Equal(0, CliRunner.Run("init", "-p", fixture.Root).ExitCode);

        var result = CliRunner.Run("uses", "Assets/AddressableAssetsData/AddressableAssetSettings.asset", "-p", fixture.Root, "--missing-only", "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StdOut);
        var item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.True(item.GetProperty("isScriptReference").GetBoolean());
        Assert.Equal("m_Script", item.GetProperty("propertyPath").GetString());
        Assert.Equal("a55e7700000000000000000000000005", item.GetProperty("componentScriptGuid").GetString());
    }

    [Fact]
    public void MissingPrefabOverride_CarriesSourcePrefabContext()
    {
        using var fixture = FixtureCopy.Create();
        var prefab = fixture.Combine("Assets", "Prefabs", "BrokenOverride.prefab");
        File.WriteAllText(prefab, """
            %YAML 1.1
            %TAG !u! tag:unity3d.com,2011:
            --- !u!1001 &7000001
            PrefabInstance:
              m_Modification:
                m_Modifications:
                - target: {fileID: 1, guid: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb02, type: 3}
                  propertyPath: _missing
                  value:
                  objectReference: {fileID: 11400000, guid: 99999999999999999999999999999999, type: 2}
              m_SourcePrefab: {fileID: 100100000, guid: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb02, type: 3}
            """);
        File.WriteAllText(prefab + ".meta", "fileFormatVersion: 2\nguid: 98989898989898989898989898989898\n");
        Assert.Equal(0, CliRunner.Run("init", "-p", fixture.Root).ExitCode);

        var result = CliRunner.Run("uses", "Assets/Prefabs/BrokenOverride.prefab", "-p", fixture.Root, "--missing-only", "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StdOut);
        var item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.True(item.GetProperty("isPrefabOverride").GetBoolean());
        Assert.Equal("Assets/Prefabs/Player.prefab", item.GetProperty("prefabSource").GetString());

        var grouped = CliRunner.Run("uses", "Assets/Prefabs/BrokenOverride.prefab", "-p", fixture.Root, "--missing-only", "--summary", "--json");
        Assert.Equal(0, grouped.ExitCode);
        using var groupedDocument = JsonDocument.Parse(grouped.StdOut);
        var group = Assert.Single(groupedDocument.RootElement.GetProperty("groups").EnumerateArray());
        Assert.Contains("Assets/Prefabs/Player.prefab", group.GetProperty("prefabSources").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("m_Modification.m_Modifications[0].objectReference", item.GetProperty("propertyPath").GetString());
    }

    [Fact]
    public void MissingSummary_GroupsRepeatedTargetsAndHonorsTop()
    {
        using var fixture = FixtureCopy.Create();
        Assert.Equal(0, CliRunner.Run("init", "-p", fixture.Root).ExitCode);

        var result = CliRunner.Run("uses", "Assets/Scenes/Level.unity", "-p", fixture.Root, "--missing-only", "--summary", "--top", "1", "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StdOut);
        Assert.True(document.RootElement.GetProperty("grouped").GetBoolean());
        Assert.Empty(document.RootElement.GetProperty("items").EnumerateArray());
        var group = Assert.Single(document.RootElement.GetProperty("groups").EnumerateArray());
        Assert.Equal("deadbeefdeadbeefdeadbeefdeadbeef", group.GetProperty("targetKey").GetString());
        Assert.Contains("missingThing", group.GetProperty("fields").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public void AuditAssets_UsesOneBatchAndReturnsPerTargetResults()
    {
        using var fixture = FixtureCopy.Create();
        Assert.Equal(0, CliRunner.Run("init", "-p", fixture.Root).ExitCode);
        var input = fixture.Combine("audit-assets.txt");
        File.WriteAllText(input, "Assets/Scenes/Level.unity\nAssets/UI/Menu.uxml\n");

        var result = CliRunner.Run("audit-assets", input, "-p", fixture.Root, "--missing", "--json");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Resolving 1/2", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("Resolving 2/2", result.StdErr, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(result.StdOut);
        Assert.Equal(2, document.RootElement.GetProperty("targetCount").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("resolvedTargetCount").GetInt32());
        Assert.Equal(3, document.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("results").GetArrayLength());

        var streamed = CliRunner.Run("audit-assets", input, "-p", fixture.Root, "--missing", "--jsonl");
        Assert.Equal(0, streamed.ExitCode);
        var streamedLines = streamed.StdOut.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, streamedLines.Length);
        foreach (var line in streamedLines)
        {
            using var streamedDocument = JsonDocument.Parse(line);
            Assert.True(streamedDocument.RootElement.TryGetProperty("query", out _));
        }
    }

    [Fact]
    public void UsesPathsAlias_ProvidesTheSameBatchAudit()
    {
        using var fixture = FixtureCopy.Create();
        Assert.Equal(0, CliRunner.Run("init", "-p", fixture.Root).ExitCode);
        var input = fixture.Combine("audit-assets.txt");
        File.WriteAllText(input, "Assets/Scenes/Level.unity\nAssets/UI/Menu.uxml\n");

        var result = CliRunner.Run("uses", "--missing-only", "--paths", input, "-p", fixture.Root, "--summary", "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StdOut);
        Assert.True(document.RootElement.GetProperty("grouped").GetBoolean());
        Assert.NotEmpty(document.RootElement.GetProperty("groups").EnumerateArray());
    }

    [Fact]
    public void WhoUsesGuids_AnswersMultipleTargetsFromOneInvocation()
    {
        using var fixture = FixtureCopy.Create();
        Assert.Equal(0, CliRunner.Run("init", "-p", fixture.Root).ExitCode);
        var input = fixture.Combine("guids.txt");
        File.WriteAllText(input, "dddddddddddddddddddddddddddddd04\ndeadbeefdeadbeefdeadbeefdeadbeef\n");

        var result = CliRunner.Run("who-uses", "--guids", input, "-p", fixture.Root, "--json");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Resolving 1/2", result.StdErr, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(result.StdOut);
        Assert.Equal(2, document.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("results").GetArrayLength());

        var streamed = CliRunner.Run("who-uses", "--guids", input, "-p", fixture.Root, "--jsonl");
        Assert.Equal(0, streamed.ExitCode);
        var streamedLines = streamed.StdOut.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, streamedLines.Length);
        foreach (var line in streamedLines)
        {
            using var streamedDocument = JsonDocument.Parse(line);
            Assert.Equal("who-uses", streamedDocument.RootElement.GetProperty("query").GetString());
        }
    }

    [Fact]
    public void BuildReachabilityCache_PersistsAcrossProcessesAndInvalidatesOnGraphWrites()
    {
        using var fixture = FixtureCopy.Create();
        Assert.Equal(0, CliRunner.Run("init", "-p", fixture.Root).ExitCode);

        string dbPath;
        using (var engine = UnBrambleEngine.Open(fixture.Root))
        {
            dbPath = engine.DbPath;
            Assert.NotEmpty(engine.ComputeBuildReachablePaths());
        }

        Assert.Equal(1, CacheValid(dbPath));
        using (var reopened = UnBrambleEngine.Open(fixture.Root))
        {
            Assert.NotEmpty(reopened.ComputeBuildReachablePaths());
        }

        Assert.Equal(1, CacheValid(dbPath));
        var scene = fixture.Combine("Assets", "Scenes", "Level.unity");
        File.AppendAllText(scene, "\n# cache invalidation regression\n");
        File.SetLastWriteTimeUtc(scene, DateTime.UtcNow.AddSeconds(2));
        using (var changed = UnBrambleEngine.Open(fixture.Root))
        {
            var refresh = changed.EnsureFresh();
            Assert.True(refresh.SweepPerformed);
            Assert.Equal(0, CacheValid(dbPath));
            Assert.NotEmpty(changed.ComputeBuildReachablePaths());
        }

        Assert.Equal(1, CacheValid(dbPath));
    }

    private static long CacheValid(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT valid FROM build_reachable_state WHERE id = 1;";
        return (long)command.ExecuteScalar()!;
    }
}
