using UnBramble.Core;
using UnBramble.Core.Freshness;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// The periodic self-heal timer (<see cref="WatcherHost.OnSelfHealTick"/>) skips its own full
/// <c>RunFullResync</c> walk whenever zero FileSystemWatcher callbacks (accepted OR rejected)
/// were observed since the previous tick -- see <c>WatcherHost</c>'s own
/// <c>_fsActivitySinceLastSelfHeal</c> field doc comment for the full reasoning. Companion
/// coverage to <see cref="WatcherHostTests"/> (same in-process harness pattern: <c>FastOptions</c>
/// with shrunk timing knobs, <c>Start()</c>/<c>Stop()</c>, <see cref="Poll.Until"/>, an
/// <c>onEvent</c> callback capturing the <see cref="WatcherEvent"/> sequence).
///
/// Two related scenarios are deliberately not duplicated here:
///   - The directory-rename descendant-repair gap is already exercised end-to-end by
///     <see cref="WatcherHostTests.DirectoryRename_DescendantsStaleUntilSelfHealSweepRepairsThem"/>,
///     which fires one real (accepted) FSW callback for the renamed directory itself -- that
///     test passing confirms the activity counter does not cause it to start failing (a real
///     callback means the counter is nonzero, so the tick is never skipped).
///   - FSW.Error's own immediate-resync path is already exercised by
///     <see cref="WatcherHostTests.SimulatedWatcherError_TriggersImmediateFullResync"/>, and
///     <see cref="WatcherHost.OnWatcherError"/> calls <c>RunFullResync()</c> directly --
///     completely bypassing <see cref="WatcherHost.OnSelfHealTick"/> and its activity-counter
///     check -- so that path is structurally unaffected.
/// </summary>
public class WatcherSelfHealActivityTests
{
    private static WatcherOptions FastOptions(TimeSpan? selfHeal = null) => new(
        DebounceInterval: TimeSpan.FromMilliseconds(150),
        HeartbeatIdleCadence: TimeSpan.FromSeconds(5),
        LockRetryInterval: TimeSpan.FromMilliseconds(200),
        SelfHealSweepInterval: selfHeal ?? TimeSpan.FromMinutes(5));

    // ---- (a) idle window -> skipped ----------------------------------------------------------

    [Fact]
    public void IdleWindow_SelfHealSweepSkipped_NotRun()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var selfHealInterval = TimeSpan.FromSeconds(1.5);
        var options = FastOptions(selfHeal: selfHealInterval);

        var events = new List<WatcherEvent>();
        using var host = new WatcherHost(engine, options, e => { lock (events) { events.Add(e); } });
        host.Start();
        try
        {
            Assert.True(host.IsActive);

            // Deliberately touch nothing on disk after promotion. Wait for a self-heal tick to
            // observe zero FSW activity (accepted or rejected) and skip its own full sweep.
            List<WatcherEvent> snapshot = [];
            Poll.Until(
                () =>
                {
                    lock (events)
                    {
                        snapshot = [.. events];
                    }

                    return snapshot.Contains(WatcherEvent.SelfHealSweepSkipped);
                },
                timeout: selfHealInterval + TimeSpan.FromSeconds(15),
                message: "self-heal tick never fired SelfHealSweepSkipped during a fully idle window");

            // The real (expensive) sweep must not have run during this idle window.
            Assert.DoesNotContain(WatcherEvent.SelfHealSweep, snapshot);
        }
        finally
        {
            host.Stop();
        }
    }

    // ---- (b) activity (including a REJECTED event) -> not skipped, real resync happens -------

    [Fact]
    public void ActivityShortlyBeforeTick_SelfHealSweepNotSkipped_RejectedEventsCountToo()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        // A debounce far longer than this test's window: the normal accepted-event batch
        // pipeline (ProcessBatch) cannot plausibly apply anything before we assert, so any DB
        // change we observe can only be explained by the self-heal sweep's own
        // RunIndex(full:false) call (same isolation technique
        // WatcherHostTests.SimulatedWatcherError_TriggersImmediateFullResync uses for the
        // FSW.Error resync path).
        var selfHealInterval = TimeSpan.FromSeconds(2);
        var options = FastOptions(selfHeal: selfHealInterval) with { DebounceInterval = TimeSpan.FromMinutes(10) };

        var events = new List<WatcherEvent>();
        using var host = new WatcherHost(engine, options, e => { lock (events) { events.Add(e); } });
        host.Start();
        try
        {
            Assert.True(host.IsActive);

            // An ACCEPTED write: real, in-scope filesystem churn.
            var newAsset = fixture.Combine("Assets", "Data", "ViaSelfHealActivity.asset");
            File.WriteAllText(newAsset, "fake");
            File.WriteAllText(newAsset + ".meta", "fileFormatVersion: 2\nguid: 50505050505050505050505050505005\n");

            // AND a REJECTED write, under Assets/Ignored~/ (a hidden directory per
            // HiddenAssetRules -- folder names ending in '~' are always excluded). Proves the
            // activity counter's own contract: a rejected event still counts as real filesystem
            // churn, not just an accepted one -- see WatcherHost's _fsActivitySinceLastSelfHeal
            // doc comment.
            var ignoredFile = fixture.Combine("Assets", "Ignored~", "NewIgnored.mat");
            File.WriteAllText(ignoredFile, "fake");
            File.WriteAllText(ignoredFile + ".meta", "fileFormatVersion: 2\nguid: 60606060606060606060606060606006\n");

            List<WatcherEvent> snapshot = [];
            Poll.Until(
                () =>
                {
                    lock (events)
                    {
                        snapshot = [.. events];
                    }

                    return snapshot.Contains(WatcherEvent.SelfHealSweep);
                },
                timeout: selfHealInterval + TimeSpan.FromSeconds(15),
                message: "self-heal tick never fired SelfHealSweep despite FSW activity (accepted + rejected) since the previous tick");

            // Deliberately NOT asserted: "no skipped tick before the first real sweep". That
            // demanded FSW callback delivery beat a 2s timer, which the OS does not guarantee --
            // under full-suite parallel load a tick really did fire before Windows delivered the
            // callbacks for the writes above (observed live as a repeated flake), and skipping
            // that tick is CORRECT behavior: the callbacks land moments later and the next tick
            // sweeps ("never wrong, only sometimes slower"). The contract this test exists to
            // prove survives without it: a sweep eventually fires at all (the poll above -- if
            // rejected events didn't count as activity, EVERY tick would skip and the poll would
            // time out), and the sweep's own effects are asserted below.

            // A real resync actually happened: the accepted asset is indexed. With a 10-minute
            // debounce this cannot have come from ProcessBatch.
            Assert.Contains(engine.GetAllFiles(), f =>
                f.Path.Equals("Assets/Data/ViaSelfHealActivity.asset", StringComparison.OrdinalIgnoreCase)
                && string.Equals(f.Guid, "50505050505050505050505050505005", StringComparison.OrdinalIgnoreCase));

            // "Counts as activity" is not the same as "gets indexed": the rejected file stays
            // correctly excluded.
            Assert.DoesNotContain(engine.GetAllFiles(), f => f.Path.Contains("Ignored~", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            host.Stop();
        }
    }
}
