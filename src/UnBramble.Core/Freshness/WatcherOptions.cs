namespace UnBramble.Core.Freshness;

/// <summary>
/// Injectable timing knobs for <see cref="WatcherHost"/>. <see cref="Default"/> carries the
/// shipped values; tests shrink these to keep the test suite fast without changing any logic.
/// </summary>
public sealed record WatcherOptions(
    TimeSpan DebounceInterval,
    TimeSpan HeartbeatIdleCadence,
    TimeSpan LockRetryInterval,
    TimeSpan SelfHealSweepInterval)
{
    public static WatcherOptions Default { get; } = new(
        DebounceInterval: TimeSpan.FromSeconds(1),
        HeartbeatIdleCadence: TimeSpan.FromSeconds(5),
        LockRetryInterval: TimeSpan.FromSeconds(5),
        SelfHealSweepInterval: TimeSpan.FromMinutes(5));
}
