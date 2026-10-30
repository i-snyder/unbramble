using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using UnBramble.Core;
using UnBramble.Core.Freshness;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

public class LocalPackageWatcherTests
{
    private static WatcherOptions Options => new(
        DebounceInterval: TimeSpan.FromMilliseconds(100),
        HeartbeatIdleCadence: TimeSpan.FromMilliseconds(100),
        LockRetryInterval: TimeSpan.FromMilliseconds(100),
        SelfHealSweepInterval: TimeSpan.FromMinutes(10));

    [Fact]
    public void ExternalPackages_DiscoveredDuringPromotionAndManifestRetarget_RemainWatched()
    {
        using var temp = TempDir.Create();
        var project = CreateProject(temp.Root);
        var first = CreatePackage(temp.Root, "First Package", "com.example.external", '1');
        var second = CreatePackage(temp.Root, "Second Package", "com.example.external", '2');
        var added = CreatePackage(temp.Root, "Added Package", "com.example.added", '3');
        WriteManifest(project, ("com.example.external", first));
        using var engine = UnBrambleEngine.Open(project);
        var diagnostics = new ConcurrentQueue<string>();

        // No initial index: promotion must install watchers for roots first discovered by
        // its own catch-up sweep, before it publishes the first freshness heartbeat.
        using var host = new WatcherHost(engine, Options, onDiagnostic: diagnostics.Enqueue);
        host.Start();
        Assert.True(host.IsActive);
        AssertResolved(project, new string('1', 32), "Packages/com.example.external/Runtime/Entity.asset");

        WriteMeta(first, '4');
        WaitForGuid(project, new string('4', 32));

        // A manifest edit replaces one physical root and introduces another. Both new
        // roots must continue producing targeted updates after the manifest batch commits.
        WriteManifest(project, ("com.example.external", second), ("com.example.added", added));
        WaitForGuid(project, new string('2', 32));
        WaitForGuid(project, new string('3', 32));
        using (var reader = UnBrambleEngine.Open(project))
        {
            Assert.Empty(reader.Resolve(new string('4', 32)));
        }

        // Inventory is committed before watcher reconciliation and its catch-up pass.
        // Wait for both replacement watchers and the completed batch before testing the
        // steady-state event path; the separate attachment test covers edits in that gap.
        Poll.Until(() => diagnostics.Any(line => line.Contains(
                "watching root prefix=Packages/com.example.external ", StringComparison.Ordinal)
                && line.EndsWith("realPath=" + second, StringComparison.OrdinalIgnoreCase))
            && diagnostics.Any(line => line.Contains(
                "watching root prefix=Packages/com.example.added ", StringComparison.Ordinal)),
            message: "the manifest batch never attached watchers for the replacement package roots");
        Poll.Until(() => HeartbeatFile.TryRead(project) is not null,
            message: "the manifest batch never completed watcher reconciliation");

        WriteMeta(second, '5');
        WriteMeta(added, '6');
        WaitForGuid(project, new string('5', 32));
        WaitForGuid(project, new string('6', 32));
        Assert.Contains(diagnostics, line => line.Contains(
            "ACCEPTED path=Packages/com.example.external/Runtime/Entity.asset", StringComparison.Ordinal));
        Assert.Contains(diagnostics, line => line.Contains(
            "ACCEPTED path=Packages/com.example.added/Runtime/Entity.asset", StringComparison.Ordinal));
    }

    [Fact]
    public void NewlyDiscoveredRoot_IsResweptAfterWatcherAttaches_BeforeHeartbeatPublication()
    {
        using var temp = TempDir.Create();
        var project = CreateProject(temp.Root);
        var package = CreatePackage(temp.Root, "External Package", "com.example.external", '1');
        WriteManifest(project, ("com.example.external", package));
        using var engine = UnBrambleEngine.Open(project);
        var changedDuringAttachment = false;
        var options = Options with { DebounceInterval = TimeSpan.FromMinutes(10) };
        using var host = new WatcherHost(engine, options, onDiagnostic: line =>
        {
            if (!changedDuringAttachment && line.Contains("watching root prefix=Packages/com.example.external ", StringComparison.Ordinal))
            {
                changedDuringAttachment = true;
                WriteMeta(package, '2');
            }
        });

        host.Start();

        Assert.True(changedDuringAttachment);
        AssertResolved(project, new string('2', 32), "Packages/com.example.external/Runtime/Entity.asset");
    }

