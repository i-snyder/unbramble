using Microsoft.Data.Sqlite;
using UnBramble.Core;
using UnBramble.Core.Freshness;
using UnBramble.Core.Store;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

public class StatsSnapshotTests
{
    [Fact]
    public void MissingStore_ReadsProjectConfigWithoutCreatingState()
    {
        using var fixture = FixtureCopy.Create();
        File.WriteAllText(fixture.Combine("unbramble.json"), """{"dbPath":"custom/index.db","watch":{"autoStart":false},"unknownKey":7}""");

        using var snapshot = UnBrambleEngine.OpenStatsSnapshot(fixture.Root);

        Assert.Equal(StatsSnapshotStatus.Missing, snapshot.Status);
        Assert.Null(snapshot.Engine);
        Assert.False(snapshot.Config.Watch.AutoStart);
        Assert.Contains(snapshot.Warnings, warning => warning.Contains("unknownKey", StringComparison.Ordinal));
        Assert.Equal(fixture.Combine("custom", "index.db"), snapshot.DbPath);
        Assert.False(Directory.Exists(fixture.Combine("custom")));
        Assert.False(Directory.Exists(fixture.Combine(".unbramble")));
    }

    [Theory]
    [InlineData(UnBrambleStore.CurrentSchemaVersion - 1)]
    [InlineData(UnBrambleStore.CurrentSchemaVersion + 1)]
    public void OtherSchema_ReportsVersionWithoutMigratingOrTakingTheWriterLease(int version)
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        using var writer = IndexWriterLock.TryAcquire(fixture.Root);
        Assert.NotNull(writer);
        Execute(engine.DbPath, $"UPDATE meta_kv SET value='{version}' WHERE key='schema_version';");

        using var first = UnBrambleEngine.OpenStatsSnapshot(fixture.Root);
        using var second = UnBrambleEngine.OpenStatsSnapshot(fixture.Root);

        Assert.Equal(StatsSnapshotStatus.Incompatible, first.Status);
        Assert.Null(first.Engine);
        Assert.Equal(version, second.StoredSchemaVersion);

        var status = CliRunner.Run("stats", fixture.Root, "--json");
        Assert.Equal(0, status.ExitCode);
        Assert.Contains("\"indexState\":\"incompatible\"", status.StdOut);
        Assert.Equal(version < UnBrambleStore.CurrentSchemaVersion, AutoWatchMarkers.TryReadLastSpawnAttempt(fixture.Root).HasValue);
    }

    [Fact]
    public void IncompleteStore_HidesCommittedPartialCounts()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);
        Assert.True(engine.GetStats().Files.Total > 0);
        Execute(engine.DbPath, "UPDATE meta_kv SET value='0' WHERE key='index_complete';");

        using var snapshot = UnBrambleEngine.OpenStatsSnapshot(fixture.Root);

        Assert.Equal(StatsSnapshotStatus.Incomplete, snapshot.Status);
        Assert.Null(snapshot.Engine);
    }

    [Fact]
    public void AvailableStore_AllReadsObserveOneCommitWhileIndexingContinues()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);
        using var snapshot = UnBrambleEngine.OpenStatsSnapshot(fixture.Root);
        Assert.Equal(StatsSnapshotStatus.Available, snapshot.Status);
        Assert.NotNull(snapshot.Engine);
        var countBefore = snapshot.Engine.GetStats().Files.Total;

        File.Copy(fixture.Combine("Assets", "Materials", "Rock.mat"), fixture.Combine("Assets", "Materials", "Second.mat"));
        File.Copy(fixture.Combine("Assets", "Materials", "Rock.mat.meta"), fixture.Combine("Assets", "Materials", "Second.mat.meta"));
        engine.RunIndex(full: false);

        Assert.Equal(countBefore, snapshot.Engine.GetStats().Files.Total);
        Assert.Empty(snapshot.Engine.GetGuidCollisionGroups());
        using var nextSnapshot = UnBrambleEngine.OpenStatsSnapshot(fixture.Root);
        Assert.NotNull(nextSnapshot.Engine);
        Assert.Equal(countBefore + 1, nextSnapshot.Engine.GetStats().Files.Total);
        Assert.Single(nextSnapshot.Engine.GetGuidCollisionGroups());
    }

    [Fact]
    public void CorruptMetadata_FailsWithoutRebuilding()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        Execute(engine.DbPath, "UPDATE meta_kv SET value='invalid' WHERE key='index_complete';");

        var error = Assert.Throws<InvalidDataException>(() => UnBrambleEngine.OpenStatsSnapshot(fixture.Root));

        Assert.Contains("index completion marker", error.Message);
        Assert.Throws<InvalidDataException>(() => UnBrambleEngine.OpenStatsSnapshot(fixture.Root));
    }

    [Fact]
    public void ForeignDatabase_FailsWithoutAddingIndexTables()
    {
        using var fixture = FixtureCopy.Create();
        SQLitePCL.Batteries_V2.Init();
        File.WriteAllText(fixture.Combine("unbramble.json"), """{"dbPath":"other.db"}""");
        var dbPath = fixture.Combine("other.db");
        Execute(dbPath, "CREATE TABLE unrelated(value TEXT);");

        Assert.Throws<InvalidDataException>(() => UnBrambleEngine.OpenStatsSnapshot(fixture.Root));
        Assert.False(Directory.Exists(fixture.Combine(".unbramble")));
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var names = connection.CreateCommand();
        names.CommandText = "SELECT group_concat(name) FROM sqlite_master WHERE type='table';";
        Assert.Equal("unrelated", names.ExecuteScalar());
    }

    private static void Execute(string dbPath, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
