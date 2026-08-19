using UnBramble.Core;
using UnBramble.Core.Freshness;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// The detached worker's two automatic-lifetime behaviors (docs/architecture.md
/// "Auto-spawn watcher"): (1) never queues as a passive standby on lock contention -- it exits
/// immediately instead; (2) self-terminates after an idle TTL of no fast-path query activity.
/// Same in-process harness pattern as <see cref="WatcherHostTests"/>/<see
/// cref="WatcherSelfHealActivityTests"/>: shrunk <see cref="WatcherOptions"/> timing knobs and
/// <see cref="Poll.Until"/> instead of racing real wall-clock timers (and, for the idle TTL,
/// a shrunk TTL passed directly to the constructor rather than a real 2-hour wait).
/// </summary>
public class WatcherAutoModeTests
{
    private static WatcherOptions FastOptions(TimeSpan? selfHeal = null) => new(
        DebounceInterval: TimeSpan.FromMilliseconds(150),
        HeartbeatIdleCadence: TimeSpan.FromSeconds(5),
        LockRetryInterval: TimeSpan.FromMilliseconds(200),
        SelfHealSweepInterval: selfHeal ?? TimeSpan.FromMilliseconds(300));

    [Fact]
    public void TryStartOnceForAuto_LockAlreadyHeld_ReturnsFalseImmediately_DoesNotQueue()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        // Simulate "another watcher process already owns this project": hold the exclusive lock
        // directly, the same handle a real WatcherHost.Promote would be holding.
        using var heldLock = WatcherLock.TryAcquire(fixture.Root);
        Assert.NotNull(heldLock);

        var events = new List<WatcherEvent>();
        using var host = new WatcherHost(engine, FastOptions(), e => { lock (events) { events.Add(e); } }, autoMode: true);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var acquired = host.TryStartOnceForAuto();
        stopwatch.Stop();

        Assert.False(acquired);
        Assert.False(host.IsActive);

        // "Does not queue" means it never even raises LockWaiting/schedules a retry -- a normal
        // Start() call under contention would fire LockWaiting and keep retrying on a timer.
        Assert.DoesNotContain(WatcherEvent.LockWaiting, events);

        // Returns essentially instantly -- well under the retry interval a normal Start() would
        // wait before trying again.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"TryStartOnceForAuto took {stopwatch.Elapsed} -- expected an instant return.");
    }

    [Fact]
    public void TryStartOnceForAuto_LockFree_AcquiresAndPromotesLikeStart()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        using var host = new WatcherHost(engine, FastOptions(), autoMode: true);
        try
        {
            var acquired = host.TryStartOnceForAuto();

            Assert.True(acquired);
            Assert.True(host.IsActive);
        }
        finally
        {
            host.Stop();
        }
    }

    [Fact]
    public void AutoMode_NoQueryActivity_SelfTerminatesAfterIdleTtl()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var idleTtl = TimeSpan.FromMilliseconds(500);
        var events = new List<WatcherEvent>();
        using var host = new WatcherHost(
            engine,
            FastOptions(selfHeal: TimeSpan.FromMilliseconds(200)),
            e => { lock (events) { events.Add(e); } },
            autoMode: true,
            autoIdleTtl: idleTtl);

        try
        {
            Assert.True(host.TryStartOnceForAuto());

            Poll.Until(
                () => { lock (events) { return events.Contains(WatcherEvent.AutoIdleTimeout); } },
                timeout: idleTtl + TimeSpan.FromSeconds(15),
                message: "auto-mode watcher never raised AutoIdleTimeout despite no query activity past its idle TTL");
        }
        finally
        {
            host.Stop();
        }
    }

    [Fact]
    public void AutoMode_RecentFastPathQuery_DoesNotSelfTerminateWithinTtl()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var idleTtl = TimeSpan.FromSeconds(2);
        var events = new List<WatcherEvent>();
        using var host = new WatcherHost(
            engine,
            FastOptions(selfHeal: TimeSpan.FromMilliseconds(200)),
            e => { lock (events) { events.Add(e); } },
            autoMode: true,
            autoIdleTtl: idleTtl);

        try
        {
            Assert.True(host.TryStartOnceForAuto());

            // Simulate a fast-path query landing partway through the TTL window (this is exactly
            // what UnBrambleEngine.EnsureFresh's heartbeat-fresh branch does for a real query).
            Thread.Sleep(TimeSpan.FromMilliseconds(800));
            AutoWatchMarkers.TouchLastQuery(fixture.Root, DateTime.UtcNow);

            // Give the self-heal timer several more ticks -- long enough that a naive
            // "TTL measured from start" implementation would have already exited, but the
            // touched marker should keep pushing the deadline out.
            Thread.Sleep(TimeSpan.FromSeconds(1.6));

            lock (events)
            {
                Assert.DoesNotContain(WatcherEvent.AutoIdleTimeout, events);
            }

            Assert.True(host.IsActive);
        }
        finally
        {
            host.Stop();
        }
    }
}
