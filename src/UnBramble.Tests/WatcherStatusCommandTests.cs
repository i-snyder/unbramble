using System.Text;
using System.Text.Json;
using UnBramble.Core.Config;
using UnBramble.Core.Freshness;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

public class WatcherStatusCommandTests
{
    [Fact]
    public void FreshProject_ReportsAbsentWithoutCreatingState()
    {
        using var fixture = FixtureCopy.Create();

        var (exitCode, stdout, stderr) = CliRunner.Run("watcher-status", fixture.Root, "--json");

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
        using var json = JsonDocument.Parse(stdout);
        var result = json.RootElement;
        Assert.Equal(1, result.GetProperty("unbrambleSchema").GetInt32());
        Assert.Equal(fixture.Root, result.GetProperty("project").GetString());
        Assert.Equal("absent", result.GetProperty("state").GetString());
        Assert.False(result.GetProperty("ownerActive").GetBoolean());
        Assert.False(result.GetProperty("indexReadinessChecked").GetBoolean());
        Assert.Equal("no-owner", result.GetProperty("reason").GetString());
        Assert.Equal(DateTimeKind.Utc, result.GetProperty("checkedAtUtc").GetDateTime().Kind);
        AssertNullObservations(result);
        Assert.False(Directory.Exists(UnBramblePaths.StateDirFor(fixture.Root)));
    }

