using UnBramble.Core;
using UnBramble.Core.Freshness;
using UnBramble.Core.Monitoring;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// End-to-end: a real <see cref="WatcherHost"/> driving a real <see cref="UnBrambleEngine"/>
/// against the fixture project, wired up exactly the way the watcher worker wires it in
/// Program.cs (both diagnostic sinks feeding one <see cref="WatchStatusTracker"/>) -- proving the
/// status-file mechanism actually reflects real watch activity, not just synthetic lines fed
/// directly to the tracker (covered separately by <see cref="WatchStatusTrackerTests"/>).
/// </summary>
public class WatchStatusEndToEndTests
{
    private static WatcherOptions FastOptions() => new(
        DebounceInterval: TimeSpan.FromMilliseconds(150),
        HeartbeatIdleCadence: TimeSpan.FromSeconds(5),
        LockRetryInterval: TimeSpan.FromMilliseconds(200),
        SelfHealSweepInterval: TimeSpan.FromMinutes(5));

    /// <summary>
    /// Dashboard live-progress work, P0 step 2: proves a separate reader (`unbramble monitor`)
    /// polling the status file during a real cold start would see a live "Processing
    /// initial-index" status, not a blank/absent file, before the (potentially minutes-long on a
    /// large project) catch-up sweep even runs. Asserted synchronously inside the onEvent hook
    /// itself, right after WatcherEvent.PromotionCatchupSweepStarted -- not via polling from a
    /// separate thread -- because Promote() fires that event and calls the tracker's onEvent
    /// synchronously BEFORE its own blocking RunIndex(full:false) call (see Promote's own doc
    /// comment on promotion order); on this test's tiny fixture project that whole catch-up sweep
    /// can complete in well under a millisecond, too narrow a window for an external poll loop to
    /// reliably observe.
    /// </summary>
    [Fact]
    public void ColdStartPromotion_WritesProcessingInitialIndexStatus_BeforeCatchupSweepRuns()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);

        var tracker = new WatchStatusTracker(engine.ProjectRoot, pid: 4242);
        var sawProcessingBeforeSweepRan = false;

        using var host = new WatcherHost(engine, FastOptions(), onEvent: e =>
        {
            tracker.OnWatcherEvent(e);
            if (e == WatcherEvent.PromotionCatchupSweepStarted)
            {
                var onDisk = WatchStatusFile.TryRead(fixture.Root);
                sawProcessingBeforeSweepRan = onDisk is { State: WatchActivityState.Processing, Current: { Kind: "initial-index" } };
            }
        });

        host.Start();
        try
        {
            Assert.True(host.IsActive);
            Assert.True(sawProcessingBeforeSweepRan, "status file did not show Processing/initial-index before the catch-up sweep ran");

            // Promotion has fully completed by the time Start() returns -- status settles back
            // to Watching, same as WatcherEvent.Promoted's own handling in WatchStatusTracker.
            var final = WatchStatusFile.TryRead(fixture.Root);
            Assert.Equal(WatchActivityState.Watching, final!.State);
        }
        finally
        {
            host.Stop();
        }
    }

    [Fact]
    public void RealEdit_ThroughWatcherHost_ProducesReadableStatusFileWithSensibleTotals()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var tracker = new WatchStatusTracker(engine.ProjectRoot, pid: 9999);
        engine.EnableWatchCompilationCache(onUnitDecision: tracker.OnDiagnosticLine);

        using var host = new WatcherHost(engine, FastOptions(), onEvent: tracker.OnWatcherEvent, onDiagnostic: tracker.OnDiagnosticLine);
        host.Start();
        try
        {
            Assert.True(host.IsActive);

            // A separate reader (what `unbramble monitor` does) can already see a live status file
            // right after promotion, before any edit.
            Poll.Until(
                () => WatchStatusFile.TryRead(fixture.Root) is { State: WatchActivityState.Watching },
                message: "status file never reached Watching state after promotion");

            var fooPath = fixture.Combine("Assets", "Scripts", "Foo.cs");
            var original = File.ReadAllText(fooPath);
            File.WriteAllText(fooPath, original + "\n// watch-status end-to-end edit\n");
            File.SetLastWriteTimeUtc(fooPath, DateTime.UtcNow.AddSeconds(5));

            // Specifically waits for a completed BATCH (BatchesApplied > 0), not just any
            // LastPass -- the promotion catch-up sweep itself already produced a "sweep"-kind
            // LastPass before this edit was even made, so "LastPass is not null" alone would be
            // satisfied immediately and race past the actual edit.
            Poll.Until(
                () => WatchStatusFile.TryRead(fixture.Root)?.Totals.BatchesApplied >= 1,
                message: "status file never recorded a completed batch after a real .cs edit");

            var snapshot = WatchStatusFile.TryRead(fixture.Root)!;
            Assert.Equal(9999, snapshot.Pid);
            Assert.Equal(WatchActivityState.Watching, snapshot.State);
            Assert.Null(snapshot.Current);

            Assert.NotNull(snapshot.LastPass);
            Assert.Equal("batch", snapshot.LastPass!.Kind);
            Assert.True(snapshot.LastPass.TotalMs >= 0);
            Assert.NotEmpty(snapshot.LastPass.Steps);
            // The edited file (existing content changed) should show up as a "changed" file, not added/removed.
            Assert.True(snapshot.LastPass.Changed is > 0);

            Assert.True(snapshot.Totals.BatchesApplied >= 1);
            Assert.True(snapshot.Totals.FilesChanged >= 1);

            // The tracker's own in-process snapshot (what the watcher worker owns directly,
            // no disk round-trip) agrees with what was persisted to disk.
            var inProcess = tracker.Snapshot();
            Assert.Equal(inProcess.Totals, snapshot.Totals);
            Assert.Equal(inProcess.State, snapshot.State);
        }
        finally
        {
            host.Stop();
        }

        // After Stop(), the on-disk status reflects Stopped -- a `monitor` process watching this
        // would see the watcher cleanly go away rather than reading a stale "Watching" forever.
        var final = WatchStatusFile.TryRead(fixture.Root);
        Assert.NotNull(final);
        Assert.Equal(WatchActivityState.Stopped, final!.State);
    }
}