    [Fact]
    public void OverlappingPhysicalRoots_OnlyTheMostSpecificRootOwnsChildEvents()
    {
        using var temp = TempDir.Create();
        var project = CreateProject(temp.Root);
        var parent = CreatePackage(temp.Root, "Parent", "com.example.z-parent", '1');
        var child = CreatePackage(parent, "Child", "com.example.a-child", '2');
        WriteManifest(project, ("com.example.a-child", child), ("com.example.z-parent", parent));
        using var engine = UnBrambleEngine.Open(project);
        var diagnostics = new ConcurrentQueue<string>();
        using var host = new WatcherHost(engine, Options, onDiagnostic: diagnostics.Enqueue);
        host.Start();

        WriteMeta(child, '3');

        Poll.Until(() => diagnostics.Any(line => line.Contains(
            "REJECTED (owned-by: Packages/com.example.a-child) root=Packages/com.example.z-parent", StringComparison.Ordinal)),
            message: "the parent watcher never rejected the separately owned child event");
        WaitForGuid(project, new string('3', 32));
        AssertResolved(project, new string('3', 32), "Packages/com.example.a-child/Runtime/Entity.asset");
        using var reader = UnBrambleEngine.Open(project);
        Assert.DoesNotContain(reader.GetAllFiles(), file => file.Path.StartsWith(
            "Packages/com.example.z-parent/Child/", StringComparison.Ordinal));
    }

    [Fact]
    public void ExternalJunctionRetarget_WithoutManifestEvents_IsDetectedByIdleHeartbeat()
    {
        using var temp = TempDir.Create();
        var project = CreateProject(temp.Root);
        var first = CreatePackage(temp.Root, "First", "com.example.external", '1');
        var second = CreatePackage(temp.Root, "Second", "com.example.external", '2');
        var link = Path.Combine(temp.Root, "Package Link");
        CreateJunction(link, first);
        WriteManifest(project, ("com.example.external", link));
        using var engine = UnBrambleEngine.Open(project);
        var diagnostics = new ConcurrentQueue<string>();
        using var host = new WatcherHost(engine, Options, onDiagnostic: diagnostics.Enqueue);
        host.Start();

        // The link is outside both the project and the watched resolved package trees.
        Directory.Delete(link);
        CreateJunction(link, second);

        WaitForGuid(project, new string('2', 32));
        Poll.Until(() => diagnostics.Any(line => line.Contains(
            "local package topology changed; resyncing roots", StringComparison.Ordinal)),
            message: "the idle heartbeat never detected the external junction retarget");
        Poll.Until(() => HeartbeatFile.TryRead(project) is not null,
            message: "freshness never resumed after the external junction retarget");
        WriteMeta(second, '3');
        WaitForGuid(project, new string('3', 32));
    }

    [Fact]
    public async Task ExternalJunctionRetarget_QueryRejectsOldFreshHeartbeatUntilResyncCompletes()
    {
        using var temp = TempDir.Create();
        var project = CreateProject(temp.Root);
        var first = CreatePackage(temp.Root, "First", "com.example.external", '1');
        var second = CreatePackage(temp.Root, "Second", "com.example.external", '2');
        var link = Path.Combine(temp.Root, "Package Link");
        CreateJunction(link, first);
        WriteManifest(project, ("com.example.external", link));
        using var engine = UnBrambleEngine.Open(project);
        var options = Options with { HeartbeatIdleCadence = TimeSpan.FromMinutes(10) };
        using var host = new WatcherHost(engine, options);
        host.Start();
        using var reader = UnBrambleEngine.Open(project);
        using var waiting = new ManualResetEventSlim();
        Task? query = null;
        try
        {
            Directory.Delete(link);
            CreateJunction(link, second);
            Assert.NotNull(HeartbeatFile.TryRead(project));
            query = Task.Run(() => reader.EnsureFresh(onPhase: phase =>
            {
                if (phase.Contains("waiting for it", StringComparison.Ordinal))
                {
                    waiting.Set();
                }
            }));

            Assert.True(waiting.Wait(TimeSpan.FromSeconds(10)), "the query trusted the old heartbeat instead of waiting for the changed package topology");
            Assert.False(query.IsCompleted);
            host.SimulateWatcherError();
            await query.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal("Packages/com.example.external/Runtime/Entity.asset", Assert.Single(reader.Resolve(new string('2', 32))).Path);
            Assert.Empty(reader.Resolve(new string('1', 32)));
        }
        finally
        {
            host.Stop();
            if (query is not null)
            {
                await query.WaitAsync(TimeSpan.FromSeconds(20));
            }
        }
    }

