using System.Diagnostics;
using System.Text.Json;
using UnBramble.Core.Config;
using UnBramble.Core.Model;
using UnBramble.Core.Scanning;
using UnBramble.Core.Store;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

public class LocalPackageDiscoveryTests
{
    private const string ScriptGuid = "a1000000000000000000000000000001";

    [Fact]
    public void Fingerprint_NormalizesCaseSeparatorsAndOrder_ButRetainsLogicalAliasesAndPhysicalTargets()
    {
        using var temp = TempDir.Create();
        var first = Path.Combine(temp.Root, "First");
        var second = Path.Combine(temp.Root, "Second");
        RootMapping[] roots = [new(first, "Packages/com.example.first"), new(second, "Packages/com.example.second")];
        RootMapping[] equivalent =
        [
            new(second.ToUpperInvariant().Replace('\\', '/') + "/", "PACKAGES\\COM.EXAMPLE.SECOND\\"),
            new(first.ToUpperInvariant().Replace('\\', '/'), "PACKAGES/COM.EXAMPLE.FIRST"),
        ];

        var fingerprint = LocalPackageRoots.Fingerprint(roots);
        Assert.Equal(fingerprint, LocalPackageRoots.Fingerprint(equivalent));
        Assert.NotEqual(fingerprint, LocalPackageRoots.Fingerprint([roots[0], roots[1] with { RealPath = first }]));
        Assert.NotEqual(fingerprint, LocalPackageRoots.Fingerprint([roots[0], roots[1] with { ProjectPrefix = "Packages/com.example.alias" }]));
        Assert.NotEqual(fingerprint, LocalPackageRoots.Fingerprint([roots[0]]));
    }

    [Fact]
    public void Store_LocalPackageFingerprint_IsOptionalAndPersistsAcrossReopen()
    {
        using var temp = TempDir.Create();
        var database = Path.Combine(temp.Root, "test.db");
        var fingerprint = LocalPackageRoots.Fingerprint([]);
        using (var store = UnBrambleStore.OpenOrCreate(database, "test"))
        {
            Assert.Null(store.GetLocalPackageRootsFingerprint());
            store.SetLocalPackageRootsFingerprint(fingerprint);
        }

        using var reopened = UnBrambleStore.OpenOrCreate(database, "test");
        Assert.Equal(fingerprint, reopened.GetLocalPackageRootsFingerprint());
    }

    [Fact]
    public void Scan_LocalFolderJunction_UsesPackagePathAndPersistsResolvedTarget()
    {
        using var temp = TempDir.Create();
        var project = Path.Combine(temp.Root, "Project");
        var external = Path.Combine(temp.Root, "[Package] External", "Release");
        WriteScript(external);
        var link = Path.Combine(temp.Root, "Linked Package");
        CreateJunction(link, external);
        WriteManifest(project, new() { ["com.example.external"] = "file:" + link.Replace('\\', '/') });

        var scan = new Scanner().Scan(project, new UnBrambleConfig());

        var script = Assert.Single(scan.Entries, entry => entry.Guid == ScriptGuid);
        Assert.Equal("Packages/com.example.external/Runtime/Component.cs", script.Path);
        Assert.False(script.IdentityOnly);
        using var store = UnBrambleStore.OpenOrCreate(Path.Combine(temp.Root, "test.db"), "test");
        store.ApplySweep(scan);
        store.ReplaceRoots(scan.Roots);
        var mapping = Assert.Single(store.GetRoots(), root => root.ProjectPrefix == "Packages/com.example.external");
        Assert.Equal(Path.GetFullPath(external), mapping.RealPath, ignoreCase: true);
        var targeted = new Scanner().ScanSingleFile(project, script.Path, new UnBrambleConfig(), [], Path.Combine(external, "Runtime", "Component.cs"));
        Assert.NotNull(targeted);
        Assert.Equal(ScriptGuid, targeted.Guid);
        Assert.Equal(script.Path, targeted.Path);
    }

