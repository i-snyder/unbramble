using System.Collections.Concurrent;
using System.Xml.Linq;
using UnBramble.Core;
using UnBramble.Core.Freshness;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// Drives <see cref="WatcherHost"/> in-process against temp fixture copies with injected
/// (shrunk) timing knobs — WatcherHost's whole point is that it's host-able and drivable
/// in-process without shelling out to a real watcher process.
/// </summary>
public class WatcherHostTests
{
    private const string PrefabReferencingFooTemplate = """
        %YAML 1.1
        %TAG !u! tag:unity3d.com,2011:
        --- !u!1 &1000000001
        GameObject:
          m_ObjectHideFlags: 0
          m_CorrespondingSourceObject: {fileID: 0}
          m_PrefabInstance: {fileID: 0}
          m_PrefabAsset: {fileID: 0}
          serializedVersion: 6
          m_Component:
          - component: {fileID: 1000000002}
          m_Layer: 0
          m_Name: WatcherCreated
          m_TagString: Untagged
          m_Icon: {fileID: 0}
          m_NavMeshLayer: 0
          m_StaticEditorFlags: 0
          m_IsActive: 1
        --- !u!114 &1000000002
        MonoBehaviour:
          m_ObjectHideFlags: 0
          m_CorrespondingSourceObject: {fileID: 0}
          m_PrefabInstance: {fileID: 0}
          m_PrefabAsset: {fileID: 0}
          m_GameObject: {fileID: 1000000001}
          m_Enabled: 1
          m_EditorHideFlags: 0
          m_Script: {fileID: 11500000, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa01, type: 3}
          m_Name:
          m_EditorClassIdentifier:
        """;

    private static WatcherOptions FastOptions(TimeSpan? selfHeal = null) => new(
        DebounceInterval: TimeSpan.FromMilliseconds(150),
        HeartbeatIdleCadence: TimeSpan.FromSeconds(5),
        LockRetryInterval: TimeSpan.FromMilliseconds(200),
        SelfHealSweepInterval: selfHeal ?? TimeSpan.FromMinutes(5));

    // ---- Test 1: create / modify / delete -------------------------------------------------

    [Fact]
    public void CreateModifyDelete_AllReflectedInIndexViaEvents()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        using var host = new WatcherHost(engine, FastOptions());
        host.Start();
        try
        {
            Assert.True(host.IsActive);

            // Create: a new prefab + meta referencing Assets/Scripts/Foo.cs (guid ...aaaa01).
            var prefabPath = fixture.Combine("Assets", "Prefabs", "WatcherCreated.prefab");
            File.WriteAllText(prefabPath, PrefabReferencingFooTemplate);
            File.WriteAllText(prefabPath + ".meta", "fileFormatVersion: 2\nguid: 10101010101010101010101010101001\n");

            Poll.Until(
                () => WhoUsesFoo(engine).Any(r => r.SourcePath.Equals("Assets/Prefabs/WatcherCreated.prefab", StringComparison.OrdinalIgnoreCase)),
                message: "create: new prefab referencing Foo.cs never showed up in who-uses");

            // Modify: change the new prefab's own meta guid.
            var newPrefabMeta = prefabPath + ".meta";
            File.WriteAllText(newPrefabMeta, "fileFormatVersion: 2\nguid: 20202020202020202020202020202002\n");
            File.SetLastWriteTimeUtc(newPrefabMeta, DateTime.UtcNow.AddSeconds(2));

            Poll.Until(
                () => engine.GetAllFiles().Any(f =>
                    f.Path.Equals("Assets/Prefabs/WatcherCreated.prefab", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(f.Guid, "20202020202020202020202020202002", StringComparison.OrdinalIgnoreCase)),
                message: "modify: guid change on the prefab's meta never landed");

            // Delete: remove the prefab and its meta.
            File.Delete(prefabPath);
            File.Delete(newPrefabMeta);

            Poll.Until(
                () => !engine.GetAllFiles().Any(f => f.Path.Equals("Assets/Prefabs/WatcherCreated.prefab", StringComparison.OrdinalIgnoreCase)),
                message: "delete: prefab row was never removed");
        }
        finally
        {
            host.Stop();
        }
    }

    // ---- Test 2: asset + meta reparsed as one unit ------------------------------------------

