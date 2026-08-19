using UnBramble.Core.Freshness;
using UnBramble.Core.Monitoring;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// Drives <see cref="WatchStatusTracker"/> with synthetic <see cref="WatcherEvent"/>s and the
/// exact diagnostic-line shapes covered by <see cref="WatchDiagnosticParserTests"/>, asserting
/// the resulting <see cref="WatchStatusSnapshot"/> at each step -- state transitions, totals, and
/// the "current"/"last pass" fields the dashboard renders.
/// </summary>
public class WatchStatusTrackerTests
{
    private static WatchStatusTracker NewTracker(string projectRoot, bool writeToDisk = false) =>
        new(projectRoot, pid: 1234) { WriteToDisk = writeToDisk };

    /// <summary>
    /// Deliberately NOT Waiting (a real monitor can start painting concurrently with host.Start(),
    /// so this pre-first-event
    /// default can genuinely be rendered for up to one poll interval; Waiting's own status line
    /// claims "another watcher process is already active," which would be false here since this
    /// process hasn't even attempted to acquire the lock yet). See _state's own doc comment for
    /// the live-caught race that motivated this.
    /// </summary>
    [Fact]
    public void InitialState_IsProcessingStartup_NotFalselyWaiting()
    {
        using var fixture = FixtureCopy.Create();
        var tracker = NewTracker(fixture.Root);

        var snapshot = tracker.Snapshot();

        Assert.Equal(WatchActivityState.Processing, snapshot.State);
        Assert.Equal("startup", snapshot.Current!.Kind);
        Assert.Null(snapshot.LastPass);
        Assert.Equal(WatchTotals.Empty, snapshot.Totals);
        Assert.Equal(1234, snapshot.Pid);
    }

    [Fact]
    public void Promoted_TransitionsToWatching()
    {
        using var fixture = FixtureCopy.Create();
        var tracker = NewTracker(fixture.Root);

        tracker.OnWatcherEvent(WatcherEvent.LockWaiting);
        Assert.Equal(WatchActivityState.Waiting, tracker.Snapshot().State);
        Assert.Null(tracker.Snapshot().Current);

        tracker.OnWatcherEvent(WatcherEvent.Promoted);
        Assert.Equal(WatchActivityState.Watching, tracker.Snapshot().State);
    }

    [Fact]
    public void PromotionCatchupSweepStarted_EntersProcessingWithInitialIndexKind()
    {
        using var fixture = FixtureCopy.Create();
        var tracker = NewTracker(fixture.Root);

        tracker.OnWatcherEvent(WatcherEvent.PromotionCatchupSweepStarted);

        var snapshot = tracker.Snapshot();
        Assert.Equal(WatchActivityState.Processing, snapshot.State);
        Assert.NotNull(snapshot.Current);
        Assert.Equal("initial-index", snapshot.Current!.Kind);

        // Promoted (fired once the cold-start catch-up sweep + buffer drain complete) clears it
        // and returns to Watching, same as any other completed activity.
        tracker.OnWatcherEvent(WatcherEvent.Promoted);
        var afterPromoted = tracker.Snapshot();
        Assert.Equal(WatchActivityState.Watching, afterPromoted.State);
        Assert.Null(afterPromoted.Current);
    }