    [Fact]
    public void Discover_RelativePath_IsRelativeToPackagesFolder()
    {
        using var temp = TempDir.Create();
        var project = Path.Combine(temp.Root, "Project");
        var external = Path.Combine(temp.Root, "External");
        WriteScript(external);
        WriteManifest(project, new() { ["com.example.external"] = "file:../../External" });

        var roots = LocalPackageRoots.Discover(project, new UnBrambleConfig(), []);

        var root = Assert.Single(roots);
        Assert.Equal(external, root.RealPath, ignoreCase: true);
        Assert.Equal("Packages/com.example.external", root.ProjectPrefix);
    }

    [Fact]
    public void Discover_GitFileUrlAndTarball_AreLeftToPackageCache()
    {
        using var temp = TempDir.Create();
        var project = Path.Combine(temp.Root, "Project");
        Directory.CreateDirectory(Path.Combine(temp.Root, "Source"));
        WriteManifest(project, new()
        {
            ["com.example.git"] = "file://" + Path.Combine(temp.Root, "Source").Replace('\\', '/'),
            ["com.example.tarball"] = "file:../../package.tgz",
            ["com.example.tar"] = "file:../../package.tar.gz",
            ["com.example.registry"] = "1.2.3",
        });
        var warnings = new List<string>();

        Assert.Empty(LocalPackageRoots.Discover(project, new UnBrambleConfig(), warnings));
        Assert.Empty(warnings);
    }

    [Fact]
    public void Discover_MissingFolder_Warns()
    {
        using var temp = TempDir.Create();
        var project = Path.Combine(temp.Root, "Project");
        WriteManifest(project, new() { ["com.example.missing"] = "file:../../Missing" });
        var warnings = new List<string>();

        Assert.Empty(LocalPackageRoots.Discover(project, new UnBrambleConfig(), warnings));
        Assert.Contains(warnings, warning => warning.Contains("com.example.missing", StringComparison.Ordinal) && warning.Contains("missing", StringComparison.Ordinal));
    }

    [Fact]
    public void Discover_MalformedManifest_Warns()
    {
        using var temp = TempDir.Create();
        var project = Path.Combine(temp.Root, "Project");
        Directory.CreateDirectory(Path.Combine(project, "Packages"));
        File.WriteAllText(Path.Combine(project, "Packages", "manifest.json"), "{ malformed");
        var warnings = new List<string>();

        Assert.Empty(LocalPackageRoots.Discover(project, new UnBrambleConfig(), warnings));
        Assert.Contains(warnings, warning => warning.Contains("could not parse 'Packages/manifest.json'", StringComparison.Ordinal));
    }

