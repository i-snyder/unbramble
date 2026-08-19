using Microsoft.Data.Sqlite;
using UnBramble.Core;
using UnBramble.Core.Config;
using UnBramble.Core.Freshness;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// Regression coverage for the "database is locked" race found live against a large real
/// project: a just-promoted watcher's first catch-up sweep can take minutes before it writes its
/// first heartbeat (see <see cref="WatcherHost.Promote"/>), and every other query verb's own
/// <see cref="UnBrambleEngine.EnsureFresh"/> used to see that same stale/missing heartbeat and
/// start its OWN competing full sweep -- two independent single-transaction writers colliding on
/// the same SQLite file far longer than <c>PRAGMA busy_timeout</c> tolerates. These tests hold
/// <see cref="WatcherLock"/> directly (the same handle a real watcher holds for its whole
/// lifetime, not just its initial sweep) to simulate "another process already owns freshness"
/// without needing a real second OS process, the same pattern <see cref="WatcherAutoModeTests"/>
/// already uses for the analogous <c>TryStartOnceForAuto</c> contention case.
/// </summary>
public class EnsureFreshConcurrencyTests
{
    [Fact]
    public void Stats_CurrentSchema_RealWriterTransaction_ReturnsCommittedSnapshotInsteadOfDatabaseLocked()
    {
        using var fixture = FixtureCopy.Create();
        using (var engine = UnBrambleEngine.Open(fixture.Root))
        {
            engine.RunIndex(full: false);
        }

        using var writerLease = IndexWriterLock.TryAcquire(fixture.Root);
        Assert.NotNull(writerLease);

        using var writer = new SqliteConnection($"Data Source={UnBramblePaths.RelativeTo(fixture.Root, UnBramblePaths.DbRelativePath)}");
        writer.Open();
        using var transaction = writer.BeginTransaction();
        using (var command = writer.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE meta_kv SET value = value WHERE key = 'unity_version';";
            command.ExecuteNonQuery();
        }

        var result = CliRunner.Run("stats", "-p", fixture.Root);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("another unbramble process is currently updating", result.StdOut);
        Assert.DoesNotContain("database is locked", result.StdErr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureFresh_OrdinaryWriterHeldElsewhere_WaitsThenTakesOverWithoutSqliteRace()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var heldLock = IndexWriterLock.TryAcquire(fixture.Root);
        Assert.NotNull(heldLock);
        _ = Task.Run(() =>
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(600));
            heldLock.Dispose();
        });

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var outcome = engine.EnsureFresh();
        stopwatch.Stop();

        Assert.True(outcome.SweepPerformed);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(500));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void EnsureFresh_LockHeldElsewhere_DefaultWaits_PicksUpHeartbeatInsteadOfCompetingSweep()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        using var heldLock = WatcherLock.TryAcquire(fixture.Root);
        Assert.NotNull(heldLock);

        // Simulates the other (lock-holding) process finishing its sweep and writing its
        // heartbeat while STILL holding the lock -- exactly what a real WatcherHost.Promote does.
        _ = Task.Run(() =>
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(600));
            HeartbeatFile.Write(fixture.Root, pid: 999999, DateTime.UtcNow);
        });

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var outcome = engine.EnsureFresh();
        stopwatch.Stop();

        // Picked up the other writer's heartbeat rather than running its own RunIndex -- the
        // whole point of the fix (no duplicate, colliding sweep).
        Assert.False(outcome.SweepPerformed);
        Assert.False(outcome.ConcurrentSweepInProgress);
        Assert.NotNull(outcome.HeartbeatAge);

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(500), $"returned after {stopwatch.Elapsed} -- expected it to actually wait for the heartbeat, not race past it.");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"EnsureFresh took {stopwatch.Elapsed} to notice the heartbeat -- polling looks broken.");
    }

    [Fact]
    public void EnsureFresh_LockHeldElsewhere_NonWaitingMode_ReturnsImmediately()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        using var heldLock = WatcherLock.TryAcquire(fixture.Root);
        Assert.NotNull(heldLock);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var outcome = engine.EnsureFresh(waitForConcurrentSweep: false);
        stopwatch.Stop();

        Assert.False(outcome.SweepPerformed);
        Assert.True(outcome.ConcurrentSweepInProgress);
        Assert.Null(outcome.Summary);
        Assert.Null(outcome.HeartbeatAge);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"EnsureFresh(waitForConcurrentSweep: false) took {stopwatch.Elapsed} -- expected an instant return, same as `stats` needs to not stall behind another process's first index.");
    }

    [Fact]
    public void EnsureFresh_LockHeldElsewhere_OtherWriterDisappearsWithoutHeartbeat_TakesOverTheSweep()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var heldLock = WatcherLock.TryAcquire(fixture.Root);
        Assert.NotNull(heldLock);

        // Simulates the other process crashing mid-sweep -- the OS releases the lock the instant
        // it exits (WatcherLock's own doc comment), but it never got to write a heartbeat.
        _ = Task.Run(() =>
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(600));
            heldLock.Dispose();
        });

        var outcome = engine.EnsureFresh();

        // Nothing left to wait on once the lock frees up with no heartbeat -- this call must take
        // over and sweep itself rather than waiting forever.
        Assert.True(outcome.SweepPerformed);
        Assert.NotNull(outcome.Summary);
    }
}
