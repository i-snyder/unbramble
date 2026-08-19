namespace UnBramble.Core.Freshness;

/// <summary>
/// The pull-path skip decision: a fresh watcher heartbeat means a live watcher already owns
/// freshness, so the query-time stat-sweep can be skipped. Kept as a pure function (no
/// filesystem, no DateTime.UtcNow) specifically so it's unit-testable with an injected clock
/// independent of any real heartbeat file.
/// </summary>
public static class HeartbeatFreshness
{
    /// <summary>Default staleness threshold: 15 seconds.</summary>
    public static readonly TimeSpan DefaultStaleThreshold = TimeSpan.FromSeconds(15);

    public static bool IsFresh(DateTime heartbeatUtc, DateTime nowUtc, TimeSpan staleThreshold) =>
        nowUtc - heartbeatUtc < staleThreshold;
}
