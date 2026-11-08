using UnBramble.Core.Freshness;
using UnBramble.Core.Store;

namespace UnBramble.Core.Monitoring;

/// <summary>A momentary watcher-liveness observation, not proof that an index or query is ready.</summary>
public sealed record WatcherHealthSnapshot(
    string State,
    bool? OwnerActive,
    int? Pid,
    string? WatcherSessionId,
    DateTime? HeartbeatUtc,
    bool? HeartbeatFresh,
    bool? FreshnessSuppressed,
    string Reason);

/// <summary>Reads the current owner and heartbeat without opening the index, creating state,
/// starting a worker, or renewing its idle timer. Queries still validate freshness independently.</summary>
public static class WatcherHealth
{
    public static WatcherHealthSnapshot Read(string projectRoot, DateTime nowUtc)
    {
        using var owner = WatcherLock.TryAcquireExistingSnapshot(projectRoot, out var unavailableReason);
        if (owner is null)
        {
            return unavailableReason == WatcherLock.SnapshotUnavailableReason.Missing
                ? new("absent", false, null, null, null, null, null, "no-owner")
                : new("unknown", null, null, null, null, null, null,
                    unavailableReason == WatcherLock.SnapshotUnavailableReason.Transitioning
                        ? "owner-transition"
                        : "lock-unavailable");
        }

        if (!owner.IsWatcherActive)
        {
            return new("absent", false, null, null, null, null, null, "no-owner");
        }

        // Hold the transition gate while reading: a graceful handoff cannot replace the
        // owner session between the lock probe and the heartbeat's identity check.
        var heartbeat = HeartbeatFile.TryRead(projectRoot);
        var matchingSession = heartbeat is { } h
            && !string.IsNullOrEmpty(owner.SessionId)
            && string.Equals(h.WatcherSessionId, owner.SessionId, StringComparison.Ordinal);
        int? pid = matchingSession && heartbeat!.Value.Pid > 0 ? heartbeat.Value.Pid : null;
        bool? fresh = heartbeat is { } timestamped
            ? HeartbeatFreshness.IsFresh(timestamped.UtcTimestamp, nowUtc, HeartbeatFreshness.DefaultStaleThreshold)
            : null;

        WatcherHealthSnapshot Result(string state, string reason) =>
            new(state, true, pid, owner.SessionId, heartbeat?.UtcTimestamp, fresh, owner.IsFreshnessSuppressed, reason);

        if (owner.IsFreshnessSuppressed)
        {
            return Result("updating", "freshness-suppressed");
        }

        if (heartbeat is not { } current)
        {
            return Result("unverified", "heartbeat-unavailable");
        }

        if (!matchingSession)
        {
            return Result("unverified", "heartbeat-owner-mismatch");
        }

        if (pid is null)
        {
            return Result("unverified", "heartbeat-pid-invalid");
        }

        if (current.Schema != UnBrambleStore.CurrentSchemaVersion || current.Protocol != HeartbeatFile.CurrentProtocolVersion)
        {
            return Result("unverified", "heartbeat-incompatible");
        }

        return fresh == true
            ? Result("watching", "verified-heartbeat")
            : Result("unverified", "heartbeat-stale");
    }
}
