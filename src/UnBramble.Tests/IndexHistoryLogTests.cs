using UnBramble.Core.Monitoring;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>Schema/shape, append-not-truncate, and rotation coverage for
/// <see cref="IndexHistoryLog"/> -- same fixture-based convention as
/// <see cref="WatchStatusFileTests"/>/<see cref="HeartbeatFileTests"/>.</summary>
public class IndexHistoryLogTests
{
    private static IndexHistoryEntry SampleEntry(int pid = 4242, DateTime? timestamp = null) => new(
        TimestampUtc: timestamp ?? new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
        Pid: pid,
        Full: true,
        TriggerSource: "cli",
        ScanMs: 12345.6,
        SweepMs: 78.9,
        ReparseMs: 12.3,
        CsMs: 456.7,
        TotalMs: 12893.5,
        Added: 100,
        Changed: 5,
        Removed: 1,
        DirtyCount: 6,
        FilesSeen: 91234,
        DirsVisited: 8123,
        Roots:
        [
            new IndexHistoryRootEntry("Assets", null, IsJunction: false, Dirs: 500, Files: 4000, ElapsedMs: 300.1),
            new IndexHistoryRootEntry("Assets/BigPackage", @"D:\repos\[Package] BigPackage", IsJunction: true, Dirs: 7000, Files: 80000, ElapsedMs: 601000.0),
        ],
        SlowDirs:
        [
            new IndexHistorySlowDirEntry("Assets/BigPackage/Big/Nested", 900.4),
            new IndexHistorySlowDirEntry("Assets/BigPackage/Other", 260.0),
        ]);

    [Fact]
    public void Append_ThenReadAll_RoundTripsEveryField()
    {
        using var fixture = FixtureCopy.Create();
        var entry = SampleEntry();

        IndexHistoryLog.Append(fixture.Root, entry);
        var read = IndexHistoryLog.TryReadAll(fixture.Root);

        var got = Assert.Single(read);
        Assert.Equal(entry.TimestampUtc, got.TimestampUtc);
        Assert.Equal(entry.Pid, got.Pid);
        Assert.Equal(entry.Full, got.Full);
        Assert.Equal(entry.TriggerSource, got.TriggerSource);
        Assert.Equal(entry.ScanMs, got.ScanMs);
        Assert.Equal(entry.SweepMs, got.SweepMs);
        Assert.Equal(entry.ReparseMs, got.ReparseMs);
        Assert.Equal(entry.CsMs, got.CsMs);
        Assert.Equal(entry.TotalMs, got.TotalMs);
        Assert.Equal(entry.Added, got.Added);
        Assert.Equal(entry.Changed, got.Changed);
        Assert.Equal(entry.Removed, got.Removed);
        Assert.Equal(entry.DirtyCount, got.DirtyCount);
        Assert.Equal(entry.FilesSeen, got.FilesSeen);
        Assert.Equal(entry.DirsVisited, got.DirsVisited);

        Assert.Equal(2, got.Roots.Count);
        Assert.Equal("Assets", got.Roots[0].ProjectPrefix);
        Assert.Null(got.Roots[0].ResolvedTarget);
        Assert.False(got.Roots[0].IsJunction);
        Assert.Equal("Assets/BigPackage", got.Roots[1].ProjectPrefix);
        Assert.Equal(@"D:\repos\[Package] BigPackage", got.Roots[1].ResolvedTarget);
        Assert.True(got.Roots[1].IsJunction);
        Assert.Equal(7000, got.Roots[1].Dirs);
        Assert.Equal(80000, got.Roots[1].Files);
        Assert.Equal(601000.0, got.Roots[1].ElapsedMs);

        Assert.Equal(2, got.SlowDirs.Count);
        Assert.Equal("Assets/BigPackage/Big/Nested", got.SlowDirs[0].ProjectPrefix);
        Assert.Equal(900.4, got.SlowDirs[0].ElapsedMs);
    }

    [Fact]
    public void Append_WithNullTriggerSourceAndEmptyRoots_RoundTrips()
    {
        using var fixture = FixtureCopy.Create();
        var entry = SampleEntry() with { TriggerSource = null, Roots = [], SlowDirs = [] };

        IndexHistoryLog.Append(fixture.Root, entry);
        var read = IndexHistoryLog.TryReadAll(fixture.Root);

        var got = Assert.Single(read);
        Assert.Null(got.TriggerSource);
        Assert.Empty(got.Roots);
        Assert.Empty(got.SlowDirs);
    }