    [Fact]
    public void ScanProgressLine_DuringInitialIndex_UpdatesCurrentWithLiveCount_NoFraction()
    {
        using var fixture = FixtureCopy.Create();
        var tracker = NewTracker(fixture.Root);
        tracker.OnWatcherEvent(WatcherEvent.PromotionCatchupSweepStarted);

        tracker.OnDiagnosticLine("[scan-progress] dirs=678 files=12345 current=Assets/Foo/Bar.prefab");

        var snapshot = tracker.Snapshot();
        Assert.Equal(WatchActivityState.Processing, snapshot.State);
        Assert.Equal("scanning", snapshot.Current!.Phase);
        Assert.Equal("12,345 files, 678 dirs", snapshot.Current.Detail);
        Assert.DoesNotContain("%", snapshot.Current.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoveryLineWithUnits_ThenExtractLines_RendersUnitKOfN()
    {
        using var fixture = FixtureCopy.Create();
        var tracker = NewTracker(fixture.Root);
        tracker.OnWatcherEvent(WatcherEvent.PromotionCatchupSweepStarted);

        tracker.OnDiagnosticLine("[cs-analysis] discovery: session-cache REUSED units=3 elapsed_ms=1");

        tracker.OnDiagnosticLine("[cs-cache] Assembly-CSharp extract (full-unit): trees=10 symbols=100 elapsed_ms=5");
        var afterFirst = tracker.Snapshot();
        Assert.Equal("extracting", afterFirst.Current!.Phase);
        Assert.Equal("unit 1/3 (Assembly-CSharp)", afterFirst.Current.Detail);

        tracker.OnDiagnosticLine("[cs-cache] Game extract (syntactic): scripts=5 symbols=20 elapsed_ms=2");
        var afterSecond = tracker.Snapshot();
        Assert.Equal("unit 2/3 (Game)", afterSecond.Current!.Detail);
    }

    [Fact]
    public void OnIdleTick_AdvancesLastUpdatedUtc_WithoutChangingOtherState()
    {
        using var fixture = FixtureCopy.Create();
        var tracker = NewTracker(fixture.Root);
        tracker.OnWatcherEvent(WatcherEvent.Promoted);
        var before = tracker.Snapshot();

        Thread.Sleep(5);
        tracker.OnIdleTick();

        var after = tracker.Snapshot();
        Assert.True(after.LastUpdatedUtc > before.LastUpdatedUtc);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Current, after.Current);
        Assert.Equal(before.Totals, after.Totals);
        Assert.Equal(before.LastPass, after.LastPass);
    }

    [Fact]
    public void OnIdleTick_WriteToDiskTrue_PersistsUpdatedTimestamp()
    {
        using var fixture = FixtureCopy.Create();
        var tracker = NewTracker(fixture.Root, writeToDisk: true);
        tracker.OnWatcherEvent(WatcherEvent.Promoted);

        Thread.Sleep(5);
        tracker.OnIdleTick();

        var onDisk = WatchStatusFile.TryRead(fixture.Root);
        Assert.NotNull(onDisk);
        Assert.Equal(tracker.Snapshot().LastUpdatedUtc, onDisk!.LastUpdatedUtc);
    }

    [Fact]
    public void Stopped_TransitionsToStopped_ClearsCurrent()
    {
        using var fixture = FixtureCopy.Create();
        var tracker = NewTracker(fixture.Root);
        tracker.OnWatcherEvent(WatcherEvent.Promoted);
        tracker.OnDiagnosticLine("[watch-fsw] debounce fired: batch-size=1");

        tracker.OnWatcherEvent(WatcherEvent.Stopped);

        var snapshot = tracker.Snapshot();
        Assert.Equal(WatchActivityState.Stopped, snapshot.State);
        Assert.Null(snapshot.Current);
    }

    [Fact]
    public void DebounceFired_NonZeroBatch_EntersProcessingWithBatchKind()
    {
        using var fixture = FixtureCopy.Create();
        var tracker = NewTracker(fixture.Root);
        tracker.OnWatcherEvent(WatcherEvent.Promoted);

        tracker.OnDiagnosticLine("[watch-fsw] debounce fired: batch-size=2");

        var snapshot = tracker.Snapshot();
        Assert.Equal(WatchActivityState.Processing, snapshot.State);
        Assert.NotNull(snapshot.Current);
        Assert.Equal("batch", snapshot.Current!.Kind);
    }

    [Fact]
    public void DebounceFired_ZeroBatch_StaysIdle()
    {
        using var fixture = FixtureCopy.Create();
        var tracker = NewTracker(fixture.Root);
        tracker.OnWatcherEvent(WatcherEvent.Promoted);

        tracker.OnDiagnosticLine("[watch-fsw] debounce fired: batch-size=0");

        var snapshot = tracker.Snapshot();
        Assert.Equal(WatchActivityState.Watching, snapshot.State);
        Assert.Null(snapshot.Current);
    }

    [Fact]
    public void CsCacheStep_UpdatesCurrentUnitAndPhase_AndClassifiesBuildOutcome()
    {
        using var fixture = FixtureCopy.Create();
        var tracker = NewTracker(fixture.Root);
        tracker.OnWatcherEvent(WatcherEvent.Promoted);
        tracker.OnDiagnosticLine("[watch-fsw] debounce fired: batch-size=1");

        tracker.OnDiagnosticLine("[cs-cache] Assembly-CSharp build: INCREMENTAL self-dirty=True dep-swaps=[] elapsed_ms=2");
        var afterBuild = tracker.Snapshot();
        Assert.Equal("Assembly-CSharp", afterBuild.Current!.Unit);
        Assert.Equal("build", afterBuild.Current.Phase);
        Assert.Equal(1, afterBuild.Totals.CacheIncrementalBuilds);

        tracker.OnDiagnosticLine("[cs-cache] Assembly-CSharp extract (scoped): files=1/315 symbols=27 elapsed_ms=50");
        var afterExtract = tracker.Snapshot();
        Assert.Equal("extract (scoped)", afterExtract.Current!.Phase);

        tracker.OnDiagnosticLine("[cs-cache] Game build: FULL-REBUILD reason=cache-miss scripts=40 elapsed_ms=15");
        Assert.Equal(1, tracker.Snapshot().Totals.CacheFullRebuilds);

        tracker.OnDiagnosticLine("[cs-cache] Core build: REUSED (no-op) elapsed_ms=0");
        Assert.Equal(1, tracker.Snapshot().Totals.CacheReusedNoOp);
    }

    [Fact]
    public void SummaryLine_FinalizesLastPass_UpdatesFileTotals_ReturnsToWatching()
    {
        using var fixture = FixtureCopy.Create();
        var tracker = NewTracker(fixture.Root);
        tracker.OnWatcherEvent(WatcherEvent.Promoted);
        tracker.OnDiagnosticLine("[watch-fsw] debounce fired: batch-size=2");
        tracker.OnDiagnosticLine("[cs-cache] Assembly-CSharp build: INCREMENTAL self-dirty=True dep-swaps=[] elapsed_ms=2");

        tracker.OnDiagnosticLine(
            "[cs-analysis] ApplyTargetedUpdate(paths=2): scan_ms=3 diff_ms=1415 reparse_ms=512 cs_ms=5734 total_ms=7665 " +
            "added=1 changed=2 removed=0 dirtyPaths=2");

        var snapshot = tracker.Snapshot();
        Assert.Equal(WatchActivityState.Watching, snapshot.State);
        Assert.Null(snapshot.Current);

        Assert.NotNull(snapshot.LastPass);
        Assert.Equal("batch", snapshot.LastPass!.Kind);
        Assert.Equal(7665, snapshot.LastPass.TotalMs);
        Assert.Equal(1, snapshot.LastPass.Added);
        Assert.Equal(2, snapshot.LastPass.Changed);
        Assert.Equal(0, snapshot.LastPass.Removed);
        Assert.Contains(snapshot.LastPass.Steps, s => s.Label == "diff" && s.Ms == 1415);

        Assert.Equal(1, snapshot.Totals.FilesAdded);
        Assert.Equal(2, snapshot.Totals.FilesChanged);
        Assert.Equal(0, snapshot.Totals.FilesRemoved);
    }

    [Fact]
    public void SelfHealSweepEvent_ThenResyncStartingLine_TracksAsSelfHealSweepKind()
    {
        using var fixture = FixtureCopy.Create();
        var tracker = NewTracker(fixture.Root);
        tracker.OnWatcherEvent(WatcherEvent.Promoted);

        tracker.OnWatcherEvent(WatcherEvent.SelfHealSweep);
        tracker.OnDiagnosticLine("[watch-fsw] self-heal/error-resync sweep: starting RunIndex(full=false)");

        var snapshot = tracker.Snapshot();
        Assert.Equal(WatchActivityState.Processing, snapshot.State);
        Assert.Equal("self-heal-sweep", snapshot.Current!.Kind);
        Assert.Equal(1, snapshot.Totals.SelfHealSweeps);

        tracker.OnDiagnosticLine(
            "[cs-analysis] RunIndex(full=False): scan_ms=1 sweep_ms=2 reparse_ms=0 cs_ms=0 total_ms=3 added=0 changed=0 removed=0 dirtyPaths=0");

        var afterFinish = tracker.Snapshot();
        Assert.Equal(WatchActivityState.Watching, afterFinish.State);
        Assert.Equal("self-heal-sweep", afterFinish.LastPass!.Kind);
    }

    [Fact]
    public void ErrorResyncEvent_ThenResyncStartingLine_TracksAsErrorResyncKind()
    {
        using var fixture = FixtureCopy.Create();
        var tracker = NewTracker(fixture.Root);
        tracker.OnWatcherEvent(WatcherEvent.Promoted);

        tracker.OnWatcherEvent(WatcherEvent.ErrorResync);
        tracker.OnDiagnosticLine("[watch-fsw] self-heal/error-resync sweep: starting RunIndex(full=false)");

        Assert.Equal("error-resync", tracker.Snapshot().Current!.Kind);
        Assert.Equal(1, tracker.Snapshot().Totals.ErrorResyncs);
    }

    [Fact]
    public void SelfHealSweepSkipped_IncrementsCounterWithoutEnteringProcessing()
    {
        using var fixture = FixtureCopy.Create();
        var tracker = NewTracker(fixture.Root);
        tracker.OnWatcherEvent(WatcherEvent.Promoted);

        tracker.OnWatcherEvent(WatcherEvent.SelfHealSweepSkipped);

        var snapshot = tracker.Snapshot();
        Assert.Equal(1, snapshot.Totals.SelfHealSweepsSkipped);
        Assert.Equal(WatchActivityState.Watching, snapshot.State);
        Assert.Null(snapshot.Current);
    }

    [Fact]
    public void UnrecognizedDiagnosticLine_IsIgnored_DoesNotChangeState()
    {
        using var fixture = FixtureCopy.Create();
        var tracker = NewTracker(fixture.Root);
        tracker.OnWatcherEvent(WatcherEvent.Promoted);
        var before = tracker.Snapshot();

        tracker.OnDiagnosticLine("[watch-fsw] Changed ACCEPTED path=Assets/Foo.cs pending=1 debounce-armed_ms=1000");

        var after = tracker.Snapshot();
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Totals, after.Totals);
        Assert.Equal(before.Current, after.Current);
    }

