using UnBramble.Core.Config;
using UnBramble.Core.Freshness;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// CLI-level coverage that a query verb's freshness preamble (<c>Program.PrintFreshness</c> ->
/// <c>Program.MaybeAutoSpawnWatch</c>, docs/architecture.md "Auto-spawn watcher") actually
/// invokes the spawn-attempt DECISION when the heartbeat was stale/absent -- verified via the
/// crash-loop-guard marker file it writes (<see cref="AutoWatchMarkers.RecordSpawnAttempt"/>),
/// not via a real spawned process: this whole test process runs with
/// <c>UNBRAMBLE_DISABLE_AUTO_SPAWN=1</c> (see <see cref="AutoSpawnTestGuard"/>), which stops
/// <c>Program.MaybeAutoSpawnWatch</c> right before the actual <see
/// cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/> call -- after
/// it has already made and recorded its decision. The decision LOGIC itself
/// (<see cref="AutoSpawnPolicy"/>) has its own direct unit tests; this file only exercises that
/// the CLI's query path actually reaches it.
/// </summary>
public class AutoSpawnQueryPathTests
{
    private static string SpawnAttemptMarkerPath(string root) =>
        UnBramblePaths.RelativeTo(root, UnBramblePaths.SpawnAttemptRelativePath);

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

        // Written inside the console lock rather than before it: a heartbeat is only fresh for
        // 15s, and this call can queue behind any number of other test classes' CLI runs. See
        // CliRunner.Run(Action, ...).
        var (exitCode, _, _) = CliRunner.Run(
            () => HeartbeatFile.Write(fixture.Root, pid: 12345, DateTime.UtcNow),
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
}
