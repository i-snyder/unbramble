using System.Text.Json;
using UnBramble.Core;
using UnBramble.Core.Config;
using UnBramble.Core.Freshness;
using UnBramble.Core.Scanning;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

public class LocalPackageTests
{
    private const string PackageName = "com.example.network";
    private const string ScriptPath = "Packages/com.example.network/Runtime/NetworkEntity.cs";
    private const string PackageAssetPath = "Packages/com.example.network/Runtime/Settings.asset";
    private const string ScriptGuid = "a1710000000000000000000000000001";
    private const string ChangedScriptGuid = "a1710000000000000000000000000002";
    private const string FirstTargetGuid = "a1710000000000000000000000000003";
    private const string SecondTargetGuid = "a1710000000000000000000000000004";
    private const string PackageAssetGuid = "a1710000000000000000000000000005";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Index_ExternalLocalFolder_ResolvesScriptAndParsesPackageContent(bool relative)
    {
        using var temp = TempDir.Create();
        var project = CreateProject(temp.Root);
        var package = CreatePackage(Path.Combine(temp.Root, "[Package] Network Library"), FirstTargetGuid);
        var dependencyPath = relative
            ? Path.GetRelativePath(Path.Combine(project, "Packages"), package)
            : package;
        WriteManifest(project, "file:" + dependencyPath.Replace('\\', '/'));

        using var engine = UnBrambleEngine.Open(project);
        engine.RunIndex(full: false);

        var script = Assert.Single(engine.Resolve(ScriptGuid));
        Assert.Equal(ScriptPath, script.Path);
        Assert.False(script.IdentityOnly);
        var physicalScriptPath = Path.Combine(package, "Runtime", "NetworkEntity.cs");
        Assert.Equal(ScriptPath, Assert.Single(engine.Resolve(physicalScriptPath)).Path);
        Assert.Equal(ScriptPath, engine.ResolveQueryTarget(physicalScriptPath).Target?.Path);
        Assert.Contains(engine.GetRoots(), root =>
            root.ProjectPrefix == "Packages/" + PackageName
            && root.RealPath.Equals(package, StringComparison.OrdinalIgnoreCase));

        var prefab = engine.ResolveQueryTarget("Assets/Vehicle.prefab").Target!;
        var attachment = Assert.Single(engine.Uses(prefab, transitive: false, depthCap: 1).Results);
        Assert.True(attachment.Resolved);
        Assert.Equal(ScriptPath, attachment.TargetPath);
        Assert.DoesNotContain(engine.GetUnresolvedRefs(), reference => reference.TargetKey == ScriptGuid);

        var scriptTarget = engine.ResolveQueryTarget(ScriptGuid).Target!;
        Assert.Contains(engine.WhoUses(scriptTarget, transitive: false, depthCap: 1).Results,
            reference => reference.SourcePath == "Assets/Vehicle.prefab");
        AssertPackageTarget(engine, FirstTargetGuid);
        Assert.NotNull(engine.ResolveCsSymbol("NetworkEntity.Activate").DocId);
    }

    [Fact]
    public void TargetedUpdate_ExternalContentAndMeta_ReadsPersistedPackageMapping()
    {
        using var temp = TempDir.Create();
        var project = CreateProject(temp.Root);
        var package = CreatePackage(Path.Combine(temp.Root, "External Package"), FirstTargetGuid);
        WriteManifest(project, "file:" + package.Replace('\\', '/'));

        using var engine = UnBrambleEngine.Open(project);
        engine.RunIndex(full: false);

        var asset = Path.Combine(package, "Runtime", "Settings.asset");
        File.WriteAllText(asset, SerializedReference(SecondTargetGuid));
        File.SetLastWriteTimeUtc(asset, DateTime.UtcNow.AddSeconds(2));
        var scriptMeta = Path.Combine(package, "Runtime", "NetworkEntity.cs.meta");
        WriteMeta(scriptMeta, ChangedScriptGuid);
        File.SetLastWriteTimeUtc(scriptMeta, DateTime.UtcNow.AddSeconds(2));

        engine.ApplyTargetedUpdate([PackageAssetPath, ScriptPath]);

        AssertPackageTarget(engine, SecondTargetGuid);
        Assert.Equal(ScriptPath, Assert.Single(engine.Resolve(ChangedScriptGuid)).Path);
        Assert.Empty(engine.Resolve(ScriptGuid));
        Assert.Contains(engine.GetUnresolvedRefs(), reference =>
            reference.SourcePath == "Assets/Vehicle.prefab" && reference.TargetKey == ScriptGuid);
    }