    [Fact]
    public void ExistingState_DoesNotOpenOrMigrateIndexOrTouchMarkers()
    {
        using var fixture = FixtureCopy.Create();
        var stateDirectory = UnBramblePaths.StateDirFor(fixture.Root);
        Directory.CreateDirectory(stateDirectory);
        var files = new Dictionary<string, byte[]>
        {
            // An index-opening path would fail on this sentinel instead of returning status.
            [UnBramblePaths.DbFileName] = Encoding.UTF8.GetBytes("index must remain unopened"),
            [UnBramblePaths.LastQueryFileName] = Encoding.UTF8.GetBytes("2020-01-01T00:00:00.0000000Z"),
            [UnBramblePaths.SpawnAttemptFileName] = Encoding.UTF8.GetBytes("2020-01-01T00:00:00.0000000Z"),
            [UnBramblePaths.WatcherLockFileName] = [],
        };
        var lastWriteUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var (name, bytes) in files)
        {
            var path = Path.Combine(stateDirectory, name);
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, lastWriteUtc);
        }

        var (exitCode, stdout, _) = CliRunner.Run("watcher-status", "-p", fixture.Root, "--json");

        Assert.Equal(0, exitCode);
        using var json = JsonDocument.Parse(stdout);
        Assert.Equal("absent", json.RootElement.GetProperty("state").GetString());
        Assert.False(json.RootElement.GetProperty("indexReadinessChecked").GetBoolean());
        Assert.Equal(files.Keys.Order(StringComparer.Ordinal), Directory.GetFiles(stateDirectory).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal));
        foreach (var (name, bytes) in files)
        {
            var path = Path.Combine(stateDirectory, name);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(lastWriteUtc, File.GetLastWriteTimeUtc(path));
        }
    }

    [Fact]
    public void UnavailableOwnership_ReportsUnknownWithExplicitNullsAndExitZero()
    {
        using var fixture = FixtureCopy.Create();
        Directory.CreateDirectory(UnBramblePaths.StateDirFor(fixture.Root));
        using var exclusiveOwner = new FileStream(WatcherLock.PathFor(fixture.Root), FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        var (exitCode, stdout, _) = CliRunner.Run("watcher-status", "-p", fixture.Root, "--json");

        Assert.Equal(0, exitCode);
        using var json = JsonDocument.Parse(stdout);
        var result = json.RootElement;
        Assert.Equal("unknown", result.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("ownerActive").ValueKind);
        Assert.False(result.GetProperty("indexReadinessChecked").GetBoolean());
        Assert.Equal("lock-unavailable", result.GetProperty("reason").GetString());
        AssertNullObservations(result);
        AssertNoIndexOrMarkers(fixture.Root);
    }

    [Fact]
    public void MatchingHeartbeat_ReportsOwnerWithoutClaimingIndexReadiness()
    {
        using var fixture = FixtureCopy.Create();
        using var owner = WatcherLock.TryAcquire(fixture.Root);
        Assert.NotNull(owner);
        const int workerPid = 4242;

        var (exitCode, stdout, _) = CliRunner.Run(
            () => owner.PublishFreshness(() => HeartbeatFile.Write(fixture.Root, workerPid, DateTime.UtcNow, Guid.NewGuid().ToString("N"), owner.SessionId)),
            "watcher-status", fixture.Root, "--json");

        Assert.Equal(0, exitCode);
        using var json = JsonDocument.Parse(stdout);
        var result = json.RootElement;
        Assert.Equal("watching", result.GetProperty("state").GetString());
        Assert.True(result.GetProperty("ownerActive").GetBoolean());
        Assert.Equal(workerPid, result.GetProperty("pid").GetInt32());
        Assert.Equal(owner.SessionId, result.GetProperty("watcherSessionId").GetString());
        Assert.Equal(JsonValueKind.String, result.GetProperty("heartbeatUtc").ValueKind);
        Assert.True(result.GetProperty("heartbeatFresh").GetBoolean());
        Assert.False(result.GetProperty("freshnessSuppressed").GetBoolean());
        Assert.False(result.GetProperty("indexReadinessChecked").GetBoolean());
        AssertNoIndexOrMarkers(fixture.Root);
    }

    [Fact]
    public void TextOutput_ExplainsPerQueryFreshnessAndDiagnosticScope()
    {
        using var fixture = FixtureCopy.Create();

        var (exitCode, stdout, _) = CliRunner.Run("watcher-status", fixture.Root);

        Assert.Equal(0, exitCode);
        Assert.Contains("watcher: absent", stdout, StringComparison.Ordinal);
        Assert.Contains("Each reference query establishes freshness", stdout, StringComparison.Ordinal);
        Assert.Contains("absent watcher can make it slower", stdout, StringComparison.Ordinal);
        Assert.Contains("doesn't check index readiness or semantic coverage", stdout, StringComparison.Ordinal);
        Assert.False(Directory.Exists(UnBramblePaths.StateDirFor(fixture.Root)));
    }

    [Fact]
    public void Help_ListsFiniteDiagnosticCommandAndItsExitContract()
    {
        var help = CliRunner.Run("watcher-status", "--help");
        var allHelp = CliRunner.Run("--help");

        Assert.Equal(0, help.ExitCode);
        Assert.Contains("watcher-status [path] [--json]", help.StdOut, StringComparison.Ordinal);
        Assert.Contains("without starting a worker or updating the index", help.StdOut, StringComparison.Ordinal);
        Assert.Contains("Exit 0 includes absent or unknown watchers", help.StdOut, StringComparison.Ordinal);
        Assert.Equal(0, allHelp.ExitCode);
        Assert.Contains("watcher-status [path] [--json]", allHelp.StdOut, StringComparison.Ordinal);
        Assert.Contains("A sandboxed host may also stop workers it launched", allHelp.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void NonUnityDirectory_ExitsOneWithoutCreatingState()
    {
        using var fixture = FixtureCopy.Create();
        File.Delete(fixture.Combine("ProjectSettings", "ProjectVersion.txt"));

        var (exitCode, stdout, stderr) = CliRunner.Run("watcher-status", fixture.Root, "--json");

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("no Unity project found", stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(UnBramblePaths.StateDirFor(fixture.Root)));
    }

    private static void AssertNullObservations(JsonElement result)
    {
        foreach (var name in new[] { "pid", "watcherSessionId", "heartbeatUtc", "heartbeatFresh", "freshnessSuppressed" })
        {
            Assert.Equal(JsonValueKind.Null, result.GetProperty(name).ValueKind);
        }
    }

    private static void AssertNoIndexOrMarkers(string projectRoot)
    {
        Assert.False(File.Exists(UnBramblePaths.RelativeTo(projectRoot, UnBramblePaths.DbRelativePath)));
        Assert.False(File.Exists(UnBramblePaths.RelativeTo(projectRoot, UnBramblePaths.LastQueryRelativePath)));
        Assert.False(File.Exists(UnBramblePaths.RelativeTo(projectRoot, UnBramblePaths.SpawnAttemptRelativePath)));
    }
}