    [Fact]
    public void WriteToDisk_True_PersistsEveryUpdate_ReadableByASeparateReader()
    {
        using var fixture = FixtureCopy.Create();
        var tracker = NewTracker(fixture.Root, writeToDisk: true);

        tracker.OnWatcherEvent(WatcherEvent.Promoted);
        var onDiskAfterPromote = WatchStatusFile.TryRead(fixture.Root);
        Assert.NotNull(onDiskAfterPromote);
        Assert.Equal(WatchActivityState.Watching, onDiskAfterPromote!.State);

        tracker.OnDiagnosticLine("[watch-fsw] debounce fired: batch-size=1");
        var onDiskProcessing = WatchStatusFile.TryRead(fixture.Root);
        Assert.Equal(WatchActivityState.Processing, onDiskProcessing!.State);

        tracker.OnDiagnosticLine(
            "[cs-analysis] ApplyTargetedUpdate(paths=1): scan_ms=1 diff_ms=1 reparse_ms=1 cs_ms=1 total_ms=4 added=0 changed=1 removed=0 dirtyPaths=1");
        var onDiskDone = WatchStatusFile.TryRead(fixture.Root);
        Assert.Equal(WatchActivityState.Watching, onDiskDone!.State);
        Assert.Equal(4, onDiskDone.LastPass!.TotalMs);

        // What tracker.Snapshot() reports in-process matches what a separate reader sees on disk
        // (compared field-by-field, not via record equality: LastPass.Steps is an IReadOnlyList
        // whose synthesized record equality is reference-based, and the disk-read copy is
        // necessarily a distinct list instance from the in-memory one).
        var inProcess = tracker.Snapshot();
        Assert.Equal(inProcess.State, onDiskDone.State);
        Assert.Equal(inProcess.Totals, onDiskDone.Totals);
        Assert.Equal(inProcess.LastPass!.Kind, onDiskDone.LastPass!.Kind);
        Assert.Equal(inProcess.LastPass.TotalMs, onDiskDone.LastPass.TotalMs);
        Assert.Equal(
            inProcess.LastPass.Steps.Select(s => (s.Label, s.Ms)),
            onDiskDone.LastPass.Steps.Select(s => (s.Label, s.Ms)));
    }
}
