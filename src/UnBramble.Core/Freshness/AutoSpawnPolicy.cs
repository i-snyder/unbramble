namespace UnBramble.Core.Freshness;

/// <summary>
/// The query-path decision of whether to attempt spawning the detached watcher worker
/// process (docs/architecture.md, "Auto-spawn watcher"). Kept as a pure function, same reasoning
/// as <see cref="HeartbeatFreshness"/>/<see cref="AutoIdleGate"/>: the interesting logic here is
/// "should we try", not the actual <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>
/// call, which is unit-tested nowhere near as usefully as this decision is.
/// </summary>
public static class AutoSpawnPolicy
{
    /// <summary>Crash-loop guard: don't attempt another spawn within this long
    /// of the last attempt, regardless of that attempt's outcome. Queries stay correct either
    /// way -- see <see cref="UnBrambleEngine.EnsureFresh"/> -- this only bounds how often a
    /// failing/crashing auto-watcher gets re-spawned.</summary>
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(3);

    /// <summary>
    /// True only when ALL of: this query actually had to sweep (a fresh heartbeat means a
    /// watcher already owns freshness -- nothing to fix), the project's `watch.autoStart` config
    /// is enabled, and the crash-loop cooldown since the last spawn attempt (if any) has elapsed.
    /// </summary>
    public static bool ShouldSpawn(bool sweepPerformed, bool autoStartEnabled, DateTime? lastAttemptUtc, DateTime nowUtc, TimeSpan cooldown)
    {
        if (!sweepPerformed || !autoStartEnabled)
        {
            return false;
        }

        return lastAttemptUtc is not { } last || nowUtc - last >= cooldown;
    }
}
