using UnBramble.Core.Freshness;
using UnBramble.Core.Store;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// Every query verb begins with a pull-path stat-sweep (previously only wired into init/index),
/// skipped only when a fresh watcher heartbeat is present. The --verbose freshness line is
/// covered here too (the CLI-level counterpart of verify-all's watch-smoke step).
/// </summary>
public class PullSweepEverywhereTests
{
    [Fact]
    public void Cli_Resolve_NeverIndexedBefore_StillFindsMatches()
    {
        // No 'init'/'index' call at all -- resolve alone must build the index via the pull path.
        using var fixture = FixtureCopy.Create();

        var (exitCode, stdOut, _) = CliRunner.Run("resolve", "Assets/Scripts/Foo.cs", "-p", fixture.Root);

        Assert.Equal(0, exitCode);
        Assert.Contains("Assets/Scripts/Foo.cs", stdOut, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cli_Stats_NeverIndexedBefore_ReportsNonZeroFileCount()
    {
        using var fixture = FixtureCopy.Create();

        var (exitCode, stdOut, _) = CliRunner.Run("stats", "-p", fixture.Root);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("Files: 0 ", stdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_WhoUses_PicksUpAChangeMadeAfterInit_WithoutAnExplicitReindex()
    {
        using var fixture = FixtureCopy.Create();
        _ = CliRunner.Run("init", "-p", fixture.Root);

        // A new prefab appears on disk after 'init' -- no 'index' call in between.
        var prefabPath = fixture.Combine("Assets", "Prefabs", "PulledIn.prefab");
        File.WriteAllText(prefabPath, "%YAML 1.1\n--- !u!1 &1\nGameObject:\n  m_Name: X\n");
        File.WriteAllText(prefabPath + ".meta", "fileFormatVersion: 2\nguid: 40404040404040404040404040404004\n");

        var (exitCode, stdOut, _) = CliRunner.Run("resolve", "PulledIn", "-p", fixture.Root);

        Assert.Equal(0, exitCode);
        Assert.Contains("PulledIn.prefab", stdOut, StringComparison.OrdinalIgnoreCase);
    }

    // Freshness lines land on STDOUT in text mode (one ordered stream — see
    // Program.PrintFreshness's stream-routing note); --json keeps them on stderr.

    [Fact]
    public void Cli_WhoUses_Verbose_NoHeartbeat_ReportsSweptFreshnessLine()
    {
        using var fixture = FixtureCopy.Create();
        _ = CliRunner.Run("init", "-p", fixture.Root);

        var (exitCode, stdOut, _) = CliRunner.Run("who-uses", "Assets/Scripts/Foo.cs", "-p", fixture.Root, "--verbose");

        Assert.Equal(0, exitCode);
        Assert.Contains("freshness: swept", stdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_WhoUses_Verbose_FreshHeartbeat_SkipsSweepAndReportsHeartbeatFreshnessLine()
    {
        using var fixture = FixtureCopy.Create();
        _ = CliRunner.Run("init", "-p", fixture.Root);
        HeartbeatFile.Write(fixture.Root, pid: 999, DateTime.UtcNow.AddSeconds(-2));

        var (exitCode, stdOut, _) = CliRunner.Run("who-uses", "Assets/Scripts/Foo.cs", "-p", fixture.Root, "--verbose");

        Assert.Equal(0, exitCode);
        Assert.Contains("freshness: watcher heartbeat", stdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("freshness: swept", stdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_WhoUses_Verbose_StaleHeartbeat_StillSweeps()
    {
        using var fixture = FixtureCopy.Create();
        _ = CliRunner.Run("init", "-p", fixture.Root);
        HeartbeatFile.Write(fixture.Root, pid: 999, DateTime.UtcNow.AddSeconds(-30));

        var (exitCode, stdOut, _) = CliRunner.Run("who-uses", "Assets/Scripts/Foo.cs", "-p", fixture.Root, "--verbose");

        Assert.Equal(0, exitCode);
        Assert.Contains("freshness: swept", stdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_Stats_FreshHeartbeat_SkipsSweep_ButStillAnswersCorrectly()
    {
        using var fixture = FixtureCopy.Create();
        _ = CliRunner.Run("init", "-p", fixture.Root);
        HeartbeatFile.Write(fixture.Root, pid: 999, DateTime.UtcNow.AddSeconds(-1));

        var (exitCode, stdOut, _) = CliRunner.Run("stats", "-p", fixture.Root, "--verbose");

        Assert.Equal(0, exitCode);
        Assert.Contains("freshness: watcher heartbeat", stdOut, StringComparison.Ordinal);
        Assert.Contains("Project:", stdOut, StringComparison.Ordinal);
    }

    // A fresh heartbeat is only trusted when its schema stamp matches this binary's store
    // shape — the schema-bump upgrade scenario: an old-binary watcher keeps heartbeating while
    // the new binary drops and rebuilds the store, and trusting it would answer from empty.

    [Fact]
    public void Cli_WhoUses_FreshHeartbeatWithoutSchemaStamp_NotTrusted_StillSweeps()
    {
        using var fixture = FixtureCopy.Create();
        _ = CliRunner.Run("init", "-p", fixture.Root);

        var (exitCode, stdOut, _) = CliRunner.Run(
            beforeRun: () => WriteRawHeartbeat(fixture.Root, schemaField: null),
            "who-uses", "Assets/Scripts/Foo.cs", "-p", fixture.Root, "--verbose");

        Assert.Equal(0, exitCode);
        Assert.Contains("freshness: swept", stdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("freshness: watcher heartbeat", stdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_WhoUses_FreshHeartbeatWithOldSchemaStamp_NotTrusted_StillSweeps()
    {
        using var fixture = FixtureCopy.Create();
        _ = CliRunner.Run("init", "-p", fixture.Root);

        var (exitCode, stdOut, _) = CliRunner.Run(
            beforeRun: () => WriteRawHeartbeat(fixture.Root, schemaField: UnBrambleStore.CurrentSchemaVersion - 1),
            "who-uses", "Assets/Scripts/Foo.cs", "-p", fixture.Root, "--verbose");

        Assert.Equal(0, exitCode);
        Assert.Contains("freshness: swept", stdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("freshness: watcher heartbeat", stdOut, StringComparison.Ordinal);
    }

    /// <summary>A raw heartbeat file as an OLD binary would write it — HeartbeatFile.Write always
    /// stamps the current schema, so simulating an old writer means bypassing it.</summary>
    private static void WriteRawHeartbeat(string projectRoot, int? schemaField)
    {
        var path = HeartbeatFile.PathFor(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var utc = DateTime.UtcNow.AddSeconds(-2).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var schemaPart = schemaField is { } s ? $",\"schema\":{s}" : "";
        File.WriteAllText(path, $"{{\"pid\":999,\"utc\":\"{utc}\"{schemaPart}}}");
    }

    [Fact]
    public void Cli_WhoUses_Json_KeepsFreshnessOffStdout()
    {
        using var fixture = FixtureCopy.Create();
        _ = CliRunner.Run("init", "-p", fixture.Root);

        var (exitCode, stdOut, stdErr) = CliRunner.Run("who-uses", "Assets/Scripts/Foo.cs", "-p", fixture.Root, "--verbose", "--json");

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("freshness:", stdOut, StringComparison.Ordinal);
        Assert.Contains("freshness: swept", stdErr, StringComparison.Ordinal);
    }
}
