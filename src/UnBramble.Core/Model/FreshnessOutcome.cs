namespace UnBramble.Core.Model;

/// <summary>
/// Result of <see cref="UnBrambleEngine.EnsureFresh"/>: the watcher's heartbeat was fresh enough
/// to trust (no sweep performed, <see cref="HeartbeatAge"/> set), a stat-sweep just ran (<see
/// cref="Summary"/> set), or -- <see cref="ConcurrentSweepInProgress"/> only, non-waiting callers
/// only, see that flag's own doc comment -- another process already owns the write lock and is
/// mid-sweep, so this call returned immediately without sweeping OR waiting, or <see
/// cref="ConcurrentUpdateCompleted"/> shows that this caller waited for the writer that repaired
/// an incomplete index. At most one of <see cref="HeartbeatAge"/>/<see cref="Summary"/> is
/// non-null, and never both.
/// </summary>
public sealed record FreshnessOutcome(
    bool SweepPerformed,
    bool ConcurrentSweepInProgress,
    TimeSpan? HeartbeatAge,
    IndexSummary? Summary,
    bool ConcurrentUpdateCompleted = false)
{
    public static FreshnessOutcome SkippedFreshHeartbeat(TimeSpan age) => new(false, false, age, null);

    public static FreshnessOutcome Swept(IndexSummary summary) => new(true, false, null, summary);

    /// <summary>
    /// This caller found an incomplete index, waited for the current writer, then observed that
    /// writer's completed marker while holding the finite writer lock. No duplicate sweep ran.
    /// </summary>
    public static FreshnessOutcome SkippedCompletedConcurrentUpdate() => new(false, false, null, null, true);

    /// <summary>
    /// Only reachable when <see cref="UnBrambleEngine.EnsureFresh"/> is called with
    /// <c>waitForConcurrentSweep: false</c> (currently just `stats`, which reports current status
    /// rather than blocking on it) AND another process already holds <see
    /// cref="UnBramble.Core.Freshness.WatcherLock"/>: the caller neither swept itself (that would
    /// race the other writer's own transaction, see EnsureFresh's own doc comment) nor waited for
    /// the other writer's heartbeat (the whole point of the non-waiting mode). Whatever this call
    /// returns reflects the last committed state, not necessarily the current one.
    /// </summary>
    public static FreshnessOutcome SkippedConcurrentSweep() => new(false, true, null, null);
}
