using UnBramble.Core.Freshness;

namespace UnBramble.Tests;

/// <summary>The query-path auto-spawn decision (docs/architecture.md "Auto-spawn watcher") —
/// a pure function, tested directly with injected timestamps rather than through a real spawn.</summary>
public class AutoSpawnPolicyTests
{
    private static readonly DateTime Now = new(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(3);

    [Fact]
    public void SweepNotPerformed_NeverSpawns()
    {
        // A fresh heartbeat means a watcher already owns freshness -- nothing to fix.
        Assert.False(AutoSpawnPolicy.ShouldSpawn(sweepPerformed: false, autoStartEnabled: true, lastAttemptUtc: null, Now, Cooldown));
    }

    [Fact]
    public void AutoStartDisabled_NeverSpawns()
    {
        Assert.False(AutoSpawnPolicy.ShouldSpawn(sweepPerformed: true, autoStartEnabled: false, lastAttemptUtc: null, Now, Cooldown));
    }

    [Fact]
    public void NoPriorAttempt_SweepPerformedAndEnabled_Spawns()
    {
        Assert.True(AutoSpawnPolicy.ShouldSpawn(sweepPerformed: true, autoStartEnabled: true, lastAttemptUtc: null, Now, Cooldown));
    }

    [Fact]
    public void RecentAttempt_WithinCooldown_DoesNotSpawnAgain()
    {
        var lastAttempt = Now - TimeSpan.FromSeconds(30);
        Assert.False(AutoSpawnPolicy.ShouldSpawn(sweepPerformed: true, autoStartEnabled: true, lastAttempt, Now, Cooldown));
    }

    [Fact]
    public void AttemptExactlyAtCooldownBoundary_SpawnsAgain()
    {
        var lastAttempt = Now - Cooldown;
        Assert.True(AutoSpawnPolicy.ShouldSpawn(sweepPerformed: true, autoStartEnabled: true, lastAttempt, Now, Cooldown));
    }

    [Fact]
    public void OldAttempt_PastCooldown_SpawnsAgain()
    {
        var lastAttempt = Now - TimeSpan.FromMinutes(10);
        Assert.True(AutoSpawnPolicy.ShouldSpawn(sweepPerformed: true, autoStartEnabled: true, lastAttempt, Now, Cooldown));
    }
}
