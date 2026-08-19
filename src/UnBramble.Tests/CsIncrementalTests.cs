using Microsoft.Data.Sqlite;
using UnBramble.Core;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>A `.cs` change marks its assembly and its transitive dependents dirty; both get re-analyzed on the next index.</summary>
public class CsIncrementalTests
{
    [Fact]
    public void EditingCoreUtil_ReanalyzesBothCoreAndGame_AnalyzedUtcAdvances()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var before = ReadAnalyzedUtc(engine.DbPath);
        Assert.True(before.ContainsKey("Core"));
        Assert.True(before.ContainsKey("Game"));

        var coreUtilPath = fixture.Combine("Assets", "Scripts", "Core", "CoreUtil.cs");
        File.AppendAllText(coreUtilPath, "\n// touched\n");
        File.SetLastWriteTimeUtc(coreUtilPath, DateTime.UtcNow.AddSeconds(5));

        engine.RunIndex(full: false);
        var after = ReadAnalyzedUtc(engine.DbPath);

        Assert.NotEqual(before["Core"], after["Core"]);
        Assert.NotEqual(before["Game"], after["Game"]);
    }

    [Fact]
    public void EditingUnrelatedAsset_DoesNotDisturbCsAssemblyAnalysis()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var before = ReadAnalyzedUtc(engine.DbPath);

        var rockMat = fixture.Combine("Assets", "Materials", "Rock.mat");
        File.AppendAllText(rockMat, "\n# touched\n");
        File.SetLastWriteTimeUtc(rockMat, DateTime.UtcNow.AddSeconds(5));

        engine.RunIndex(full: false);
        var after = ReadAnalyzedUtc(engine.DbPath);

        // Neither assembly's own .cs files nor its asmdef changed -- "never wrong, only
        // sometimes slower" means an unrelated asset touch must not force a re-analysis.
        Assert.Equal(before["Core"], after["Core"]);
        Assert.Equal(before["Game"], after["Game"]);
    }

    private static Dictionary<string, string> ReadAnalyzedUtc(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, analyzed_utc FROM assemblies;";
        using var reader = cmd.ExecuteReader();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);
        }

        return result;
    }
}
