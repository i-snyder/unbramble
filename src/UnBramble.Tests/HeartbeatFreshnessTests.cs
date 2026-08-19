using UnBramble.Core.Freshness;

namespace UnBramble.Tests;

/// <summary>
/// The pull-path skip decision, unit-tested as a pure function with an injected clock — no real
/// heartbeat file or wall-clock timing involved.
/// </summary>
public class HeartbeatFreshnessTests
{
    [Fact]
    public void IsFresh_HeartbeatYoungerThanThreshold_ReturnsTrue()
    {
        var heartbeat = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var now = heartbeat + TimeSpan.FromSeconds(5);

        Assert.True(HeartbeatFreshness.IsFresh(heartbeat, now, TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void IsFresh_HeartbeatOlderThanThreshold_ReturnsFalse()
    {
        var heartbeat = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var now = heartbeat + TimeSpan.FromSeconds(16);

        Assert.False(HeartbeatFreshness.IsFresh(heartbeat, now, TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void IsFresh_ExactlyAtThreshold_ReturnsFalse()
    {
        var heartbeat = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var now = heartbeat + TimeSpan.FromSeconds(15);

        // Strict "<" per the decision function: exactly-at-threshold is treated as stale, not fresh.
        Assert.False(HeartbeatFreshness.IsFresh(heartbeat, now, TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void IsFresh_HeartbeatSlightlyInTheFuture_StillTreatedAsFresh()
    {
        // Minor cross-process clock skew shouldn't make a just-written heartbeat look stale.
        var heartbeat = new DateTime(2026, 1, 1, 12, 0, 1, DateTimeKind.Utc);
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(HeartbeatFreshness.IsFresh(heartbeat, now, TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void DefaultStaleThreshold_IsFifteenSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), HeartbeatFreshness.DefaultStaleThreshold);
    }
}
