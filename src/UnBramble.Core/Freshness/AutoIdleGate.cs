namespace UnBramble.Core.Freshness;

/// <summary>
/// The idle-TTL self-termination decision for an `--auto`-spawned watcher (docs/architecture.md,
/// "Auto-spawn watcher"). Kept as a pure function (no filesystem, no <see
/// cref="DateTime.UtcNow"/>) for the same reason <see cref="HeartbeatFreshness"/> is: unit-
/// testable with injected timestamps instead of a real multi-hour wait.
/// </summary>
public static class AutoIdleGate
{
    /// <summary>Default idle window: ~2 hours of no
    /// fast-path query activity before an `--auto` watcher exits.</summary>
    public static readonly TimeSpan DefaultIdleTtl = TimeSpan.FromHours(2);

    /// <summary>
    /// True once <paramref name="nowUtc"/> is at least <paramref name="idleTtl"/> past the last
    /// recorded fast-path query (<paramref name="lastQueryUtc"/>) -- or, if no query has ever
    /// touched the marker yet, past <paramref name="referenceUtc"/> (the watcher's own start
    /// time), so a freshly auto-spawned watcher always gets a full TTL window before it can
    /// self-terminate even if nothing queries it again.
    /// </summary>
    public static bool ShouldExit(DateTime? lastQueryUtc, DateTime referenceUtc, DateTime nowUtc, TimeSpan idleTtl) =>
        nowUtc - (lastQueryUtc ?? referenceUtc) >= idleTtl;
}
