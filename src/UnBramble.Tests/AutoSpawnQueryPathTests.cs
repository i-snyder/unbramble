using System.Reflection;
using System.Text.Json;
using UnBramble.Cli;
using UnBramble.Core.Config;
using UnBramble.Core.Freshness;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// CLI-level coverage that a successful query schedules an automatic watcher only after its
/// output and read lease finish, when the heartbeat was stale/absent. Verified via an injected
/// observer and the crash-loop-guard marker (<see cref="AutoWatchMarkers.RecordSpawnAttempt"/>),
/// not via a real spawned process: this whole test process runs with
/// <c>UNBRAMBLE_DISABLE_AUTO_SPAWN=1</c> (see <see cref="AutoSpawnTestGuard"/>), which skips
/// the actual process launch after the deferred decision is recorded. The injected observer
/// replaces the launch entirely in lifecycle tests. The decision logic itself
/// (<see cref="AutoSpawnPolicy"/>) has its own direct unit tests; this file only exercises that
/// the CLI's query path actually reaches it.
/// </summary>
public class AutoSpawnQueryPathTests
{
    private static string SpawnAttemptMarkerPath(string root) =>
        UnBramblePaths.RelativeTo(root, UnBramblePaths.SpawnAttemptRelativePath);

    [Fact]
    public void Cli_Resolve_AutoSpawnWaitsForCompleteOutputAndReleasedReadLease()
    {
        using var fixture = FixtureCopy.Create();
        Assert.Equal(0, CliRunner.Run("init", "-p", fixture.Root).ExitCode);
        var spawnCalls = 0;
        var writerWasAvailable = false;
        string? outputAtSpawn = null;

        var result = RunWithSpawnObserver((root, output) =>
        {
            spawnCalls++;
            outputAtSpawn = output;
            using var writer = IndexWriterLock.TryAcquire(root);
            writerWasAvailable = writer is not null;
        }, "resolve", "Assets/Scripts/Foo.cs", "-p", fixture.Root, "--json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, spawnCalls);
        Assert.True(writerWasAvailable, "automatic watcher startup ran while its launching query still held the index read lease");
        Assert.Equal(result.StdOut, outputAtSpawn);
        using var answer = JsonDocument.Parse(outputAtSpawn!);
        Assert.NotNull(AutoWatchMarkers.TryReadLastSpawnAttempt(fixture.Root));
    }

    [Fact]
    public void Cli_FailedLookup_DiscardsPendingAutoSpawnWithoutLeakingIntoNextInvocation()
    {
        using var fixture = FixtureCopy.Create();
        Assert.Equal(0, CliRunner.Run("init", "-p", fixture.Root).ExitCode);
        var spawnCalls = 0;

        var failed = RunWithSpawnObserver((_, _) => spawnCalls++,
            "resolve", "NoSuchAsset_4f29", "-p", fixture.Root);
        var next = RunWithSpawnObserver((_, _) => spawnCalls++, "--version");

        Assert.Equal(2, failed.ExitCode);
        Assert.Equal(0, next.ExitCode);
        Assert.Equal(0, spawnCalls);
        Assert.False(File.Exists(SpawnAttemptMarkerPath(fixture.Root)));
    }

    [Fact]
    public void Cli_FindingsGate_IsACompletedQueryAndDefersAutoSpawn()
    {
        using var fixture = FixtureCopy.Create();
        Assert.Equal(0, CliRunner.Run("init", "-p", fixture.Root).ExitCode);
        var spawnCalls = 0;
        var writerWasAvailable = false;

        var result = RunWithSpawnObserver((root, _) =>
        {
            spawnCalls++;
            using var writer = IndexWriterLock.TryAcquire(root);
            writerWasAvailable = writer is not null;
        }, "uses", "Assets/Scenes/Level.unity", "-p", fixture.Root, "--missing-only", "--fail-if-found", "--json");

        Assert.Equal(3, result.ExitCode);
        Assert.Equal(1, spawnCalls);
        Assert.True(writerWasAvailable);
    }

    [Fact]
    public void Cli_AutoSpawnFailure_DoesNotChangeCompletedQueryResult()
    {
        using var fixture = FixtureCopy.Create();
        Assert.Equal(0, CliRunner.Run("init", "-p", fixture.Root).ExitCode);
        var spawnCalls = 0;

        var result = RunWithSpawnObserver((_, _) =>
        {
            spawnCalls++;
            throw new InvalidOperationException("simulated background launch failure");
        }, "resolve", "Assets/Scripts/Foo.cs", "-p", fixture.Root, "--json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, spawnCalls);
        using var answer = JsonDocument.Parse(result.StdOut);
    }

    [Fact]
    public void MonitorStartup_AlwaysRecordsAndTouchesBeforeSpawningWorker()
    {
        using var fixture = FixtureCopy.Create();

        var method = typeof(UnBramble.Cli.Program).GetMethod(
            "EnsureWatcherForMonitor",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        method.Invoke(null, [fixture.Root, false]);

        Assert.NotNull(AutoWatchMarkers.TryReadLastSpawnAttempt(fixture.Root));
        Assert.NotNull(AutoWatchMarkers.TryReadLastQuery(fixture.Root));
    }

    [Fact]
    public void Cli_Resolve_NoHeartbeat_RecordsASpawnAttempt()
    {
        using var fixture = FixtureCopy.Create();
        _ = CliRunner.Run("init", "-p", fixture.Root);

        Assert.False(File.Exists(SpawnAttemptMarkerPath(fixture.Root)));

        var (exitCode, _, _) = CliRunner.Run("resolve", "Assets/Scripts/Foo.cs", "-p", fixture.Root);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(SpawnAttemptMarkerPath(fixture.Root)), "expected the query path to have recorded a spawn attempt when no fresh heartbeat existed");
    }

    [Fact]
    public void Cli_Resolve_FreshHeartbeat_NeverAttemptsSpawn()
    {
        using var fixture = FixtureCopy.Create();
        _ = CliRunner.Run("init", "-p", fixture.Root);
        using var engine = UnBramble.Core.UnBrambleEngine.Open(fixture.Root);
        using var watcher = WatcherLock.TryAcquire(fixture.Root);
        Assert.NotNull(watcher);

        // Written inside the console lock rather than before it: a heartbeat is only fresh for
        // 15s, and this call can queue behind any number of other test classes' CLI runs. See
        // CliRunner.Run(Action, ...).
        var (exitCode, _, _) = CliRunner.Run(
            () => watcher.PublishFreshness(() => HeartbeatFile.Write(fixture.Root, pid: 12345, DateTime.UtcNow, engine.StoreInstanceId, watcher.SessionId)),
            "resolve", "Assets/Scripts/Foo.cs", "-p", fixture.Root);

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(SpawnAttemptMarkerPath(fixture.Root)), "a fresh heartbeat means a watcher already owns freshness -- nothing to auto-spawn for");
    }

    [Fact]
    public void Cli_Resolve_WatchAutoStartDisabledInConfig_NeverAttemptsSpawn()
    {
        using var fixture = FixtureCopy.Create();
        File.WriteAllText(Path.Combine(fixture.Root, "unbramble.json"), """{"watch":{"autoStart":false}}""");
        _ = CliRunner.Run("init", "-p", fixture.Root);

        var (exitCode, _, _) = CliRunner.Run("resolve", "Assets/Scripts/Foo.cs", "-p", fixture.Root);

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(SpawnAttemptMarkerPath(fixture.Root)), "watch.autoStart=false must suppress auto-spawn attempts entirely");
    }

