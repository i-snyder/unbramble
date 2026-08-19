using System.Text.Json;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// Guid-collision reporting: a duplicate-heavy first index used to
/// print one warning per colliding guid, flooding setup output. Past a small inline cap the
/// sweep now emits ONE counted warning pointing at `stats --collisions`, which derives the
/// current collision state live from the DB.
/// </summary>
public class GuidCollisionTests
{
    private const string RockGuid = "dddddddddddddddddddddddddddddd04";

    private static void SeedRockCopies(FixtureCopy fixture, int count)
    {
        var materials = Path.Combine(fixture.Root, "Assets", "Materials");
        for (var i = 0; i < count; i++)
        {
            File.Copy(Path.Combine(materials, "Rock.mat"), Path.Combine(materials, $"RockCopy{i}.mat"));
            File.Copy(Path.Combine(materials, "Rock.mat.meta"), Path.Combine(materials, $"RockCopy{i}.mat.meta"));
        }
    }

    [Fact]
    public void Index_ManyCollisions_CompactedToOneCountedWarning()
    {
        using var fixture = FixtureCopy.Create();
        SeedRockCopies(fixture, 5);

        var (exit, _, stdErr) = CliRunner.Run("index", "-p", fixture.Root);

        Assert.Equal(0, exit);
        Assert.Contains("guid collisions found this sweep", stdErr);
        Assert.Contains("stats --collisions", stdErr);
        Assert.DoesNotContain("collision between", stdErr);
    }

    [Fact]
    public void Index_FewCollisions_StayInline()
    {
        using var fixture = FixtureCopy.Create();
        SeedRockCopies(fixture, 1);

        var (exit, _, stdErr) = CliRunner.Run("index", "-p", fixture.Root);

        Assert.Equal(0, exit);
        Assert.Contains("collision between", stdErr);
        Assert.DoesNotContain("guid collisions found this sweep", stdErr);
    }

    [Fact]
    public void Stats_ShowsCollisionCountRow_AndCollisionsFlagListsGroups()
    {
        using var fixture = FixtureCopy.Create();
        SeedRockCopies(fixture, 5);
        _ = CliRunner.Run("index", "-p", fixture.Root);

        var (statsExit, statsOut, _) = CliRunner.Run("stats", "-p", fixture.Root);
        Assert.Equal(0, statsExit);
        Assert.Contains("Guid collisions: 1 guid claimed by 6 files (list: stats --collisions)", statsOut);

        var (listExit, listOut, _) = CliRunner.Run("stats", "-p", fixture.Root, "--collisions");
        Assert.Equal(0, listExit);
        Assert.Contains($"guid {RockGuid}:", listOut);
        Assert.Contains("Assets/Materials/Rock.mat", listOut);
        Assert.Contains("Assets/Materials/RockCopy4.mat", listOut);
    }

    [Fact]
    public void Stats_Collisions_Json()
    {
        using var fixture = FixtureCopy.Create();
        SeedRockCopies(fixture, 2);
        _ = CliRunner.Run("index", "-p", fixture.Root);

        var (exit, stdOut, _) = CliRunner.Run("stats", "-p", fixture.Root, "--collisions", "--json");

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(stdOut);
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        var group = doc.RootElement.GetProperty("groups").EnumerateArray().Single();
        Assert.Equal(RockGuid, group.GetProperty("guid").GetString());
        Assert.Equal(3, group.GetProperty("paths").GetArrayLength());
    }

    [Fact]
    public void Stats_NoCollisions_NoRow()
    {
        using var fixture = FixtureCopy.Create();
        _ = CliRunner.Run("index", "-p", fixture.Root);

        var (exit, stdOut, _) = CliRunner.Run("stats", "-p", fixture.Root);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("Guid collisions:", stdOut);
    }
}
