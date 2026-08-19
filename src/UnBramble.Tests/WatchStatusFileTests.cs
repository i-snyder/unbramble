using UnBramble.Core.Monitoring;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>Round-trip and corruption handling for <see cref="WatchStatusFile"/> -- the same
/// contract as <see cref="HeartbeatFileTests"/>, applied to the richer status JSON.</summary>
public class WatchStatusFileTests
{
    private static WatchStatusSnapshot SampleSnapshot(string projectRoot) => new(
        SchemaVersion: 1,
        Pid: 4242,
        ProjectRoot: projectRoot,
        State: WatchActivityState.Processing,
        StartedUtc: new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
        LastUpdatedUtc: new DateTime(2026, 3, 4, 5, 6, 9, DateTimeKind.Utc),
        Current: new CurrentActivity(
            Kind: "batch",
            StartedUtc: new DateTime(2026, 3, 4, 5, 6, 8, DateTimeKind.Utc),
            LastStepUtc: new DateTime(2026, 3, 4, 5, 6, 9, DateTimeKind.Utc),
            Unit: "Assembly-CSharp",
            Phase: "extract (scoped)",
            Detail: "files=1/315 symbols=27"),
        Totals: new WatchTotals(
            BatchesApplied: 3,
            SelfHealSweeps: 1,
            SelfHealSweepsSkipped: 5,
            ErrorResyncs: 0,
            CacheFullRebuilds: 1,
            CacheIncrementalBuilds: 2,
            CacheReusedNoOp: 7,
            FilesAdded: 1,
            FilesChanged: 4,
            FilesRemoved: 0),
        LastPass: new LastPassSummary(
            Kind: "batch",
            CompletedUtc: new DateTime(2026, 3, 4, 5, 6, 8, DateTimeKind.Utc),
            TotalMs: 187,
            Steps: [new PassStep("scan", 3), new PassStep("diff", 12), new PassStep("total", 187)],
            Added: 0,
            Changed: 2,
            Removed: 0));

    [Fact]
    public void WriteThenRead_RoundTripsEveryField()
    {
        using var fixture = FixtureCopy.Create();
        var snapshot = SampleSnapshot(fixture.Root);

        WatchStatusFile.Write(fixture.Root, snapshot);
        var read = WatchStatusFile.TryRead(fixture.Root);

        Assert.NotNull(read);
        Assert.Equal(snapshot.Pid, read!.Pid);
        Assert.Equal(snapshot.ProjectRoot, read.ProjectRoot);
        Assert.Equal(snapshot.State, read.State);
        Assert.Equal(snapshot.StartedUtc, read.StartedUtc);
        Assert.Equal(snapshot.LastUpdatedUtc, read.LastUpdatedUtc);

        Assert.NotNull(read.Current);
        Assert.Equal(snapshot.Current!.Kind, read.Current!.Kind);
        Assert.Equal(snapshot.Current.Unit, read.Current.Unit);
        Assert.Equal(snapshot.Current.Phase, read.Current.Phase);
        Assert.Equal(snapshot.Current.Detail, read.Current.Detail);
        Assert.Equal(snapshot.Current.StartedUtc, read.Current.StartedUtc);
        Assert.Equal(snapshot.Current.LastStepUtc, read.Current.LastStepUtc);

        Assert.Equal(snapshot.Totals, read.Totals);

        Assert.NotNull(read.LastPass);
        Assert.Equal(snapshot.LastPass!.Kind, read.LastPass!.Kind);
        Assert.Equal(snapshot.LastPass.TotalMs, read.LastPass.TotalMs);
        Assert.Equal(snapshot.LastPass.Added, read.LastPass.Added);
        Assert.Equal(snapshot.LastPass.Changed, read.LastPass.Changed);
        Assert.Equal(snapshot.LastPass.Removed, read.LastPass.Removed);
        Assert.Equal(snapshot.LastPass.Steps.Select(s => (s.Label, s.Ms)), read.LastPass.Steps.Select(s => (s.Label, s.Ms)));
    }

    [Fact]
    public void WriteWithNullCurrentAndLastPass_RoundTrips()
    {
        using var fixture = FixtureCopy.Create();
        var snapshot = new WatchStatusSnapshot(
            1, 100, fixture.Root, WatchActivityState.Watching, DateTime.UtcNow, DateTime.UtcNow, null, WatchTotals.Empty, null);

        WatchStatusFile.Write(fixture.Root, snapshot);
        var read = WatchStatusFile.TryRead(fixture.Root);

        Assert.NotNull(read);
        Assert.Null(read!.Current);
        Assert.Null(read.LastPass);
        Assert.Equal(WatchActivityState.Watching, read.State);
    }

    [Fact]
    public void TryRead_NoFileYet_ReturnsNull()
    {
        using var fixture = FixtureCopy.Create();

        Assert.Null(WatchStatusFile.TryRead(fixture.Root));
    }

    [Fact]
    public void TryRead_CorruptFile_ReturnsNullRatherThanThrowing()
    {
        using var fixture = FixtureCopy.Create();
        var path = WatchStatusFile.PathFor(fixture.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not valid json");

        Assert.Null(WatchStatusFile.TryRead(fixture.Root));
    }

    [Fact]
    public void Write_OverwritesPreviousStatusAtomically_NoStrayTempFiles()
    {
        using var fixture = FixtureCopy.Create();
        WatchStatusFile.Write(fixture.Root, SampleSnapshot(fixture.Root) with { Pid = 1 });
        WatchStatusFile.Write(fixture.Root, SampleSnapshot(fixture.Root) with { Pid = 2 });

        var read = WatchStatusFile.TryRead(fixture.Root);
        Assert.NotNull(read);
        Assert.Equal(2, read!.Pid);

        var directory = Path.GetDirectoryName(WatchStatusFile.PathFor(fixture.Root))!;
        Assert.DoesNotContain(Directory.GetFiles(directory), f => f.Contains(".tmp-", StringComparison.Ordinal));
    }

    [Fact]
    public void Write_LivesAlongsideHeartbeatFile_NeverOverwritesIt()
    {
        using var fixture = FixtureCopy.Create();
        UnBramble.Core.Freshness.HeartbeatFile.Write(fixture.Root, pid: 999, DateTime.UtcNow);

        WatchStatusFile.Write(fixture.Root, SampleSnapshot(fixture.Root));

        var heartbeat = UnBramble.Core.Freshness.HeartbeatFile.TryRead(fixture.Root);
        var status = WatchStatusFile.TryRead(fixture.Root);
        Assert.NotNull(heartbeat);
        Assert.Equal(999, heartbeat!.Value.Pid);
        Assert.NotNull(status);
        Assert.NotEqual(WatchStatusFile.PathFor(fixture.Root), UnBramble.Core.Freshness.HeartbeatFile.PathFor(fixture.Root));
    }
}