    [Fact]
    public void Cli_Resolve_SecondCallWithinCooldown_DoesNotRecordANewerAttempt()
    {
        using var fixture = FixtureCopy.Create();
        _ = CliRunner.Run("init", "-p", fixture.Root);

        var (firstExit, _, _) = CliRunner.Run("resolve", "Assets/Scripts/Foo.cs", "-p", fixture.Root);
        Assert.Equal(0, firstExit);
        var firstAttempt = AutoWatchMarkers.TryReadLastSpawnAttempt(fixture.Root);
        Assert.NotNull(firstAttempt);

        // A live watcher never actually got spawned (UNBRAMBLE_DISABLE_AUTO_SPAWN), so the
        // heartbeat is still absent/stale -- this second call sweeps again and would attempt
        // another spawn if not for the crash-loop cooldown.
        var (secondExit, _, _) = CliRunner.Run("resolve", "Assets/Scripts/Foo.cs", "-p", fixture.Root);
        Assert.Equal(0, secondExit);
        var secondAttempt = AutoWatchMarkers.TryReadLastSpawnAttempt(fixture.Root);

        Assert.Equal(firstAttempt, secondAttempt);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunWithSpawnObserver(Action<string, string> observeSpawn, params string[] args)
    {
        lock (ConsoleTestLock.Gate)
        {
            var originalOut = Console.Out;
            var originalErr = Console.Error;
            using var stdOut = new StringWriter();
            using var stdErr = new StringWriter();
            Console.SetOut(stdOut);
            Console.SetError(stdErr);
            try
            {
                Action<string> observer = root => observeSpawn(root, stdOut.ToString());
                var method = typeof(Program).GetMethod("RunWithDeferredWatcherStart", BindingFlags.NonPublic | BindingFlags.Static)!;
                var exitCode = (int)method.Invoke(null, [args, observer])!;
                return (exitCode, stdOut.ToString(), stdErr.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }
        }
    }
}
