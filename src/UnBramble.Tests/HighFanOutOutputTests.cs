using System.Text;
using System.Text.Json;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// High-fan-out validation (`uses` on a settings asset returned 398 dependencies, ~all
/// registry-package internals): text output collapses Library/PackageCache dependencies past
/// the threshold with an honest counted line (--verbose expands), `--under` scopes either verb
/// to one location, and `--json` never collapses anything.
///
/// The stock fixture has only one PackageCache file, so each test seeds its own high-fan-out
/// shape: 8 identity-only PackageCache targets plus one Assets-side target, all referenced by
/// one seeded FanOut.asset.
/// </summary>
public class HighFanOutOutputTests
{
    private const string PackageRelDir = "Library/PackageCache/com.fake.bigpkg@1.0.0/Runtime";
    private const int SeededPackageCacheRefs = 8;

    private static FixtureCopy CreateFanOutFixture()
    {
        var fixture = FixtureCopy.Create();

        var pkgDir = Path.Combine(fixture.Root, PackageRelDir.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(pkgDir);

        var refLines = new StringBuilder();
        for (var i = 0; i < SeededPackageCacheRefs; i++)
        {
            var guid = $"fadefadefadefadefadefadefadefa{i:00}";
            File.WriteAllText(Path.Combine(pkgDir, $"Res{i}.mat"), "binary-ish placeholder\n");
            File.WriteAllText(Path.Combine(pkgDir, $"Res{i}.mat.meta"), $"fileFormatVersion: 2\nguid: {guid}\nNativeFormatImporter:\n  mainObjectFileID: 2100000\n");
            refLines.Append($"  - {{fileID: 2100000, guid: {guid}, type: 2}}\n");
        }

        // Rock.mat (dddddddddddddddddddddddddddddd04) is the lone project-side dependency the
        // collapse must never hide.
        var fanOut =
            "%YAML 1.1\n" +
            "%TAG !u! tag:unity3d.com,2011:\n" +
            "--- !u!114 &11400000\n" +
            "MonoBehaviour:\n" +
            "  m_Items:\n" +
            refLines +
            "  m_Main: {fileID: 2100000, guid: dddddddddddddddddddddddddddddd04, type: 2}\n";
        File.WriteAllText(Path.Combine(fixture.Root, "Assets", "Data", "FanOut.asset"), fanOut);
        File.WriteAllText(
            Path.Combine(fixture.Root, "Assets", "Data", "FanOut.asset.meta"),
            "fileFormatVersion: 2\nguid: fa11fa11fa11fa11fa11fa11fa11fa11\nNativeFormatImporter:\n  mainObjectFileID: 11400000\n");

        return fixture;
    }

    [Fact]
    public void Uses_Text_CollapsesPackageCacheDependencies_KeepsProjectOnes()
    {
        using var fixture = CreateFanOutFixture();
        var (exit, stdOut, _) = CliRunner.Run("uses", "Assets/Data/FanOut.asset", "-p", fixture.Root);

        Assert.Equal(0, exit);
        Assert.Contains("9 direct dependencies", stdOut);
        Assert.Contains("Assets/Materials/Rock.mat", stdOut);
        Assert.Contains($"({SeededPackageCacheRefs} under Library/PackageCache", stdOut);
        Assert.DoesNotContain("com.fake.bigpkg", stdOut);
    }

    [Fact]
    public void Uses_Verbose_ListsPackageCacheDependencies()
    {
        using var fixture = CreateFanOutFixture();
        var (exit, stdOut, _) = CliRunner.Run("uses", "Assets/Data/FanOut.asset", "-p", fixture.Root, "--verbose");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("under Library/PackageCache —", stdOut);
        for (var i = 0; i < SeededPackageCacheRefs; i++)
        {
            Assert.Contains($"{PackageRelDir}/Res{i}.mat", stdOut);
        }
    }

    [Fact]
    public void Uses_UnderAssets_KeepsOnlyProjectSideDependency()
    {
        using var fixture = CreateFanOutFixture();
        var (exit, stdOut, _) = CliRunner.Run("uses", "Assets/Data/FanOut.asset", "-p", fixture.Root, "--under", "Assets");

        Assert.Equal(0, exit);
        Assert.Contains("1 direct dependency ", stdOut);
        Assert.Contains("Assets/Materials/Rock.mat", stdOut);
        Assert.DoesNotContain("Library/PackageCache", stdOut);
    }

    [Fact]
    public void Uses_UnderPackageCache_KeepsOnlyPackageDependencies_AndListsThem()
    {
        using var fixture = CreateFanOutFixture();
        var (exit, stdOut, _) = CliRunner.Run("uses", "Assets/Data/FanOut.asset", "-p", fixture.Root, "--under", "Library/PackageCache");

        Assert.Equal(0, exit);
        Assert.Contains($"{SeededPackageCacheRefs} direct dependencies", stdOut);
        Assert.DoesNotContain("Rock.mat", stdOut);
        // An explicit --under scope IS the expansion request — never collapse inside it.
        Assert.Contains($"{PackageRelDir}/Res0.mat", stdOut);
    }

    [Fact]
    public void Uses_Json_NeverCollapses()
    {
        using var fixture = CreateFanOutFixture();
        var (exit, stdOut, _) = CliRunner.Run("uses", "Assets/Data/FanOut.asset", "-p", fixture.Root, "--json");

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(stdOut);
        Assert.Equal(9, doc.RootElement.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public void WhoUses_Under_ScopesReferencers()
    {
        using var fixture = CreateFanOutFixture();
        var (exit, stdOut, _) = CliRunner.Run(
            "who-uses", "Assets/Materials/Rock.mat", "-p", fixture.Root, "--under", "Assets/Data", "--json");

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(stdOut);
        var results = doc.RootElement.GetProperty("results").EnumerateArray().ToList();
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.StartsWith("Assets/Data/", r.GetProperty("source").GetString()));
    }
}