    [Fact]
    public void LockedManifestDuringIdleCheck_SuppressesFreshnessAndRetriesOnNextHeartbeat()
    {
        using var temp = TempDir.Create();
        var project = CreateProject(temp.Root);
        var package = CreatePackage(temp.Root, "External", "com.example.external", '1');
        WriteManifest(project, ("com.example.external", package));
        using var engine = UnBrambleEngine.Open(project);
        var diagnostics = new ConcurrentQueue<string>();
        using var host = new WatcherHost(engine, Options, onDiagnostic: diagnostics.Enqueue);
        host.Start();

        // A read lock produces no filesystem change event; only the idle topology check
        // sees the temporary failure, so it must also arrange its own prompt recovery.
        using (var locked = new FileStream(Path.Combine(project, "Packages", "manifest.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Poll.Until(() => diagnostics.Any(line => line.Contains(
                "local package topology check failed", StringComparison.Ordinal)),
                message: "the idle check never observed the locked manifest");
            Assert.Null(HeartbeatFile.TryRead(project));
        }

        Poll.Until(() => HeartbeatFile.TryRead(project) is not null,
            message: "the next idle check never recovered freshness after the manifest was unlocked");
        AssertResolved(project, new string('1', 32), "Packages/com.example.external/Runtime/Entity.asset");
    }

    private static string CreateProject(string root)
    {
        var project = Path.Combine(root, "Project");
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        Directory.CreateDirectory(Path.Combine(project, "Packages"));
        Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
        File.WriteAllText(Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.0.1f1\n");
        File.WriteAllText(Path.Combine(project, "ProjectSettings", "EditorSettings.asset"), "m_SerializationMode: 2\n");
        return project;
    }

    private static string CreatePackage(string root, string folder, string packageName, char guidCharacter)
    {
        var package = Path.Combine(root, folder);
        Directory.CreateDirectory(Path.Combine(package, "Runtime"));
        File.WriteAllText(Path.Combine(package, "package.json"), JsonSerializer.Serialize(new { name = packageName, version = "1.0.0" }));
        File.WriteAllText(Path.Combine(package, "Runtime", "Entity.asset"), "%YAML 1.1\n--- !u!114 &1\nMonoBehaviour:\n  m_Name: Entity\n");
        WriteMeta(package, guidCharacter);
        return package;
    }

    private static void WriteMeta(string package, char guidCharacter) =>
        File.WriteAllText(Path.Combine(package, "Runtime", "Entity.asset.meta"), $"fileFormatVersion: 2\nguid: {new string(guidCharacter, 32)}\n");

    private static void WriteManifest(string project, params (string Name, string Path)[] packages) =>
        File.WriteAllText(Path.Combine(project, "Packages", "manifest.json"), JsonSerializer.Serialize(new
        {
            dependencies = packages.ToDictionary(package => package.Name, package => "file:" + package.Path.Replace('\\', '/')),
        }));

    private static void WaitForGuid(string project, string guid) => Poll.Until(() =>
    {
        using var reader = UnBrambleEngine.Open(project);
        return reader.Resolve(guid).Count == 1;
    }, message: $"external package GUID {guid} was never refreshed by the watcher");

    private static void AssertResolved(string project, string guid, string path)
    {
        using var reader = UnBrambleEngine.Open(project);
        Assert.Equal(path, Assert.Single(reader.Resolve(guid)).Path);
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("failed to start mklink");
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}
