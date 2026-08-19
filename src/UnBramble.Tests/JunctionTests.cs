using System.Diagnostics;
using UnBramble.Core.Config;
using UnBramble.Core.Model;
using UnBramble.Core.Scanning;
using UnBramble.Core.Store;

namespace UnBramble.Tests;

/// <summary>Section 6.5: junction-following, realpath dedupe, dangling links, roots persistence.</summary>
public class JunctionTests
{
    [Fact]
    public void Scan_JunctionAndDirectPath_ToSameRealDirectory_ScansContentsOnce()
    {
        var root = CreateTempRoot();
        try
        {
            var assets = Path.Combine(root, "Assets");
            Directory.CreateDirectory(assets);
            var real = Path.Combine(assets, "Real");
            Directory.CreateDirectory(real);
            WriteAssetWithMeta(Path.Combine(real, "Thing.asset"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb99");

            CreateJunction(Path.Combine(assets, "ViaJunction"), real);

            var config = new UnBrambleConfig { Roots = ["Assets"] };
            var result = new Scanner().Scan(root, config);

            var matches = result.Entries.Where(e => e.Guid == "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb99").ToList();
            var single = Assert.Single(matches);
            Assert.True(
                string.Equals(single.Path, "Assets/Real/Thing.asset", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(single.Path, "Assets/ViaJunction/Thing.asset", StringComparison.OrdinalIgnoreCase),
                $"unexpected path '{single.Path}'");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Scan_TargetOnlyReachableViaJunction_RecordsPathAsSeenThroughJunction()
    {
        var root = CreateTempRoot();
        try
        {
            var assets = Path.Combine(root, "Assets");
            Directory.CreateDirectory(assets);
            var external = Path.Combine(root, "ExternalTarget");
            Directory.CreateDirectory(external);
            WriteAssetWithMeta(Path.Combine(external, "ExternalThing.asset"), "cccccccccccccccccccccccccccccc99");

            CreateJunction(Path.Combine(assets, "Vendor"), external);

            var config = new UnBrambleConfig { Roots = ["Assets"] };
            var result = new Scanner().Scan(root, config);

            var match = Assert.Single(result.Entries, e => e.Guid == "cccccccccccccccccccccccccccccc99");
            Assert.Equal("Assets/Vendor/ExternalThing.asset", match.Path, ignoreCase: true);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Scan_DanglingJunction_WarnsAndDoesNotCrash()
    {
        var root = CreateTempRoot();
        try
        {
            var assets = Path.Combine(root, "Assets");
            Directory.CreateDirectory(assets);
            CreateJunction(Path.Combine(assets, "Dangling"), Path.Combine(root, "DoesNotExist"));

            var config = new UnBrambleConfig { Roots = ["Assets"] };
            var result = new Scanner().Scan(root, config);

            Assert.Contains(
                result.Warnings,
                w => w.Contains("dangling", StringComparison.OrdinalIgnoreCase) && w.Contains("Assets/Dangling", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// A genuine cycle: Assets/Link -> External (junction), and External/Back -> Assets
    /// (junction back to an ancestor of Assets/Link, i.e. the root the scan is currently
    /// walking). Without cycle detection that's live *during* an in-progress recursive
    /// descent, this recurses forever: Assets -> Link -> External -> Back -> Assets -> Link ->
    /// ... Scanner.Walk's `visitedReal` set is added-to *before* recursing into children (not
    /// only after a subtree finishes), so it doubles as a "currently visiting" guard — this
    /// test proves that holds by running the scan on a background thread with a hard wall-clock
    /// budget and failing loudly if it's ever exceeded, rather than hanging the whole test run.
    /// </summary>
    [Fact]
    public async Task Scan_CyclicJunction_TerminatesAndDedupesRatherThanHanging()
    {
        var root = CreateTempRoot();
        try
        {
            var assets = Path.Combine(root, "Assets");
            Directory.CreateDirectory(assets);
            var external = Path.Combine(root, "External");
            Directory.CreateDirectory(external);
            WriteAssetWithMeta(Path.Combine(external, "ExternalThing.asset"), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeee99");

            // Assets/Link -> External
            CreateJunction(Path.Combine(assets, "Link"), external);
            // External/Back -> Assets (ancestor of Assets/Link) -- closes the cycle.
            CreateJunction(Path.Combine(external, "Back"), assets);

            var config = new UnBrambleConfig { Roots = ["Assets"] };
            var scanner = new Scanner();

            var task = Task.Run(() => scanner.Scan(root, config));
            var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30)));

            Assert.True(
                ReferenceEquals(winner, task),
                "Scan of a cyclic junction topology did not terminate within 30s -- this is the hang bug.");
            var result = await task;

            // Sane, deduped result: the external file is reachable (via Assets/Link) exactly
            // once, not once per trip around the cycle.
            var matches = result.Entries.Where(e => e.Guid == "eeeeeeeeeeeeeeeeeeeeeeeeeeeeee99").ToList();
            Assert.Single(matches);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Sweep_PersistsRootMappingForJunctionTarget()
    {
        var root = CreateTempRoot();
        try
        {
            var assets = Path.Combine(root, "Assets");
            Directory.CreateDirectory(assets);
            var external = Path.Combine(root, "ExternalTarget2");
            Directory.CreateDirectory(external);
            WriteAssetWithMeta(Path.Combine(external, "Thing.asset"), "dddddddddddddddddddddddddddddd99");

            CreateJunction(Path.Combine(assets, "Vendor2"), external);

            var config = new UnBrambleConfig { Roots = ["Assets"] };
            var scanResult = new Scanner().Scan(root, config);

            var dbPath = Path.Combine(root, "unbramble-test.db");
            using var store = UnBrambleStore.OpenOrCreate(dbPath, "test");
            store.ApplySweep(scanResult);
            store.ReplaceRoots(scanResult.Roots);

            var expectedRealPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(external));
            var mapping = Assert.Single(
                store.GetRoots(),
                r => string.Equals(r.RealPath, expectedRealPath, StringComparison.OrdinalIgnoreCase));
            Assert.Equal("Assets/Vendor2", mapping.ProjectPrefix, ignoreCase: true);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// A root-level junction must produce its own
    /// <see cref="ScanRootStat"/> entry with IsJunction=true and ResolvedTarget pointing at the
    /// real (outside-the-project-root) directory -- this is the field a human or an
    /// Add-MpPreference exclusion needs, and it's the discriminator between "one junction target
    /// scanned slowly" and "a uniform code regression". The ordinary Assets root itself must also
    /// get a (non-junction) entry.
    /// </summary>
    [Fact]
    public void Scan_RootLevelJunction_RecordsOwnRootStatWithResolvedTarget()
    {
        var root = CreateTempRoot();
        try
        {
            var assets = Path.Combine(root, "Assets");
            Directory.CreateDirectory(assets);
            var external = Path.Combine(root, "ExternalPackage");
            Directory.CreateDirectory(external);
            WriteAssetWithMeta(Path.Combine(external, "Thing.asset"), "ffffffffffffffffffffffffffffff99");

            CreateJunction(Path.Combine(assets, "Vendor3"), external);

            var config = new UnBrambleConfig { Roots = ["Assets"] };
            var result = new Scanner().Scan(root, config);

            var expectedRealPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(external));
            var junctionStat = Assert.Single(result.RootStats, s => s.IsJunction);
            Assert.Equal("Assets/Vendor3", junctionStat.ProjectPrefix, ignoreCase: true);
            Assert.Equal(expectedRealPath, junctionStat.ResolvedTarget, ignoreCase: true);
            Assert.True(junctionStat.Files >= 1, "junction target's own file should be counted under its own root stat");

            var assetsStat = Assert.Single(result.RootStats, s => !s.IsJunction);
            Assert.Equal("Assets", assetsStat.ProjectPrefix);
            Assert.Null(assetsStat.ResolvedTarget);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// Real-project crash found via real-world full-scale validation: `SQLite Error 19:
    /// UNIQUE constraint failed: files.path`, unhandled, deep inside sweep-diff apply, aborting
    /// the whole index with no indication of which path collided. Root cause traced to
    /// `Scanner.Walk`'s `visitedReal` guard: it only prevents re-walking the same REAL
    /// directory path twice, and does nothing when two DIFFERENT real files resolve to the
    /// identical (case-insensitive) project-relative path -- which happens when a real
    /// directory has Windows' per-directory case-sensitivity flag enabled (`fsutil file
    /// setCaseSensitiveInfo ... enable`, a genuine NTFS/WSL-interop feature, not exotic --
    /// common in large third-party asset trees synced with non-Windows-native tooling) and
    /// legitimately contains two files whose names differ only by case (e.g. "Thing.asset" and
    /// "thing.asset"). `Directory.EnumerateFileSystemInfos()` returns both as distinct entries;
    /// `UnBrambleStore`'s `files.path` column is `UNIQUE COLLATE NOCASE`, so both compose to the
    /// same stored path. Reproduced here via two junctions into one case-sensitive external
    /// directory (rather than a bare case-sensitive folder directly under Assets) to mirror a
    /// real project's topology, where the colliding directory is itself only reachable through
    /// a package junction, not a plain in-project folder.
    /// </summary>
    [Fact]
    public void Scan_CaseOnlyDuplicateFilenamesInJunctionTarget_DedupesWithWarningInsteadOfProducingCollidingPaths()
    {
        var root = CreateTempRoot();
        try
        {
            var assets = Path.Combine(root, "Assets");
            Directory.CreateDirectory(assets);
            var external = Path.Combine(root, "ExternalCaseSensitive");
            Directory.CreateDirectory(external);
            EnableCaseSensitivity(external);

            // Two genuinely distinct real files, differing only by case -- both legal to create
            // once the directory is case-sensitive, exactly as observed on a real external
            // package tree that crashed a real project's scan.
            WriteAssetWithMeta(Path.Combine(external, "Thing.asset"), "11111111111111111111111111111199");
            WriteAssetWithMeta(Path.Combine(external, "thing.asset"), "22222222222222222222222222222299");

            CreateJunction(Path.Combine(assets, "CasePkg"), external);

            var config = new UnBrambleConfig { Roots = ["Assets"] };

            // The bug: this used to be observable only as an unhandled SQLite UNIQUE-constraint
            // exception several layers away, inside UnBrambleStore.ApplySweep -- Scan() itself
            // never threw, it just silently produced two colliding entries. The fix moves the
            // dedup to the scan boundary, so Scan() itself never returns colliding paths.
            var result = new Scanner().Scan(root, config);

            var casePkgEntries = result.Entries.Where(e => e.Path.StartsWith("Assets/CasePkg/", StringComparison.OrdinalIgnoreCase)).ToList();
            var single = Assert.Single(casePkgEntries, e => e.Path.EndsWith("thing.asset", StringComparison.OrdinalIgnoreCase));
            Assert.True(
                single.Guid == "11111111111111111111111111111199" || single.Guid == "22222222222222222222222222222299",
                $"unexpected guid '{single.Guid}' for the surviving entry");

            Assert.Contains(
                result.Warnings,
                w => w.Contains("duplicate project path", StringComparison.OrdinalIgnoreCase) &&
                     w.Contains("Assets/CasePkg", StringComparison.OrdinalIgnoreCase));

            // The real end-to-end guarantee: a deduped ScanResult must actually apply to the
            // store without the UNIQUE-constraint crash a real run hit.
            var dbPath = Path.Combine(root, "unbramble-casedupe-test.db");
            using var store = UnBrambleStore.OpenOrCreate(dbPath, "test");
            store.ApplySweep(result);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void EnableCaseSensitivity(string directoryPath)
    {
        var startInfo = new ProcessStartInfo("fsutil.exe", $"file setCaseSensitiveInfo \"{directoryPath}\" enable")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start fsutil.exe");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"fsutil file setCaseSensitiveInfo failed ({process.ExitCode}): {process.StandardError.ReadToEnd()} " +
                "-- this test requires Windows 10 1903+ with per-directory case sensitivity support (Developer Mode " +
                "or admin may be required on some configurations).");
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "unbramble-junction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteAssetWithMeta(string assetPath, string guid)
    {
        File.WriteAllText(assetPath, "fake asset content");
        File.WriteAllText(assetPath + ".meta", $"fileFormatVersion: 2\nguid: {guid}\n");
    }

    private static void CreateJunction(string linkPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start cmd.exe");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"mklink /J failed ({process.ExitCode}): {process.StandardError.ReadToEnd()}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
