using UnBramble.Core;
using UnBramble.Core.Freshness;
using UnBramble.Core.Monitoring;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

public class WatcherStatusOwnershipTests
{
    // No periodic status writes can hide a contender's overwrite during these synchronous tests.
    private static WatcherOptions QuietOptions() => new(
        DebounceInterval: TimeSpan.FromHours(1),
        HeartbeatIdleCadence: TimeSpan.FromHours(1),
        LockRetryInterval: TimeSpan.FromHours(1),
        SelfHealSweepInterval: TimeSpan.FromHours(1));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonOwner_StopAndDispose_PreserveActiveWatchersStatus(bool attemptStart)
    {
        using var fixture = FixtureCopy.Create();
        using var ownerEngine = UnBrambleEngine.Open(fixture.Root);
        ownerEngine.RunIndex(full: false);
        using var contenderEngine = UnBrambleEngine.Open(fixture.Root);
        var ownerTracker = new WatchStatusTracker(fixture.Root, pid: 101);
        var contenderTracker = new WatchStatusTracker(fixture.Root, pid: 202);
        using var owner = new WatcherHost(ownerEngine, QuietOptions(), ownerTracker.OnWatcherEvent);
        using var contender = new WatcherHost(contenderEngine, QuietOptions(), contenderTracker.OnWatcherEvent);
        Assert.True(owner.TryStartOnceForAuto());
        var before = File.ReadAllBytes(WatchStatusFile.PathFor(fixture.Root));

        if (attemptStart)
        {
            Assert.False(contender.TryStartOnceForAuto());
        }

        // Mirrors the worker's explicit finally cleanup followed by using-var disposal.
        contender.Stop();
        contender.Dispose();

        Assert.Equal(before, File.ReadAllBytes(WatchStatusFile.PathFor(fixture.Root)));
        var status = WatchStatusFile.TryRead(fixture.Root)!;
        Assert.Equal(101, status.Pid);
        Assert.Equal(WatchActivityState.Watching, status.State);
        Assert.True(owner.IsActive);
    }

    [Fact]
    public void OwnerStop_PublishesStoppedBeforeReleasingLease_ExactlyOnce()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);
        var tracker = new WatchStatusTracker(fixture.Root, pid: 303);
        var stoppedCount = 0;
        var heldLeaseWhilePublishing = false;
        var ownerActiveWhilePublishing = false;
        var freshnessSuppressedWhilePublishing = false;
        using var host = new WatcherHost(engine, QuietOptions(), onEvent: e =>
        {
            if (e == WatcherEvent.Stopped)
            {
                stoppedCount++;
                using (var competingLease = WatcherLock.TryAcquire(fixture.Root))
                {
                    heldLeaseWhilePublishing = competingLease is null;
                }

                using var ownerSnapshot = WatcherLock.TryAcquireSnapshot(fixture.Root, out _);
                ownerActiveWhilePublishing = ownerSnapshot?.IsWatcherActive == true;
                freshnessSuppressedWhilePublishing = ownerSnapshot?.IsFreshnessSuppressed == true;
            }

            tracker.OnWatcherEvent(e);
        });
        Assert.True(host.TryStartOnceForAuto());

        host.Stop();
        host.Stop();
        host.Dispose();

        Assert.True(heldLeaseWhilePublishing, "a replacement watcher could acquire the lease before the old STOPPED status was published");
        Assert.True(ownerActiveWhilePublishing, "the stopped status must still belong to the active owner session");
        Assert.True(freshnessSuppressedWhilePublishing, "queries could trust the heartbeat after the watcher stopped observing changes");
        Assert.Equal(1, stoppedCount);
        Assert.False(host.IsActive);
        var status = WatchStatusFile.TryRead(fixture.Root)!;
        Assert.Equal(303, status.Pid);
        Assert.Equal(WatchActivityState.Stopped, status.State);
        using var releasedLease = WatcherLock.TryAcquire(fixture.Root);
        Assert.NotNull(releasedLease);
    }

    [Fact]
    public void PreviousOwner_RepeatedCleanup_PreservesSuccessorsStatus()
    {
        using var fixture = FixtureCopy.Create();
        using var firstEngine = UnBrambleEngine.Open(fixture.Root);
        firstEngine.RunIndex(full: false);
        using var nextEngine = UnBrambleEngine.Open(fixture.Root);
        var firstTracker = new WatchStatusTracker(fixture.Root, pid: 404);
        var nextTracker = new WatchStatusTracker(fixture.Root, pid: 505);
        using var first = new WatcherHost(firstEngine, QuietOptions(), firstTracker.OnWatcherEvent);
        using var next = new WatcherHost(nextEngine, QuietOptions(), nextTracker.OnWatcherEvent);
        Assert.True(first.TryStartOnceForAuto());
        first.Stop();
        Assert.True(next.TryStartOnceForAuto());
        var before = File.ReadAllBytes(WatchStatusFile.PathFor(fixture.Root));

        first.Stop();
        first.Dispose();

        Assert.Equal(before, File.ReadAllBytes(WatchStatusFile.PathFor(fixture.Root)));
        var status = WatchStatusFile.TryRead(fixture.Root)!;
        Assert.Equal(505, status.Pid);
        Assert.Equal(WatchActivityState.Watching, status.State);
        Assert.True(next.IsActive);
    }

    [Fact]
    public void PromotionFailure_StopStillPublishesWhileOwningLease_AndReleasesIt()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);
        var tracker = new WatchStatusTracker(fixture.Root, pid: 606);
        var failure = new InvalidOperationException("synthetic promotion failure");
        var heldLeaseWhilePublishing = false;
        using var host = new WatcherHost(engine, QuietOptions(), onEvent: e =>
        {
            tracker.OnWatcherEvent(e);
            if (e == WatcherEvent.PromotionCatchupSweepStarted)
            {
                throw failure;
            }

            if (e == WatcherEvent.Stopped)
            {
                using var competingLease = WatcherLock.TryAcquire(fixture.Root);
                heldLeaseWhilePublishing = competingLease is null;
            }
        });

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => host.TryStartOnceForAuto()));
        Assert.False(host.IsActive);
        host.Stop();

        Assert.True(heldLeaseWhilePublishing);
        var status = WatchStatusFile.TryRead(fixture.Root)!;
        Assert.Equal(606, status.Pid);
        Assert.Equal(WatchActivityState.Stopped, status.State);
        using var releasedLease = WatcherLock.TryAcquire(fixture.Root);
        Assert.NotNull(releasedLease);
    }

    [Fact]
    public void StoppedCallbackFailure_ReleasesLease_AndDoesNotReplayCallback()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);
        var failure = new InvalidOperationException("synthetic status callback failure");
        var stoppedCount = 0;
        var host = new WatcherHost(engine, QuietOptions(), onEvent: e =>
        {
            if (e == WatcherEvent.Stopped)
            {
                stoppedCount++;
                throw failure;
            }
        });

        try
        {
            Assert.True(host.TryStartOnceForAuto());
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(host.Stop));
            Assert.False(host.IsActive);
            using var releasedLease = WatcherLock.TryAcquire(fixture.Root);
            Assert.NotNull(releasedLease);

            Assert.Null(Record.Exception(host.Dispose));
            Assert.Equal(1, stoppedCount);
        }
        finally
        {
            try
            {
                host.Dispose();
            }
            catch (InvalidOperationException ex) when (ReferenceEquals(ex, failure))
            {
                // Keep cleanup safe while this regression is run against the old behavior.
            }
        }
    }
}