    [Fact]
    public void Discover_ManifestSharingDenied_ThrowsInsteadOfDroppingPackageRoots()
    {
        using var temp = TempDir.Create();
        var project = Path.Combine(temp.Root, "Project");
        WriteManifest(project, new());
        using var locked = new FileStream(Path.Combine(project, "Packages", "manifest.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Throws<IOException>(() => LocalPackageRoots.Discover(project, new UnBrambleConfig(), []));
    }

    [Fact]
    public void Discover_EmbeddedPackageNameOverridesExternalDependency_EvenWithDifferentFolderName()
    {
        using var temp = TempDir.Create();
        var project = Path.Combine(temp.Root, "Project");
        var external = Path.Combine(temp.Root, "External");
        WriteScript(external);
        WriteManifest(project, new() { ["com.example.external"] = "file:../../External" });
        var embedded = Path.Combine(project, "Packages", "RenamedFolder");
        Directory.CreateDirectory(embedded);
        File.WriteAllText(Path.Combine(embedded, "package.json"), """{ "name": "com.example.external", "version": "1.0.0" }""");

        Assert.Empty(LocalPackageRoots.Discover(project, new UnBrambleConfig(), []));
    }

    [Fact]
    public void Discover_PackagesOutsideConfiguredRoots_DoesNotExpandScope()
    {
        using var temp = TempDir.Create();
        var project = Path.Combine(temp.Root, "Project");
        WriteManifest(project, new() { ["com.example.external"] = "file:../../External" });
        var warnings = new List<string>();

        Assert.Empty(LocalPackageRoots.Discover(project, new UnBrambleConfig { Roots = ["Assets"] }, warnings));
        Assert.Empty(warnings);
    }

    [Fact]
    public void Scan_TwoPackageAliasesForOneRealFolder_KeepDeterministicFirstPathAndMapping()
    {
        using var temp = TempDir.Create();
        var project = Path.Combine(temp.Root, "Project");
        WriteScript(Path.Combine(temp.Root, "External"));
        WriteManifest(project, new()
        {
            ["com.example.z"] = "file:../../External",
            ["com.example.a"] = "file:../../External",
        });

        var scan = new Scanner().Scan(project, new UnBrambleConfig());
        var script = Assert.Single(scan.Entries, entry => entry.Guid == ScriptGuid);
        Assert.Equal("Packages/com.example.a/Runtime/Component.cs", script.Path);
        using var store = UnBrambleStore.OpenOrCreate(Path.Combine(temp.Root, "test.db"), "test");
        store.ReplaceRoots(scan.Roots);
        var mapping = Assert.Single(store.GetRoots(), root => root.RealPath.Equals(Path.Combine(temp.Root, "External"), StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Packages/com.example.a", mapping.ProjectPrefix);
    }

    [Fact]
    public void Scan_PackageAlreadyUnderConfiguredPhysicalRoot_KeepsPhysicalPathWithoutShadowMapping()
    {
        using var temp = TempDir.Create();
        var project = Path.Combine(temp.Root, "Project");
        var physicalPackage = Path.Combine(project, "LocalPackages", "External");
        WriteScript(physicalPackage);
        WriteManifest(project, new() { ["com.example.external"] = "file:../LocalPackages/External" });

        var scan = new Scanner().Scan(project, new UnBrambleConfig());

        var script = Assert.Single(scan.Entries, entry => entry.Guid == ScriptGuid);
        Assert.Equal("LocalPackages/External/Runtime/Component.cs", script.Path);
        Assert.DoesNotContain(scan.Roots, root => root.ProjectPrefix == "Packages/com.example.external");
        Assert.Equal(LocalPackageRoots.Fingerprint([new RootMapping(physicalPackage, "Packages/com.example.external")]), scan.LocalPackageRootsFingerprint);
        var targeted = new Scanner().ScanSingleFile(project, script.Path, new UnBrambleConfig(), []);
        Assert.NotNull(targeted);
        Assert.Equal(script, targeted);
    }

    [Fact]
    public void Scan_DeferredJunctionAliasOfLocalPackage_DoesNotPersistUnusedAliasMapping()
    {
        using var temp = TempDir.Create();
        var project = Path.Combine(temp.Root, "Project");
        var external = Path.Combine(temp.Root, "External");
        WriteScript(external);
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        CreateJunction(Path.Combine(project, "Assets", "Alias"), external);
        WriteManifest(project, new() { ["com.example.external"] = "file:../../External" });

        var scan = new Scanner().Scan(project, new UnBrambleConfig());

        var script = Assert.Single(scan.Entries, entry => entry.Guid == ScriptGuid);
        Assert.Equal("Packages/com.example.external/Runtime/Component.cs", script.Path);
        Assert.DoesNotContain(scan.Roots, root => root.ProjectPrefix == "Assets/Alias");
        Assert.Single(scan.Roots, root => root.RealPath.Equals(external, StringComparison.OrdinalIgnoreCase));
    }

    private static void WriteScript(string root)
    {
        var runtime = Path.Combine(root, "Runtime");
        Directory.CreateDirectory(runtime);
        File.WriteAllText(Path.Combine(runtime, "Component.cs"), "public class Component { }");
        File.WriteAllText(Path.Combine(runtime, "Component.cs.meta"), $"fileFormatVersion: 2\nguid: {ScriptGuid}\n");
    }

    private static void WriteManifest(string project, Dictionary<string, string> dependencies)
    {
        var packages = Path.Combine(project, "Packages");
        Directory.CreateDirectory(packages);
        File.WriteAllText(Path.Combine(packages, "manifest.json"), JsonSerializer.Serialize(new { dependencies }));
    }

    private static void CreateJunction(string linkPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start cmd.exe");
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}