    [Fact]
    public void TouchingOnlyTheMetaFile_ReparsesTheOwningAsset()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        using var host = new WatcherHost(engine, FastOptions());
        host.Start();
        try
        {
            var metaPath = fixture.Combine("Assets", "Data", "A.asset.meta");
            var content = File.ReadAllText(metaPath)
                .Replace("a0a0a0a0a0a0a0a0a0a0a0a0a0a0a019", "a0a0a0a0a0a0a0a0a0a0a0a0a0a0a099", StringComparison.Ordinal);
            File.WriteAllText(metaPath, content);
            File.SetLastWriteTimeUtc(metaPath, DateTime.UtcNow.AddSeconds(5));

            Poll.Until(
                () =>
                {
                    var row = engine.GetAllFiles().FirstOrDefault(f => f.Path.Equals("Assets/Data/A.asset", StringComparison.OrdinalIgnoreCase));
                    return row is not null && string.Equals(row.Guid, "a0a0a0a0a0a0a0a0a0a0a0a0a0a0a099", StringComparison.OrdinalIgnoreCase);
                },
                message: "touching only the .meta never reparsed the owning asset's guid");

            Assert.DoesNotContain(
                engine.GetAllFiles(),
                f => string.Equals(f.Guid, "a0a0a0a0a0a0a0a0a0a0a0a0a0a0a019", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            host.Stop();
        }
    }

    // ---- Test 4: lock / promotion order + buffer-not-lost-during-buffering ------------------

    [Fact]
    public void LockAndPromotion_PassiveHostPromotesAfterActiveStops_OrderIsLoadBearing_BufferNotLost()
    {
        using var fixture = FixtureCopy.Create();
        using var engineA = UnBrambleEngine.Open(fixture.Root);
        engineA.RunIndex(full: false);

        var options = FastOptions();

        var eventsA = new List<WatcherEvent>();
        using var hostA = new WatcherHost(engineA, options, e => { lock (eventsA) { eventsA.Add(e); } });

        using var engineB = UnBrambleEngine.Open(fixture.Root);
        var eventsB = new List<WatcherEvent>();
        string? writtenDuringBuffering = null;

        using var hostB = new WatcherHost(engineB, options, e =>
        {
            lock (eventsB)
            {
                eventsB.Add(e);
            }

            // Guaranteed to fire while hostB's internal _buffering flag is still true: Promote()
            // only flips it false AFTER this sweep completes and the buffer is drained.
            if (e == WatcherEvent.PromotionCatchupSweepStarted)
            {
                var newAsset = fixture.Combine("Assets", "Data", "DuringBuffering.asset");
                File.WriteAllText(newAsset, "fake");
                File.WriteAllText(newAsset + ".meta", "fileFormatVersion: 2\nguid: f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0\n");
                writtenDuringBuffering = "Assets/Data/DuringBuffering.asset";
            }
        });

        try
        {
            hostA.Start();
            Assert.True(hostA.IsActive);

            hostB.Start();
            Assert.False(hostB.IsActive, "hostB should lose the lock while hostA holds it");

            hostA.Stop();

            Poll.Until(() => hostB.IsActive, TimeSpan.FromSeconds(15), message: "hostB never promoted after hostA released the lock");

            List<WatcherEvent> snapshot;
            lock (eventsB)
            {
                snapshot = [.. eventsB];
            }

            var buffering = snapshot.IndexOf(WatcherEvent.PromotionBufferingStarted);
            var sweepStarted = snapshot.IndexOf(WatcherEvent.PromotionCatchupSweepStarted);
            var sweepCompleted = snapshot.IndexOf(WatcherEvent.PromotionCatchupSweepCompleted);
            var drained = snapshot.IndexOf(WatcherEvent.PromotionBufferDrained);
            var heartbeatStarted = snapshot.IndexOf(WatcherEvent.PromotionHeartbeatStarted);

            Assert.True(buffering >= 0, "buffering-started event never fired");
            Assert.True(sweepStarted >= 0, "catch-up sweep never started");
            Assert.True(sweepCompleted >= 0, "catch-up sweep never completed");
            Assert.True(drained >= 0, "buffer-drained event never fired");
            Assert.True(heartbeatStarted >= 0, "heartbeat-started event never fired");

            // The promotion-order assertion: buffering strictly before the catch-up sweep,
            // strictly before the drain, strictly before the heartbeat.
            Assert.True(buffering < sweepStarted, "buffering must start before the catch-up sweep");
            Assert.True(sweepStarted < sweepCompleted, "catch-up sweep start must precede its own completion");
            Assert.True(sweepCompleted < drained, "catch-up sweep must complete before the buffer drains");
            Assert.True(drained < heartbeatStarted, "the buffer must be drained before the heartbeat starts");

            Assert.NotNull(writtenDuringBuffering);
            Poll.Until(
                () => engineB.GetAllFiles().Any(f =>
                    f.Path.Equals(writtenDuringBuffering, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(f.Guid, "f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0", StringComparison.OrdinalIgnoreCase)),
                message: "a file written during hostB's buffering window was lost");
        }
        finally
        {
            hostA.Stop();
            hostB.Stop();
        }
    }

    // ---- Test 5: FSW.Error -> immediate full resync ------------------------------------------

    [Fact]
    public void SimulatedWatcherError_TriggersImmediateFullResync()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        // A deliberately long debounce so a normal event-driven batch could not plausibly have
        // applied the change within this test's short assertion window -- only the resync path
        // (which bypasses debounce entirely, calling RunIndex synchronously) can explain it.
        var options = FastOptions() with { DebounceInterval = TimeSpan.FromMinutes(10) };

        using var host = new WatcherHost(engine, options);
        host.Start();
        try
        {
            var newAsset = fixture.Combine("Assets", "Data", "ViaResync.asset");
            File.WriteAllText(newAsset, "fake");
            File.WriteAllText(newAsset + ".meta", "fileFormatVersion: 2\nguid: 30303030303030303030303030303003\n");

            Assert.DoesNotContain(engine.GetAllFiles(), f => f.Path.Equals("Assets/Data/ViaResync.asset", StringComparison.OrdinalIgnoreCase));

            host.SimulateWatcherError();

            Assert.Contains(engine.GetAllFiles(), f =>
                f.Path.Equals("Assets/Data/ViaResync.asset", StringComparison.OrdinalIgnoreCase)
                && string.Equals(f.Guid, "30303030303030303030303030303003", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            host.Stop();
        }
    }

    // ---- Test 6: directory rename/delete self-heal gap ---------------------------------------

    [Fact]
    public void DirectoryRename_DescendantsStaleUntilSelfHealSweepRepairsThem()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var selfHealInterval = TimeSpan.FromSeconds(2);
        var options = FastOptions(selfHeal: selfHealInterval);

        using var host = new WatcherHost(engine, options);
        host.Start();
        try
        {
            var oldDir = fixture.Combine("Assets", "Data");
            var newDir = fixture.Combine("Assets", "DataRenamed");
            Directory.Move(oldDir, newDir);
            if (File.Exists(oldDir + ".meta"))
            {
                File.Move(oldDir + ".meta", newDir + ".meta");
            }

            // Give the debounce/event path a fair chance to run, then confirm the known gap:
            // FileSystemWatcher fires one event for the renamed directory itself, none for
            // its descendants, so the descendant file (A.asset) does not yet appear at its new
            // path. This is expected behavior, not a bug -- clever descendant inference to close
            // it is deliberately not attempted.
            Thread.Sleep(TimeSpan.FromMilliseconds(800));
            Assert.DoesNotContain(
                engine.GetAllFiles(),
                f => f.Path.Equals("Assets/DataRenamed/A.asset", StringComparison.OrdinalIgnoreCase));

            // The periodic self-heal full sweep is the only thing that closes this gap.
            Poll.Until(
                () => engine.GetAllFiles().Any(f =>
                    f.Path.Equals("Assets/DataRenamed/A.asset", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(f.Guid, "a0a0a0a0a0a0a0a0a0a0a0a0a0a0a019", StringComparison.OrdinalIgnoreCase)),
                timeout: selfHealInterval + TimeSpan.FromSeconds(15),
                message: "self-heal sweep never repaired the renamed directory's descendants");

            Assert.DoesNotContain(
                engine.GetAllFiles(),
                f => f.Path.Equals("Assets/Data/A.asset", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            host.Stop();
        }
    }

    // ---- Test 7: watch-startup compilation-cache pre-warm ------------------------------------

    /// <summary>
    /// Proves the fix for the watch-startup gap: `watch` used to leave its persistent Roslyn
    /// compilation cache (<see cref="UnBramble.Core.CSharp.CsCompilationCache"/>) completely empty
    /// until the user's FIRST real file-change event, paying the full cold-cache-population cost
    /// synchronously on that edit instead of during its own startup (see
    /// <see cref="UnBrambleEngine.EnableWatchCompilationCache"/>'s doc comment on the pre-warm it
    /// now arms). Forces the realistic gap condition -- the project already indexed once BEFORE
    /// `watch` starts, via a separate, disposed engine instance -- so the watch engine's own
    /// Promote() catch-up sweep would otherwise hit RunCsAnalysis's R1 skip-gate (nothing dirty,
    /// every unit name already known) and never touch the cache at all.
    /// </summary>
    [Fact]
    public void Start_PreWarmsCompilationCacheDuringPromotion_FirstRealEditIsCacheHitNotFullRebuild()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticCsproj(fixture.Root, "Core");
        WriteSemanticCsproj(fixture.Root, "Game");

        // The project was already indexed once before `watch` starts -- e.g. a prior `unbramble
        // index`, or an earlier watch session -- via a SEPARATE engine instance, now disposed.
        // The watch engine below therefore opens against an already-populated store where every
        // unit name is already known and nothing is dirty: exactly the condition under which the
        // R1 skip-gate would fire on Promote's own catch-up sweep absent the pre-warm fix.
        using (var priorEngine = UnBrambleEngine.Open(fixture.Root))
        {
            priorEngine.RunIndex(full: false);
        }

        using var engine = UnBrambleEngine.Open(fixture.Root);
        var log = new ConcurrentQueue<string>();
        engine.EnableWatchCompilationCache(log.Enqueue);

        using var host = new WatcherHost(engine, FastOptions());
        host.Start(); // Synchronous: Promote() (including its catch-up sweep) has already run.
        try
        {
            Assert.True(host.IsActive);

            // Proof #1: the pre-warm already happened DURING promotion, before any real edit --
            // both Semantic units already show a cold "cache miss" full build in the log.
            Assert.Contains(log, l => l.Contains("Core build: FULL-REBUILD reason=cache-miss"));
            Assert.Contains(log, l => l.Contains("Game build: FULL-REBUILD reason=cache-miss"));

            log.Clear();

            // Proof #2: the user's FIRST real edit is now a cheap incremental derive (a cache
            // HIT), never another full rebuild -- if the pre-warm hadn't actually run, THIS edit
            // is the one that would have paid for the cold "cache-miss" build instead.
            var coreUtilPath = fixture.Combine("Assets", "Scripts", "Core", "CoreUtil.cs");
            File.WriteAllText(coreUtilPath, File.ReadAllText(coreUtilPath) + "\n// pre-warm test edit\n");
            File.SetLastWriteTimeUtc(coreUtilPath, DateTime.UtcNow.AddSeconds(5));

            Poll.Until(
                () => log.Any(l => l.Contains("Core build:")),
                message: "first real edit after promote never produced a cache-decision log line for Core");

            Assert.DoesNotContain(log, l => l.Contains("FULL-REBUILD"));
            Assert.Contains(log, l => l.Contains("Core build: INCREMENTAL self-dirty=True"));
        }
        finally
        {
            host.Stop();
        }
    }

    /// <summary>Minimal generated-IDE-csproj stand-in at the fixture root -- what
    /// <c>CsModeSelector.Determine</c> looks for to engage Semantic mode: one
    /// <c>&lt;Reference&gt;/&lt;HintPath&gt;</c> per trusted platform assembly, so semantic
    /// analysis resolves real, resolvable BCL types. Same trick <see cref="CsWatchCompilationCacheTests"/>
    /// uses, duplicated here (not shared) to keep each test file's fixture setup self-contained.</summary>
    private static void WriteSemanticCsproj(string fixtureRoot, string assemblyName)
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var itemGroup = new XElement(
            "ItemGroup",
            paths.Select(p => new XElement(
                "Reference",
                new XAttribute("Include", Path.GetFileNameWithoutExtension(p)),
                new XElement("HintPath", p))));
        var doc = new XDocument(new XElement("Project", itemGroup));
        doc.Save(Path.Combine(fixtureRoot, assemblyName + ".csproj"));
    }

    private static List<(string SourcePath, bool Resolved)> WhoUsesFoo(UnBrambleEngine engine)
    {
        var resolution = engine.ResolveQueryTarget("Assets/Scripts/Foo.cs");
        if (resolution.Target is null)
        {
            return [];
        }

        var answer = engine.WhoUses(resolution.Target, transitive: false, depthCap: UnBrambleEngine.DefaultDepthCap);
        return [.. answer.Results.Select(r => (r.SourcePath, r.Resolved))];
    }
}
