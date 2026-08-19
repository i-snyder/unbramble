using UnBramble.Core;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>Section 6.6 (incremental sweep) and 6.7 (guid collision).</summary>
public class IncrementalSweepTests
{
    [Fact]
    public void Sweep_TouchedFileWithChangedMetaGuid_RowUpdated_OldGuidGone()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var metaPath = fixture.Combine("Assets", "Data", "A.asset.meta");
        var content = File.ReadAllText(metaPath)
            .Replace("a0a0a0a0a0a0a0a0a0a0a0a0a0a0a019", "a0a0a0a0a0a0a0a0a0a0a0a0a0a0a099", StringComparison.Ordinal);
        File.WriteAllText(metaPath, content);
        BumpMtime(metaPath);
        BumpMtime(fixture.Combine("Assets", "Data", "A.asset"));

        var summary = engine.RunIndex(full: false);

        var files = engine.GetAllFiles().ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        Assert.True(files.TryGetValue("Assets/Data/A.asset", out var row));
        Assert.Equal("a0a0a0a0a0a0a0a0a0a0a0a0a0a0a099", row!.Guid, ignoreCase: true);
        Assert.DoesNotContain(files.Values, f => string.Equals(f.Guid, "a0a0a0a0a0a0a0a0a0a0a0a0a0a0a019", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, summary.Changed);
    }

    [Fact]
    public void Sweep_DeletedFileAndMeta_RowGone()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        File.Delete(fixture.Combine("Assets", "Data", "B.asset"));
        File.Delete(fixture.Combine("Assets", "Data", "B.asset.meta"));

        var summary = engine.RunIndex(full: false);

        var paths = engine.GetAllFiles().Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Assets/Data/B.asset", paths);
        Assert.Equal(1, summary.Removed);
    }

    [Fact]
    public void Sweep_NewAssetAndMeta_RowAppears()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var newAsset = fixture.Combine("Assets", "Data", "C.asset");
        File.WriteAllText(newAsset, "fake");
        File.WriteAllText(newAsset + ".meta", "fileFormatVersion: 2\nguid: c0c0c0c0c0c0c0c0c0c0c0c0c0c0c021\n");

        var summary = engine.RunIndex(full: false);

        var files = engine.GetAllFiles().ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        Assert.True(files.TryGetValue("Assets/Data/C.asset", out var row));
        Assert.Equal("c0c0c0c0c0c0c0c0c0c0c0c0c0c0c021", row!.Guid, ignoreCase: true);
        Assert.Equal(1, summary.Added);
    }

    [Fact]
    public void Sweep_RenamedFileAndMeta_ExactlyOneRowNewPathSameGuid_NoCollisionWarning()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var oldAsset = fixture.Combine("Assets", "Materials", "Rock.mat");
        var newAsset = fixture.Combine("Assets", "Materials", "RockRenamed.mat");
        File.Move(oldAsset, newAsset);
        File.Move(oldAsset + ".meta", newAsset + ".meta");

        var summary = engine.RunIndex(full: false);

        var matches = engine.GetAllFiles()
            .Where(f => string.Equals(f.Guid, "dddddddddddddddddddddddddddddd04", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var single = Assert.Single(matches);
        Assert.Equal("Assets/Materials/RockRenamed.mat", single.Path, ignoreCase: true);
        Assert.DoesNotContain(summary.Warnings, w => w.Contains("collision", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Sweep_CopiedAssetAndMetaToSecondPath_BothRowsExist_CollisionWarningEmitted()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var original = fixture.Combine("Assets", "Materials", "Rock.mat");
        var copy = fixture.Combine("Assets", "Materials", "RockCopy.mat");
        File.Copy(original, copy);
        File.Copy(original + ".meta", copy + ".meta");

        var summary = engine.RunIndex(full: false);

        var matches = engine.GetAllFiles()
            .Where(f => string.Equals(f.Guid, "dddddddddddddddddddddddddddddd04", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal(2, matches.Count);
        Assert.Contains(matches, f => f.Path.Equals("Assets/Materials/Rock.mat", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(matches, f => f.Path.Equals("Assets/Materials/RockCopy.mat", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(summary.Warnings, w => w.Contains("collision", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Sweep_NoChanges_ReportsZeroDiffOnSecondRun()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var summary = engine.RunIndex(full: false);

        Assert.Equal(0, summary.Added);
        Assert.Equal(0, summary.Changed);
        Assert.Equal(0, summary.Removed);
    }

    private static void BumpMtime(string path) =>
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
}
