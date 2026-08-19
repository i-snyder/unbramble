using UnBramble.Core.Freshness;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>Round-trip and corruption handling for the heartbeat file itself (JSON {pid, utc}, atomic write).</summary>
public class HeartbeatFileTests
{
    [Fact]
    public void WriteThenRead_RoundTripsPidAndUtc()
    {
        using var fixture = FixtureCopy.Create();
        var utc = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        HeartbeatFile.Write(fixture.Root, pid: 4242, utc);
        var read = HeartbeatFile.TryRead(fixture.Root);

        Assert.NotNull(read);
        Assert.Equal(4242, read!.Value.Pid);
        Assert.Equal(utc, read.Value.UtcTimestamp);
    }

    [Fact]
    public void TryRead_NoFileYet_ReturnsNull()
    {
        using var fixture = FixtureCopy.Create();

        Assert.Null(HeartbeatFile.TryRead(fixture.Root));
    }

    [Fact]
    public void TryRead_CorruptFile_ReturnsNullRatherThanThrowing()
    {
        using var fixture = FixtureCopy.Create();
        var path = HeartbeatFile.PathFor(fixture.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not valid json");

        Assert.Null(HeartbeatFile.TryRead(fixture.Root));
    }

    [Fact]
    public void Write_OverwritesPreviousHeartbeatAtomically()
    {
        using var fixture = FixtureCopy.Create();
        HeartbeatFile.Write(fixture.Root, 1, DateTime.UtcNow);
        var second = DateTime.UtcNow.AddSeconds(1);

        HeartbeatFile.Write(fixture.Root, 2, second);
        var read = HeartbeatFile.TryRead(fixture.Root);

        Assert.NotNull(read);
        Assert.Equal(2, read!.Value.Pid);

        // The atomic write leaves no stray temp files behind.
        var directory = Path.GetDirectoryName(HeartbeatFile.PathFor(fixture.Root))!;
        Assert.DoesNotContain(Directory.GetFiles(directory), f => f.Contains(".tmp-", StringComparison.Ordinal));
    }

    /// <summary>
    /// Live-caught bug (found via real-project validation against a large real Unity
    /// project): <see cref="HeartbeatFile.Write"/>'s atomic-write
    /// pattern (unique temp file, then <see cref="File.Move(string, string, bool)"/> to the SAME
    /// shared destination) is safe against a reader racing a writer, but NOT against two writers
    /// racing each other -- two concurrent <see cref="File.Move(string, string, bool)"/> calls
    /// targeting the same destination can throw (observed live as
    /// <see cref="UnauthorizedAccessException"/>), which is fatal to a whole watch process when
    /// raised unguarded from a <see cref="Timer"/> callback. This test hammers
    /// <see cref="HeartbeatFile.Write"/> from many threads against the same
    /// <paramref name="projectRoot"/>-equivalent simultaneously, repeated across many iterations
    /// to maximize the chance of hitting the race window that killed the real process.
    ///
    /// Non-vacuousness: with <see cref="HeartbeatFile.Write"/>'s try/catch temporarily reverted,
    /// this test reliably reproduced <see cref="UnauthorizedAccessException"/> on this machine
    /// within the first few iterations (typically well under 50ms in), confirming this is a real
    /// regression test and not a vacuous one. With the fix applied (as committed), it passes
    /// consistently.
    /// </summary>
    [Fact]
    public async Task ConcurrentWrites_FromManyThreads_NeverThrowsAndLeavesValidFile()
    {
        using var fixture = FixtureCopy.Create();

        const int threadCount = 24;
        const int iterationsPerThread = 200;

        var barrier = new Barrier(threadCount);
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, threadCount).Select(threadIndex => Task.Run(() =>
        {
            try
            {
                barrier.SignalAndWait();
                for (var i = 0; i < iterationsPerThread; i++)
                {
                    HeartbeatFile.Write(fixture.Root, pid: 1000 + threadIndex, DateTime.UtcNow);
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.True(exceptions.IsEmpty,
            $"HeartbeatFile.Write threw under concurrent writers: {string.Join("; ", exceptions.Select(e => e.ToString()))}");

        // No corrupted/partial file was left behind either: the last writer to finish still
        // leaves a fully readable, parseable heartbeat.
        var read = HeartbeatFile.TryRead(fixture.Root);
        Assert.NotNull(read);

        // No orphaned temp files leaked from a torn write.
        var directory = Path.GetDirectoryName(HeartbeatFile.PathFor(fixture.Root))!;
        Assert.DoesNotContain(Directory.GetFiles(directory), f => f.Contains(".tmp-", StringComparison.Ordinal));
    }
}
