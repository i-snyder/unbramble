using UnBramble.Core.Freshness;

namespace UnBramble.Tests;

/// <summary>The `--auto` watcher's idle-TTL self-termination decision (docs/architecture.md
/// "Auto-spawn watcher") — a pure function, tested directly with injected timestamps instead of
/// a real multi-hour wait.</summary>
public class AutoIdleGateTests
{
    private static readonly DateTime StartedUtc = new(2026, 7, 10, 8, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(2);

    [Fact]
    public void RecentQuery_WithinTtl_DoesNotExit()
    {
        var lastQuery = StartedUtc + TimeSpan.FromMinutes(30);
        var now = lastQuery + TimeSpan.FromMinutes(5);

        Assert.False(AutoIdleGate.ShouldExit(lastQuery, StartedUtc, now, Ttl));
    }

    [Fact]
    public void QueryOlderThanTtl_Exits()
    {
        var lastQuery = StartedUtc + TimeSpan.FromMinutes(10);
        var now = lastQuery + Ttl + TimeSpan.FromMinutes(1);

        Assert.True(AutoIdleGate.ShouldExit(lastQuery, StartedUtc, now, Ttl));
    }

    [Fact]
    public void ExactlyAtTtlBoundary_Exits()
    {
        var lastQuery = StartedUtc + TimeSpan.FromMinutes(10);
        var now = lastQuery + Ttl;

        Assert.True(AutoIdleGate.ShouldExit(lastQuery, StartedUtc, now, Ttl));
    }

    [Fact]
    public void NoQueryEverRecorded_FallsBackToReferenceStartTime()
    {
        // A freshly spawned watcher that nothing has queried yet still gets a full TTL window
        // measured from its own start time, not an immediate exit.
        var now = StartedUtc + TimeSpan.FromMinutes(30);
        Assert.False(AutoIdleGate.ShouldExit(lastQueryUtc: null, StartedUtc, now, Ttl));

        var muchLater = StartedUtc + Ttl + TimeSpan.FromMinutes(1);
        Assert.True(AutoIdleGate.ShouldExit(lastQueryUtc: null, StartedUtc, muchLater, Ttl));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(180)]
    public void PreStartQueryMarker_PreservesFullStartupGrace(int minutesBeforeStart)
    {
        var previousWorkerQuery = StartedUtc - TimeSpan.FromMinutes(minutesBeforeStart);

        // A retained marker must neither trigger the first self-heal tick's idle check nor
        // shorten the new worker's window when that marker was still recent at startup.
        Assert.False(AutoIdleGate.ShouldExit(previousWorkerQuery, StartedUtc, StartedUtc.AddMinutes(5), Ttl));
        Assert.False(AutoIdleGate.ShouldExit(previousWorkerQuery, StartedUtc, StartedUtc + Ttl - TimeSpan.FromTicks(1), Ttl));
        Assert.True(AutoIdleGate.ShouldExit(previousWorkerQuery, StartedUtc, StartedUtc + Ttl, Ttl));
    }

    [Fact]
    public void PostStartQuery_RenewsIdleWindowBeyondStartupDeadline()
    {
        var lastQuery = StartedUtc.AddMinutes(30);

        Assert.False(AutoIdleGate.ShouldExit(lastQuery, StartedUtc, StartedUtc + Ttl + TimeSpan.FromMinutes(5), Ttl));
        Assert.False(AutoIdleGate.ShouldExit(lastQuery, StartedUtc, lastQuery + Ttl - TimeSpan.FromTicks(1), Ttl));
        Assert.True(AutoIdleGate.ShouldExit(lastQuery, StartedUtc, lastQuery + Ttl, Ttl));
    }
}
