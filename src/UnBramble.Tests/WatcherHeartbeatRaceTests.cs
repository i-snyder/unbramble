using UnBramble.Core;
using UnBramble.Core.Freshness;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// Live-caught bug (found via real-project validation against a large real Unity project):
/// <see cref="WatcherHost"/> calls <c>WriteHeartbeat()</c>
/// from at least four independently-scheduled contexts within one process -- the debounce timer
/// (<c>ProcessBatch</c>), the self-heal timer (both its real-sweep path via
/// <c>RunFullResync</c> and its skip path), the separate heartbeat-idle timer (firing every
/// <see cref="WatcherOptions.HeartbeatIdleCadence"/>), and <c>Promote</c> -- with no mutual
/// exclusion between them prior to the fix. <see cref="HeartbeatFile.Write"/>'s atomic-write
/// pattern is only safe against a reader racing a writer, not two writers racing each other:
/// two concurrent writers both moving a uniquely-named temp file onto the SAME shared
/// destination can throw <see cref="UnauthorizedAccessException"/>, which is fatal to the whole
/// process when raised unguarded from an unhandled <see cref="Timer"/> callback -- exactly what
/// killed a real watcher run.
///
/// This test drives a real <see cref="WatcherHost"/> with a deliberately tiny
/// <see cref="WatcherOptions.HeartbeatIdleCadence"/> AND a tiny
/// <see cref="WatcherOptions.SelfHealSweepInterval"/> (so the idle-heartbeat timer and the
/// self-heal timer both fire many times per second) while continuously feeding it real file
/// activity (so self-heal ticks take the real <c>RunFullResync</c> sweep path, not just the
/// skip path, and the debounce timer's own <c>ProcessBatch</c>-triggered heartbeat writes are
/// also in the mix) for a couple of seconds -- maximizing the number of genuinely overlapping
/// <c>WriteHeartbeat()</c> calls. If <see cref="WatcherHost"/>'s own <c>_heartbeatGate</c> lock
/// (or <see cref="HeartbeatFile.Write"/>'s defense-in-depth try/catch) were missing or
/// insufficient, this reliably crashes the whole test process outright (an unhandled exception
/// on a ThreadPool timer callback kills the process, it doesn't just fail one test) -- so the
/// real assertion is simply that this test method returns at all, plus an explicit canary edit
/// afterward proving the watcher is still alive and correctly processing new events, not just
/// that its timers happen to still be ticking.
/// </summary>
public class WatcherHeartbeatRaceTests
{
    [Fact]
    public void HeavyOverlappingHeartbeatAndSelfHealTimers_UnderRealActivity_WatcherSurvivesAndStaysResponsive()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        // Deliberately tiny, but not so tiny that RunFullResync (a real, non-instant RunIndex
        // walk) can't ever keep up with its own timer period -- that would create an unbounded,
        // ever-growing backlog of queued sweeps (a self-inflicted livelock in the test harness)
        // rather than exercising the actual bug under test, which is about concurrent heartbeat
        // WRITES racing each other, not about outrunning the self-heal sweep's own throughput.
        // HeartbeatIdleCadence can stay much tighter since a heartbeat write itself is cheap
        // (no engine walk involved).
        var options = new WatcherOptions(
            DebounceInterval: TimeSpan.FromMilliseconds(50),
            HeartbeatIdleCadence: TimeSpan.FromMilliseconds(15),
            LockRetryInterval: TimeSpan.FromMilliseconds(200),
            SelfHealSweepInterval: TimeSpan.FromMilliseconds(150));

        using var host = new WatcherHost(engine, options);
        host.Start();
        try
        {
            Assert.True(host.IsActive);

            var dataDir = fixture.Combine("Assets", "Data");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            var churnIndex = 0;
            while (DateTime.UtcNow < deadline)
            {
                // Real, in-scope filesystem churn so self-heal ticks take the real RunFullResync
                // sweep path (not just the cheap skip path) at least some of the time, keeping
                // ProcessBatch/RunFullResync/self-heal-idle-timer heartbeat writers all genuinely
                // active concurrently.
                churnIndex++;
                var churnFile = Path.Combine(dataDir, $"HeartbeatRaceChurn{churnIndex}.asset");
                File.WriteAllText(churnFile, "fake");
                File.WriteAllText(churnFile + ".meta",
                    $"fileFormatVersion: 2\nguid: {churnIndex:x8}000000000000000000000000\n");

                Thread.Sleep(20);

                // host.IsActive flips false only on Stop(); if an unhandled exception had
                // escaped a Timer callback, the whole test process would already be dead by now
                // rather than this flag merely being false, but assert it anyway for a clear
                // failure message in case that ever changes.
                Assert.True(host.IsActive, "WatcherHost stopped being active partway through the heartbeat race window");
            }

            // Canary: the watcher is not just "still ticking" but still correctly processing new
            // events after two seconds of heavy overlapping heartbeat/self-heal timer pressure.
            var canaryPath = fixture.Combine("Assets", "Data", "HeartbeatRaceCanary.asset");
            File.WriteAllText(canaryPath, "fake");
            const string canaryGuid = "cacacacacacacacacacacacacacacaca"; // 32 hex chars ("ca" x 16)
            File.WriteAllText(canaryPath + ".meta", $"fileFormatVersion: 2\nguid: {canaryGuid}\n");

            Poll.Until(
                () => engine.GetAllFiles().Any(f =>
                    f.Path.Equals("Assets/Data/HeartbeatRaceCanary.asset", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(f.Guid, canaryGuid, StringComparison.OrdinalIgnoreCase)),
                timeout: TimeSpan.FromSeconds(30),
                message: "canary edit after the heartbeat race window was never indexed -- watcher appears to have died silently");

            // And the heartbeat file itself is left valid, not torn/corrupted, after all that
            // concurrent pressure. A short poll (rather than a single-shot read) tolerates the
            // ordinary, documented-as-best-effort lag between ProcessBatch applying the canary
            // batch (which is what the poll above just observed) and its own trailing
            // WriteHeartbeat() call actually landing -- that gap is normal, not a symptom of the
            // bug under test.
            HeartbeatFile.Heartbeat? heartbeat = null;
            Poll.Until(
                () => (heartbeat = HeartbeatFile.TryRead(fixture.Root)) is not null,
                timeout: TimeSpan.FromSeconds(3),
                message: "heartbeat file was never left in a valid, readable state after the race window");
            Assert.NotNull(heartbeat);
        }
        finally
        {
            host.Stop();
        }
    }
}