    [Fact]
    public void Sweep_RemovedManifestDependency_RemovesExternalIdentityAndReferences()
    {
        using var temp = TempDir.Create();
        var project = CreateProject(temp.Root);
        var package = CreatePackage(Path.Combine(temp.Root, "External Package"), FirstTargetGuid);
        WriteManifest(project, "file:" + package.Replace('\\', '/'));

        using var engine = UnBrambleEngine.Open(project);
        engine.RunIndex(full: false);
        WriteManifest(project, dependency: null);

        engine.RunIndex(full: false);

        Assert.Empty(engine.Resolve(ScriptGuid));
        Assert.DoesNotContain(engine.GetAllFiles(), file => file.Path.StartsWith("Packages/" + PackageName + "/", StringComparison.Ordinal));
        Assert.DoesNotContain(engine.GetRoots(), root => root.ProjectPrefix == "Packages/" + PackageName);
        Assert.Contains(engine.GetUnresolvedRefs(), reference => reference.TargetKey == ScriptGuid);
        Assert.DoesNotContain(engine.WhoUses(engine.ResolveQueryTarget(FirstTargetGuid).Target!, transitive: false, depthCap: 1).Results,
            reference => reference.SourcePath == PackageAssetPath);
    }

    [Fact]
    public void Sweep_RetargetedPackageWithEqualMetadata_ReparsesNewContent()
    {
        using var temp = TempDir.Create();
        var project = CreateProject(temp.Root);
        var first = CreatePackage(Path.Combine(temp.Root, "First Package"), FirstTargetGuid);
        var second = CreatePackage(Path.Combine(temp.Root, "Second Package"), SecondTargetGuid);
        foreach (var firstFile in Directory.GetFiles(first, "*", SearchOption.AllDirectories))
        {
            var secondFile = Path.Combine(second, Path.GetRelativePath(first, firstFile));
            File.SetLastWriteTimeUtc(secondFile, File.GetLastWriteTimeUtc(firstFile));
        }

        WriteManifest(project, "file:" + first.Replace('\\', '/'));
        using var engine = UnBrambleEngine.Open(project);
        engine.RunIndex(full: false);

        WriteManifest(project, "file:" + second.Replace('\\', '/'));
        engine.RunIndex(full: false);

        AssertPackageTarget(engine, SecondTargetGuid);
        Assert.Contains(engine.GetRoots(), root => root.ProjectPrefix == "Packages/" + PackageName && root.RealPath == second);
    }

    [Fact]
    public void Scan_EmbeddedPackage_OverridesManifestLocalFolder()
    {
        using var temp = TempDir.Create();
        var project = CreateProject(temp.Root);
        var external = CreatePackage(Path.Combine(temp.Root, "External Package"), FirstTargetGuid);
        var embedded = CreatePackage(Path.Combine(project, "Packages", PackageName), SecondTargetGuid);
        WriteManifest(project, "file:" + external.Replace('\\', '/'));

        using var engine = UnBrambleEngine.Open(project);
        engine.RunIndex(full: false);

        AssertPackageTarget(engine, SecondTargetGuid);
        Assert.Single(engine.Resolve(ScriptGuid));
        Assert.DoesNotContain(engine.GetRoots(), root => root.RealPath == external);
        Assert.True(Directory.Exists(embedded));
    }

