using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using UnBramble.Core;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// Section 6.1 (exhaustive edge-set equality — not subset) and 6.2 (query-time resolution)
/// against expected-edges.json. Reads the store's raw tables directly (via the same sqlite
/// file the engine just built) since the ground truth here is defined at the table-shape
/// level, not through any single query verb.
/// </summary>
public class EdgeExtractionTests
{
    private static readonly string ExpectedEdgesPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "expected-edges.json");

    [Fact]
    public void Index_GuidEdgeSet_ExactlyMatchesLedger_NoExtras()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var ledger = LoadLedger();
        var expected = ledger.GuidEdges.Select(e => (e.Source, e.TargetGuid)).ToHashSet();
        var actual = LoadDistinctGuidPairs(engine.DbPath);

        // Symmetric difference reported explicitly: extras are bugs too (dashed UUIDs,
        // resource(), m_SceneGUID, self-refs, hidden files, .cs comment guids, identity-only
        // sources are all "extra" failure modes this equality check exists to catch).
        var missing = expected.Except(actual).ToList();
        var extra = actual.Except(expected).ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0,
            $"missing: [{string.Join(", ", missing)}]; extra: [{string.Join(", ", extra)}]");
    }

    [Fact]
    public void Index_PathEdgeSet_ExactlyMatchesLedger_NoExtras()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var ledger = LoadLedger();
        var expected = ledger.PathEdges.Select(e => (e.Source, e.Raw)).ToHashSet();
        var actual = LoadDistinctPathPairs(engine.DbPath);

        var missing = expected.Except(actual).ToList();
        var extra = actual.Except(expected).ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0,
            $"missing: [{string.Join(", ", missing)}]; extra: [{string.Join(", ", extra)}]");
    }

    [Fact]
    public void Index_ForbiddenSourcePaths_NeverAppearAsARefSource()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var ledger = LoadLedger();
        var guidSources = LoadDistinctGuidPairs(engine.DbPath).Select(p => p.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pathSources = LoadDistinctPathPairs(engine.DbPath).Select(p => p.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var forbidden in ledger.ForbiddenSourcePaths)
        {
            Assert.DoesNotContain(forbidden, guidSources);
            Assert.DoesNotContain(forbidden, pathSources);
        }
    }

    [Fact]
    public void Index_ForbiddenTargetGuids_NeverStored()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var ledger = LoadLedger();
        var allTargets = LoadAllTargetGuids(engine.DbPath);

        foreach (var forbidden in ledger.ForbiddenTargetGuids)
        {
            Assert.DoesNotContain(forbidden, allTargets);
        }

        foreach (var substring in ledger.ForbiddenTargetSubstrings)
        {
            Assert.DoesNotContain(allTargets, t => t.Contains(substring, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Resolution_GuidEdges_MatchLedgerResolvesTo()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var ledger = LoadLedger();
        var resolved = LoadGuidResolution(engine.DbPath);

        foreach (var edge in ledger.GuidEdges)
        {
            Assert.True(resolved.TryGetValue((edge.Source, edge.TargetGuid), out var actualTarget),
                $"no refs row found for {edge.Source} -> {edge.TargetGuid}");
            Assert.Equal(edge.ResolvesTo, actualTarget, ignoreCase: true);
        }

        // The viaMeta edge specifically must resolve into PackageCache (identity-only files
        // are real join targets — this is the whole point of the LEFT JOIN in all_refs).
        var viaMeta = ledger.GuidEdges.Single(e => e.ViaMeta);
        Assert.Equal("Library/PackageCache/com.fake.graphtools@1.0.0/Editor/FakeImporter.cs", resolved[(viaMeta.Source, viaMeta.TargetGuid)], ignoreCase: true);
    }

    [Fact]
    public void Resolution_PathEdges_MatchLedgerResolvesTo()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var ledger = LoadLedger();
        var resolved = LoadPathResolution(engine.DbPath);

        foreach (var edge in ledger.PathEdges)
        {
            Assert.True(resolved.TryGetValue((edge.Source, edge.Raw), out var actualTarget),
                $"no path_refs row found for {edge.Source} -> {edge.Raw}");
            Assert.Equal(edge.ResolvesTo, actualTarget, ignoreCase: true);
        }
    }

    private static HashSet<(string Source, string TargetGuid)> LoadDistinctGuidPairs(string dbPath)
    {
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT sf.path, r.target_guid FROM refs r JOIN files sf ON sf.id = r.source_file_id;";
        using var reader = cmd.ExecuteReader();
        var result = new HashSet<(string, string)>();
        while (reader.Read())
        {
            result.Add((reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    private static HashSet<(string Source, string Raw)> LoadDistinctPathPairs(string dbPath)
    {
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT sf.path, p.target_path_raw FROM path_refs p JOIN files sf ON sf.id = p.source_file_id;";
        using var reader = cmd.ExecuteReader();
        var result = new HashSet<(string, string)>();
        while (reader.Read())
        {
            result.Add((reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    private static List<string> LoadAllTargetGuids(string dbPath)
    {
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT target_guid FROM refs;";
        using var reader = cmd.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static Dictionary<(string Source, string TargetGuid), string?> LoadGuidResolution(string dbPath)
    {
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT sf.path, r.target_guid, tf.path
            FROM refs r
            JOIN files sf ON sf.id = r.source_file_id
            LEFT JOIN files tf ON tf.guid = r.target_guid;
            """;
        using var reader = cmd.ExecuteReader();
        var result = new Dictionary<(string, string), string?>();
        while (reader.Read())
        {
            result[(reader.GetString(0), reader.GetString(1))] = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        return result;
    }

    private static Dictionary<(string Source, string Raw), string?> LoadPathResolution(string dbPath)
    {
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT sf.path, p.target_path_raw, tf.path
            FROM path_refs p
            JOIN files sf ON sf.id = p.source_file_id
            LEFT JOIN files tf ON tf.path = p.target_path_norm;
            """;
        using var reader = cmd.ExecuteReader();
        var result = new Dictionary<(string, string), string?>();
        while (reader.Read())
        {
            result[(reader.GetString(0), reader.GetString(1))] = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        return result;
    }

    private static SqliteConnection Open(string dbPath)
    {
        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        return conn;
    }

    private static Ledger LoadLedger()
    {
        var json = File.ReadAllText(ExpectedEdgesPath);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var ledger = JsonSerializer.Deserialize<Ledger>(json, options);
        Assert.NotNull(ledger);
        return ledger!;
    }

    private sealed class Ledger
    {
        [JsonPropertyName("guidEdges")]
        public List<GuidEdge> GuidEdges { get; set; } = [];

        [JsonPropertyName("pathEdges")]
        public List<PathEdge> PathEdges { get; set; } = [];

        [JsonPropertyName("forbiddenSourcePaths")]
        public List<string> ForbiddenSourcePaths { get; set; } = [];

        [JsonPropertyName("forbiddenTargetGuids")]
        public List<string> ForbiddenTargetGuids { get; set; } = [];

        [JsonPropertyName("forbiddenTargetSubstrings")]
        public List<string> ForbiddenTargetSubstrings { get; set; } = [];
    }

    private sealed class GuidEdge
    {
        [JsonPropertyName("source")]
        public string Source { get; set; } = "";

        [JsonPropertyName("targetGuid")]
        public string TargetGuid { get; set; } = "";

        [JsonPropertyName("resolvesTo")]
        public string? ResolvesTo { get; set; }

        [JsonPropertyName("viaMeta")]
        public bool ViaMeta { get; set; }
    }

    private sealed class PathEdge
    {
        [JsonPropertyName("source")]
        public string Source { get; set; } = "";

        [JsonPropertyName("raw")]
        public string Raw { get; set; } = "";

        [JsonPropertyName("resolvesTo")]
        public string? ResolvesTo { get; set; }
    }
}
