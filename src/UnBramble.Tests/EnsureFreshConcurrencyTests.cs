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
            heldLock!.PublishFreshness(() => HeartbeatFile.Write(fixture.Root, pid: 999999, DateTime.UtcNow, engine.StoreInstanceId, heldLock.SessionId));
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

    [Fact]
    public async Task EnsureFresh_IndexBecomesIncompleteAfterWaitBegins_RecoversThroughWriterLock()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        using var watcherLock = WatcherLock.TryAcquire(fixture.Root);
        Assert.NotNull(watcherLock);

        using var waitStarted = new ManualResetEventSlim();
        var task = Task.Run(() => engine.EnsureFresh(
            onPhase: phase =>
            {
                if (phase.Contains("waiting for it", StringComparison.Ordinal))
                {
                    waitStarted.Set();
                }
            }));

        Assert.True(waitStarted.Wait(TimeSpan.FromSeconds(5)), "EnsureFresh never entered its concurrent-writer wait.");
        SetIndexComplete(engine.DbPath, complete: false);

        var outcome = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(outcome.SweepPerformed);
        Assert.Equal("1", ReadIndexComplete(engine.DbPath));
    }

    [Fact]
    public async Task EnsureFresh_ConcurrentWriterCompletesRepair_DoesNotRunRedundantSweep()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);
        SetIndexComplete(engine.DbPath, complete: false);

        var writerLock = IndexWriterLock.TryAcquire(fixture.Root);
        Assert.NotNull(writerLock);
        try
        {
            using var recoveryStarted = new ManualResetEventSlim();
            var scanProgressCalls = 0;
            var task = Task.Run(() => engine.EnsureFresh(
                onScanProgress: _ => Interlocked.Increment(ref scanProgressCalls),
                onPhase: phase =>
                {
                    if (phase.Contains("previous index did not complete", StringComparison.Ordinal))
                    {
                        recoveryStarted.Set();
                    }
                }));

            Assert.True(recoveryStarted.Wait(TimeSpan.FromSeconds(5)), "EnsureFresh never entered incomplete-index recovery.");
            SetIndexComplete(engine.DbPath, complete: true);
            writerLock.Dispose();
            writerLock = null;

            var outcome = await task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(outcome.SweepPerformed);
            Assert.True(outcome.ConcurrentUpdateCompleted);
            Assert.Equal(0, scanProgressCalls);
        }
        finally
        {
            writerLock?.Dispose();
        }
    }

    [Fact]
    public void EnsureFresh_LiveWatcherWithoutCurrentProtocol_RequiresRetirement()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        using var watcherLock = WatcherLock.TryAcquire(fixture.Root);
        Assert.NotNull(watcherLock);
        watcherLock.PublishFreshness(() => WriteRawHeartbeatWithoutProtocol(fixture.Root));

        var exception = Assert.Throws<InvalidOperationException>(() => engine.EnsureFresh());

        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
        Assert.Contains("unbramble stop", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureFresh_FreshHeartbeatForDifferentDatabaseInstance_IsRejected()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        using var watcherLock = WatcherLock.TryAcquire(fixture.Root);
        Assert.NotNull(watcherLock);
        watcherLock.PublishFreshness(() => HeartbeatFile.Write(
            fixture.Root,
            Environment.ProcessId,
            DateTime.UtcNow,
            Guid.NewGuid().ToString("N"),
            watcherLock.SessionId));

        var exception = Assert.Throws<InvalidOperationException>(() => engine.EnsureFresh());
        Assert.Contains("does not match this database", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureFresh_CurrentHeartbeatInheritedByOlderLockOwner_IsRejected()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var currentOwner = WatcherLock.TryAcquire(fixture.Root);
        Assert.NotNull(currentOwner);
        currentOwner.PublishFreshness(() => HeartbeatFile.Write(
            fixture.Root,
            Environment.ProcessId,
            DateTime.UtcNow,
            engine.StoreInstanceId,
            currentOwner.SessionId));
        currentOwner.Dispose();

        using var olderOwner = new FileStream(
            WatcherLock.PathFor(fixture.Root),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var exception = Assert.Throws<InvalidOperationException>(() => engine.EnsureFresh());
        Assert.Contains("legacy watcher", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureFresh_IncompleteIndexWithLegacyWatcher_DoesNotMutateBeforeRejectingOwner()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);
        SetIndexComplete(engine.DbPath, complete: false);

        using var olderOwner = new FileStream(
            WatcherLock.PathFor(fixture.Root),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            engine.EnsureFresh(waitForConcurrentSweep: false));

        Assert.Contains("legacy watcher", exception.Message, StringComparison.Ordinal);
        Assert.Equal("0", ReadIndexComplete(engine.DbPath));
    }

    [Fact]
    public void EnsureFresh_ReplacedDatabaseStartsIncompleteAndCannotTrustOldHeartbeat()
    {
        using var fixture = FixtureCopy.Create();
        string dbPath;
        string oldStoreInstanceId;
        using (var engine = UnBrambleEngine.Open(fixture.Root))
        {
            engine.RunIndex(full: false);
            dbPath = engine.DbPath;
            oldStoreInstanceId = engine.StoreInstanceId;
        }

        using var watcherLock = WatcherLock.TryAcquire(fixture.Root);
        Assert.NotNull(watcherLock);
        watcherLock.PublishFreshness(() => HeartbeatFile.Write(
            fixture.Root,
            Environment.ProcessId,
            DateTime.UtcNow,
            oldStoreInstanceId,
            watcherLock.SessionId));

        SqliteConnection.ClearAllPools();
        File.Delete(dbPath);
        File.Delete(dbPath + "-wal");
        File.Delete(dbPath + "-shm");

        using var replacement = UnBrambleEngine.Open(fixture.Root);
        Assert.NotEqual(oldStoreInstanceId, replacement.StoreInstanceId);

        var outcome = replacement.EnsureFresh();
        Assert.True(outcome.SweepPerformed);
        Assert.NotEmpty(replacement.GetAllFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnsureFresh_RetainsLegacyWatcherGuardThroughQueryLifetime(bool useHeartbeatFastPath)
    {
        using var fixture = FixtureCopy.Create();
        var engine = UnBrambleEngine.Open(fixture.Root);
        WatcherLock.WatcherLease? currentWatcher = null;
        try
        {
            engine.RunIndex(full: false);
            if (useHeartbeatFastPath)
            {
                currentWatcher = WatcherLock.TryAcquire(fixture.Root);
                Assert.NotNull(currentWatcher);
                currentWatcher.PublishFreshness(() => HeartbeatFile.Write(
                    fixture.Root,
                    Environment.ProcessId,
                    DateTime.UtcNow,
                    engine.StoreInstanceId,
                    currentWatcher.SessionId));
            }

            var outcome = engine.EnsureFresh();
            Assert.Equal(!useHeartbeatFastPath, outcome.SweepPerformed);
            currentWatcher?.Dispose();
            currentWatcher = null;

            Assert.Throws<IOException>(() =>
            {
                using var legacyWatcher = new FileStream(
                    WatcherLock.PathFor(fixture.Root),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            });

            var target = engine.ResolveQueryTarget("Assets/Data/B.asset").Target;
            Assert.NotNull(target);
            Assert.Contains(
                engine.WhoUses(target, transitive: false, depthCap: 1).Results,
                result => result.SourcePath == "Assets/Data/A.asset");

            engine.Dispose();
            using var succeedsAfterQuery = new FileStream(
                WatcherLock.PathFor(fixture.Root),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        finally
        {
            currentWatcher?.Dispose();
            engine.Dispose();
        }
    }

    [Fact]
    public async Task EnsureFresh_HoldsSharedReadLeaseThroughQuery_AndBlocksWriterUntilDispose()
    {
        using var fixture = FixtureCopy.Create();
        var engine = UnBrambleEngine.Open(fixture.Root);
        UnBrambleEngine? secondEngine = null;
        try
        {
            engine.RunIndex(full: false);
            using var watcherLock = WatcherLock.TryAcquire(fixture.Root);
            Assert.NotNull(watcherLock);
            watcherLock.PublishFreshness(() => HeartbeatFile.Write(fixture.Root, Environment.ProcessId, DateTime.UtcNow, engine.StoreInstanceId, watcherLock.SessionId));

            var outcome = engine.EnsureFresh();
            Assert.False(outcome.SweepPerformed);

            // Query leases are shared with one another, not a global single-query mutex.
            secondEngine = UnBrambleEngine.Open(fixture.Root);
            var secondOutcome = secondEngine.EnsureFresh();
            Assert.False(secondOutcome.SweepPerformed);

            var writerTask = Task.Run(() =>
            {
                using var writerLease = IndexWriterLock.Acquire(fixture.Root);
            });

            var first = await Task.WhenAny(writerTask, Task.Delay(TimeSpan.FromMilliseconds(600)));
            Assert.NotSame(writerTask, first);

            var target = engine.ResolveQueryTarget("Assets/Scripts/Foo.cs").Target;
            Assert.NotNull(target);
            Assert.NotEmpty(engine.WhoUses(target, transitive: false, depthCap: 1).Results);

            engine.Dispose();
            var afterFirstReader = await Task.WhenAny(writerTask, Task.Delay(TimeSpan.FromMilliseconds(400)));
            Assert.NotSame(writerTask, afterFirstReader);

            secondEngine.Dispose();
            secondEngine = null;
            await writerTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            secondEngine?.Dispose();
            engine.Dispose();
        }
    }

    [Fact]
    public async Task EnsureFresh_WaitingWriterIntent_BlocksLaterReadersUntilWriterFinishes()
    {
        using var fixture = FixtureCopy.Create();
        var firstEngine = UnBrambleEngine.Open(fixture.Root);
        var secondEngine = UnBrambleEngine.Open(fixture.Root);
        using var writerWaiting = new ManualResetEventSlim();
        using var writerAcquired = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        Task? writerTask = null;
        Task<UnBramble.Core.Model.FreshnessOutcome>? secondQueryTask = null;
        try
        {
            firstEngine.RunIndex(full: false);
            using var watcherLock = WatcherLock.TryAcquire(fixture.Root);
            Assert.NotNull(watcherLock);
            watcherLock.PublishFreshness(() => HeartbeatFile.Write(fixture.Root, Environment.ProcessId, DateTime.UtcNow, firstEngine.StoreInstanceId, watcherLock.SessionId));
            firstEngine.EnsureFresh();

            writerTask = Task.Run(() =>
            {
                using var writerLease = IndexWriterLock.Acquire(
                    fixture.Root,
                    message =>
                    {
                        if (message.Contains("active queries", StringComparison.Ordinal))
                        {
                            writerWaiting.Set();
                        }
                    });
                writerAcquired.Set();
                releaseWriter.Wait();
            });

            Assert.True(writerWaiting.Wait(TimeSpan.FromSeconds(5)), "Writer never established intent while the first query held its read lease.");

            secondQueryTask = Task.Run(() => secondEngine.EnsureFresh());
            var beforeFirstReaderExits = await Task.WhenAny(secondQueryTask, Task.Delay(TimeSpan.FromMilliseconds(500)));
            Assert.NotSame(secondQueryTask, beforeFirstReaderExits);

            firstEngine.Dispose();
            Assert.True(writerAcquired.Wait(TimeSpan.FromSeconds(5)), "Writer did not acquire after the existing reader exited.");

            var whileWriterOwnsLock = await Task.WhenAny(secondQueryTask, Task.Delay(TimeSpan.FromMilliseconds(400)));
            Assert.NotSame(secondQueryTask, whileWriterOwnsLock);

            releaseWriter.Set();
            await writerTask.WaitAsync(TimeSpan.FromSeconds(5));
            var outcome = await secondQueryTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(outcome.SweepPerformed);
        }
        finally
        {
            releaseWriter.Set();
            firstEngine.Dispose();
            secondEngine.Dispose();
            if (writerTask is not null)
            {
                await writerTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    private static void SetIndexComplete(string dbPath, bool complete)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE meta_kv SET value = @value WHERE key = 'index_complete';";
        command.Parameters.AddWithValue("@value", complete ? "1" : "0");
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static string? ReadIndexComplete(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta_kv WHERE key = 'index_complete';";
        return command.ExecuteScalar() as string;
    }

    private static void WriteRawHeartbeatWithoutProtocol(string projectRoot)
    {
        var path = HeartbeatFile.PathFor(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var utc = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(
            path,
            $"{{\"pid\":999,\"utc\":\"{utc}\",\"schema\":{UnBramble.Core.Store.UnBrambleStore.CurrentSchemaVersion}}}");
    }
}
