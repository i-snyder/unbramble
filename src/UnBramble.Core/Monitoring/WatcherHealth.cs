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
        if (owner is null && unavailableReason != WatcherLock.SnapshotUnavailableReason.Missing)
        {
            return new("unknown", null, null, null, null, null, null,
                unavailableReason == WatcherLock.SnapshotUnavailableReason.Transitioning
                    ? "owner-transition"
                    : "lock-unavailable");
        }

        if (owner is null || !owner.IsWatcherActive)
        {
            var writerActive = IndexWriterLock.ProbeWriterActive(projectRoot) == true;
            if (owner is null && writerActive)
            {
                // No gate can be held on a missing watcher.lock. If it appeared during the
                // writer probe, the writer may be the new watcher doing its own startup sweep.
                using var recheck = WatcherLock.TryAcquireExistingSnapshot(projectRoot, out var recheckReason);
                if (recheckReason != WatcherLock.SnapshotUnavailableReason.Missing)
                {
                    return new("unknown", null, null, null, null, null, null, "owner-transition");
                }
            }

            return new("absent", false, null, null, null, null, null, writerActive ? "index-writer-active" : "no-owner");
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
            // Progress can supply the owner's PID before a heartbeat, but a retained progress
            // file cannot establish the phase, even when it belongs to the current session.
            if (pid is null && owner.SessionId is not null)
            {
                var status = WatchStatusFile.TryRead(projectRoot, maxBytes: 64 * 1024);
                if (status is { Pid: > 0 } && string.Equals(status.WatcherSessionId, owner.SessionId, StringComparison.Ordinal))
                {
                    pid = status.Pid;
                }
            }

            return owner.IsStarting ? Result("starting", "watcher-startup") : Result("updating", "freshness-suppressed");
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
