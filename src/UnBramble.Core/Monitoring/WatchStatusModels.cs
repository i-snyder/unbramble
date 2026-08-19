namespace UnBramble.Core.Monitoring;

/// <summary>High-level activity state of a `watch` process, as surfaced to `unbramble monitor`.
/// Distinct from <see cref="Freshness.WatcherEvent"/> (an
/// ordered lifecycle/progress event stream consumed once) -- this is the current, queryable
/// STATE derived from that stream, snapshotted to disk so a separate process can read it.</summary>
public enum WatchActivityState
{
    /// <summary>Lock not yet won; not the active watcher for this project.</summary>
    Waiting,

    /// <summary>Active watcher, currently idle (no batch/sweep in flight).</summary>
    Watching,

    /// <summary>Active watcher, currently applying a change batch or running a full sweep.</summary>
    Processing,

    /// <summary>Stop()/Dispose() completed. A monitor reading this knows to stop polling.</summary>
    Stopped,
}

/// <summary>
/// What's in flight right now, if anything. <see cref="StartedUtc"/>/<see cref="LastStepUtc"/>
/// are timestamps, not precomputed durations -- deliberately, same reasoning as
/// <see cref="Freshness.HeartbeatFile"/>: a reader (potentially in a different process, on a
/// different clock-read cadence) computes "elapsed so far" itself as `DateTime.UtcNow -
/// StartedUtc`, rather than trusting a duration baked in at write time that's already stale by
/// the time it's read.
/// </summary>
public sealed record CurrentActivity(
    string Kind,
    DateTime StartedUtc,
    DateTime LastStepUtc,
    string? Unit,
    string? Phase,
    string? Detail);

/// <summary>Running totals since this tracker was created (i.e. since `watch` started).</summary>
public sealed record WatchTotals(
    long BatchesApplied,
    long SelfHealSweeps,
    long SelfHealSweepsSkipped,
    long ErrorResyncs,
    long CacheFullRebuilds,
    long CacheIncrementalBuilds,
    long CacheReusedNoOp,
    long FilesAdded,
    long FilesChanged,
    long FilesRemoved)
{
    public static WatchTotals Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>One named timing component of a completed pass (e.g. "scan" -> 3ms), parsed from
/// the `..._ms=N` fields on a `[cs-analysis]` summary line. Order is preserved from the source
/// line (insertion order), not sorted -- reads left-to-right the same way the log line did.</summary>
public sealed record PassStep(string Label, double Ms);

/// <summary>Timing breakdown of the most recently COMPLETED batch/sweep -- "that last edit took
/// Xs, and here's where the time went" without digging through raw log lines.</summary>
public sealed record LastPassSummary(
    string Kind,
    DateTime CompletedUtc,
    double TotalMs,
    IReadOnlyList<PassStep> Steps,
    int? Added,
    int? Changed,
    int? Removed);

/// <summary>
/// The full snapshot written to `.unbramble/watch.status.json` (see
/// <see cref="WatchStatusFile"/>) and read back by `unbramble monitor`. Also usable in-process
/// in-process by the watcher worker, since <see cref="WatchStatusTracker"/>
/// exposes the same snapshot directly.
/// </summary>
public sealed record WatchStatusSnapshot(
    int SchemaVersion,
    int Pid,
    string ProjectRoot,
    WatchActivityState State,
    DateTime StartedUtc,
    DateTime LastUpdatedUtc,
    CurrentActivity? Current,
    WatchTotals Totals,
    LastPassSummary? LastPass);
