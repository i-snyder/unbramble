using System.Text.Json;
using System.Text.Json.Serialization;
using UnBramble.Core;
using UnBramble.Core.Model;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// After `index`, every expected-edges.json ledger path has a row with the ledger guid, plus
/// specific identity-only / guid-less / hidden-exclusion checks.
/// </summary>
public class InventoryCorrectnessTests
{
    private static readonly string ExpectedEdgesPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "expected-edges.json");

    private static Dictionary<string, string> LoadLedgerGuids()
    {
        var json = File.ReadAllText(ExpectedEdgesPath);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var doc = JsonSerializer.Deserialize<LedgerRoot>(json, options);
        Assert.NotNull(doc);
        return doc!.Guids;
    }

    [Fact]
    public void Index_EveryLedgerPath_HasRowWithLedgerGuid()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var files = engine.GetAllFiles().ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var ledger = LoadLedgerGuids();

        foreach (var (path, expectedGuid) in ledger)
        {
            Assert.True(files.TryGetValue(path, out var row), $"no row indexed for ledger path '{path}'");
            Assert.Equal(expectedGuid, row!.Guid, ignoreCase: true);
        }
    }

    [Fact]
    public void Index_PackageCacheFile_IsIdentityOnly()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var files = engine.GetAllFiles().ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        const string path = "Library/PackageCache/com.fake.graphtools@1.0.0/Editor/FakeImporter.cs";

        Assert.True(files.TryGetValue(path, out var row));
        Assert.True(row!.IdentityOnly);
        Assert.Equal(FileKind.Script, row.Kind);
    }

    [Fact]
    public void Index_ProjectSettingsAsset_IsGuidLessSettingsRow()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var files = engine.GetAllFiles().ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        const string path = "ProjectSettings/EditorBuildSettings.asset";

        Assert.True(files.TryGetValue(path, out var row));
        Assert.Null(row!.Guid);
        Assert.Equal(FileKind.Settings, row.Kind);
    }

    [Fact]
    public void Index_HiddenAssets_HaveNoRows()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var paths = engine.GetAllFiles().Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("Assets/Ignored~/Skipped.mat", paths);
        Assert.DoesNotContain("Assets/.hidden.mat", paths);
        Assert.DoesNotContain(paths, p => p.StartsWith("Assets/Ignored~/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Index_FolderRows_ExistWithGuids()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var files = engine.GetAllFiles().ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);

        foreach (var folderPath in new[] { "Assets/Scripts", "Assets/Prefabs", "Assets/Materials", "Assets/Data" })
        {
            Assert.True(files.TryGetValue(folderPath, out var row), $"no folder row for '{folderPath}'");
            Assert.Equal(FileKind.Folder, row!.Kind);
            Assert.NotNull(row.Guid);
        }
    }

    private sealed class LedgerRoot
    {
        [JsonPropertyName("guids")]
        public Dictionary<string, string> Guids { get; set; } = new();
    }
}