    [Fact]
    public void Scan_ExternalPackage_HonorsHiddenAssetRulesAndConfiguredRoots()
    {
        using var temp = TempDir.Create();
        var project = CreateProject(temp.Root);
        var package = CreatePackage(Path.Combine(temp.Root, "External Package"), FirstTargetGuid);
        var sample = Path.Combine(package, "Samples~", "Sample.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(sample)!);
        File.WriteAllText(sample, "class Sample { }");
        WriteMeta(sample + ".meta", ChangedScriptGuid);
        WriteManifest(project, "file:" + package.Replace('\\', '/'));

        var scanner = new Scanner();
        var included = scanner.Scan(project, new UnBrambleConfig());
        Assert.Contains(included.Entries, entry => entry.Path == ScriptPath);
        Assert.DoesNotContain(included.Entries, entry => entry.Guid == ChangedScriptGuid);

        var excluded = scanner.Scan(project, new UnBrambleConfig { Roots = ["Assets", "ProjectSettings"] });
        Assert.DoesNotContain(excluded.Entries, entry => entry.Path == ScriptPath);
    }

    [Fact]
    public void Watcher_ExternalPackageMetaChange_RefreshesWithoutSweep()
    {
        using var temp = TempDir.Create();
        var project = CreateProject(temp.Root);
        var package = CreatePackage(Path.Combine(temp.Root, "External Package"), FirstTargetGuid);
        WriteManifest(project, "file:" + package.Replace('\\', '/'));
        using var engine = UnBrambleEngine.Open(project);
        engine.RunIndex(full: false);

        using var host = new WatcherHost(engine, new WatcherOptions(
            DebounceInterval: TimeSpan.FromMilliseconds(100),
            HeartbeatIdleCadence: TimeSpan.FromSeconds(5),
            LockRetryInterval: TimeSpan.FromMilliseconds(100),
            SelfHealSweepInterval: TimeSpan.FromHours(1)));
        host.Start();
        try
        {
            var meta = Path.Combine(package, "Runtime", "NetworkEntity.cs.meta");
            WriteMeta(meta, ChangedScriptGuid);
            File.SetLastWriteTimeUtc(meta, DateTime.UtcNow.AddSeconds(2));

            Poll.Until(() =>
            {
                using var reader = UnBrambleEngine.Open(project);
                return reader.Resolve(ChangedScriptGuid).Count == 1;
            }, message: "The external package's meta change never reached the index through its watcher.");
            using var finalReader = UnBrambleEngine.Open(project);
            Assert.Empty(finalReader.Resolve(ScriptGuid));
        }
        finally
        {
            host.Stop();
        }
    }

    private static void AssertPackageTarget(UnBrambleEngine engine, string expectedGuid)
    {
        var target = engine.ResolveQueryTarget(PackageAssetPath).Target!;
        var reference = Assert.Single(engine.Uses(target, transitive: false, depthCap: 1).Results);
        Assert.True(reference.Resolved);
        Assert.Equal(expectedGuid, reference.TargetKey);
    }

    private static string CreateProject(string root)
    {
        var project = Path.Combine(root, "Project");
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        Directory.CreateDirectory(Path.Combine(project, "Packages"));
        Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
        File.WriteAllText(Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.0.1f1\n");
        File.WriteAllText(Path.Combine(project, "ProjectSettings", "EditorSettings.asset"), "EditorSettings:\n  m_SerializationMode: 2\n");
        File.WriteAllText(Path.Combine(project, "Assets", "Vehicle.prefab"),
            "%YAML 1.1\n--- !u!114 &1\nMonoBehaviour:\n  m_Script: {fileID: 11500000, guid: " + ScriptGuid + ", type: 3}\n");
        WriteMeta(Path.Combine(project, "Assets", "Vehicle.prefab.meta"), "a1710000000000000000000000000006");
        foreach (var (name, guid) in new[] { ("First.mat", FirstTargetGuid), ("Second.mat", SecondTargetGuid) })
        {
            var asset = Path.Combine(project, "Assets", name);
            File.WriteAllText(asset, "%YAML 1.1\n--- !u!21 &1\nMaterial:\n  m_Name: Target\n");
            WriteMeta(asset + ".meta", guid);
        }

        return project;
    }

    private static string CreatePackage(string root, string targetGuid)
    {
        Directory.CreateDirectory(Path.Combine(root, "Runtime"));
        File.WriteAllText(Path.Combine(root, "package.json"), "{\"name\":\"" + PackageName + "\",\"version\":\"1.0.0\"}");
        File.WriteAllText(Path.Combine(root, "Runtime", "Network.asmdef"), "{\"name\":\"Example.Network\",\"noEngineReferences\":true}");
        WriteMeta(Path.Combine(root, "Runtime", "Network.asmdef.meta"), "a1710000000000000000000000000007");
        File.WriteAllText(Path.Combine(root, "Runtime", "NetworkEntity.cs"),
            "namespace Example; public class NetworkEntity { public void Activate() { } }");
        WriteMeta(Path.Combine(root, "Runtime", "NetworkEntity.cs.meta"), ScriptGuid);
        File.WriteAllText(Path.Combine(root, "Runtime", "Settings.asset"), SerializedReference(targetGuid));
        WriteMeta(Path.Combine(root, "Runtime", "Settings.asset.meta"), PackageAssetGuid);
        return root;
    }

    private static string SerializedReference(string guid) =>
        "%YAML 1.1\n--- !u!114 &1\nMonoBehaviour:\n  m_Target: {fileID: 2100000, guid: " + guid + ", type: 2}\n";

    private static void WriteMeta(string path, string guid) =>
        File.WriteAllText(path, "fileFormatVersion: 2\nguid: " + guid + "\n");

    private static void WriteManifest(string project, string? dependency)
    {
        var dependencies = dependency is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { [PackageName] = dependency };
        File.WriteAllText(Path.Combine(project, "Packages", "manifest.json"), JsonSerializer.Serialize(new { dependencies }));
    }
}
