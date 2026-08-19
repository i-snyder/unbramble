using UnBramble.Cli;
using UnBramble.Core.Freshness;
using UnBramble.Core.Monitoring;

namespace UnBramble.Tests;

/// <summary>
/// Exercises <c>WatchDashboard.BuildFrame</c> (the pure string-building half of the `monitor` /
/// `monitor` live view -- no Console I/O, so directly testable) via reflection, since
/// it's `internal` to UnBramble.Cli and this test project doesn't have an InternalsVisibleTo grant.
/// Asserts the frame is CONTENT-correct for each snapshot shape the dashboard needs to render
/// (current status + phase, running totals, last completed pass breakdown) -- not exact
/// pixel/column layout, which is free to evolve.
/// </summary>
public class WatchDashboardFrameTests
{
    private static string BuildFrame(string projectRoot, WatchStatusSnapshot? snapshot, HeartbeatFile.Heartbeat? heartbeat, DateTime nowUtc)
    {
        var method = typeof(Program).Assembly.GetType("UnBramble.Cli.WatchDashboard")!
            .GetMethod("BuildFrame", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (string)method.Invoke(null, ["unbramble monitor", projectRoot, snapshot, heartbeat, nowUtc])!;
    }

    [Fact]
    public void NoSnapshotYet_NoHeartbeat_ShowsWaitingForWatcher()
    {
        var frame = BuildFrame(@"C:\proj", snapshot: null, heartbeat: null, DateTime.UtcNow);

        Assert.Contains("no watch status found yet", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void IdleWatching_ShowsWatchingIdleStatus()
    {
        var now = DateTime.UtcNow;
        var snapshot = new WatchStatusSnapshot(
            1, 555, @"C:\proj", WatchActivityState.Watching, now.AddMinutes(-5), now.AddSeconds(-2),
            Current: null, Totals: WatchTotals.Empty with { BatchesApplied = 3 }, LastPass: null);

        var frame = BuildFrame(@"C:\proj", snapshot, heartbeat: null, now);

        Assert.Contains("WATCHING (idle)", frame, StringComparison.Ordinal);
        Assert.Contains("batches applied:     3", frame, StringComparison.Ordinal);
        Assert.Contains("pid 555", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void Processing_ShowsUnitPhaseAndElapsed()
    {
        var now = DateTime.UtcNow;
        var current = new CurrentActivity("batch", now.AddSeconds(-3), now.AddSeconds(-1), "Assembly-CSharp", "extract (scoped)", "files=1/315 symbols=27");
        var snapshot = new WatchStatusSnapshot(
            1, 555, @"C:\proj", WatchActivityState.Processing, now.AddMinutes(-1), now,
            current, WatchTotals.Empty, LastPass: null);

        var frame = BuildFrame(@"C:\proj", snapshot, heartbeat: null, now);

        Assert.Contains("PROCESSING batch", frame, StringComparison.Ordinal);
        Assert.Contains("Assembly-CSharp: extract (scoped)", frame, StringComparison.Ordinal);
        Assert.Contains("files=1/315 symbols=27", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void LastPass_ShowsTimingBreakdownAndFileCounts()
    {
        var now = DateTime.UtcNow;
        var lastPass = new LastPassSummary(
            "batch", now.AddSeconds(-2), 187,
            [new PassStep("scan", 3), new PassStep("diff", 12), new PassStep("reparse", 45), new PassStep("cs", 127), new PassStep("total", 187)],
            Added: 0, Changed: 2, Removed: 0);
        var snapshot = new WatchStatusSnapshot(
            1, 555, @"C:\proj", WatchActivityState.Watching, now.AddMinutes(-1), now,
            Current: null, Totals: WatchTotals.Empty, LastPass: lastPass);

        var frame = BuildFrame(@"C:\proj", snapshot, heartbeat: null, now);

        Assert.Contains("last completed pass (batch,", frame, StringComparison.Ordinal);
        Assert.Contains("total 187ms", frame, StringComparison.Ordinal);
        Assert.Contains("diff=12ms", frame, StringComparison.Ordinal);
        Assert.Contains("+0 ~2 -0 files", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void StaleHeartbeat_FlagsWatcherAsPossiblyExited()
    {
        var now = DateTime.UtcNow;
        var snapshot = new WatchStatusSnapshot(
            1, 555, @"C:\proj", WatchActivityState.Watching, now.AddMinutes(-10), now.AddMinutes(-5),
            Current: null, Totals: WatchTotals.Empty, LastPass: null);
        var staleHeartbeat = new HeartbeatFile.Heartbeat(555, now.AddMinutes(-5));

        var frame = BuildFrame(@"C:\proj", snapshot, staleHeartbeat, now);

        Assert.Contains("heartbeat stale", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void FreshHeartbeat_DoesNotFlagAsStale()
    {
        var now = DateTime.UtcNow;
        var snapshot = new WatchStatusSnapshot(
            1, 555, @"C:\proj", WatchActivityState.Watching, now.AddMinutes(-1), now,
            Current: null, Totals: WatchTotals.Empty, LastPass: null);
        var freshHeartbeat = new HeartbeatFile.Heartbeat(555, now.AddSeconds(-2));

        var frame = BuildFrame(@"C:\proj", snapshot, freshHeartbeat, now);

        Assert.DoesNotContain("heartbeat stale", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessingState_WithStaleHeartbeat_DoesNotFlagAsStale()
    {
        var now = DateTime.UtcNow;
        var current = new CurrentActivity("initial-index", now.AddMinutes(-3), now, null, "scanning", "12,345 files, 678 dirs");
        var snapshot = new WatchStatusSnapshot(
            1, 555, @"C:\proj", WatchActivityState.Processing, now.AddMinutes(-3), now.AddMinutes(-3),
            current, Totals: WatchTotals.Empty, LastPass: null);
        var staleHeartbeat = new HeartbeatFile.Heartbeat(555, now.AddMinutes(-5));

        var frame = BuildFrame(@"C:\proj", snapshot, staleHeartbeat, now);

        Assert.DoesNotContain("heartbeat stale", frame, StringComparison.Ordinal);
        Assert.Contains("PROCESSING initial-index", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void StaleHeartbeat_ButRecentStatusUpdate_DoesNotFlagAsStale()
    {
        var now = DateTime.UtcNow;
        var snapshot = new WatchStatusSnapshot(
            1, 555, @"C:\proj", WatchActivityState.Watching, now.AddMinutes(-10), now.AddSeconds(-1),
            Current: null, Totals: WatchTotals.Empty, LastPass: null);
        var staleHeartbeat = new HeartbeatFile.Heartbeat(555, now.AddMinutes(-5));

        var frame = BuildFrame(@"C:\proj", snapshot, staleHeartbeat, now);

        Assert.DoesNotContain("heartbeat stale", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void Waiting_ShowsAnotherWatcherActiveMessage()
    {
        var now = DateTime.UtcNow;
        var snapshot = new WatchStatusSnapshot(
            1, 555, @"C:\proj", WatchActivityState.Waiting, now, now, Current: null, Totals: WatchTotals.Empty, LastPass: null);

        var frame = BuildFrame(@"C:\proj", snapshot, heartbeat: null, now);

        Assert.Contains("WAITING", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void Stopped_ShowsStoppedStatus()
    {
        var now = DateTime.UtcNow;
        var snapshot = new WatchStatusSnapshot(
            1, 555, @"C:\proj", WatchActivityState.Stopped, now.AddMinutes(-2), now, Current: null, Totals: WatchTotals.Empty, LastPass: null);

        var frame = BuildFrame(@"C:\proj", snapshot, heartbeat: null, now);

        Assert.Contains("STOPPED", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void Title_IsMonitor()
    {
        var method = typeof(Program).Assembly.GetType("UnBramble.Cli.WatchDashboard")!
            .GetMethod("BuildFrame", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var now = DateTime.UtcNow;
        var snapshot = new WatchStatusSnapshot(
            1, 555, @"C:\proj", WatchActivityState.Watching, now, now, Current: null, Totals: WatchTotals.Empty, LastPass: null);

        var frame = (string)method.Invoke(null, ["unbramble monitor", @"C:\proj", snapshot, null, now])!;

        Assert.StartsWith("unbramble monitor —", frame, StringComparison.Ordinal);
    }
}
