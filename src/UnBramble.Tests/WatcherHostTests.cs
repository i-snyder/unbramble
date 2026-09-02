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

    [Fact]
    public async Task Promotion_InvalidatesInheritedOldHeartbeat_BeforeCatchupSweep()
    {
        using var fixture = FixtureCopy.Create();
        using var hostEngine = UnBrambleEngine.Open(fixture.Root);
        hostEngine.RunIndex(full: false);
        WriteRawHeartbeatWithoutProtocol(fixture.Root);

        IDisposable? writerBlocker = IndexWriterLock.Acquire(fixture.Root);
        using var promotionStarted = new ManualResetEventSlim();
        using var host = new WatcherHost(
            hostEngine,
            FastOptions(),
            onEvent: e =>
            {
                if (e == WatcherEvent.PromotionCatchupSweepStarted)
                {
                    promotionStarted.Set();
                }
            });
        var startTask = Task.Run(host.Start);
        Task<UnBramble.Core.Model.FreshnessOutcome>? queryTask = null;
        UnBrambleEngine? queryEngine = null;
        try
        {
            Assert.True(promotionStarted.Wait(TimeSpan.FromSeconds(5)), "Watcher never entered promotion.");
            Assert.Null(HeartbeatFile.TryRead(fixture.Root));

            queryEngine = UnBrambleEngine.Open(fixture.Root);
            queryTask = Task.Run(() => queryEngine.EnsureFresh());
            var whilePromotionBlocked = await Task.WhenAny(queryTask, Task.Delay(TimeSpan.FromMilliseconds(500)));
            Assert.NotSame(queryTask, whilePromotionBlocked);

            writerBlocker.Dispose();
            writerBlocker = null;

            await startTask.WaitAsync(TimeSpan.FromSeconds(15));
            var outcome = await queryTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(outcome.ConcurrentSweepInProgress);

            var heartbeat = HeartbeatFile.TryRead(fixture.Root);
            Assert.NotNull(heartbeat);
            Assert.Equal(HeartbeatFile.CurrentProtocolVersion, heartbeat.Value.Protocol);
        }
        finally
        {
            writerBlocker?.Dispose();
            queryEngine?.Dispose();
            host.Stop();
            await startTask.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

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

    [Fact]
    public void LockedAssetBatch_IsRetriedWithoutPublishingEmptyReferencesOrSuccessHeartbeat()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);
        var diagnostics = new ConcurrentQueue<string>();

        var options = FastOptions() with
        {
            HeartbeatIdleCadence = TimeSpan.FromMilliseconds(100),
            LockRetryInterval = TimeSpan.FromSeconds(2),
        };
        using var host = new WatcherHost(engine, options, onDiagnostic: diagnostics.Enqueue);
        host.Start();
        try
        {
            var assetPath = fixture.Combine("Assets", "Data", "A.asset");
            using (var locked = new FileStream(assetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                locked.SetLength(0);
                locked.Write(System.Text.Encoding.UTF8.GetBytes(
                    "%YAML 1.1\n--- !u!114 &1\nMonoBehaviour:\n  m_Name: changed\n"));
                locked.Flush(flushToDisk: true);

                Poll.Until(
                    () => diagnostics.Any(line => line.Contains("batch failed", StringComparison.Ordinal)),
                    timeout: TimeSpan.FromSeconds(10),
                    message: "the locked watcher batch never failed visibly");

                Assert.Null(HeartbeatFile.TryRead(fixture.Root));
                Thread.Sleep(TimeSpan.FromMilliseconds(400));
                Assert.Null(HeartbeatFile.TryRead(fixture.Root));

                using var reader = UnBrambleEngine.Open(fixture.Root);
                var b = reader.ResolveQueryTarget("Assets/Data/B.asset").Target;
                Assert.NotNull(b);
                Assert.Contains(
                    reader.WhoUses(b, transitive: false, depthCap: 1).Results,
                    result => result.SourcePath == "Assets/Data/A.asset");
            }

            Poll.Until(
                () =>
                {
                    var b = engine.ResolveQueryTarget("Assets/Data/B.asset").Target;
                    return b is not null
                        && engine.WhoUses(b, transitive: false, depthCap: 1).Results.All(
                            result => result.SourcePath != "Assets/Data/A.asset");
                },
                timeout: TimeSpan.FromSeconds(15),
                message: "the watcher never retried the locked asset after it was released");
            Poll.Until(
                () => HeartbeatFile.TryRead(fixture.Root) is not null,
                message: "a successful retry never resumed heartbeat publication");
        }
        finally
        {
            host.Stop();
        }
    }

    [Fact]
    public async Task AcceptedEvent_InvalidatesHeartbeatThroughoutDebounceUntilCommit()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);
        var diagnostics = new ConcurrentQueue<string>();
        var options = FastOptions() with
        {
            DebounceInterval = TimeSpan.FromSeconds(2),
            HeartbeatIdleCadence = TimeSpan.FromMilliseconds(100),
        };

        using var host = new WatcherHost(engine, options, onDiagnostic: diagnostics.Enqueue);
        host.Start();
        UnBrambleEngine? queryEngine = null;
        try
        {
            Assert.NotNull(HeartbeatFile.TryRead(fixture.Root));
            var assetPath = fixture.Combine("Assets", "Data", "A.asset");
            File.WriteAllText(assetPath, "%YAML 1.1\n--- !u!114 &1\nMonoBehaviour:\n  m_Name: debounce\n");

            Poll.Until(
                () => diagnostics.Any(line => line.Contains("ACCEPTED path=Assets/Data/A.asset", StringComparison.OrdinalIgnoreCase)),
                message: "the asset edit was never accepted by the watcher");

            Assert.Null(HeartbeatFile.TryRead(fixture.Root));
            Thread.Sleep(TimeSpan.FromMilliseconds(400));
            Assert.Null(HeartbeatFile.TryRead(fixture.Root));

            queryEngine = UnBrambleEngine.Open(fixture.Root);
            var queryTask = Task.Run(() => queryEngine.EnsureFresh());
            var duringDebounce = await Task.WhenAny(queryTask, Task.Delay(TimeSpan.FromMilliseconds(500)));
            Assert.NotSame(queryTask, duringDebounce);

            var outcome = await queryTask.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(outcome.SweepPerformed);
            Assert.True(
                outcome.HeartbeatAge is not null || outcome.ConcurrentUpdateCompleted,
                "the query should finish from either the watcher's published heartbeat or the batch commit it directly waited for");
        }
        finally
        {
            queryEngine?.Dispose();
            host.Stop();
        }
    }

    [Fact]
    public void EventQueuedDuringBatch_PreventsFirstBatchFromRepublishingHeartbeat()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);
        var diagnostics = new ConcurrentQueue<string>();
        using var firstBatchApplied = new ManualResetEventSlim();
        using var secondBatchApplied = new ManualResetEventSlim();
        var batchCount = 0;
        var heartbeatMissingAfterFirstBatch = false;
        var options = FastOptions() with { HeartbeatIdleCadence = TimeSpan.FromMilliseconds(100) };
        IDisposable? writerBlocker = null;

        using var host = new WatcherHost(
            engine,
            options,
            onEvent: watcherEvent =>
            {
                if (watcherEvent != WatcherEvent.BatchApplied)
                {
                    return;
                }

                var count = Interlocked.Increment(ref batchCount);
                if (count == 1)
                {
                    heartbeatMissingAfterFirstBatch = HeartbeatFile.TryRead(fixture.Root) is null;
                    firstBatchApplied.Set();
                }
                else if (count == 2)
                {
                    secondBatchApplied.Set();
                }
            },
            onDiagnostic: diagnostics.Enqueue);

        host.Start();
        try
        {
            writerBlocker = IndexWriterLock.Acquire(fixture.Root);
            var firstPath = fixture.Combine("Assets", "Data", "A.asset");
            File.AppendAllText(firstPath, "\n");
            Poll.Until(
                () => diagnostics.Any(line => line.Contains("debounce fired", StringComparison.Ordinal)),
                message: "the first batch never began while the writer lease was blocked");

            var secondPath = fixture.Combine("Assets", "Data", "B.asset");
            File.AppendAllText(secondPath, "\n");
            Poll.Until(
                () => diagnostics.Any(line => line.Contains("ACCEPTED path=Assets/Data/B.asset", StringComparison.OrdinalIgnoreCase)),
                message: "the second edit was not queued during the first batch");

            writerBlocker.Dispose();
            writerBlocker = null;

            Assert.True(firstBatchApplied.Wait(TimeSpan.FromSeconds(15)), "the first batch never completed");
            Assert.True(heartbeatMissingAfterFirstBatch, "the first batch published a heartbeat while a second accepted edit was pending");
            Assert.True(secondBatchApplied.Wait(TimeSpan.FromSeconds(15)), "the queued second batch never completed");
            Poll.Until(
                () => HeartbeatFile.TryRead(fixture.Root) is not null,
                message: "heartbeat publication did not resume after all queued batches committed");
        }
        finally
        {
            writerBlocker?.Dispose();
            host.Stop();
        }
    }

    [Fact]
    public async Task ErrorResync_CannotPublishWhileDebouncedBatchIsWaitingToRun()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);
        var diagnostics = new ConcurrentQueue<string>();
        using var errorResyncCompleted = new ManualResetEventSlim();
        using var batchApplied = new ManualResetEventSlim();
        var heartbeatMissingAtResyncCompletion = false;
        IDisposable? writerBlocker = null;
        using var host = new WatcherHost(
            engine,
            FastOptions(),
            onEvent: watcherEvent =>
            {
                if (watcherEvent == WatcherEvent.ErrorResync)
                {
                    heartbeatMissingAtResyncCompletion = HeartbeatFile.TryRead(fixture.Root) is null;
                    errorResyncCompleted.Set();
                }
                else if (watcherEvent == WatcherEvent.BatchApplied)
                {
                    batchApplied.Set();
                }
            },
            onDiagnostic: diagnostics.Enqueue);

        host.Start();
        Task? resyncTask = null;
        try
        {
            writerBlocker = IndexWriterLock.Acquire(fixture.Root);
            resyncTask = Task.Run(host.SimulateWatcherError);
            Poll.Until(
                () => diagnostics.Any(line => line.Contains("acquired database gate", StringComparison.Ordinal)),
                message: "the error resync never reached the blocked writer lease");

            File.AppendAllText(fixture.Combine("Assets", "Data", "A.asset"), "\n");
            Poll.Until(
                () => diagnostics.Any(line => line.Contains("ACCEPTED path=Assets/Data/A.asset", StringComparison.OrdinalIgnoreCase)),
                message: "the edit was not accepted while the resync was blocked");
            await Task.Delay(TimeSpan.FromMilliseconds(400));

            writerBlocker.Dispose();
            writerBlocker = null;

            Assert.True(errorResyncCompleted.Wait(TimeSpan.FromSeconds(15)), "the error resync never completed");
            Assert.True(heartbeatMissingAtResyncCompletion, "the resync published freshness while an accepted batch was still pending");
            Assert.True(batchApplied.Wait(TimeSpan.FromSeconds(15)), "the pending batch never ran after the resync");
            Poll.Until(
                () => HeartbeatFile.TryRead(fixture.Root) is not null,
                message: "heartbeat publication did not resume after the resync and batch both committed");
        }
        finally
        {
            writerBlocker?.Dispose();
            host.Stop();
            if (resyncTask is not null)
            {
                await resyncTask.WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
    }

    [Fact]
    public async Task QueuedErrorResync_PreventsBatchAheadOfItFromPublishingFreshness()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);
        var diagnostics = new ConcurrentQueue<string>();
        using var batchApplied = new ManualResetEventSlim();
        using var errorResyncCompleted = new ManualResetEventSlim();
        var heartbeatMissingAtBatchCompletion = false;
        IDisposable? writerBlocker = null;
        using var host = new WatcherHost(
            engine,
            FastOptions(),
            onEvent: watcherEvent =>
            {
                if (watcherEvent == WatcherEvent.BatchApplied)
                {
                    heartbeatMissingAtBatchCompletion = HeartbeatFile.TryRead(fixture.Root) is null;
                    batchApplied.Set();
                }
                else if (watcherEvent == WatcherEvent.ErrorResync)
                {
                    errorResyncCompleted.Set();
                }
            },
            onDiagnostic: diagnostics.Enqueue);

        host.Start();
        Task? errorTask = null;
        try
        {
            writerBlocker = IndexWriterLock.Acquire(fixture.Root);
            File.AppendAllText(fixture.Combine("Assets", "Data", "A.asset"), "\n");
            Poll.Until(
                () => diagnostics.Any(line => line.Contains("debounce fired", StringComparison.Ordinal)),
                message: "the batch never entered the operation gate");

            errorTask = Task.Run(host.SimulateWatcherError);
            Poll.Until(
                () => diagnostics.Any(line => line.Contains("intent registered", StringComparison.Ordinal)),
                message: "the error resync never registered while waiting behind the batch");

            writerBlocker.Dispose();
            writerBlocker = null;

            Assert.True(batchApplied.Wait(TimeSpan.FromSeconds(15)), "the batch never completed");
            Assert.True(heartbeatMissingAtBatchCompletion, "the batch published freshness while an error resync was queued behind it");
            Assert.True(errorResyncCompleted.Wait(TimeSpan.FromSeconds(15)), "the queued error resync never completed");
            await errorTask.WaitAsync(TimeSpan.FromSeconds(15));
            Poll.Until(
                () => HeartbeatFile.TryRead(fixture.Root) is not null,
                message: "heartbeat publication did not resume after the queued resync committed");
        }
        finally
        {
            writerBlocker?.Dispose();
            host.Stop();
            if (errorTask is not null)
            {
                await errorTask.WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
    }

    [Fact]
    public void FailedErrorResync_BlocksLaterBatchFreshnessUntilAFullResyncSucceeds()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);
        var diagnostics = new ConcurrentQueue<string>();
        using var batchApplied = new ManualResetEventSlim();
        using var host = new WatcherHost(
            engine,
            FastOptions(),
            onEvent: watcherEvent =>
            {
                if (watcherEvent == WatcherEvent.BatchApplied)
                {
                    batchApplied.Set();
                }
            },
            onDiagnostic: diagnostics.Enqueue);

        host.Start();
        var intentPath = IndexWriterLock.IntentPathFor(fixture.Root);
        try
        {
            File.Delete(intentPath);
            Directory.CreateDirectory(intentPath);

            host.SimulateWatcherError();
            Assert.Contains(
                diagnostics,
                line => line.Contains("self-heal/error-resync sweep: FAILED", StringComparison.Ordinal));
            Assert.Null(HeartbeatFile.TryRead(fixture.Root));

            Directory.Delete(intentPath);
            using (File.Create(intentPath))
            {
            }

            File.AppendAllText(fixture.Combine("Assets", "Data", "A.asset"), "\n");
            Assert.True(batchApplied.Wait(TimeSpan.FromSeconds(15)), "the unrelated targeted batch never completed");
            Assert.Null(HeartbeatFile.TryRead(fixture.Root));
            using (var suppressedSnapshot = WatcherLock.TryAcquireSnapshot(fixture.Root, out var unavailableReason))
            {
                Assert.NotNull(suppressedSnapshot);
                Assert.Equal(WatcherLock.SnapshotUnavailableReason.None, unavailableReason);
                Assert.True(suppressedSnapshot.IsFreshnessSuppressed);
            }

            host.SimulateWatcherError();
            Poll.Until(
                () => HeartbeatFile.TryRead(fixture.Root) is not null,
                message: "a successful full resync did not clear the required-resync latch");
            using var freshSnapshot = WatcherLock.TryAcquireSnapshot(fixture.Root, out var freshUnavailableReason);
            Assert.NotNull(freshSnapshot);
            Assert.Equal(WatcherLock.SnapshotUnavailableReason.None, freshUnavailableReason);
            Assert.False(freshSnapshot.IsFreshnessSuppressed);
        }
        finally
        {
            if (Directory.Exists(intentPath))
            {
                Directory.Delete(intentPath);
            }

            host.Stop();
        }
    }

    [Fact]
    public async Task ErrorResync_CannotRunInsidePromotionAndLeaveFreshnessSuppressed()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);
        using var catchupStarted = new ManualResetEventSlim();
        using var promotionPaused = new ManualResetEventSlim();
        using var continuePromotion = new ManualResetEventSlim();
        var diagnostics = new ConcurrentQueue<string>();
        var heartbeatMissingWhenPromoted = false;
        IDisposable? writerBlocker = IndexWriterLock.Acquire(fixture.Root);
        using var host = new WatcherHost(
            engine,
            FastOptions(),
            onEvent: watcherEvent =>
            {
                if (watcherEvent == WatcherEvent.PromotionCatchupSweepStarted)
                {
                    catchupStarted.Set();
                }
                else if (watcherEvent == WatcherEvent.PromotionCatchupSweepCompleted)
                {
                    promotionPaused.Set();
                    continuePromotion.Wait();
                }
                else if (watcherEvent == WatcherEvent.Promoted)
                {
                    heartbeatMissingWhenPromoted = HeartbeatFile.TryRead(fixture.Root) is null;
                }
            },
            onDiagnostic: diagnostics.Enqueue);

        var startTask = Task.Run(host.Start);
        Task? errorTask = null;
        try
        {
            Assert.True(catchupStarted.Wait(TimeSpan.FromSeconds(5)), "promotion never entered its catch-up sweep");
            writerBlocker.Dispose();
            writerBlocker = null;
            Assert.True(promotionPaused.Wait(TimeSpan.FromSeconds(10)), "promotion never reached the post-catch-up pause");

            errorTask = Task.Run(host.SimulateWatcherError);
            Poll.Until(
                () => diagnostics.Any(line => line.Contains("intent registered", StringComparison.Ordinal)),
                message: "the error resync never registered while promotion was paused");
            var whilePromotionPaused = await Task.WhenAny(errorTask, Task.Delay(TimeSpan.FromMilliseconds(750)));
            Assert.NotSame(errorTask, whilePromotionPaused);

            continuePromotion.Set();
            await startTask.WaitAsync(TimeSpan.FromSeconds(15));
            await errorTask.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(host.IsActive);
            Assert.True(heartbeatMissingWhenPromoted, "promotion published freshness while an error resync was queued behind it");
            Assert.NotNull(HeartbeatFile.TryRead(fixture.Root));
            using var snapshot = WatcherLock.TryAcquireSnapshot(fixture.Root, out var unavailableReason);
            Assert.NotNull(snapshot);
            Assert.Equal(WatcherLock.SnapshotUnavailableReason.None, unavailableReason);
            Assert.False(snapshot.IsFreshnessSuppressed);
        }
        finally
        {
            continuePromotion.Set();
            writerBlocker?.Dispose();
            host.Stop();
            await startTask.WaitAsync(TimeSpan.FromSeconds(15));
            if (errorTask is not null)
            {
                await errorTask.WaitAsync(TimeSpan.FromSeconds(15));
            }
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

    private static void WriteRawHeartbeatWithoutProtocol(string projectRoot)
    {
        var path = HeartbeatFile.PathFor(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var utc = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(
            path,
            $"{{\"pid\":999,\"utc\":\"{utc}\",\"schema\":{UnBramble.Core.Store.UnBrambleStore.CurrentSchemaVersion}}}");
    }
}
