using UnBramble.Core.Config;
using UnBramble.Core.Freshness;
using UnBramble.Core.Monitoring;
using UnBramble.Core.Store;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

public class WatcherHealthTests
{
    private static readonly DateTime Now = new(2026, 7, 10, 8, 0, 0, DateTimeKind.Utc);
    private const string StoreId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void MissingState_IsAbsentWithoutCreatingStateDirectory()
    {
        using var fixture = FixtureCopy.Create();
        var stateDirectory = UnBramblePaths.StateDirFor(fixture.Root);
        Assert.False(Directory.Exists(stateDirectory));

        var health = WatcherHealth.Read(fixture.Root, Now);

        Assert.Equal("absent", health.State);
        Assert.False(health.OwnerActive);
        Assert.Null(health.Pid);
        Assert.Null(health.WatcherSessionId);
        Assert.Null(health.HeartbeatUtc);
        Assert.Null(health.HeartbeatFresh);
        Assert.Null(health.FreshnessSuppressed);
        Assert.False(Directory.Exists(stateDirectory));
    }

    [Fact]
    public void MissingLock_DoesNotOpenDatabaseOrChangeExistingState()
    {
        using var fixture = FixtureCopy.Create();
        var stateDirectory = UnBramblePaths.StateDirFor(fixture.Root);
        Directory.CreateDirectory(stateDirectory);
        var databasePath = UnBramblePaths.RelativeTo(fixture.Root, UnBramblePaths.DbRelativePath);
        File.WriteAllText(databasePath, "deliberately not a database");
        AutoWatchMarkers.TouchLastQuery(fixture.Root, Now.AddHours(-4));
        var before = Directory.GetFiles(stateDirectory).ToDictionary(path => path, File.ReadAllBytes);

        var health = WatcherHealth.Read(fixture.Root, Now);

        Assert.Equal("absent", health.State);
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(stateDirectory).Order());
        foreach (var (path, contents) in before)
        {
            Assert.Equal(contents, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public void ReleasedOwner_RecentHeartbeatDoesNotImplyActiveWatcher()
    {
        using var fixture = FixtureCopy.Create();
        using (var owner = WatcherLock.TryAcquire(fixture.Root))
        {
            Assert.NotNull(owner);
            owner.PublishFreshness(() => HeartbeatFile.Write(fixture.Root, 123, Now, StoreId, owner.SessionId));
        }

        var health = WatcherHealth.Read(fixture.Root, Now);

        Assert.Equal("absent", health.State);
        Assert.False(health.OwnerActive);
        Assert.Null(health.Pid);
        Assert.Null(health.WatcherSessionId);
    }

    [Fact]
    public void CurrentOwner_MatchingHeartbeatReportsWatchingWithoutRenewingMarkers()
    {
        using var fixture = FixtureCopy.Create();
        using var owner = WatcherLock.TryAcquire(fixture.Root);
        Assert.NotNull(owner);
        owner.PublishFreshness(() => HeartbeatFile.Write(fixture.Root, 123, Now.AddSeconds(-2), StoreId, owner.SessionId));
        AutoWatchMarkers.TouchLastQuery(fixture.Root, Now.AddMinutes(-10));
        AutoWatchMarkers.RecordSpawnAttempt(fixture.Root, Now.AddMinutes(-12));
        File.WriteAllText(WatchStatusFile.PathFor(fixture.Root), "{\"pid\":99999,\"state\":\"Stopped\"}");
        var stateDirectory = UnBramblePaths.StateDirFor(fixture.Root);
        var pathsBefore = Directory.GetFiles(stateDirectory);
        // An active owner's byte-range lock prevents reading the entire lock file on Windows.
        var before = pathsBefore.Where(path => path != WatcherLock.PathFor(fixture.Root))
            .ToDictionary(path => path, File.ReadAllBytes);

        var health = WatcherHealth.Read(fixture.Root, Now);

        Assert.Equal("watching", health.State);
        Assert.True(health.OwnerActive);
        Assert.Equal(123, health.Pid);
        Assert.Equal(owner.SessionId, health.WatcherSessionId);
        Assert.Equal(Now.AddSeconds(-2), health.HeartbeatUtc);
        Assert.True(health.HeartbeatFresh);
        Assert.False(health.FreshnessSuppressed);
        Assert.Equal("verified-heartbeat", health.Reason);
        Assert.Equal(pathsBefore.Order(), Directory.GetFiles(stateDirectory).Order());
        foreach (var (path, contents) in before)
        {
            Assert.Equal(contents, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public void PromotingOwner_WithoutHeartbeatReportsUpdating()
    {
        using var fixture = FixtureCopy.Create();
        using var owner = WatcherLock.TryAcquire(fixture.Root);
        Assert.NotNull(owner);

        var health = WatcherHealth.Read(fixture.Root, Now);

        Assert.Equal("updating", health.State);
        Assert.True(health.OwnerActive);
        Assert.True(health.FreshnessSuppressed);
        Assert.Null(health.Pid);
        Assert.Null(health.HeartbeatFresh);
        Assert.Equal(owner.SessionId, health.WatcherSessionId);
    }

    [Fact]
    public void SuppressedFreshness_TakesPrecedenceOverRecentMatchingHeartbeat()
    {
        using var fixture = FixtureCopy.Create();
        using var owner = WatcherLock.TryAcquire(fixture.Root);
        Assert.NotNull(owner);
        owner.PublishFreshness(() => HeartbeatFile.Write(fixture.Root, 123, Now, StoreId, owner.SessionId));
        owner.SuppressFreshness(static () => { });

        var health = WatcherHealth.Read(fixture.Root, Now);

        Assert.Equal("updating", health.State);
        Assert.True(health.HeartbeatFresh);
        Assert.True(health.FreshnessSuppressed);
        Assert.Equal("freshness-suppressed", health.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActiveOwner_MissingOrCorruptHeartbeatIsUnverified(bool corrupt)
    {
        using var fixture = FixtureCopy.Create();
        using var owner = WatcherLock.TryAcquire(fixture.Root);
        Assert.NotNull(owner);
        owner.PublishFreshness(static () => { });
        if (corrupt)
        {
            File.WriteAllText(HeartbeatFile.PathFor(fixture.Root), "not json");
        }

        var health = WatcherHealth.Read(fixture.Root, Now);

        Assert.Equal("unverified", health.State);
        Assert.True(health.OwnerActive);
        Assert.Null(health.Pid);
        Assert.Null(health.HeartbeatFresh);
        Assert.Equal("heartbeat-unavailable", health.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("fedcba9876543210fedcba9876543210")]
    public void HeartbeatFromAnotherSession_DoesNotIdentifyCurrentOwner(string recordedSession)
    {
        using var fixture = FixtureCopy.Create();
        using var owner = WatcherLock.TryAcquire(fixture.Root);
        Assert.NotNull(owner);
        owner.PublishFreshness(() => WriteHeartbeat(fixture.Root, 999, recordedSession, UnBrambleStore.CurrentSchemaVersion, HeartbeatFile.CurrentProtocolVersion));

        var health = WatcherHealth.Read(fixture.Root, Now);

        Assert.Equal("unverified", health.State);
        Assert.True(health.OwnerActive);
        Assert.Null(health.Pid);
        Assert.True(health.HeartbeatFresh);
        Assert.Equal(owner.SessionId, health.WatcherSessionId);
        Assert.Equal("heartbeat-owner-mismatch", health.Reason);
    }

    [Theory]
    [InlineData(14, "watching", true)]
    [InlineData(15, "unverified", false)]
    [InlineData(16, "unverified", false)]
    public void HeartbeatAge_UsesTheQueryFreshnessThreshold(int ageSeconds, string state, bool fresh)
    {
        using var fixture = FixtureCopy.Create();
        using var owner = WatcherLock.TryAcquire(fixture.Root);
        Assert.NotNull(owner);
        owner.PublishFreshness(() => HeartbeatFile.Write(fixture.Root, 123, Now.AddSeconds(-ageSeconds), StoreId, owner.SessionId));

        var health = WatcherHealth.Read(fixture.Root, Now);

        Assert.Equal(state, health.State);
        Assert.Equal(fresh, health.HeartbeatFresh);
        Assert.Equal(123, health.Pid);
    }

    [Theory]
    [InlineData(0, HeartbeatFile.CurrentProtocolVersion)]
    [InlineData(UnBrambleStore.CurrentSchemaVersion, 0)]
    public void IncompatibleHeartbeat_IsUnverified(int schema, int protocol)
    {
        using var fixture = FixtureCopy.Create();
        using var owner = WatcherLock.TryAcquire(fixture.Root);
        Assert.NotNull(owner);
        owner.PublishFreshness(() => WriteHeartbeat(fixture.Root, 123, owner.SessionId, schema, protocol));

        var health = WatcherHealth.Read(fixture.Root, Now);

        Assert.Equal("unverified", health.State);
        Assert.True(health.OwnerActive);
        Assert.Equal("heartbeat-incompatible", health.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidHeartbeatPid_IsNotExposedAsVerifiedOwner(int pid)
    {
        using var fixture = FixtureCopy.Create();
        using var owner = WatcherLock.TryAcquire(fixture.Root);
        Assert.NotNull(owner);
        owner.PublishFreshness(() => HeartbeatFile.Write(fixture.Root, pid, Now, StoreId, owner.SessionId));

        var health = WatcherHealth.Read(fixture.Root, Now);

        Assert.Equal("unverified", health.State);
        Assert.Null(health.Pid);
        Assert.Equal("heartbeat-pid-invalid", health.Reason);
    }

    [Fact]
    public void ExclusiveLegacyLock_ReportsUnknownRatherThanAbsent()
    {
        using var fixture = FixtureCopy.Create();
        Directory.CreateDirectory(UnBramblePaths.StateDirFor(fixture.Root));
        using var exclusive = new FileStream(WatcherLock.PathFor(fixture.Root), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);

        var health = WatcherHealth.Read(fixture.Root, Now);

        Assert.Equal("unknown", health.State);
        Assert.Null(health.OwnerActive);
        Assert.Null(health.Pid);
        Assert.Equal("lock-unavailable", health.Reason);
    }

    [Fact]
    public void BusyTransition_ReportsUnknownRatherThanAbsent()
    {
        using var fixture = FixtureCopy.Create();
        using var owner = WatcherLock.TryAcquire(fixture.Root);
        Assert.NotNull(owner);
        using var transition = WatcherLock.TryAcquireSnapshot(fixture.Root, out _);
        Assert.NotNull(transition);

        var health = WatcherHealth.Read(fixture.Root, Now);

        Assert.Equal("unknown", health.State);
        Assert.Null(health.OwnerActive);
        Assert.Equal("owner-transition", health.Reason);
    }

    [Fact]
    public void InaccessibleLockPath_ReportsUnknown()
    {
        using var fixture = FixtureCopy.Create();
        Directory.CreateDirectory(WatcherLock.PathFor(fixture.Root));

        var health = WatcherHealth.Read(fixture.Root, Now);

        Assert.Equal("unknown", health.State);
        Assert.Null(health.OwnerActive);
    }

    [Fact]
    public void WindowsReadOnlyLockFile_CanBeInspectedWithoutWriteAccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = FixtureCopy.Create();
        var path = WatcherLock.PathFor(fixture.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "unchanged");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var health = WatcherHealth.Read(fixture.Root, Now);

            Assert.Equal("absent", health.State);
            Assert.False(health.OwnerActive);
            Assert.Equal("unchanged", File.ReadAllText(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    private static void WriteHeartbeat(string root, int pid, string session, int schema, int protocol) =>
        File.WriteAllText(HeartbeatFile.PathFor(root), $$"""{"pid":{{pid}},"utc":"{{Now:O}}","schema":{{schema}},"protocol":{{protocol}},"storeInstanceId":"{{StoreId}}","watcherSessionId":"{{session}}"}""");
}
