using System.Runtime.Versioning;
using System.Text;
using UnBramble.Core;
using UnBramble.Core.Freshness;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

public class WatcherLockSessionTests
{
    [Fact]
    public async Task StableSnapshot_BlocksOwnerDisposalAndHandoffUntilReleased_AndNewOwnerReturnsOnlyAfterInvalidation()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        using var oldOwner = WatcherLock.TryAcquire(fixture.Root);
        Assert.NotNull(oldOwner);
        oldOwner.PublishFreshness(() => HeartbeatFile.Write(
            fixture.Root,
            Environment.ProcessId,
            DateTime.UtcNow,
            engine.StoreInstanceId,
            oldOwner.SessionId));

        using var snapshot = WatcherLock.TryAcquireSnapshot(fixture.Root, out var unavailableReason);
        Assert.NotNull(snapshot);
        Assert.Equal(WatcherLock.SnapshotUnavailableReason.None, unavailableReason);
        Assert.True(snapshot.IsWatcherActive);
        Assert.Equal(oldOwner.SessionId, snapshot.SessionId);

        var disposeTask = Task.Run(oldOwner.Dispose);
        var whileSnapshotHeld = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromMilliseconds(500)));
        Assert.NotSame(disposeTask, whileSnapshotHeld);

        // Disposal and handoff both wait for the transition gate while a query owns this stable
        // snapshot, so neither can change ownership halfway through the snapshot.
        Assert.Null(WatcherLock.TryAcquire(fixture.Root));
        Assert.NotNull(HeartbeatFile.TryRead(fixture.Root));

        snapshot.Dispose();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        using var newOwner = WatcherLock.TryAcquire(fixture.Root);
        Assert.NotNull(newOwner);
        Assert.Null(HeartbeatFile.TryRead(fixture.Root));

        using var newSnapshot = WatcherLock.TryAcquireSnapshot(fixture.Root, out unavailableReason);
        Assert.NotNull(newSnapshot);
        Assert.True(newSnapshot.IsWatcherActive);
        Assert.Equal(newOwner.SessionId, newSnapshot.SessionId);
    }

    [Fact]
    public void NoOwnerSnapshot_HoldsBothGatesAgainstAConcurrentPromotion()
    {
        using var fixture = FixtureCopy.Create();
        using var snapshot = WatcherLock.TryAcquireSnapshot(fixture.Root, out var unavailableReason);
        Assert.NotNull(snapshot);
        Assert.Equal(WatcherLock.SnapshotUnavailableReason.None, unavailableReason);
        Assert.False(snapshot.IsWatcherActive);

        Assert.Null(WatcherLock.TryAcquire(fixture.Root));
    }

    [Fact]
    public async Task SuppressedFreshness_BlocksFastPathEvenWhenHeartbeatDeletionIsDenied()
    {
        using var fixture = FixtureCopy.Create();
        using var writer = UnBrambleEngine.Open(fixture.Root);
        writer.RunIndex(full: false);
        using var owner = WatcherLock.TryAcquire(fixture.Root);
        Assert.NotNull(owner);
        owner.PublishFreshness(() => HeartbeatFile.Write(
            fixture.Root,
            Environment.ProcessId,
            DateTime.UtcNow,
            writer.StoreInstanceId,
            owner.SessionId));

        using (var heartbeatBlocker = new FileStream(
            HeartbeatFile.PathFor(fixture.Root),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite))
        {
            Assert.ThrowsAny<IOException>(() => owner.SuppressFreshness(() => HeartbeatFile.Invalidate(fixture.Root)));
            Assert.NotNull(HeartbeatFile.TryRead(fixture.Root));

            using var snapshot = WatcherLock.TryAcquireSnapshot(fixture.Root, out var unavailableReason);
            Assert.NotNull(snapshot);
            Assert.Equal(WatcherLock.SnapshotUnavailableReason.None, unavailableReason);
            Assert.True(snapshot.IsWatcherActive);
            Assert.True(snapshot.IsFreshnessSuppressed);
        }

        using var reader = UnBrambleEngine.Open(fixture.Root);
        var queryTask = Task.Run(() => reader.EnsureFresh());
        var whileSuppressed = await Task.WhenAny(queryTask, Task.Delay(TimeSpan.FromMilliseconds(500)));
        Assert.NotSame(queryTask, whileSuppressed);

        owner.PublishFreshness(() => HeartbeatFile.Write(
            fixture.Root,
            Environment.ProcessId,
            DateTime.UtcNow,
            writer.StoreInstanceId,
            owner.SessionId));

        var outcome = await queryTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(outcome.SweepPerformed);
        Assert.NotNull(outcome.HeartbeatAge);
    }

    [Fact]
    public void Snapshot_ReprobesOwnerAfterProcessDeathReleasesSuppressedLocks()
    {
        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);
        var sessionId = Guid.NewGuid().ToString("N");
        var path = WatcherLock.PathFor(fixture.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var simulatedProcess = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite);
        simulatedProcess.SetLength(3);
        simulatedProcess.Position = 3;
        simulatedProcess.Write(Encoding.UTF8.GetBytes(sessionId));
        simulatedProcess.Flush(flushToDisk: true);
        simulatedProcess.Lock(0, 1);
        simulatedProcess.Lock(2, 1);
        HeartbeatFile.Write(
            fixture.Root,
            Environment.ProcessId,
            DateTime.UtcNow,
            engine.StoreInstanceId,
            sessionId);

        using var snapshot = WatcherLock.TryAcquireSnapshot(
            fixture.Root,
            out var unavailableReason,
            afterInitialOwnerProbe: () =>
            {
                // OS process death releases both locks without acquiring the transition byte.
                if (!OperatingSystem.IsMacOS())
                {
                    ReleaseSimulatedProcessLocks(simulatedProcess);
                }
            });

        Assert.NotNull(snapshot);
        Assert.Equal(WatcherLock.SnapshotUnavailableReason.None, unavailableReason);
        Assert.False(snapshot.IsWatcherActive);
        Assert.False(snapshot.IsFreshnessSuppressed);
    }

    [UnsupportedOSPlatform("macos")]
    private static void ReleaseSimulatedProcessLocks(FileStream stream)
    {
        stream.Unlock(2, 1);
        stream.Unlock(0, 1);
    }
}
