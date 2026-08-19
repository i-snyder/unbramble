using UnBramble.Core.Config;
using UnBramble.Core.Freshness;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>Round-trip and corruption handling for the two auto-spawn marker files
/// (docs/architecture.md "Auto-spawn watcher") — same shape as <see cref="HeartbeatFileTests"/>.</summary>
public class AutoWatchMarkersTests
{
    [Fact]
    public void LastQuery_NoFileYet_ReturnsNull()
    {
        using var fixture = FixtureCopy.Create();
        Assert.Null(AutoWatchMarkers.TryReadLastQuery(fixture.Root));
    }

    [Fact]
    public void LastQuery_TouchThenRead_RoundTrips()
    {
        using var fixture = FixtureCopy.Create();
        var utc = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        AutoWatchMarkers.TouchLastQuery(fixture.Root, utc);

        Assert.Equal(utc, AutoWatchMarkers.TryReadLastQuery(fixture.Root));
    }

    [Fact]
    public void LastQuery_SecondTouchOverwritesTheFirst()
    {
        using var fixture = FixtureCopy.Create();
        AutoWatchMarkers.TouchLastQuery(fixture.Root, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var second = new DateTime(2026, 1, 1, 0, 5, 0, DateTimeKind.Utc);

        AutoWatchMarkers.TouchLastQuery(fixture.Root, second);

        Assert.Equal(second, AutoWatchMarkers.TryReadLastQuery(fixture.Root));
    }

    [Fact]
    public void LastQuery_CorruptFile_ReturnsNullRatherThanThrowing()
    {
        using var fixture = FixtureCopy.Create();
        var path = UnBramblePaths.RelativeTo(fixture.Root, UnBramblePaths.LastQueryRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not a timestamp");

        Assert.Null(AutoWatchMarkers.TryReadLastQuery(fixture.Root));
    }

    [Fact]
    public void SpawnAttempt_NoFileYet_ReturnsNull()
    {
        using var fixture = FixtureCopy.Create();
        Assert.Null(AutoWatchMarkers.TryReadLastSpawnAttempt(fixture.Root));
    }

    [Fact]
    public void SpawnAttempt_RecordThenRead_RoundTrips()
    {
        using var fixture = FixtureCopy.Create();
        var utc = new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc);

        AutoWatchMarkers.RecordSpawnAttempt(fixture.Root, utc);

        Assert.Equal(utc, AutoWatchMarkers.TryReadLastSpawnAttempt(fixture.Root));
    }

    [Fact]
    public void TheTwoMarkers_AreIndependent()
    {
        using var fixture = FixtureCopy.Create();
        var queryUtc = new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc);
        var attemptUtc = new DateTime(2026, 1, 1, 2, 0, 0, DateTimeKind.Utc);

        AutoWatchMarkers.TouchLastQuery(fixture.Root, queryUtc);
        AutoWatchMarkers.RecordSpawnAttempt(fixture.Root, attemptUtc);

        Assert.Equal(queryUtc, AutoWatchMarkers.TryReadLastQuery(fixture.Root));
        Assert.Equal(attemptUtc, AutoWatchMarkers.TryReadLastSpawnAttempt(fixture.Root));
    }
}