    [Fact]
    public void Append_Twice_IsAppendNotOverwrite()
    {
        using var fixture = FixtureCopy.Create();

        IndexHistoryLog.Append(fixture.Root, SampleEntry(pid: 1));
        IndexHistoryLog.Append(fixture.Root, SampleEntry(pid: 2));
        IndexHistoryLog.Append(fixture.Root, SampleEntry(pid: 3));

        var read = IndexHistoryLog.TryReadAll(fixture.Root);
        Assert.Equal(3, read.Count);
        Assert.Equal([1, 2, 3], read.Select(e => e.Pid));
    }

    [Fact]
    public void Append_OneLinePerCall_FileHasOneNewlineTerminatedJsonObjectPerAppend()
    {
        using var fixture = FixtureCopy.Create();

        IndexHistoryLog.Append(fixture.Root, SampleEntry(pid: 1));
        IndexHistoryLog.Append(fixture.Root, SampleEntry(pid: 2));

        var path = IndexHistoryLog.PathFor(fixture.Root);
        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("{", lines[0]);
        Assert.EndsWith("}", lines[0]);
        Assert.StartsWith("{", lines[1]);
        Assert.EndsWith("}", lines[1]);
    }

    [Fact]
    public void TryReadAll_NoFileYet_ReturnsEmpty()
    {
        using var fixture = FixtureCopy.Create();

        Assert.Empty(IndexHistoryLog.TryReadAll(fixture.Root));
    }

    [Fact]
    public void TryReadAll_SkipsCorruptLinesRatherThanThrowing()
    {
        using var fixture = FixtureCopy.Create();
        IndexHistoryLog.Append(fixture.Root, SampleEntry(pid: 1));

        var path = IndexHistoryLog.PathFor(fixture.Root);
        File.AppendAllText(path, "{ not valid json\n");

        IndexHistoryLog.Append(fixture.Root, SampleEntry(pid: 2));

        var read = IndexHistoryLog.TryReadAll(fixture.Root);
        Assert.Equal([1, 2], read.Select(e => e.Pid));
    }

    [Fact]
    public void Append_LivesAlongsideWatchStatusFile_NeverOverwritesIt()
    {
        using var fixture = FixtureCopy.Create();
        UnBramble.Core.Freshness.HeartbeatFile.Write(fixture.Root, pid: 999, DateTime.UtcNow);

        IndexHistoryLog.Append(fixture.Root, SampleEntry());

        var heartbeat = UnBramble.Core.Freshness.HeartbeatFile.TryRead(fixture.Root);
        Assert.NotNull(heartbeat);
        Assert.Equal(999, heartbeat!.Value.Pid);
        Assert.NotEqual(IndexHistoryLog.PathFor(fixture.Root), UnBramble.Core.Freshness.HeartbeatFile.PathFor(fixture.Root));
    }

    [Fact]
    public void Append_PastRotationThreshold_KeepsMostRecentLinesOnly()
    {
        using var fixture = FixtureCopy.Create();
        var path = IndexHistoryLog.PathFor(fixture.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Pre-seed the file past the rotation size threshold with more lines than
        // MaxLinesAfterRotation keeps, using cheap placeholder lines (rotation only counts bytes
        // and lines -- it doesn't need to parse them) so the test doesn't pay for thousands of
        // real Append() calls.
        var placeholderLine = new string('x', 500);
        var seedLineCount = IndexHistoryLog.MaxLinesAfterRotation + 500;
        using (var writer = new StreamWriter(path))
        {
            for (var i = 0; i < seedLineCount; i++)
            {
                writer.WriteLine(placeholderLine);
            }
        }

        Assert.True(new FileInfo(path).Length >= IndexHistoryLog.RotateAtBytes, "test setup must exceed the rotation threshold");

        // This append should trigger rotation (trims the placeholder lines down to
        // MaxLinesAfterRotation) and then add its own real, parseable line on top.
        IndexHistoryLog.Append(fixture.Root, SampleEntry(pid: 777));

        var lines = File.ReadAllLines(path);
        Assert.Equal(IndexHistoryLog.MaxLinesAfterRotation + 1, lines.Length);

        // The real entry -- appended after rotation -- must still be present and parseable.
        var parsed = IndexHistoryLog.TryReadAll(fixture.Root);
        Assert.Contains(parsed, e => e.Pid == 777);
    }
}
